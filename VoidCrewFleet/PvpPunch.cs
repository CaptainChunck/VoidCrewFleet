using System;
using System.Collections.Generic;
using System.Reflection;
using CG;
using CG.Client.Player.Interactions;
using CG.Input;
using CG.Game;
using CG.Game.Player;
using ExitGames.Client.Photon;
using Gameplay.Chat;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;

namespace VoidCrewFleet
{
    /// <summary>
    /// Fist fighting in PvP, the only way to fight a boarder inside a ship. Hold the right mouse button (a gamepad's left
    /// trigger) to raise your fists into a fighting stance; each left click (right trigger) then punches, alternating arms,
    /// left first. A punch hits the enemy player in front of you within reach and in plain sight: it hurts them (five
    /// punches from full health knock one out) and pushes them back. Everyone around sees the stance (a player property)
    /// and every punch (a message to the players nearby; FistPose poses the arms). The punched player's own client applies
    /// the damage, the way the game applies all damage to a character, so the hit shows on their screen like any other.
    /// It first checks the puncher, so a modified game can't punch from afar or faster: an enemy whose character is alive,
    /// not seated or in a clone tube, close by where this client sees them, and punching no faster than the real rate (a
    /// burst of two allowed for network jitter). Nobody can be punched inside a clone tube.
    /// </summary>
    internal static class PvpPunch
    {
        public const float Reach = 2.0f;
        public const float Damage = 0.2f;
        public const string KeyStance = "VCF_Fist";
        private const float CooldownSeconds = 0.45f;
        private const float MaxAngle = 35f;
        private const float HintSeconds = 0.1f;
        private const float StanceSendSeconds = 0.2f;

        /// <summary>The punches a victim takes from one player at once (more come only one per cooldown), for network jitter.</summary>
        private const float PunchBurst = 2f;

        /// <summary>Players this close see a punch (the victim always does).</summary>
        private const float WatchDistance = 30f;

        private const int SightMask = (1 << 10) | (1 << 12) | (1 << 27);

        /// <summary>The heights on a body (from the feet) a punch can be aimed at.</summary>
        private static readonly float[] TargetHeights = { 0.6f, 1.1f, 1.6f };

        internal const InteractionMode Busy = InteractionMode.Menu | InteractionMode.TerminalScreen | InteractionMode.Dead | InteractionMode.ControllingShip |
                                              InteractionMode.ControllingTurret | InteractionMode.Seated | InteractionMode.Cryptic | InteractionMode.InCloneTube |
                                              InteractionMode.UsingPeriscope | InteractionMode.WorldSpaceUI | InteractionMode.Build | InteractionMode.WaitingForRespawn |
                                              InteractionMode.SchematicView;

        /// <summary>Weightless: the gamepad's triggers move the player up and down, so they don't fight with them there.</summary>
        private const InteractionMode WeightlessModes = InteractionMode.InSpace | InteractionMode.SwimZeroGrav | InteractionMode.FlyingJetpack;

        private static readonly FieldInfo ChatOpenField = GameMember.Field(typeof(TextChat), "_chatOpened");
        private static readonly FieldInfo CloneTubesField = GameMember.Field(typeof(SpawnSystem), "clonetubes");

        /// <summary>The victim's count of each puncher's punches in hand (it grows back by one per cooldown, up to the burst).</summary>
        private struct PunchBudget
        {
            public float Punches;
            public float At;
        }

        private static readonly Dictionary<int, PunchBudget> Budgets = new Dictionary<int, PunchBudget>();
        private static float _nextPunch;
        private static float _nextHint;
        private static bool _stanceUp;
        private static int _nextArm;
        private static bool _sentStance;
        private static float _stanceSentAt = -10f;
        private static InputAction _padStance;
        private static InputAction _padPunch;
        private static bool _testHold;

        /// <summary>The local player's fists are raised.</summary>
        public static bool LocalStanceUp
        {
            get { return _stanceUp; }
        }

        private static bool IsEnemyActor(int actor)
        {
            int mine = FleetShips.TeamOfActor(FleetShips.LocalActor);
            int theirs = FleetShips.TeamOfActor(actor);
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

        /// <summary>Whether a player has their fists raised (the local player's own state, else their player property).</summary>
        public static bool StanceOf(Player player)
        {
            if (player == null) return false;
            if (player == LocalPlayer.Instance) return _stanceUp;
            Photon.Realtime.Player owner = player.photonView != null ? player.photonView.Owner : null;
            object value;
            return owner != null && owner.CustomProperties != null && owner.CustomProperties.TryGetValue(KeyStance, out value) && value is bool && (bool)value;
        }

        // ---------------------------------------------------------------- every frame

        /// <summary>Every frame: the stance and punches, and a few times a second the hint.</summary>
        public static void Frame()
        {
            try
            {
                if (!PvpCombat.Active)
                {
                    SetStance(false);
                    SendStance();
                    ShowHint(null);
                    SetPadActions(false);
                    if (Budgets.Count > 0) Budgets.Clear();
                    return;
                }
                SetPadActions(true);
                bool fight = CanFight();
                SetStance(fight && StanceHeld());
                SendStance();
                if (_stanceUp && PunchPressed() && Time.unscaledTime >= _nextPunch) Punch();
                if (Time.unscaledTime >= _nextHint)
                {
                    _nextHint = Time.unscaledTime + HintSeconds;
                    UpdateHint(fight);
                }
            }
            catch (Exception e)
            {
                Log.Failure("Fist fighting", e);
            }
        }

        /// <summary>The local player is alive, on foot and not in a menu, chat or seat (also used by the hacking terminal).</summary>
        internal static bool CanPunch()
        {
            LocalPlayer me = LocalPlayer.Instance;
            if (me == null || PlayerLife.IsDead(me)) return false;
            if ((me.InteractionState.ActiveModes & Busy) != 0 || MenuScreenController.IsOpen()) return false;
            if (TextChat.Instance != null && GameMember.Read(ChatOpenField, TextChat.Instance, false)) return false;
            return Camera.main != null;
        }

        /// <summary>The local player can raise their fists: they can punch and their hands are empty.</summary>
        private static bool CanFight()
        {
            LocalPlayer me = LocalPlayer.Instance;
            return CanPunch() && me.Payload == null;
        }

        /// <summary>The game's Fire action (left mouse button).</summary>
        internal static InputAction Fire()
        {
            InputService service = ServiceBase<InputService>.Instance;
            return service != null && service.InputActionReferences != null && service.InputActionReferences.Fire1 != null
                ? service.InputActionReferences.Fire1.action
                : null;
        }

        /// <summary>The game's second fire action (right mouse button).</summary>
        private static InputAction Fire2()
        {
            InputService service = ServiceBase<InputService>.Instance;
            return service != null && service.InputActionReferences != null && service.InputActionReferences.Fire2 != null
                ? service.InputActionReferences.Fire2.action
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

        private static bool UsingGamepad
        {
            get
            {
                InputService service = ServiceBase<InputService>.Instance;
                return service != null && service.UsingGamepad;
            }
        }

        /// <summary>
        /// The gamepad's triggers, which the game leaves free on foot (B and X are the game's second fire and fire): on only
        /// during a match.
        /// </summary>
        private static void SetPadActions(bool on)
        {
            if (_padStance == null)
            {
                if (!on) return;
                _padStance = new InputAction("Fleet fist stance", InputActionType.Button, "<Gamepad>/leftTrigger");
                _padPunch = new InputAction("Fleet fist punch", InputActionType.Button, "<Gamepad>/rightTrigger");
            }
            if (on == _padStance.enabled) return;
            if (on)
            {
                _padStance.Enable();
                _padPunch.Enable();
            }
            else
            {
                _padStance.Disable();
                _padPunch.Disable();
            }
        }

        private static bool FromMouse(InputAction action)
        {
            InputControl control = action.activeControl;
            return control == null || !(control.device is Gamepad);
        }

        private static bool Weightless()
        {
            LocalPlayer me = LocalPlayer.Instance;
            return me != null && (me.InteractionState.ActiveModes & WeightlessModes) != 0;
        }

        /// <summary>The stance is held: the right mouse button, or the gamepad's left trigger in gravity.</summary>
        private static bool StanceHeld()
        {
            if (_testHold) return true;
            InputAction fire2 = Fire2();
            if (fire2 != null && fire2.IsPressed() && FromMouse(fire2)) return true;
            return _padStance != null && _padStance.IsPressed() && !Weightless();
        }

        /// <summary>A punch is asked for this frame: the left mouse button, or the gamepad's right trigger.</summary>
        private static bool PunchPressed()
        {
            InputAction fire = Fire();
            if (fire != null && fire.WasPressedThisFrame() && FromMouse(fire)) return true;
            return _padPunch != null && _padPunch.WasPressedThisFrame() && !Weightless();
        }

        // ---------------------------------------------------------------- the stance

        private static void SetStance(bool up)
        {
            if (up == _stanceUp) return;
            _stanceUp = up;
            // Every raise starts with the left fist.
            if (up) _nextArm = 0;
        }

        /// <summary>The stance as a player property, at most five times a second (the last state always goes out).</summary>
        private static void SendStance()
        {
            if (_stanceUp == _sentStance || Time.unscaledTime - _stanceSentAt < StanceSendSeconds) return;
            Photon.Realtime.Player local = PhotonNetwork.LocalPlayer;
            if (local == null || !PhotonNetwork.InRoom) return;
            _stanceSentAt = Time.unscaledTime;
            _sentStance = _stanceUp;
            local.SetCustomProperties(new Hashtable { { KeyStance, _stanceUp ? (object)true : null } });
        }

        /// <summary>Picking things up waits while the fists are up (left click is a punch then).</summary>
        [GamePatch(PvpCombat.Feature, "CarryableInteract", "StartInteraction", PatchKind.Prefix, Optional = true,
            Arguments = new[] { "CG.Client.Player.Interactions.AbstractInteractable", "System.Boolean" })]
        private static bool PickUpPrefix(bool ignorePlacingObjects, LocalPlayer ___player)
        {
            return ignorePlacingObjects || !_stanceUp || ___player == null || ___player != LocalPlayer.Instance;
        }

        // ---------------------------------------------------------------- punching

        private static void Punch()
        {
            LocalPlayer me = LocalPlayer.Instance;
            _nextPunch = Time.unscaledTime + CooldownSeconds;
            int arm = _nextArm;
            _nextArm = 1 - _nextArm;
            Player target = FindTarget();
            Vector3 eye;
            Vector3 aim;
            if (!Aim.Get(out eye, out aim)) aim = me.transform.forward;
            FistPose.Punch(me, arm, target, aim);
            int victim = target != null ? ActorOf(target) : -1;
            var watchers = new List<int>();
            ClientGame game = ClientGame.Current;
            if (game != null)
            {
                foreach (Player player in game.Players)
                {
                    if (player == null || player == me) continue;
                    if ((player.transform.position - me.transform.position).sqrMagnitude <= WatchDistance * WatchDistance) watchers.Add(ActorOf(player));
                }
            }
            if (victim >= 0) watchers.Add(victim);
            Net.ToOtherActors(watchers, Msg.FistSwing, (byte)arm, victim, victim >= 0 ? Damage : 0f, me.transform.position);
            Shake(0.15f);
            if (target != null) Log.Info("PvP: punched " + NameOf(target) + " (" + (arm == 0 ? "left" : "right") + ").");
            else Log.Verbose("PvP: punched the air (" + (arm == 0 ? "left" : "right") + ").");
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
                if (player == null || (object)player == me || PlayerLife.IsDead(player) || !IsEnemyActor(ActorOf(player))) continue;
                Vector3 feet = player.transform.position;
                Vector3 up = player.transform.up;
                float score = float.MaxValue;
                Vector3 aimPoint = feet;
                foreach (float height in TargetHeights)
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

        private static void UpdateHint(bool fight)
        {
            if (!fight)
            {
                ShowHint(null);
                return;
            }
            bool pad = UsingGamepad;
            Player target = FindTarget();
            if (_stanceUp)
            {
                ShowHint((pad ? "RT" : "LMB") + "  PUNCH" + (target != null ? " " + NameOf(target).ToUpperInvariant() : ""));
                return;
            }
            if (pad && Weightless()) target = null;
            ShowHint(target != null ? (pad ? "HOLD LT" : "HOLD RMB") + "  RAISE YOUR FISTS" : null);
        }

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

        // ---------------------------------------------------------------- others' punches

        public static void Listen()
        {
            Net.On(Msg.FistSwing, (sender, data) =>
            {
                if (!PvpCombat.Active) return;
                int arm = Net.Get(data, 0, (byte)0) == 1 ? 1 : 0;
                int victim = Net.Get(data, 1, -1);
                Player attacker = FleetShips.CharacterOf(sender);
                bool attackerAlive = attacker != null && !PlayerLife.IsDead(attacker);
                LocalPlayer me = LocalPlayer.Instance;
                Player target = victim < 0 ? null : (me != null && victim == FleetShips.LocalActor ? me : FleetShips.CharacterOf(victim));
                if (attackerAlive) FistPose.Punch(attacker, arm, target, attacker.transform.forward);
                if (victim != FleetShips.LocalActor || me == null) return;
                if (!IsEnemyActor(sender))
                {
                    Log.Warn("PvP: ignored a punch from player " + sender + ", who is not on the other team.");
                    return;
                }
                if (PlayerLife.IsDead(me) || (me.InteractionState.ActiveModes & InteractionMode.InCloneTube) != 0) return;
                // The puncher as this client sees them (never the position in the message): alive, free to punch and close by.
                if (!attackerAlive)
                {
                    Log.Warn("PvP: ignored a punch from player " + sender + ", who has no living character here.");
                    return;
                }
                if (SeatedOrInTube(attacker))
                {
                    Log.Warn("PvP: ignored a punch from " + NameOf(attacker) + ", who is seated or in a clone tube.");
                    return;
                }
                Vector3 from = attacker.transform.position;
                float distance = Vector3.Distance(from, me.transform.position);
                if (distance > Reach + 3f)
                {
                    Log.Warn("PvP: ignored a punch from " + distance.ToString("0.0") + " m away.");
                    return;
                }
                if (!TakeFromBudget(sender))
                {
                    Log.Verbose("PvP: ignored a punch from " + NameOf(attacker) + ", faster than anyone can punch.");
                    return;
                }
                TakePunch(me, from, Mathf.Clamp(Net.Get(data, 2, Damage), 0f, Damage));
                Log.Info("PvP: you were punched by " + NameOf(attacker) + ".");
            });
        }

        /// <summary>
        /// Whether one more punch from this player fits the real punch rate: one per cooldown, plus a burst of two so punches
        /// that arrive bunched up by the network still land. Counts the punch when it does.
        /// </summary>
        private static bool TakeFromBudget(int sender)
        {
            float now = Time.unscaledTime;
            PunchBudget budget;
            float punches = Budgets.TryGetValue(sender, out budget) ? Mathf.Min(PunchBurst, budget.Punches + (now - budget.At) / CooldownSeconds) : PunchBurst;
            bool fits = punches >= 1f;
            Budgets[sender] = new PunchBudget { Punches = fits ? punches - 1f : punches, At = now };
            return fits;
        }

        /// <summary>Whether a character is seated (a seat, the helm or a turret) or in a clone tube, as every client sees it.</summary>
        private static bool SeatedOrInTube(Player character)
        {
            try
            {
                SittingInChair seat = character.Locomotion != null ? character.Locomotion.GetAbility<SittingInChair>() : null;
                if (seat != null && seat.IsActive) return true;
                var tubes = SpawnSystem.Instance != null ? GameMember.Read<List<CloneTube>>(CloneTubesField, SpawnSystem.Instance, null) : null;
                if (tubes == null) return false;
                foreach (CloneTube tube in tubes)
                {
                    if (tube != null && tube.GetOccupant() == character) return true;
                }
            }
            catch (Exception e)
            {
                Log.Failure("Checking whether a puncher is seated", e);
            }
            return false;
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

        /// <summary>Self-test: raise or lower the local player's fists as if the stance were held.</summary>
        public static void ForceStanceForTest(bool up)
        {
            _testHold = up;
        }

        /// <summary>Self-test: throw a punch as if left mouse had been clicked.</summary>
        public static void PunchForTest()
        {
            if (_stanceUp) Punch();
        }
    }
}
