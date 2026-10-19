using System;
using System.Collections.Generic;
using System.Reflection;
using CG;
using CG.Client.Player.Interactions;
using CG.Game;
using CG.Game.Player;
using CG.Ship.Modules;
using CG.Ship.Shield;
using CG.Ship.TargetLocking;
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
    /// Auto-turrets against the enemy SHIP: the game's turret logic was made for NPC ships and misbehaved against a player
    /// ship, so in PvP the host runs that step itself:
    /// - aim at the hull's centre (not the ship's root, at the bottom rear of a Frigate) with a straight-line lead from
    ///   the ship's real velocity and turn rate (player ships strafe and drift, the game assumes they fly nose first);
    /// - fire only while on target and the lead point is inside the turret's arc, and let go of the trigger otherwise (the
    ///   game kept the trigger down once it had fired, so a turret clamped at the edge of its arc sprayed into space);
    /// - the line of fire is tested with the layers bullets actually hit, ignoring the turret's own ship's shields, and
    ///   the target ship's own modules and shields count as hits (the game took them for cover and dropped the target);
    /// - a visible enemy ship always wins over an enemy player in space.
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
        private static readonly MethodInfo IsAThreatMethod = GameMember.Method(typeof(AutoShootingTurretController), "IsAThreat");
        private static readonly MethodInfo UpdateActiveElementsMethod = GameMember.Method(typeof(AutoShootingTurretController), "UpdateActiveElements");

        /// <summary>What the game's bullets hit (static and space-object layers; not the interior and pivot layer 27).</summary>
        private const int ShotMask = (1 << 10) | (1 << 12);
        private static readonly RaycastHit[] SightHits = new RaycastHit[16];
        private static readonly Dictionary<int, Vector3> HullCentres = new Dictionary<int, Vector3>();

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
                                                     WeaponBarrelProjectile ___activeBarrel, WeaponTarget ___activeTarget, ref int ___currentTargetPriority)
        {
            if (!PvpCombat.Active || ___potentialTargets == null) return;
            // A ship (or anything else) is in sight while the turret is on a player: the ship wins.
            if (___potentialTargets.Count > 0)
            {
                if (___activeTarget.TargetedUnit is Player) ___currentTargetPriority = -1;
                return;
            }
            if (___turret == null || ___activePivot == null || ___activeBarrel == null) return;
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
                                                 WeaponTarget ___noTarget, ref WeaponPivot ___activePivot, ref WeaponBarrelProjectile ___activeBarrel,
                                                 BulletMagazine ___magazine, float ___trackingSpeed, TargetLockComponent ___targetLockComponent, ref bool ___reloading)
        {
            var enemyShip = ___activeTarget.TargetedUnit as PlayerControlledShip;
            if (enemyShip != null && PvpCombat.Active)
                return ShipStep(__instance, ___turret, enemyShip, ___noTarget, ref ___activePivot, ref ___activeBarrel, ___trackingSpeed, ___targetLockComponent, ref ___reloading);
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
                else if (delta.magnitude > OnTargetDelta * 2f || !canFire) ___turret.SendInput(WeaponActionType.Primary, false);
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("An auto-turret shooting at a player", e);
                return true;
            }
        }

        /// <summary>Host, an auto-turret's physics step while its target is the enemy player ship.</summary>
        private static bool ShipStep(AutoShootingTurretController controller, CompositeWeaponModule turret, PlayerControlledShip ship, WeaponTarget noTarget,
                                     ref WeaponPivot pivot, ref WeaponBarrelProjectile barrel, float trackingSpeed, TargetLockComponent locks, ref bool reloading)
        {
            try
            {
                if (turret == null || turret.photonView == null || !turret.photonView.AmController || !turret.IsPowered) return true;
                if (CheckMagazineMethod != null) CheckMagazineMethod.Invoke(controller, null);
                PlayerControlledShip own = FleetShips.OwnerOf(turret);
                bool threat = IsAThreatMethod != null && (bool)IsAThreatMethod.Invoke(controller, new object[] { ship });
                bool locked = locks != null && locks.IsTargetLocked(ship);
                bool valid = ship.Valid() && !ship.StartedDeath && own != null && ship.Sector == own.Sector && !ship.IsInvulnerable && (threat || locked);
                if (valid && !ShipVisible(turret, own, ship, pivot, barrel))
                {
                    // The other pivot may see it.
                    turret.TogglePivot();
                    if (UpdateActiveElementsMethod != null) UpdateActiveElementsMethod.Invoke(controller, null);
                    if (!ShipVisible(turret, own, ship, pivot, barrel))
                    {
                        turret.TogglePivot();
                        if (UpdateActiveElementsMethod != null) UpdateActiveElementsMethod.Invoke(controller, null);
                        valid = false;
                    }
                }
                if (!valid || pivot == null || barrel == null)
                {
                    if (SetActiveTargetMethod != null) SetActiveTargetMethod.Invoke(controller, new object[] { noTarget });
                    turret.SendInput(WeaponActionType.Primary, false);
                    return false;
                }
                Vector3 muzzle = Muzzle(barrel);
                Vector3 aim = LeadPoint(ship, muzzle, turret.ProjectileSpeed != null ? turret.ProjectileSpeed.Value : 1000f);
                Vector2 delta = pivot.GetPerspectiveDeltaToTarget(aim);
                turret.SendInput(WeaponActionType.AimMove, delta.normalized * Mathf.Min(Time.fixedDeltaTime * trackingSpeed, delta.magnitude));
                bool leadInArc = pivot.IsOrbitObjectInFiringArc(aim);
                if (delta.magnitude < 2f && leadInArc && !reloading) turret.SendInput(WeaponActionType.Primary, true);
                else if (delta.magnitude > 4f || !leadInArc || reloading) turret.SendInput(WeaponActionType.Primary, false);
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("An auto-turret shooting at the enemy ship", e);
                return true;
            }
        }

        private static bool ShipVisible(CompositeWeaponModule turret, PlayerControlledShip own, PlayerControlledShip ship, WeaponPivot pivot, WeaponBarrelProjectile barrel)
        {
            if (pivot == null || barrel == null) return false;
            Vector3 muzzle = Muzzle(barrel);
            Vector3 centre = HullCentre(ship);
            float range = turret.Range != null ? turret.Range.Value : 1000f;
            return Vector3.Distance(centre, muzzle) <= range && pivot.IsOrbitObjectInFiringArc(centre) && ShipInSight(own, muzzle, centre, ship);
        }

        /// <summary>
        /// PvP line of fire for auto-turrets: players by the mod's own test, the enemy ship by what bullets hit (its own modules
        /// and shields count as hits), and a ship's inside parts never (they're behind the hull: the turret falls back to the
        /// ship itself, as the game does).
        /// </summary>
        [GamePatch(Feature, "AutoShootingTurretController", "IsOccluded", PatchKind.Prefix)]
        private static bool IsOccludedPrefix(WeaponTarget target, CompositeWeaponModule ___turret, WeaponBarrelProjectile ___activeBarrel, ref bool __result)
        {
            if (!PvpCombat.Active || ___turret == null || ___activeBarrel == null) return true;
            try
            {
                var victim = target.TargetedUnit as Player;
                if (victim != null)
                {
                    var health = target.ShootingElement as CustomCharacterHealth;
                    __result = health == null || !InSight(Muzzle(___activeBarrel), health.CentralPosition, victim);
                    return false;
                }
                var ship = target.TargetedUnit as PlayerControlledShip;
                if (ship == null) return true;
                if (!ReferenceEquals(target.ShootingElement, ship))
                {
                    __result = true;
                    return false;
                }
                __result = !ShipInSight(FleetShips.OwnerOf(___turret), Muzzle(___activeBarrel), HullCentre(ship), ship);
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("An auto-turret checking its line of fire", e);
                return true;
            }
        }

        private static bool ShipInSight(PlayerControlledShip own, Vector3 from, Vector3 to, PlayerControlledShip target)
        {
            Vector3 line = to - from;
            float length = line.magnitude;
            if (length < 1f) return true;
            int count = Physics.RaycastNonAlloc(from, line / length, SightHits, length + 2f, ShotMask, QueryTriggerInteraction.Ignore);
            Collider first = null;
            float nearest = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                Collider collider = SightHits[i].collider;
                if (collider == null || SightHits[i].distance >= nearest) continue;
                // Our own shields: bullets fly out through them.
                if (own != null && collider.GetComponentInParent<ShieldDamagable>() != null && FleetShips.OwnerOf(collider) == own) continue;
                nearest = SightHits[i].distance;
                first = collider;
            }
            return first == null || FleetShips.OwnerOf(first) == target;
        }

        /// <summary>For the self-test: the hull centre the turrets aim at, and how far its shield segments sit from it.</summary>
        public static string DescribeShip(PlayerControlledShip ship)
        {
            if (ship == null) return "no ship";
            float worst = 0f;
            int segments = 0;
            foreach (ShieldDamagable shield in ship.GetComponentsInChildren<ShieldDamagable>(true))
            {
                segments++;
                worst = Mathf.Max(worst, shield.transform.localPosition.magnitude);
            }
            return "hull centre " + HullCentreLocal(ship).ToString("F1") + ", " + segments + " shield segments, furthest " + worst.ToString("0.0") + " m off the ship";
        }

        /// <summary>The middle of a ship's hull, in world space.</summary>
        private static Vector3 HullCentre(PlayerControlledShip ship)
        {
            return ship.transform.TransformPoint(HullCentreLocal(ship));
        }

        /// <summary>The middle of the ship's own hull colliders (not its modules or shields), in its own space; cached.</summary>
        private static Vector3 HullCentreLocal(PlayerControlledShip ship)
        {
            int id = ship.GetInstanceID();
            Vector3 centre;
            if (HullCentres.TryGetValue(id, out centre)) return centre;
            Transform root = ship.transform;
            var bounds = new Bounds();
            bool any = false;
            foreach (Collider collider in ship.GetComponentsInChildren<Collider>(false))
            {
                if (collider == null || collider.isTrigger || collider.gameObject.layer != 12) continue;
                if (!ReferenceEquals(collider.GetComponentInParent<IDamageReceiver>(), ship)) continue;
                Bounds world = collider.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = world.center + Vector3.Scale(world.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 local = root.InverseTransformPoint(corner);
                    if (!any)
                    {
                        bounds = new Bounds(local, Vector3.zero);
                        any = true;
                    }
                    else bounds.Encapsulate(local);
                }
            }
            centre = any ? bounds.center : Vector3.zero;
            HullCentres[id] = centre;
            Log.Info("PvP: " + FleetShips.Describe(ship) + "'s hull centre is " + centre.ToString("F1") + " in its own space (auto-turrets aim there).");
            return centre;
        }

        /// <summary>Where to aim: the hull centre after the shot's flight time, moving with the ship's velocity and turning with it.</summary>
        private static Vector3 LeadPoint(PlayerControlledShip ship, Vector3 muzzle, float speed)
        {
            Transform root = ship.transform;
            Vector3 offset = root.rotation * HullCentreLocal(ship);
            Vector3 velocity = ship.Velocity;
            float yawRate = ship.AngularVelocity.y;
            speed = Mathf.Max(speed, 1f);
            Vector3 point = root.position + offset;
            for (int i = 0; i < 3; i++)
            {
                float t = Mathf.Min(Vector3.Distance(muzzle, point) / speed, 3f);
                point = root.position + velocity * t + Quaternion.AngleAxis(yawRate * t, root.up) * offset;
            }
            return point;
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
