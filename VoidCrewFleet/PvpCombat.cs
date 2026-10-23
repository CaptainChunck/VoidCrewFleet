using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CG;
using CG.Game;
using CG.Game.Player;
using CG.Objects;
using CG.Ship.Hull;
using CG.Ship.Modules;
using CG.Ship.Shield;
using CG.Ship.TargetLocking;
using CG.Space;
using CG.Space.Projectiles;
using CG.Utils;
using Gameplay.CompositeWeapons;
using Gameplay.Damage;
using Gameplay.Utilities;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Ship against ship. The game tells friend from foe by one number per object, its faction: the same faction never
    /// hurts itself, and every player-side object is faction 1. In PvP the BLUE team (its ships, their parts and its crew)
    /// keeps faction 1 and the RED team gets faction 5, a faction of its own, on every client. With that:
    /// - the ships' guns, missiles and point defence treat the other team's ships as enemies, and NPCs attack both teams; a
    ///   team's own ships can't hurt each other, as in co-op;
    /// - an enemy ship's shields stop shots (the game lets player shots through every player shield), and a shot on an
    ///   enemy turret or module hits that ship's hull, the way NPC shots on a player ship do; a piercing round stops at the
    ///   first thing of the enemy ship it hits (its shield, a part or its hull), so it hits that ship once;
    /// - a shot on an enemy shield does the same whoever fires it (the host or another gunner), with the helm's
    ///   target-lock bonus when that ship is locked;
    /// - gunners who are not the host damage the enemy hull with module defects, as the host's own shots do;
    /// - a missile belongs to the ship whose launcher fired it (the game gives it "the player ship"), also in co-op;
    /// - the host marks every BLUE ship and every RED ship hostile to each other, so auto turrets engage and enemy ships
    ///   are scanned.
    /// </summary>
    internal static class PvpCombat
    {
        public const string Feature = "PvP ship combat";

        /// <summary>The game's own player faction: the BLUE team (ships 1, 3 and 5).</summary>
        public const int BlueFaction = 1;

        /// <summary>The RED team (ships 2, 4 and 6): hostile to BLUE, to every NPC faction, and every NPC to it.</summary>
        public const int RedFaction = 5;

        private static readonly MethodInfo ImpactDamageReceiverMethod = GameMember.Method(typeof(WeaponProjectile), "ImpactDamageReceiver");
        private static readonly FieldInfo DamageReceivedField = GameMember.Field(typeof(OrbitObject), "DamageReceived");
        private static readonly FieldInfo BarrelDamageTypeField = GameMember.Field(typeof(WeaponBarrelBase), "modifiedDamageType");

        /// <summary>The enemy shield a shot is hitting right now (while the game applies the hit), with the shot's damage and gun.</summary>
        private static ShieldDamagable _shieldShot;
        private static float _shieldShotDamage;
        private static CompositeWeaponModule _shieldShotWeapon;

        /// <summary>The shot whose latest hit was on the enemy ship (its shield, a part passed to its hull, or the hull).</summary>
        private static WeaponProjectile _stoppedShot;

        /// <summary>One ship's parts are walked again this often, in turn, for anything that came aboard since.</summary>
        private const float RewalkSeconds = 5f;

        /// <summary>The faction each ship's parts were last given (by the ship's instance ID).</summary>
        private static readonly Dictionary<int, int> WalkedShips = new Dictionary<int, int>();

        private static float _nextRewalk;
        private static int _rewalkTurn;
        private static readonly HashSet<int> HostilityToldTo = new HashSet<int>();
        private static int _hostilityShips;

        /// <summary>A PvP match with its ships in the world.</summary>
        public static bool Active
        {
            get { return FleetSession.IsPvP && FleetShips.Active; }
        }

        public static int FactionOfTeam(int team)
        {
            return team == 1 ? RedFaction : BlueFaction;
        }

        public static int FactionOfSlot(int slot)
        {
            return FactionOfTeam(FleetShips.TeamOfSlot(slot));
        }

        /// <summary>The faction of the team a player is on (BLUE until they are on a team).</summary>
        public static int FactionOfActor(int actor)
        {
            return FactionOfTeam(FleetShips.TeamOfActor(actor));
        }

        /// <summary>Whether two player ships are on different teams (PvP).</summary>
        public static bool Enemies(PlayerControlledShip a, PlayerControlledShip b)
        {
            if (a == null || b == null || a == b) return false;
            int slotA = FleetShips.Slot(a);
            int slotB = FleetShips.Slot(b);
            return slotA >= 0 && slotB >= 0 && FleetShips.TeamOfSlot(slotA) != FleetShips.TeamOfSlot(slotB);
        }

        private static DamageType DefaultDamageType
        {
            get { return DataTable<DamageTypesTable>.Instance.defaultDamageType; }
        }

        // ---------------------------------------------------------------- tougher hulls

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<OrbitObject, object> Toughened =
            new System.Runtime.CompilerServices.ConditionalWeakTable<OrbitObject, object>();

        /// <summary>
        /// PvP: every ship's hull has HullMultiplier times the game's hit points (4 by default), so a fight lasts. The game
        /// sets a new object's hit points to its maximum right after this, on every client, so the bigger maximum is also
        /// where every ship starts, and the HUD's hull bar reads the same on every screen.
        /// </summary>
        [GamePatch(Feature, "GameSession", "ApplyGlobalOrbitObjectSpawnProperties", PatchKind.Postfix)]
        private static void SpawnPropertiesPostfix(OrbitObject target)
        {
            try
            {
                if (!(target is PlayerControlledShip) || !FleetSession.IsPvP) return;
                FleetPlan plan = FleetSession.Current;
                float times = plan != null ? plan.HullMultiplier : 1f;
                object done;
                if (times <= 1f || Toughened.TryGetValue(target, out done)) return;
                Toughened.Add(target, true);
                target.MaxHitPoints.AddModifier(FloatModifier.ScalarModifier(times));
                Log.Info("PvP: " + FleetShips.Describe((PlayerControlledShip)target) + "'s hull has " + times + " times the hit points (" +
                         target.MaxHitPointsValue.ToString("0") + ").");
            }
            catch (Exception e)
            {
                Log.Failure("Making a PvP ship's hull tougher", e);
            }
        }

        // ---------------------------------------------------------------- factions, on every client

        /// <summary>
        /// A few times a second: every player has their team's faction, and so does every ship part. A ship's parts are all
        /// walked when the ship is first seen (and again if its faction is wrong); a module installed later gets it as it
        /// goes in (ModuleInstalledPostfix) and a gun as it starts (WeaponStartPostfix). Every few seconds one ship, in
        /// turn, is walked again for anything else that came aboard since (items, for one).
        /// </summary>
        public static void KeepFactions()
        {
            if (!Active)
            {
                if (WalkedShips.Count > 0) WalkedShips.Clear();
                return;
            }
            IList<PlayerControlledShip> ships = FleetShips.All;
            int again = -1;
            if (Time.unscaledTime >= _nextRewalk && ships.Count > 0)
            {
                _nextRewalk = Time.unscaledTime + RewalkSeconds;
                again = (_rewalkTurn++ & int.MaxValue) % ships.Count;
            }
            for (int i = 0; i < ships.Count; i++)
            {
                PlayerControlledShip ship = ships[i];
                int slot = FleetShips.Slot(ship);
                if (slot < 0) continue;
                int faction = FactionOfSlot(slot);
                int id = ship.GetInstanceID();
                int walked;
                if (i != again && ship.faction == faction && WalkedShips.TryGetValue(id, out walked) && walked == faction) continue;
                ApplyToShip(ship, faction);
                WalkedShips[id] = faction;
            }
            ClientGame game = ClientGame.Current;
            if (game == null || game.Players == null) return;
            foreach (Player player in game.Players)
            {
                if (player == null) continue;
                int faction = FactionOfActor(player.ActorId);
                if (player.faction != faction) player.faction = faction;
            }
        }

        /// <summary>
        /// Every part of a ship: its own hierarchy, its hull's (which the game keeps apart from the ship) and any module
        /// outside both; each part once.
        /// </summary>
        private static void ApplyToShip(PlayerControlledShip ship, int faction)
        {
            Transform root = ship.transform;
            int changed = Apply(ship.GetComponentsInChildren<OrbitObject>(true), faction);
            ShipHull hull = ship.Hull;
            Transform hullRoot = hull != null ? hull.transform : null;
            if (hullRoot != null && !hullRoot.IsChildOf(root)) changed += Apply(hull.GetComponentsInChildren<OrbitObject>(true), faction);
            List<CellModule> modules = ship.GetAllModules();
            if (modules != null)
            {
                foreach (CellModule module in modules)
                {
                    if (module == null) continue;
                    Transform place = module.transform;
                    if (place.IsChildOf(root) || (hullRoot != null && place.IsChildOf(hullRoot))) continue;
                    changed += Apply(module.GetComponentsInChildren<OrbitObject>(true), faction);
                }
            }
            if (changed > 0) Log.Verbose("PvP: " + changed + " parts of " + FleetShips.Describe(ship) + " are now faction " + faction + ".");
        }

        /// <summary>A module installed in a ship's socket (one built during the match, for one) takes that ship's team faction.</summary>
        [GamePatch(Feature, "CG.Ship.Hull.BuildSocket", "SetModule", PatchKind.Postfix, Optional = true)]
        private static void ModuleInstalledPostfix(BuildSocket __instance, CellModule module)
        {
            if (module == null || !Active) return;
            try
            {
                int slot = FleetShips.Slot(FleetShips.OwnerOf(__instance));
                if (slot < 0) return;
                int faction = FactionOfSlot(slot);
                int changed = Apply(module.GetComponentsInChildren<OrbitObject>(true), faction);
                if (changed > 0) Log.Verbose("PvP: a module of " + FleetShips.Describe(FleetShips.OwnerOf(__instance)) + " is now faction " + faction + " (" + changed + " parts).");
            }
            catch (Exception e)
            {
                Log.Failure("Giving a new module its team", e);
            }
        }

        /// <summary>Player-side parts take the team's faction; neutral parts stay neutral and characters go by their team.</summary>
        private static int Apply(OrbitObject[] objects, int faction)
        {
            int changed = 0;
            foreach (OrbitObject part in objects)
            {
                if (part == null || part.faction == faction || part is Player) continue;
                if (part.faction != BlueFaction && part.faction != RedFaction) continue;
                part.faction = faction;
                changed++;
            }
            return changed;
        }

        /// <summary>The game makes every ship gun faction 1 when it starts; a RED gun is RED.</summary>
        [GamePatch(Feature, "Gameplay.CompositeWeapons.CompositeWeaponModule", "Start", PatchKind.Postfix)]
        private static void WeaponStartPostfix(CompositeWeaponModule __instance)
        {
            try
            {
                if (!Active) return;
                int slot = FleetShips.Slot(FleetShips.OwnerOf(__instance));
                if (slot >= 0) __instance.faction = FactionOfSlot(slot);
            }
            catch (Exception e)
            {
                Log.Failure("Giving a ship gun its team", e);
            }
        }

        // ---------------------------------------------------------------- hits on the enemy ship

        /// <summary>
        /// A ship gun's shot hits something of the other ship: its shield absorbs the shot (the game lets player shots pass
        /// every player shield), and a turret, module or socket passes the hit to the ship's hull. A shot on the shield of a
        /// ship the gun's helm has locked gets the target-lock bonus, as a shot on its hull does. Whatever of the enemy ship
        /// it hits, the hull included, a piercing round stops there (CanPenetratePostfix).
        /// </summary>
        [GamePatch(Feature, "Gameplay.CompositeWeapons.WeaponProjectile", "TryHit", PatchKind.Prefix)]
        private static bool TryHitPrefix(WeaponProjectile __instance, RaycastHit hit, ref IHitReceiver hitReceiver, float localWeaponDamageModifier, ref bool __result)
        {
            _stoppedShot = null;
            if (!Active) return true;
            try
            {
                var component = hitReceiver as Component;
                if (component == null || __instance.SourceWeapon == null) return true;
                PlayerControlledShip target = FleetShips.OwnerOf(component);
                if (target == null) return true;
                PlayerControlledShip shooter = FleetShips.OwnerOf(__instance.SourceWeapon);
                // A team's own ships: the game's own handling (same faction, so no damage).
                if (!Enemies(shooter, target)) return true;

                var shield = hitReceiver as ShieldDamagable;
                if (shield != null)
                {
                    __result = false;
                    if (!shield.Valid() || ImpactDamageReceiverMethod == null) return false;
                    float modifier = localWeaponDamageModifier * LockBonus(__instance.SourceWeapon, shooter, target);
                    _shieldShot = shield;
                    _shieldShotDamage = modifier * __instance.Damage;
                    _shieldShotWeapon = __instance.SourceWeapon;
                    try
                    {
                        ImpactDamageReceiverMethod.Invoke(__instance, new object[] { shield, hit.point, hit.normal, modifier });
                    }
                    finally
                    {
                        _shieldShot = null;
                        _shieldShotWeapon = null;
                    }
                    _stoppedShot = __instance;
                    __result = true;
                    return false;
                }
                if (hitReceiver is CellModule || hitReceiver is CarryableObject || hitReceiver is CarryablesSocket)
                {
                    hitReceiver = target;
                    _stoppedShot = __instance;
                }
                else if (IsHullOf(hitReceiver, target))
                {
                    // The game's own hit on the hull; a piercing round would go on and hit the hull again through the parts behind.
                    _stoppedShot = __instance;
                }
            }
            catch (Exception e)
            {
                Log.Failure("Resolving a hit on the enemy ship", e);
            }
            return true;
        }

        /// <summary>The ship itself, or a part of its hull that takes its hits for it (the game's hull pieces).</summary>
        private static bool IsHullOf(IHitReceiver receiver, PlayerControlledShip ship)
        {
            if (ReferenceEquals(receiver, ship)) return true;
            var damageReceiver = receiver as IDamageReceiver;
            return damageReceiver != null && ReferenceEquals(damageReceiver.DamageableElement, ship);
        }

        /// <summary>
        /// The helm's target-lock damage bonus, as the game's TryHit gives a shot at a locked target. The game looks up the
        /// object hit, and a shield is never locked itself: here the shield's ship counts.
        /// </summary>
        private static float LockBonus(CompositeWeaponModule weapon, PlayerControlledShip shooter, PlayerControlledShip target)
        {
            TargetLockComponent locks = weapon.TargetLockComponent;
            if (locks == null || !locks.IsTargetLocked(target)) return 1f;
            CG.Ship.Modules.Helm helm = shooter.GetModule<CG.Ship.Modules.Helm>();
            return helm != null && helm.TargetLockDamage != null ? helm.TargetLockDamage.Value : 1f;
        }

        /// <summary>
        /// A piercing round stops at its first hit on the enemy ship: in its shield, at the part whose hit went to the hull,
        /// or in the hull. (The game's CanPenetrate lets it go on through a ship's hull and its parts, so one round hit that
        /// hull again with every part behind.)
        /// </summary>
        [GamePatch(Feature, "Gameplay.CompositeWeapons.PenetratingWeaponProjectile", "CanPenetrate", PatchKind.Postfix)]
        private static void CanPenetratePostfix(PenetratingWeaponProjectile __instance, ref bool __result)
        {
            if ((object)_stoppedShot == null) return;
            if (ReferenceEquals(_stoppedShot, __instance)) __result = false;
            _stoppedShot = null;
        }

        /// <summary>
        /// A gunner who is not the host hits the enemy shield. The game would send the host the damage already multiplied
        /// against the shield's defence type, and the ship as the attacker; instead the host gets the shot's own damage and
        /// the gun that fired, and applies the hit exactly as it applies its own guns' (SendDamagePrefix).
        /// </summary>
        [GamePatch(Feature, "ProjectileDamageDistributor", "SendDamageToHost", PatchKind.Prefix)]
        private static void SendToHostPrefix(ref float damage, int targetIndex, ref int source)
        {
            if ((object)_shieldShot == null) return;
            try
            {
                if (targetIndex != _shieldShot.GetViewID()) return;
                damage = _shieldShotDamage;
                int weapon = _shieldShotWeapon != null ? _shieldShotWeapon.GetViewID() : 0;
                if (weapon != 0) source = weapon;
            }
            catch (Exception e)
            {
                Log.Failure("Sending a hit on the enemy shield to the host", e);
            }
        }

        /// <summary>
        /// The damage type a gun's shots carry: the gunner's empowered type on its barrels, else the gun's own. (Damage types
        /// are assets that live as long as the game, so plain reference checks do.)
        /// </summary>
        private static DamageType ShotDamageType(OrbitObject attacker)
        {
            var weapon = attacker as CompositeWeaponModule;
            if (weapon == null) return DefaultDamageType;
            CompositeWeaponModule.CompositeWeaponOutsideElements elements = weapon.ActiveOutsideElementCollection;
            if (BarrelDamageTypeField != null && elements != null && elements.Barrels != null)
            {
                foreach (WeaponBarrelBase barrel in elements.Barrels)
                {
                    if (barrel == null) continue;
                    DamageType empowered = GameMember.Read<DamageType>(BarrelDamageTypeField, barrel, null);
                    if ((object)empowered != null) return empowered;
                }
            }
            DamageType own = weapon.DamageType;
            return (object)own != null ? own : DefaultDamageType;
        }

        private struct EnemyHit
        {
            public PlayerControlledShip Ship;
            public float Damage;
            public OrbitObject Attacker;
        }

        /// <summary>
        /// Host: damage reported by a gunner who is not the host. A shield hit (the shot's own damage, from the gun that
        /// fired: SendToHostPrefix) is applied exactly as the host's own guns apply theirs: the shield absorbs its share, and
        /// what it can't hold goes to the hull with the gun's damage type. A hull hit also counts toward module defects, like
        /// the host's hits.
        /// </summary>
        [GamePatch(Feature, "ProjectileDamageDistributor", "SendDamageData", PatchKind.Prefix)]
        private static void SendDamagePrefix(int[] targets, float[] damage, int sourceId, out List<EnemyHit> __state)
        {
            __state = null;
            if (!Active || !PhotonNetwork.IsMasterClient || targets == null || damage == null) return;
            try
            {
                OrbitObject attacker = ObjectRegistry.FindByPhotonView<OrbitObject>(sourceId);
                PlayerControlledShip attackerShip = attacker != null ? FleetShips.OwnerOf(attacker) : null;
                if (attackerShip == null) return;
                for (int i = 0; i < targets.Length && i < damage.Length; i++)
                {
                    if (damage[i] <= 0f) continue;
                    PhotonView view = PhotonView.Find(targets[i]);
                    if (view == null) continue;
                    var shield = view.GetComponent<ShieldDamagable>();
                    if (shield != null)
                    {
                        if (!Enemies(attackerShip, FleetShips.OwnerOf(shield))) continue;
                        shield.ApplyHitDamage(damage[i], attacker, ShotDamageType(attacker), shield.transform.position, Quaternion.identity);
                        damage[i] = 0f;
                        continue;
                    }
                    var ship = view.GetComponent<PlayerControlledShip>();
                    if (ship == null || !Enemies(attackerShip, ship)) continue;
                    if (__state == null) __state = new List<EnemyHit>();
                    __state.Add(new EnemyHit { Ship = ship, Damage = damage[i], Attacker = attacker });
                }
            }
            catch (Exception e)
            {
                Log.Failure("Applying an enemy gunner's damage", e);
            }
        }

        [GamePatch(Feature, "ProjectileDamageDistributor", "SendDamageData", PatchKind.Postfix)]
        private static void SendDamagePostfix(List<EnemyHit> __state)
        {
            if (__state == null || DamageReceivedField == null) return;
            foreach (EnemyHit hit in __state)
            {
                try
                {
                    if (hit.Ship == null) continue;
                    var handler = DamageReceivedField.GetValue(hit.Ship) as Action<float, Vector3, OrbitObject, DamageType>;
                    if (handler != null) handler(hit.Damage, hit.Ship.transform.position, hit.Attacker, DefaultDamageType);
                }
                catch (Exception e)
                {
                    Log.Failure("Counting an enemy hit toward defects", e);
                }
            }
        }

        // ---------------------------------------------------------------- missiles

        /// <summary>
        /// A missile fired by a ship's launcher belongs to that ship: it doesn't hit it and carries its faction. (The game
        /// makes every player missile come from "the player ship", which with two ships is a different ship on each client.)
        /// </summary>
        [GamePatch(FleetShips.Feature, "CG.Space.Projectiles.SyncedProjectilesSynchronizer", "RegisterNpcProjectile", PatchKind.Prefix)]
        private static void RegisterProjectilePrefix(SyncedProjectile projectile)
        {
            try
            {
                var missile = projectile as Missile;
                if (missile == null || missile.Launcher == null || !FleetShips.Active) return;
                PlayerControlledShip ship = FleetShips.OwnerOf(missile.Launcher);
                if (ship == null) return;
                missile.Source = ship;
                missile.Faction = ship.Faction;
            }
            catch (Exception e)
            {
                Log.Failure("Giving a missile its ship", e);
            }
        }

        /// <summary>
        /// The targets a ship's missiles may pick: the game passes faction 1 (the players) and compares locked targets
        /// with faction 1; in PvP each ship uses its own team's faction.
        /// </summary>
        [GamePatch(Feature, "CG.Ship.Modules.Weapons.ProjectileUtils", "GetPossibleMissileTargets", PatchKind.Prefix)]
        private static bool MissileTargetsPrefix(Vector3 firePosition, float maxMissileRange, TargetLockComponent targetLockComponent, int faction,
            ref List<OrbitObject> __result)
        {
            if (!Active || targetLockComponent == null) return true;
            try
            {
                PlayerControlledShip ship = FleetShips.OwnerOf(targetLockComponent);
                if (ship == null) return true;
                int own = ship.Faction;
                var list = new List<OrbitObject>();
                if (targetLockComponent.HasAnyTargetLocks())
                {
                    foreach (TargetLock targetLock in targetLockComponent.TargetLocks)
                    {
                        if (targetLock == null || !targetLock.HasTarget || targetLock.TargetObject == null) continue;
                        if (!targetLock.TargetObject.IsObjectFromDifferentFaction(own)) continue;
                        if ((targetLock.TargetObject.WorldPosition - firePosition).magnitude > maxMissileRange) continue;
                        list.Add(targetLock.TargetObject);
                    }
                }
                else if (GameSessionManager.ActiveSector != null)
                {
                    foreach (SpaceCraft craft in GameSessionManager.ActiveSector.GetSectorObjectCategory<TargetableShipsCategory>().SectorObjects)
                    {
                        if (craft == null || !craft.IsObjectFromEnemyFaction(own)) continue;
                        if ((craft.WorldPosition - firePosition).magnitude > maxMissileRange) continue;
                        list.Add(craft);
                    }
                }
                __result = list;
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Choosing missile targets", e);
                return true;
            }
        }

        // ---------------------------------------------------------------- the ships are enemies from the start

        /// <summary>
        /// Host, a few times a second: once the teams are confirmed, every BLUE ship and every RED ship are hostile to each
        /// other, as an NPC ship is once it has been shot. Auto turrets then engage enemy ships and they count as scanned
        /// (lockable). Players who join later are told too (the game doesn't keep that message for them).
        /// </summary>
        public static void KeepHostility()
        {
            if (!Active || !PhotonNetwork.IsMasterClient || !PvpTeams.Locked) return;
            try
            {
                var ships = new List<PlayerControlledShip>(FleetShips.All);
                int signature = 17;
                foreach (PlayerControlledShip ship in ships.OrderBy(FleetShips.ViewId)) signature = signature * 31 + FleetShips.ViewId(ship);
                if (signature != _hostilityShips)
                {
                    // Players in the room now are told by SetAggressiveTowards itself; only later joiners need the message.
                    _hostilityShips = signature;
                    HostilityToldTo.Clear();
                    foreach (Photon.Realtime.Player player in PhotonNetwork.PlayerList) HostilityToldTo.Add(player.ActorNumber);
                }
                var pairs = new List<KeyValuePair<PlayerControlledShip, PlayerControlledShip>>();
                foreach (PlayerControlledShip a in ships)
                {
                    foreach (PlayerControlledShip b in ships)
                    {
                        if (!Enemies(a, b) || a.faction == b.faction || a.StartedDeath || b.StartedDeath) continue;
                        pairs.Add(new KeyValuePair<PlayerControlledShip, PlayerControlledShip>(a, b));
                        if (!a.IsHostile(b)) a.SetAggressiveTowards(b);
                    }
                }
                foreach (Photon.Realtime.Player player in PhotonNetwork.PlayerList)
                {
                    if (player.IsLocal || !HostilityToldTo.Add(player.ActorNumber)) continue;
                    foreach (KeyValuePair<PlayerControlledShip, PlayerControlledShip> pair in pairs)
                        pair.Key.photonView.RPC("NotifyAggressiveTowards", player, pair.Value.ViewID);
                }
            }
            catch (Exception e)
            {
                Log.Failure("Making the teams' ships enemies", e);
            }
        }
    }
}
