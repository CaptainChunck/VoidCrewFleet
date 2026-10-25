using System;
using System.Collections.Generic;
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
    ///   enemy player in range, in the turret's arc and in sight becomes the target. The host deals the hit when the burst
    ///   arrives, if the player is still out in space and in sight. (Which enemy players float near a ship, with their
    ///   health, is worked out a few times a second, and each turret's line of sight to them ten times a second, not at
    ///   every physics step.)
    /// - Auto-turrets (run by the host): when there's no ship to shoot, an enemy player becomes the turret's target. The
    ///   turret aims and fires as it does at a ship. The game's bullets pass through people, so each time the turret fires
    ///   while on target (once per fire cycle, however many barrels fire together) it may hit the player instead, as the
    ///   game's EVA hunters roll for their hits: likely up close, much less so far away.
    /// - The damage goes to the hit player's own game, like a punch.
    /// Auto-turrets against the enemy SHIP: the game's turret logic was made for NPC ships and misbehaved against a player
    /// ship, so in PvP the host runs that step itself:
    /// - aim at the hull's centre (not the ship's root, at the bottom rear of a Frigate) with a straight-line lead from
    ///   the ship's real velocity and turn rate (player ships strafe and drift, the game assumes they fly nose first);
    /// - fire only while on target and the lead point is inside the turret's arc, and let go of the trigger otherwise (the
    ///   game kept the trigger down once it had fired, so a turret clamped at the edge of its arc sprayed into space);
    /// - the line of fire is tested with the layers bullets actually hit, ignoring the shields of the turret's own ship and
    ///   its team's ships (player shots pass through those), and the target ship's own modules and shields count as hits
    ///   (the game took them for cover and dropped the target);
    /// - a visible enemy ship always wins over an enemy player in space.
    /// </summary>
    internal static class PvpTurrets
    {
        public const string Feature = "PvP turrets shoot enemy players";

        private const float PlayerRange = 250f;
        private const float PointDefenseHit = 0.25f;

        // An auto-turret's shots at a player (a full-health player has 1 health). Each fire cycle while it is on target (a
        // twin or quad gun fires its barrels together, and they roll once) hits with a chance falling from HitChanceClose
        // (within HitChanceCloseRange) to HitChanceFar (at PlayerRange), as the game's EVA hunters roll for their hits. A
        // hit takes AutoTurretDamagePerSecond divided by the gun's fire cycles a second, so a turret that hit with every
        // cycle would take that much a second. Up close one turret takes about 0.17 a second (a boarder lasts ~6 s, ~3 s
        // against two; it was 2.5 s and ~1 s), at 250 m about 0.05.
        private const float AutoTurretDamagePerSecond = 0.25f;
        private const float HitChanceClose = 0.7f;
        private const float HitChanceCloseRange = 25f;
        private const float HitChanceFar = 0.2f;

        /// <summary>The gun's fire cycles a second are read within these bounds (so one hit never takes more than 0.25).</summary>
        private const float MinFireRate = 1f;
        private const float MaxFireRate = 30f;

        /// <summary>A shot counts at the player while the turret was on target this recently (its last physics step).</summary>
        private const float OnTargetSeconds = 0.1f;

        /// <summary>How often each ship's list of enemy players floating near it is worked out (point defenses ask at every physics step).</summary>
        private const float EnemyScanSeconds = 0.25f;

        /// <summary>How long a turret's line of sight to a player is taken as tested (point defenses ask at every physics step).</summary>
        private const float SightSeconds = 0.1f;

        /// <summary>The most entries the per-collider and per-sight caches hold before they start again.</summary>
        private const int MaxCached = 4096;

        private const float OnTargetDelta = 2.5f;
        private const float SendSeconds = 0.25f;
        private const int SightMask = 134222848; // the auto-turret's own occlusion test: ship hulls and objects

        // The auto-turret controller's own private steps, called directly (no reflection call at every physics step). A step the
        // game no longer has is logged at start-up (PvpCombat.Bind): without IsAThreat, auto-turrets only engage locked targets.
        private static readonly Action<AutoShootingTurretController, WeaponTarget> SetActiveTargetCall =
            PvpCombat.Bind<Action<AutoShootingTurretController, WeaponTarget>>(GameMember.Method(typeof(AutoShootingTurretController), "SetActiveTarget"));

        private static readonly Action<AutoShootingTurretController> CheckMagazineCall =
            PvpCombat.Bind<Action<AutoShootingTurretController>>(GameMember.Method(typeof(AutoShootingTurretController), "CheckMagazine"));

        private static readonly Func<AutoShootingTurretController, OrbitObject, bool> IsAThreatCall =
            PvpCombat.Bind<Func<AutoShootingTurretController, OrbitObject, bool>>(GameMember.Method(typeof(AutoShootingTurretController), "IsAThreat"));

        private static readonly Action<AutoShootingTurretController> UpdateActiveElementsCall =
            PvpCombat.Bind<Action<AutoShootingTurretController>>(GameMember.Method(typeof(AutoShootingTurretController), "UpdateActiveElements"));

        /// <summary>A point defense's current target (the game's private field), read directly; null when the game has changed.</summary>
        private static readonly HarmonyLib.AccessTools.FieldRef<KineticPointDefenseModule, IDamageReceiver> PointDefenseTarget =
            PvpCombat.FieldRef<KineticPointDefenseModule, IDamageReceiver>(GameMember.Field(typeof(KineticPointDefenseModule), "_targetProjectile"));

        /// <summary>What the game's bullets hit (static and space-object layers; not the interior and pivot layer 27).</summary>
        private const int ShotMask = (1 << 10) | (1 << 12);
        private static readonly RaycastHit[] SightHits = new RaycastHit[16];
        private static readonly Dictionary<int, Vector3> HullCentres = new Dictionary<int, Vector3>();

        /// <summary>
        /// Colliders the auto-turrets' lines of fire have met, by instance ID: the player ship whose shield the collider is
        /// part of, or null for anything else (an NPC's shield included), so a ray hit needs no walk up the hierarchy.
        /// </summary>
        private static readonly Dictionary<int, PlayerControlledShip> ShieldShips = new Dictionary<int, PlayerControlledShip>();

        /// <summary>A turret's latest line-of-sight test to a player (by pivot and player: see SightSeconds).</summary>
        private struct Sight
        {
            public bool Clear;
            public float At;
        }

        private static readonly Dictionary<long, Sight> Sights = new Dictionary<long, Sight>();

        /// <summary>The point defense each enemy player was last given to as its target (by the instance ID of the player's health).</summary>
        private static readonly Dictionary<int, KineticPointDefenseModule> PointDefenseOn = new Dictionary<int, KineticPointDefenseModule>();

        /// <summary>Host: an auto-turret on target at an enemy player (kept by the gun's instance ID).</summary>
        private struct PlayerAim
        {
            public int Actor;
            public Vector3 Muzzle;
            public float Distance;
            public float LastOnTarget;
        }

        private static readonly Dictionary<int, PlayerAim> OnPlayer = new Dictionary<int, PlayerAim>();

        /// <summary>Host: when each gun last rolled for a hit on a player (by its instance ID), for one roll per fire cycle.</summary>
        private static readonly Dictionary<int, float> LastRoll = new Dictionary<int, float>();

        private static readonly Dictionary<int, float> PendingDamage = new Dictionary<int, float>();
        private static readonly Dictionary<int, Vector3> PendingFrom = new Dictionary<int, Vector3>();
        private static float _nextSend;
        private static float _lastWarning = -100f;

        /// <summary>A ship's enemy players in open space, each with their health (what turrets aim at and hit), as last worked out.</summary>
        private sealed class EnemyScan
        {
            public float At = float.NegativeInfinity;
            public readonly List<Player> Players = new List<Player>();
            public readonly List<CustomCharacterHealth> Healths = new List<CustomCharacterHealth>();
        }

        private static readonly Dictionary<int, EnemyScan> Scans = new Dictionary<int, EnemyScan>();
        private static readonly EnemyScan NoEnemies = new EnemyScan();

        // ---------------------------------------------------------------- who can be shot

        /// <summary>
        /// Enemy players of this ship who are alive and floating in open space, with their health, worked out at most every
        /// 0.25 s (so check each one is still there and alive before using them).
        /// </summary>
        private static EnemyScan EnemiesInSpace(PlayerControlledShip ship)
        {
            if (ship == null) return NoEnemies;
            EnemyScan scan;
            int id = ship.GetInstanceID();
            if (!Scans.TryGetValue(id, out scan)) Scans[id] = scan = new EnemyScan();
            if (Time.time - scan.At < EnemyScanSeconds) return scan;
            scan.At = Time.time;
            scan.Players.Clear();
            scan.Healths.Clear();
            ClientGame game = ClientGame.Current;
            if (game == null || game.Players == null) return scan;
            foreach (Player player in game.Players)
            {
                if (player == null || PlayerLife.IsDead(player) || !player.IsInSpace) continue;
                int actor = PvpCombat.ActorOf(player);
                if (actor < 0 || !PvpBoarding.IsEnemyOf(actor, ship)) continue;
                if (FleetShips.InteriorAt(player.transform.position) != null) continue;
                CustomCharacterHealth health = HealthOf(player);
                if (health == null) continue;
                scan.Players.Add(player);
                scan.Healths.Add(health);
            }
            return scan;
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

        /// <summary>InSight from a turret pivot, tested again only once the last test for that pivot and player is SightSeconds old.</summary>
        private static bool InSightCached(WeaponPivot pivot, Vector3 from, Vector3 to, Player player)
        {
            long key = ((long)pivot.GetInstanceID() << 32) | (uint)player.GetInstanceID();
            float now = Time.time;
            Sight sight;
            if (Sights.TryGetValue(key, out sight) && now - sight.At < SightSeconds) return sight.Clear;
            bool clear = InSight(from, to, player);
            if (Sights.Count >= MaxCached) Sights.Clear();
            Sights[key] = new Sight { Clear = clear, At = now };
            return clear;
        }

        // ---------------------------------------------------------------- point defenses

        /// <summary>
        /// No missile to shoot: the nearest enemy player in range, in the arc and in sight. A player the game's list says
        /// another point defense is on is left to it, unless that one has let go of them (PointDefenseStillOn).
        /// </summary>
        [GamePatch(Feature, "CG.Ship.Modules.Weapons.ProjectileUtils", "GetValidPointDefenseTargets", PatchKind.Postfix)]
        private static void PointDefenseTargetsPostfix(WeaponPivot pivot, float trackingRange, List<IDamageReceiver> excludeList, ref IDamageReceiver __result)
        {
            if (__result != null || pivot == null || !PvpCombat.Active) return;
            try
            {
                EnemyScan enemies = EnemiesInSpace(FleetShips.OwnerOf(pivot));
                if (enemies.Players.Count == 0) return;
                Vector3 origin = pivot.transform.position;
                float range = Mathf.Min(trackingRange, PlayerRange);
                CustomCharacterHealth best = null;
                float bestDistance = float.MaxValue;
                for (int i = 0; i < enemies.Players.Count; i++)
                {
                    Player player = enemies.Players[i];
                    CustomCharacterHealth health = enemies.Healths[i];
                    if (player == null || health == null || PlayerLife.IsDead(player)) continue;
                    if (excludeList != null && excludeList.Contains(health))
                    {
                        if (PointDefenseStillOn(health)) continue;
                        // Left on the list by a point defense that let go of them without taking them off it.
                        excludeList.Remove(health);
                    }
                    Vector3 aim = health.CentralPosition;
                    float distance = Vector3.Distance(aim, origin);
                    if (distance > range || distance >= bestDistance) continue;
                    if (!pivot.IsOrbitObjectInFiringArc(aim) || !InSightCached(pivot, origin, aim, player)) continue;
                    best = health;
                    bestDistance = distance;
                }
                if (best == null) return;
                __result = best;
                // The point defense asking takes the player as its target and puts them on the list.
                PointDefenseOn[best.GetInstanceID()] = pivot.GetComponentInParent<KineticPointDefenseModule>();
            }
            catch (Exception e)
            {
                Log.Failure("A point defense looking for enemy players", e);
            }
        }

        /// <summary>
        /// Whether a point defense really is on this player: the one they were last given to is still tracking or firing at
        /// them. The game keeps one list of what every point defense is on (each skips what another tracks) and takes a
        /// target off it only when that point defense loses it. One switched off while on a player (power, a breaker,
        /// sabotage) left them on the list for good, and as a character is reused on respawn, no point defense shot that
        /// player again all match.
        /// </summary>
        private static bool PointDefenseStillOn(CustomCharacterHealth health)
        {
            KineticPointDefenseModule module;
            if (!PointDefenseOn.TryGetValue(health.GetInstanceID(), out module) || module == null) return false;
            KineticPointDefenseModule.LogicState state = module.State;
            if (state != KineticPointDefenseModule.LogicState.TargetAcquired && state != KineticPointDefenseModule.LogicState.Cooldown) return false;
            return PointDefenseTarget == null || ReferenceEquals(PointDefenseTarget(module), health);
        }

        /// <summary>
        /// Host: a point defense burst reached a player. A hit, not the game's "destroy the missile", if the player is still
        /// out in space (not just inside an airlock or the ship) and in the point defense's sight: the burst arrives a moment
        /// after it was fired, and a point defense on target doesn't check its sight again before it fires.
        /// </summary>
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
                    int actor = PvpCombat.ActorOf(victim);
                    if (victim != null && !PlayerLife.IsDead(victim) && actor >= 0 && PvpBoarding.IsEnemyOf(actor, FleetShips.OwnerOf(source)) &&
                        victim.IsInSpace && FleetShips.InteriorAt(victim.transform.position) == null && InSight(PointDefenseOrigin(source), health.CentralPosition, victim))
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

        /// <summary>Where a point defense fires from: its turret pivot, else the module.</summary>
        private static Vector3 PointDefenseOrigin(OrbitObject source)
        {
            var module = source as KineticPointDefenseModule;
            WeaponPivot pivot = module != null ? module.Pivot : null;
            return pivot != null ? pivot.transform.position : source.transform.position;
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
                EnemyScan enemies = EnemiesInSpace(FleetShips.OwnerOf(___turret));
                if (enemies.Players.Count == 0) return;
                Vector3 muzzle = Muzzle(___activeBarrel);
                float range = Mathf.Min(___turret.Range != null ? ___turret.Range.Value : PlayerRange, PlayerRange);
                for (int i = 0; i < enemies.Players.Count; i++)
                {
                    Player player = enemies.Players[i];
                    CustomCharacterHealth health = enemies.Healths[i];
                    if (player == null || health == null || PlayerLife.IsDead(player)) continue;
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
        /// step would drop a target that isn't a ship); while it is on target, its shots may hit the player (BarrelShootPostfix).
        /// Its sight of the player is tested ten times a second (InSightCached).
        /// </summary>
        [GamePatch(Feature, "AutoShootingTurretController", "FixedUpdate", PatchKind.Prefix)]
        private static bool AutoTurretStepPrefix(AutoShootingTurretController __instance, CompositeWeaponModule ___turret, WeaponTarget ___activeTarget,
                                                 WeaponTarget ___noTarget, ref WeaponPivot ___activePivot, ref WeaponBarrelProjectile ___activeBarrel,
                                                 BulletMagazine ___magazine, float ___trackingSpeed, TargetLockComponent ___targetLockComponent, ref bool ___reloading)
        {
            // On target at a player only until a step says otherwise.
            if (OnPlayer.Count > 0 && (object)___turret != null) OnPlayer.Remove(___turret.GetInstanceID());
            var enemyShip = ___activeTarget.TargetedUnit as PlayerControlledShip;
            if (enemyShip != null && PvpCombat.Active)
                return ShipStep(__instance, ___turret, enemyShip, ___noTarget, ref ___activePivot, ref ___activeBarrel, ___trackingSpeed, ___targetLockComponent, ref ___reloading);
            var victim = ___activeTarget.TargetedUnit as Player;
            if (victim == null) return true;
            try
            {
                if (___turret == null || ___turret.photonView == null || !___turret.photonView.AmController || !___turret.IsPowered || !PvpCombat.Active) return true;
                if (CheckMagazineCall != null) CheckMagazineCall(__instance);

                var health = ___activeTarget.ShootingElement as CustomCharacterHealth;
                PlayerControlledShip ship = FleetShips.OwnerOf(___turret);
                int actor = PvpCombat.ActorOf(victim);
                Vector3 muzzle = Muzzle(___activeBarrel);
                Vector3 aim = health != null ? health.CentralPosition : victim.transform.position;
                bool valid = health != null && ___activePivot != null && ___activeBarrel != null && !PlayerLife.IsDead(victim) && victim.IsInSpace &&
                             actor >= 0 && PvpBoarding.IsEnemyOf(actor, ship) && FleetShips.InteriorAt(victim.transform.position) == null &&
                             Vector3.Distance(aim, muzzle) <= PlayerRange * 1.2f && ___activePivot.IsOrbitObjectInFiringArc(aim) &&
                             InSightCached(___activePivot, muzzle, aim, victim);
                if (!valid)
                {
                    if (SetActiveTargetCall != null) SetActiveTargetCall(__instance, ___noTarget);
                    ___turret.SendInput(WeaponActionType.Primary, false);
                    return false;
                }

                Vector2 delta = ___activePivot.GetPerspectiveDeltaToTarget(aim);
                float step = Time.fixedDeltaTime * ___trackingSpeed;
                ___turret.SendInput(WeaponActionType.AimMove, delta.normalized * Mathf.Min(step, delta.magnitude));
                bool canFire = ___magazine == null || (!___magazine.IsReloading && ___magazine.AvailableShotsLeft >= 1);
                if (delta.magnitude < OnTargetDelta && canFire)
                {
                    OnPlayer[___turret.GetInstanceID()] = new PlayerAim { Actor = actor, Muzzle = muzzle, Distance = Vector3.Distance(aim, muzzle), LastOnTarget = Time.time };
                    ___turret.SendInput(WeaponActionType.Primary, true);
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

        /// <summary>
        /// Host: a gun's barrel fires. From an auto-turret on target at an enemy player, the gun's fire cycle may hit them,
        /// the closer the likelier: one roll per cycle, as a twin or quad gun fires several barrels in the same cycle.
        /// </summary>
        [GamePatch(Feature, "Gameplay.CompositeWeapons.WeaponBarrelBase", "Shoot", PatchKind.Postfix)]
        private static void BarrelShootPostfix(WeaponBarrelBase __instance, int __result)
        {
            if (OnPlayer.Count == 0 || __result <= 0) return;
            try
            {
                CompositeWeaponModule gun = __instance.WeaponBase;
                PlayerAim aim;
                if ((object)gun == null) return;
                int id = gun.GetInstanceID();
                if (!OnPlayer.TryGetValue(id, out aim)) return;
                if (Time.time - aim.LastOnTarget > OnTargetSeconds || !PvpCombat.Active || !PhotonNetwork.IsMasterClient) return;
                float rate = gun.FireRate != null ? gun.FireRate.Value : MinFireRate;
                if (!(rate >= MinFireRate)) rate = MinFireRate;
                if (rate > MaxFireRate) rate = MaxFireRate;
                // The barrels of one cycle fire in the same physics step; the next cycle comes a whole interval later.
                float last;
                if (LastRoll.TryGetValue(id, out last) && Time.time - last < 0.5f / rate) return;
                LastRoll[id] = Time.time;
                float chance = Mathf.Lerp(HitChanceClose, HitChanceFar, Mathf.InverseLerp(HitChanceCloseRange, PlayerRange, aim.Distance));
                if (UnityEngine.Random.value >= chance) return;
                AddDamage(aim.Actor, AutoTurretDamagePerSecond / rate, aim.Muzzle);
            }
            catch (Exception e)
            {
                Log.Failure("An auto-turret's shot at a player", e);
            }
        }

        /// <summary>Host, an auto-turret's physics step while its target is the enemy player ship.</summary>
        private static bool ShipStep(AutoShootingTurretController controller, CompositeWeaponModule turret, PlayerControlledShip ship, WeaponTarget noTarget,
                                     ref WeaponPivot pivot, ref WeaponBarrelProjectile barrel, float trackingSpeed, TargetLockComponent locks, ref bool reloading)
        {
            try
            {
                if (turret == null || turret.photonView == null || !turret.photonView.AmController || !turret.IsPowered) return true;
                if (CheckMagazineCall != null) CheckMagazineCall(controller);
                PlayerControlledShip own = FleetShips.OwnerOf(turret);
                bool threat = IsAThreatCall != null && IsAThreatCall(controller, ship);
                bool locked = locks != null && locks.IsTargetLocked(ship);
                bool valid = ship.Valid() && !ship.StartedDeath && own != null && ship.Sector == own.Sector && !ship.IsInvulnerable && (threat || locked);
                if (valid && !ShipVisible(turret, own, ship, pivot, barrel))
                {
                    // The other pivot may see it.
                    turret.TogglePivot();
                    if (UpdateActiveElementsCall != null) UpdateActiveElementsCall(controller);
                    if (!ShipVisible(turret, own, ship, pivot, barrel))
                    {
                        turret.TogglePivot();
                        if (UpdateActiveElementsCall != null) UpdateActiveElementsCall(controller);
                        valid = false;
                    }
                }
                if (!valid || pivot == null || barrel == null)
                {
                    if (SetActiveTargetCall != null) SetActiveTargetCall(controller, noTarget);
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
                // Shields of our own ship and our team's ships: player bullets fly through them.
                if (own != null && IsTeamShield(own, collider)) continue;
                nearest = SightHits[i].distance;
                first = collider;
            }
            return first == null || FleetShips.OwnerOf(first) == target;
        }

        /// <summary>Whether a collider belongs to a shield of this ship or of a ship of its team (not an enemy's, and not an NPC's).</summary>
        private static bool IsTeamShield(PlayerControlledShip own, Collider collider)
        {
            PlayerControlledShip ship = ShieldShip(collider);
            return ship != null && !PvpCombat.Enemies(own, ship);
        }

        /// <summary>
        /// The player ship whose shield a collider is part of, or null for anything else, worked out once per collider. A
        /// shield not (yet) on a player ship is not remembered, so it is looked at again once it is attached.
        /// </summary>
        private static PlayerControlledShip ShieldShip(Collider collider)
        {
            int id = collider.GetInstanceID();
            PlayerControlledShip ship;
            if (ShieldShips.TryGetValue(id, out ship)) return ship;
            bool shield = collider.GetComponentInParent<ShieldDamagable>() != null;
            ship = shield ? FleetShips.OwnerOf(collider) : null;
            if (shield && ship == null) return null;
            if (ShieldShips.Count >= MaxCached) ShieldShips.Clear();
            ShieldShips[id] = ship;
            return ship;
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

        /// <summary>
        /// Every frame: outside a match the turrets' targets, scans and caches are forgotten (the ships they hold go with
        /// the mission); the host sends the damage gathered for each player a few times a second.
        /// </summary>
        public static void Frame()
        {
            if (!PvpCombat.Active)
            {
                if (OnPlayer.Count > 0) OnPlayer.Clear();
                if (LastRoll.Count > 0) LastRoll.Clear();
                if (Scans.Count > 0) Scans.Clear();
                if (Sights.Count > 0) Sights.Clear();
                if (PointDefenseOn.Count > 0) PointDefenseOn.Clear();
                if (ShieldShips.Count > 0) ShieldShips.Clear();
                if (HullCentres.Count > 0) HullCentres.Clear();
            }
            if (PendingDamage.Count == 0 || Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + SendSeconds;
            if (!PvpCombat.Active || !PhotonNetwork.IsMasterClient)
            {
                PendingDamage.Clear();
                PendingFrom.Clear();
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
                if (me == null || PlayerLife.IsDead(me) || (me.InteractionState.ActiveModes & InteractionMode.InCloneTube) != 0) return;
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
