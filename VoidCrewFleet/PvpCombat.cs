using System;
using System.Collections.Generic;
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
using Photon.Pun;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Ship against ship. The game tells friend from foe by one number per object, its faction: the same faction never
    /// hurts itself, and every player-side object is faction 1. In PvP the BLUE team (ship 1, its parts and its crew)
    /// keeps faction 1 and the RED team gets faction 5, a faction of its own, on every client. With that:
    /// - the ships' guns, missiles and point defence treat the other ship as an enemy, and NPCs attack both;
    /// - an enemy ship's shields stop shots (the game lets player shots through every player shield), and a shot on an
    ///   enemy turret or module hits that ship's hull, the way NPC shots on a player ship do;
    /// - gunners who are not the host damage the enemy hull with module defects, as the host's own shots do;
    /// - a missile belongs to the ship whose launcher fired it (the game gives it "the player ship"), also in co-op;
    /// - the host marks the ships hostile to each other, so auto turrets engage and the enemy ship is scanned.
    /// </summary>
    internal static class PvpCombat
    {
        public const string Feature = "PvP ship combat";

        /// <summary>The game's own player faction: the BLUE team (ship 1).</summary>
        public const int BlueFaction = 1;

        /// <summary>The RED team (ship 2): hostile to BLUE, to every NPC faction, and every NPC to it.</summary>
        public const int RedFaction = 5;

        private static readonly MethodInfo ImpactDamageReceiverMethod = GameMember.Method(typeof(WeaponProjectile), "ImpactDamageReceiver");
        private static readonly FieldInfo DamageReceivedField = GameMember.Field(typeof(OrbitObject), "DamageReceived");

        private static float _nextFullWalk;
        private static readonly HashSet<int> HostilityToldTo = new HashSet<int>();
        private static int _hostilityShips;

        /// <summary>A PvP match with both ships in the world.</summary>
        public static bool Active
        {
            get { return FleetSession.IsPvP && FleetShips.Active; }
        }

        public static int FactionOfSlot(int slot)
        {
            return slot == 1 ? RedFaction : BlueFaction;
        }

        /// <summary>The faction of the team a player is on (BLUE until they are on a team).</summary>
        public static int FactionOfActor(int actor)
        {
            return FactionOfSlot(FleetShips.CrewSlot(actor));
        }

        private static DamageType DefaultDamageType
        {
            get { return DataTable<DamageTypesTable>.Instance.defaultDamageType; }
        }

        // ---------------------------------------------------------------- factions, on every client

        /// <summary>A few times a second: every ship part and every player has their team's faction.</summary>
        public static void KeepFactions()
        {
            if (!Active) return;
            bool full = Time.unscaledTime >= _nextFullWalk;
            if (full) _nextFullWalk = Time.unscaledTime + 2f;
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
                int slot = FleetShips.Slot(ship);
                if (slot < 0) continue;
                int faction = FactionOfSlot(slot);
                if (full || ship.faction != faction) ApplyToShip(ship, faction);
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

        private static void ApplyToShip(PlayerControlledShip ship, int faction)
        {
            int changed = Apply(ship.GetComponentsInChildren<OrbitObject>(true), faction);
            ShipHull hull = ship.Hull;
            if (hull != null) changed += Apply(hull.GetComponentsInChildren<OrbitObject>(true), faction);
            List<CellModule> modules = ship.GetAllModules();
            if (modules != null)
            {
                foreach (CellModule module in modules)
                {
                    if (module != null) changed += Apply(module.GetComponentsInChildren<OrbitObject>(true), faction);
                }
            }
            if (changed > 0) Log.Verbose("PvP: " + changed + " parts of " + FleetShips.Describe(ship) + " are now faction " + faction + ".");
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
        /// every player shield), and a turret, module or socket passes the hit to the ship's hull.
        /// </summary>
        [GamePatch(Feature, "Gameplay.CompositeWeapons.WeaponProjectile", "TryHit", PatchKind.Prefix)]
        private static bool TryHitPrefix(WeaponProjectile __instance, RaycastHit hit, ref IHitReceiver hitReceiver, float localWeaponDamageModifier, ref bool __result)
        {
            if (!Active) return true;
            try
            {
                var component = hitReceiver as Component;
                if (component == null || __instance.SourceWeapon == null) return true;
                PlayerControlledShip target = FleetShips.OwnerOf(component);
                if (target == null) return true;
                PlayerControlledShip shooter = FleetShips.OwnerOf(__instance.SourceWeapon);
                if (shooter == null || shooter == target) return true;

                var shield = hitReceiver as ShieldDamagable;
                if (shield != null)
                {
                    __result = false;
                    if (!shield.Valid() || ImpactDamageReceiverMethod == null) return false;
                    ImpactDamageReceiverMethod.Invoke(__instance, new object[] { shield, hit.point, hit.normal, localWeaponDamageModifier });
                    __result = true;
                    return false;
                }
                if (hitReceiver is CellModule || hitReceiver is CarryableObject || hitReceiver is CarryablesSocket) hitReceiver = target;
            }
            catch (Exception e)
            {
                Log.Failure("Resolving a hit on the enemy ship", e);
            }
            return true;
        }

        private struct EnemyHit
        {
            public PlayerControlledShip Ship;
            public float Damage;
            public OrbitObject Attacker;
        }

        /// <summary>
        /// Host: damage reported by a gunner who is not the host. A shield hit is absorbed the way the host's own hits are
        /// (what it can't hold goes to the hull); a hull hit also counts toward module defects, like the host's hits.
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
                        if (FleetShips.OwnerOf(shield) == attackerShip) continue;
                        shield.ApplyHitDamage(damage[i], attacker, DefaultDamageType, shield.transform.position, Quaternion.identity, true);
                        damage[i] = 0f;
                        continue;
                    }
                    var ship = view.GetComponent<PlayerControlledShip>();
                    if (ship == null || ship == attackerShip) continue;
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
        /// Host, a few times a second: once the teams are confirmed, each ship is hostile to the other, as an NPC ship is
        /// once it has been shot. Auto turrets then engage the enemy ship and it counts as scanned (lockable). Players who
        /// join later are told too (the game doesn't keep that message for them).
        /// </summary>
        public static void KeepHostility()
        {
            if (!Active || !PhotonNetwork.IsMasterClient || !PvpTeams.Locked) return;
            PlayerControlledShip blue = FleetShips.BySlot(0);
            PlayerControlledShip red = FleetShips.BySlot(1);
            if (blue == null || red == null || blue.faction == red.faction) return;
            try
            {
                int pair = FleetShips.ViewId(blue) * 31 + FleetShips.ViewId(red);
                if (pair != _hostilityShips)
                {
                    _hostilityShips = pair;
                    HostilityToldTo.Clear();
                    foreach (Photon.Realtime.Player player in PhotonNetwork.PlayerList) HostilityToldTo.Add(player.ActorNumber);
                }
                if (!blue.IsHostile(red)) blue.SetAggressiveTowards(red);
                if (!red.IsHostile(blue)) red.SetAggressiveTowards(blue);
                foreach (Photon.Realtime.Player player in PhotonNetwork.PlayerList)
                {
                    if (player.IsLocal || !HostilityToldTo.Add(player.ActorNumber)) continue;
                    blue.photonView.RPC("NotifyAggressiveTowards", player, red.ViewID);
                    red.photonView.RPC("NotifyAggressiveTowards", player, blue.ViewID);
                }
            }
            catch (Exception e)
            {
                Log.Failure("Making the ships enemies", e);
            }
        }
    }
}
