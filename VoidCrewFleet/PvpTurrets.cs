using System;
using System.Collections.Generic;
using System.Reflection;
using CG.Client.Player.Interactions;
using CG.Game;
using CG.Game.Player;
using CG.Ship.Modules;
using CG.Space;
using Gameplay.CompositeWeapons;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// PvP: a ship's point defenses and B.R.A.I.N. auto-turrets also shoot enemy players floating in open space near it.
    /// Inside a ship the turrets can't see them (punches deal with boarders there).
    /// - Point defenses pick their targets and play their bursts on every client. When there's no missile to shoot, an
    ///   enemy player in range, in the turret's arc and in sight becomes the target. The host deals the hit.
    /// - Auto-turrets (run by the host): when there's no ship to shoot, an enemy player becomes the turret's target. The
    ///   turret aims and fires as it does at a ship. The game's bullets pass through people, so while the turret is on
    ///   target the player takes damage.
    /// - The damage goes to the hit player's own game, like a punch.
    /// </summary>
    internal static class PvpTurrets
    {
        public const string Feature = "PvP turrets shoot enemy players";

        private const float PlayerRange = 250f;
        private const float PointDefenseHit = 0.25f;
        private const float AutoTurretDamagePerSecond = 0.4f;
        private const float OnTargetDelta = 2.5f;
        private const float SendSeconds = 0.25f;
        private const int SightMask = 134222848; // the auto-turret's own occlusion test: ship hulls and objects

        private static readonly MethodInfo SetActiveTargetMethod = GameMember.Method(typeof(AutoShootingTurretController), "SetActiveTarget");
        private static readonly MethodInfo CheckMagazineMethod = GameMember.Method(typeof(AutoShootingTurretController), "CheckMagazine");

        private static readonly Dictionary<int, float> PendingDamage = new Dictionary<int, float>();
        private static readonly Dictionary<int, Vector3> PendingFrom = new Dictionary<int, Vector3>();
        private static float _nextSend;
        private static float _lastWarning = -100f;

        // ---------------------------------------------------------------- who can be shot

        /// <summary>Enemy players of this ship who are alive and floating in open space.</summary>
        private static IEnumerable<Player> EnemiesInSpace(PlayerControlledShip ship)
        {
            ClientGame game = ClientGame.Current;
            if (ship == null || game == null || game.Players == null) yield break;
            foreach (Player player in game.Players)
            {
                if (player == null || player.IsDead || !player.IsInSpace) continue;
                int actor = ActorOf(player);
                if (actor < 0 || !PvpBoarding.IsEnemyOf(actor, ship)) continue;
                if (FleetShips.InteriorAt(player.transform.position) != null) continue;
                yield return player;
            }
        }

        private static int ActorOf(Player player)
        {
            return player != null && player.photonView != null && player.photonView.Owner != null ? player.photonView.Owner.ActorNumber : -1;
        }

        private static CustomCharacterHealth HealthOf(Player player)
        {
            return player != null ? player.GetComponentInChildren<CustomCharacterHealth>() : null;
        }

        /// <summary>Nothing solid between a turret and the player (the start skips the turret's own mounting).</summary>
        private static bool InSight(Vector3 from, Vector3 to, Player player)
        {
            Vector3 line = to - from;
            float length = line.magnitude;
            if (length < 2f) return true;
            Vector3 start = from + line / length * 1.5f;
            RaycastHit hit;
            if (!Physics.Linecast(start, to, out hit, SightMask, QueryTriggerInteraction.Ignore)) return true;
            return hit.collider != null && hit.collider.GetComponentInParent<Player>() == player;
        }

        // ---------------------------------------------------------------- point defenses

        /// <summary>No missile to shoot: the nearest enemy player in range, in the arc and in sight.</summary>
        [GamePatch(Feature, "CG.Ship.Modules.Weapons.ProjectileUtils", "GetValidPointDefenseTargets", PatchKind.Postfix)]
        private static void PointDefenseTargetsPostfix(WeaponPivot pivot, float trackingRange, List<IDamageReceiver> excludeList, ref IDamageReceiver __result)
        {
            if (__result != null || pivot == null || !PvpCombat.Active) return;
            try
            {
                PlayerControlledShip ship = FleetShips.OwnerOf(pivot);
                Vector3 origin = pivot.transform.position;
                float range = Mathf.Min(trackingRange, PlayerRange);
                CustomCharacterHealth best = null;
                float bestDistance = float.MaxValue;
                foreach (Player player in EnemiesInSpace(ship))
                {
                    CustomCharacterHealth health = HealthOf(player);
                    if (health == null || (excludeList != null && excludeList.Contains(health))) continue;
                    Vector3 aim = health.CentralPosition;
                    float distance = Vector3.Distance(aim, origin);
                    if (distance > range || distance >= bestDistance) continue;
                    if (!pivot.IsOrbitObjectInFiringArc(aim) || !InSight(origin, aim, player)) continue;
                    best = health;
                    bestDistance = distance;
                }
                if (best != null) __result = best;
            }
            catch (Exception e)
            {
                Log.Failure("A point defense looking for enemy players", e);
            }
        }

        /// <summary>Host: a point defense burst reached a player. A hit, not the game's "destroy the missile".</summary>
        [GamePatch(Feature, "CG.Ship.Modules.Weapons.ProjectileUtils", "DestroyProjectile", PatchKind.Prefix)]
        private static bool DestroyProjectilePrefix(IDamageReceiver target, OrbitObject source)
        {
            var health = target as CustomCharacterHealth;
            if (health == null) return true;
            try
            {
                if (PvpCombat.Active && PhotonNetwork.IsMasterClient && source != null)
                {
                    Player victim = health.GetComponentInParent<Player>();
                    int actor = ActorOf(victim);
                    if (victim != null && !victim.IsDead && actor >= 0 && PvpBoarding.IsEnemyOf(actor, FleetShips.OwnerOf(source)))
                    {
                        AddDamage(actor, PointDefenseHit, source.transform.position);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Failure("A point defense hitting a player", e);
            }
            return false;
        }

        // ---------------------------------------------------------------- auto-turrets

        /// <summary>No ship to shoot: enemy players in range, in the arc and in sight become possible targets.</summary>
        [GamePatch(Feature, "AutoShootingTurretController", "TryFindAnyPotentialTargets", PatchKind.Postfix)]
        private static void AutoTurretTargetsPostfix(List<WeaponTarget> ___potentialTargets, CompositeWeaponModule ___turret, WeaponPivot ___activePivot,
                                                     WeaponBarrelProjectile ___activeBarrel)
        {
            if (!PvpCombat.Active || ___potentialTargets == null || ___potentialTargets.Count > 0 || ___turret == null || ___activePivot == null || ___activeBarrel == null) return;
            try
            {
                PlayerControlledShip ship = FleetShips.OwnerOf(___turret);
                Vector3 muzzle = Muzzle(___activeBarrel);
                float range = Mathf.Min(___turret.Range != null ? ___turret.Range.Value : PlayerRange, PlayerRange);
                foreach (Player player in EnemiesInSpace(ship))
                {
                    CustomCharacterHealth health = HealthOf(player);
                    if (health == null) continue;
                    Vector3 aim = health.CentralPosition;
                    if (Vector3.Distance(aim, muzzle) > range || !___activePivot.IsOrbitObjectInFiringArc(aim) || !InSight(muzzle, aim, player)) continue;
                    ___potentialTargets.Add(new WeaponTarget { TargetedUnit = player, ShootingElement = health });
                }
            }
            catch (Exception e)
            {
                Log.Failure("An auto-turret looking for enemy players", e);
            }
        }

        /// <summary>
        /// Host, every physics step of an auto-turret whose target is a player: aim and fire at the player (the game's own
        /// step would drop a target that isn't a ship), and hurt the player while on target.
        /// </summary>
        [GamePatch(Feature, "AutoShootingTurretController", "FixedUpdate", PatchKind.Prefix)]
        private static bool AutoTurretStepPrefix(AutoShootingTurretController __instance, CompositeWeaponModule ___turret, WeaponTarget ___activeTarget,
                                                 WeaponTarget ___noTarget, WeaponPivot ___activePivot, WeaponBarrelProjectile ___activeBarrel,
                                                 BulletMagazine ___magazine, float ___trackingSpeed)
        {
            var victim = ___activeTarget.TargetedUnit as Player;
            if (victim == null) return true;
            try
            {
                if (___turret == null || ___turret.photonView == null || !___turret.photonView.AmController || !___turret.IsPowered || !PvpCombat.Active) return true;
                if (CheckMagazineMethod != null) CheckMagazineMethod.Invoke(__instance, null);

                var health = ___activeTarget.ShootingElement as CustomCharacterHealth;
                PlayerControlledShip ship = FleetShips.OwnerOf(___turret);
                int actor = ActorOf(victim);
                Vector3 muzzle = Muzzle(___activeBarrel);
                Vector3 aim = health != null ? health.CentralPosition : victim.transform.position;
                bool valid = health != null && ___activePivot != null && ___activeBarrel != null && !victim.IsDead && victim.IsInSpace &&
                             actor >= 0 && PvpBoarding.IsEnemyOf(actor, ship) && FleetShips.InteriorAt(victim.transform.position) == null &&
                             Vector3.Distance(aim, muzzle) <= PlayerRange * 1.2f && ___activePivot.IsOrbitObjectInFiringArc(aim) && InSight(muzzle, aim, victim);
                if (!valid)
                {
                    if (SetActiveTargetMethod != null) SetActiveTargetMethod.Invoke(__instance, new object[] { ___noTarget });
                    ___turret.SendInput(WeaponActionType.Primary, false);
                    return false;
                }

                Vector2 delta = ___activePivot.GetPerspectiveDeltaToTarget(aim);
                float step = Time.fixedDeltaTime * ___trackingSpeed;
                ___turret.SendInput(WeaponActionType.AimMove, delta.normalized * Mathf.Min(step, delta.magnitude));
                bool canFire = ___magazine == null || (!___magazine.IsReloading && ___magazine.AvailableShotsLeft >= 1);
                if (delta.magnitude < OnTargetDelta && canFire)
                {
                    ___turret.SendInput(WeaponActionType.Primary, true);
                    AddDamage(actor, AutoTurretDamagePerSecond * Time.fixedDeltaTime, muzzle);
                }
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("An auto-turret shooting at a player", e);
                return true;
            }
        }

        private static Vector3 Muzzle(WeaponBarrelProjectile barrel)
        {
            if (barrel == null) return Vector3.zero;
            return barrel.ProjectileSpawnTransform != null ? barrel.ProjectileSpawnTransform.position : barrel.transform.position;
        }

        // ---------------------------------------------------------------- damage

        private static void AddDamage(int actor, float amount, Vector3 from)
        {
            float pending;
            PendingDamage.TryGetValue(actor, out pending);
            PendingDamage[actor] = pending + amount;
            PendingFrom[actor] = from;
        }

        /// <summary>Host, every frame: sends the damage gathered for each player a few times a second.</summary>
        public static void Frame()
        {
            if (PendingDamage.Count == 0 || Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + SendSeconds;
            if (!PvpCombat.Active || !PhotonNetwork.IsMasterClient)
            {
                PendingDamage.Clear();
                return;
            }
            foreach (KeyValuePair<int, float> entry in PendingDamage)
            {
                if (entry.Value < 0.005f) continue;
                Vector3 from;
                PendingFrom.TryGetValue(entry.Key, out from);
                Net.ToActor(entry.Key, Msg.TurretHit, Mathf.Min(entry.Value, 1f), from);
            }
            PendingDamage.Clear();
            PendingFrom.Clear();
        }

        public static void Listen()
        {
            Net.On(Msg.TurretHit, (sender, data) =>
            {
                if (!PvpCombat.Active || !Net.FromHost(sender)) return;
                LocalPlayer me = LocalPlayer.Instance;
                if (me == null || me.IsDead || (me.InteractionState.ActiveModes & InteractionMode.InCloneTube) != 0) return;
                float damage = Mathf.Clamp(Net.Get(data, 0, 0f), 0f, 1f);
                if (damage <= 0f) return;
                Vector3 from = Net.Get(data, 1, me.transform.position);
                Vector3 away = me.transform.position - from;
                Quaternion facing = Quaternion.LookRotation(away.sqrMagnitude > 0.001f ? -away.normalized : me.transform.forward);
                me.ApplyHitDamage(damage, null, me.ImpactDamageType, me.transform.position + me.transform.up, facing, true);
                PvpPunch.Shake(0.35f);
                if (Time.unscaledTime - _lastWarning > 15f)
                {
                    _lastWarning = Time.unscaledTime;
                    Notice.Show("The enemy ship's turrets are shooting at you! Get inside or out of their sight.");
                }
            });
        }
    }
}
