using System;
using System.Collections.Generic;
using System.Reflection;
using CG;
using CG.Client.Player.Interactions;
using CG.Input;
using CG.Game;
using CG.Game.Player;
using Gameplay.Chat;
using UnityEngine;
using UnityEngine.InputSystem;

namespace VoidCrewFleet
{
    /// <summary>
    /// Punching in PvP (key V by default, PvP.PunchKey): the only way to fight a boarder inside a ship. A punch hits the
    /// enemy player in front of you within reach and in plain sight: it hurts them (five punches from full health kill)
    /// and pushes them back. While an enemy is in reach a hint under the crosshair says so. The punched player's own
    /// client applies the damage after checking the puncher is an enemy and close by, the way the game applies all
    /// damage to a character, so the hit shows on their screen like any other. Nobody can be punched inside a clone tube.
    /// </summary>
    internal static class PvpPunch
    {
        public const float Reach = 2.4f;
        public const float Damage = 0.2f;
        private const float CooldownSeconds = 0.7f;
        private const float MaxAngle = 35f;
        private const float HintSeconds = 0.1f;
        private const int SightMask = (1 << 10) | (1 << 12) | (1 << 27);

        private const InteractionMode Busy = InteractionMode.Menu | InteractionMode.TerminalScreen | InteractionMode.Dead | InteractionMode.ControllingShip |
                                             InteractionMode.ControllingTurret | InteractionMode.Seated | InteractionMode.Cryptic | InteractionMode.InCloneTube |
                                             InteractionMode.UsingPeriscope | InteractionMode.WorldSpaceUI | InteractionMode.Build | InteractionMode.WaitingForRespawn |
                                             InteractionMode.SchematicView;

        private static readonly FieldInfo ChatOpenField = GameMember.Field(typeof(TextChat), "_chatOpened");
        private static readonly Dictionary<int, float> LastHitFrom = new Dictionary<int, float>();
        private static float _nextPunch;
        private static float _nextHint;

        private static bool IsEnemyActor(int actor)
        {
            int mine = FleetShips.CrewSlot(FleetShips.LocalActor);
            int theirs = FleetShips.CrewSlot(actor);
            return mine >= 0 && theirs >= 0 && mine != theirs;
        }

        private static int ActorOf(Player player)
        {
            return player != null && player.photonView != null && player.photonView.Owner != null ? player.photonView.Owner.ActorNumber : -1;
        }

        private static string NameOf(Player player)
        {
            Photon.Realtime.Player owner = player != null && player.photonView != null ? player.photonView.Owner : null;
            return owner != null && !string.IsNullOrEmpty(owner.NickName) ? owner.NickName : "enemy";
        }

        /// <summary>Every frame: the punch key, and a few times a second the hint.</summary>
        public static void Frame()
        {
            if (!PvpCombat.Active || Settings.PunchKey == null)
            {
                ShowHint(null);
                return;
            }
            try
            {
                Keyboard keyboard = Keyboard.current;
                Key key = Settings.PunchKey.Value;
                if (keyboard != null && key != Key.None && keyboard[key].wasPressedThisFrame) TryPunch(false);
                // Fire punches too (a mouse button, or the VR controller's trigger), with empty hands and an enemy in reach.
                InputAction fire = Fire();
                if (fire != null && fire.WasPressedThisFrame() && !PvpHack.Busy && HandsEmpty()) TryPunch(true);
                if (Time.unscaledTime >= _nextHint)
                {
                    _nextHint = Time.unscaledTime + HintSeconds;
                    Player target = CanPunch() ? FindTarget() : null;
                    string keys = Settings.PunchKey.Value.ToString().ToUpperInvariant() + (HandsEmpty() ? " OR " + FireName(fire) : "");
                    ShowHint(target != null ? keys + "  PUNCH " + NameOf(target).ToUpperInvariant() : null);
                }
            }
            catch (Exception e)
            {
                Log.Failure("Punching", e);
            }
        }

        /// <summary>The local player is alive, on foot and not in a menu, chat or seat (also used by the hacking terminal).</summary>
        internal static bool CanPunch()
        {
            LocalPlayer me = LocalPlayer.Instance;
            if (me == null || me.IsDead) return false;
            if ((me.InteractionState.ActiveModes & Busy) != 0 || MenuScreenController.IsOpen()) return false;
            if (ChatOpenField != null && TextChat.Instance != null && ChatOpenField.GetValue(TextChat.Instance) is bool && (bool)ChatOpenField.GetValue(TextChat.Instance))
                return false;
            return Camera.main != null;
        }

        /// <summary>The game's Fire action (left mouse button, or the VR controller's trigger).</summary>
        internal static InputAction Fire()
        {
            InputService service = ServiceBase<InputService>.Instance;
            return service != null && service.InputActionReferences != null && service.InputActionReferences.Fire1 != null
                ? service.InputActionReferences.Fire1.action
                : null;
        }

        internal static string FireName(InputAction fire)
        {
            string name = null;
            try
            {
                name = fire != null ? fire.GetBindingDisplayString() : null;
            }
            catch
            {
                // Just the label.
            }
            return string.IsNullOrEmpty(name) ? "FIRE" : name.ToUpperInvariant();
        }

        private static bool HandsEmpty()
        {
            LocalPlayer me = LocalPlayer.Instance;
            return me != null && me.Payload == null;
        }

        /// <param name="onlyAtSomeone">From Fire: punch only when an enemy is in reach (a click elsewhere isn't a punch).</param>
        private static void TryPunch(bool onlyAtSomeone)
        {
            if (Time.unscaledTime < _nextPunch || !CanPunch()) return;
            Player target = FindTarget();
            if (target == null && onlyAtSomeone) return;
            _nextPunch = Time.unscaledTime + CooldownSeconds;
            Shake(0.25f);
            if (target == null)
            {
                Log.Verbose("PvP: punched the air (no enemy in reach).");
                return;
            }
            Net.ToActor(ActorOf(target), Msg.Punch, Damage, LocalPlayer.Instance.transform.position);
            Log.Info("PvP: punched " + NameOf(target) + ".");
        }

        /// <summary>The enemy player in front of the camera within reach and in plain sight, nearest to the aim first.</summary>
        private static Player FindTarget()
        {
            ClientGame game = ClientGame.Current;
            LocalPlayer me = LocalPlayer.Instance;
            Vector3 eye;
            Vector3 aim;
            if (game == null || me == null || !Aim.Get(out eye, out aim)) return null;
            Player best = null;
            float bestScore = float.MaxValue;
            foreach (Player player in game.Players)
            {
                if (player == null || (object)player == me || player.IsDead || !IsEnemyActor(ActorOf(player))) continue;
                Vector3 feet = player.transform.position;
                Vector3 up = player.transform.up;
                float score = float.MaxValue;
                Vector3 aimPoint = feet;
                foreach (float height in new[] { 0.6f, 1.1f, 1.6f })
                {
                    Vector3 point = feet + up * height;
                    Vector3 toPoint = point - eye;
                    float distance = toPoint.magnitude;
                    if (distance > Reach) continue;
                    float angle = Vector3.Angle(aim, toPoint);
                    // Close up, the body fills the view: allow a wider angle.
                    float allowed = distance < 1.2f ? MaxAngle * 1.6f : MaxAngle;
                    if (angle > allowed) continue;
                    float pointScore = angle + distance * 4f;
                    if (pointScore >= score) continue;
                    score = pointScore;
                    aimPoint = point;
                }
                if (score >= bestScore) continue;
                if (Physics.Linecast(eye, aimPoint, SightMask, QueryTriggerInteraction.Ignore)) continue;
                bestScore = score;
                best = player;
            }
            return best;
        }

        // ---------------------------------------------------------------- the hint

        private static void ShowHint(string text)
        {
            ActionHint.Set("punch", ActionHint.PunchPriority, text);
        }

        internal static void Shake(float intensity)
        {
            try
            {
                if (CameraShakeEffectController.Instance != null) CameraShakeEffectController.Instance.Shake(new CameraShakeDetail("FleetPunch", intensity, 6f, 0.25f));
            }
            catch
            {
                // Only feedback.
            }
        }

        // ---------------------------------------------------------------- being punched

        public static void Listen()
        {
            Net.On(Msg.Punch, (sender, data) =>
            {
                if (!PvpCombat.Active) return;
                if (!IsEnemyActor(sender))
                {
                    Log.Warn("PvP: ignored a punch from player " + sender + ", who is not on the other team.");
                    return;
                }
                LocalPlayer me = LocalPlayer.Instance;
                if (me == null || me.IsDead || (me.InteractionState.ActiveModes & InteractionMode.InCloneTube) != 0) return;
                float last;
                if (LastHitFrom.TryGetValue(sender, out last) && Time.unscaledTime - last < CooldownSeconds * 0.5f) return;
                Player attacker = FleetShips.CharacterOf(sender);
                Vector3 from = attacker != null ? attacker.transform.position : Net.Get(data, 1, me.transform.position);
                float distance = Vector3.Distance(from, me.transform.position);
                if (distance > Reach + 3f)
                {
                    Log.Warn("PvP: ignored a punch from " + distance.ToString("0.0") + " m away.");
                    return;
                }
                LastHitFrom[sender] = Time.unscaledTime;
                TakePunch(me, from, Mathf.Clamp(Net.Get(data, 0, Damage), 0f, Damage));
                Log.Info("PvP: you were punched by " + NameOf(attacker) + ".");
            });
        }

        /// <summary>The local player takes a punch from a point: damage as a physical hit, a push and a jolt.</summary>
        public static void TakePunch(LocalPlayer me, Vector3 from, float damage)
        {
            Vector3 push = me.transform.position - from;
            Vector3 direction = push.sqrMagnitude > 0.001f ? push.normalized : -me.transform.forward;
            me.ApplyHitDamage(damage, null, me.ImpactDamageType, me.transform.position + me.transform.up, Quaternion.LookRotation(-direction), true);
            try
            {
                if (me.Character != null) me.Character.AddForce(direction * 0.15f);
            }
            catch
            {
                // Knockback is a nicety.
            }
            Shake(0.6f);
        }
    }
}
