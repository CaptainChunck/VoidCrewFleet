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
using Gameplay.Carryables;
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
    /// - gunners who are not the host damage the enemy hull with module defects, as the host's own shots do; the host only
    ///   takes a gunner's report of a hit from a ship of the gunner's own team on the other team;
    /// - a missile belongs to the ship whose launcher fired it (the game gives it "the player ship"), also in co-op. In PvP
    ///   a payload primed by hand and let go in space fires its missiles for the team of the player who let go of it, and
    ///   what a dying projectile spawns belongs to its ship, the same on every client;
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

        /// <summary>A shot's own hit on what it struck, called directly (no reflection call per hit); null when the game has changed.</summary>
        private static readonly Action<WeaponProjectile, IDamageReceiver, Vector3, Vector3, float> ImpactDamageReceiverCall =
            Bind<Action<WeaponProjectile, IDamageReceiver, Vector3, Vector3, float>>(GameMember.Method(typeof(WeaponProjectile), "ImpactDamageReceiver"));

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

        /// <summary>
        /// How many of a ship's objects (transforms) that walk looks at per tick: a few tenths of a millisecond at most,
        /// instead of the whole ship (1-2 ms) in one frame.
        /// </summary>
        private const int RewalkPerTick = 400;

        /// <summary>The faction each ship's parts were last given (by the ship's instance ID).</summary>
        private static readonly Dictionary<int, int> WalkedShips = new Dictionary<int, int>();

        private static float _nextRewalk;
        private static int _rewalkTurn;

        /// <summary>The ship being walked again, a part per tick: the objects still to look at, the faction they get, and how many changed.</summary>
        private static PlayerControlledShip _rewalkShip;
        private static int _rewalkFaction;
        private static int _rewalkChanged;
        private static readonly List<Transform> RewalkLeft = new List<Transform>();

        /// <summary>The parts found under one object, reused by every walk.</summary>
        private static readonly List<OrbitObject> Parts = new List<OrbitObject>();

        private static readonly HashSet<int> HostilityToldTo = new HashSet<int>();
        private static int _hostilityShips;

        /// <summary>
        /// A launcher view ID no network object has: ShipInLauncher - slot names a ship in the launcher's place of a shot the
        /// host fires without a launcher (FireProjectilePrefix).
        /// </summary>
        private const int ShipInLauncher = -1000;

        private const int MaxSlots = 64;

        /// <summary>PvP, every client: the player who last let go of each payload (by the payload's instance ID).</summary>
        private static readonly Dictionary<int, int> PayloadThrowers = new Dictionary<int, int>();

        /// <summary>Host: the ship a shot fired right now without a launcher belongs to (a thrown payload's, or a dying projectile's).</summary>
        private static PlayerControlledShip _thrownShip;
        private static SyncedProjectile _dyingProjectile;

        /// <summary>Every client: the slot of the ship the host named for the shot being created right now, or -1.</summary>
        private static int _arrivingSlot = -1;

        /// <summary>The faction a missile with no launcher looks for targets as, while it looks; 0 when none is set.</summary>
        private static int _seekFaction;

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

        /// <summary>A fast call to a game method the plugin can't call by name (protected or private), or null when it is missing or has changed.</summary>
        private static T Bind<T>(MethodInfo method) where T : Delegate
        {
            if (method == null) return null;
            try
            {
                return HarmonyLib.AccessTools.MethodDelegate<T>(method);
            }
            catch (Exception e)
            {
                Log.Warn("Game method " + method.DeclaringType.Name + "." + method.Name + " can't be called directly (" + e.Message + "); the game may have changed.");
                return null;
            }
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
        /// turn, is walked again for anything else that came aboard since (items, for one), a part of it per tick (Rewalk).
        /// </summary>
        public static void KeepFactions()
        {
            if (!Active)
            {
                if (WalkedShips.Count > 0) WalkedShips.Clear();
                if (PayloadThrowers.Count > 0) PayloadThrowers.Clear();
                RewalkLeft.Clear();
                _rewalkShip = null;
                return;
            }
            IList<PlayerControlledShip> ships = FleetShips.All;
            for (int i = 0; i < ships.Count; i++)
            {
                PlayerControlledShip ship = ships[i];
                int slot = FleetShips.Slot(ship);
                if (slot < 0) continue;
                int faction = FactionOfSlot(slot);
                int id = ship.GetInstanceID();
                int walked;
                if (ship.faction == faction && WalkedShips.TryGetValue(id, out walked) && walked == faction) continue;
                ApplyToShip(ship, faction);
                WalkedShips[id] = faction;
            }
            Rewalk(ships);
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
        /// Every part of a ship at once: its own hierarchy, its hull's (which the game keeps apart from the ship) and any
        /// module outside both; each part once.
        /// </summary>
        private static void ApplyToShip(PlayerControlledShip ship, int faction)
        {
            Transform root = ship.transform;
            ship.GetComponentsInChildren(true, Parts);
            int changed = Apply(Parts, faction);
            ShipHull hull = ship.Hull;
            Transform hullRoot = hull != null ? hull.transform : null;
            if (hullRoot != null && !hullRoot.IsChildOf(root))
            {
                hull.GetComponentsInChildren(true, Parts);
                changed += Apply(Parts, faction);
            }
            List<CellModule> modules = ship.GetAllModules();
            if (modules != null)
            {
                foreach (CellModule module in modules)
                {
                    if (module == null) continue;
                    Transform place = module.transform;
                    if (place.IsChildOf(root) || (hullRoot != null && place.IsChildOf(hullRoot))) continue;
                    module.GetComponentsInChildren(true, Parts);
                    changed += Apply(Parts, faction);
                }
            }
            Parts.Clear();
            if (changed > 0) Log.Verbose("PvP: " + changed + " parts of " + FleetShips.Describe(ship) + " are now faction " + faction + ".");
        }

        /// <summary>
        /// The walk again, spread over ticks: every few seconds the next ship in turn is started (its own hierarchy, its
        /// hull's and any module outside both, as ApplyToShip), and each tick looks at the next RewalkPerTick of its objects,
        /// so no frame pays for the whole ship. Objects destroyed meanwhile are skipped; anything that comes aboard where the
        /// walk has already been is found next time round.
        /// </summary>
        private static void Rewalk(IList<PlayerControlledShip> ships)
        {
            if (RewalkLeft.Count == 0)
            {
                if (_rewalkShip != null && _rewalkChanged > 0)
                    Log.Verbose("PvP: " + _rewalkChanged + " parts of " + FleetShips.Describe(_rewalkShip) + " are now faction " + _rewalkFaction + ".");
                _rewalkShip = null;
                if (Time.unscaledTime < _nextRewalk || ships.Count == 0) return;
                _nextRewalk = Time.unscaledTime + RewalkSeconds;
                PlayerControlledShip next = ships[(_rewalkTurn++ & int.MaxValue) % ships.Count];
                int slot = FleetShips.Slot(next);
                if (slot < 0) return;
                _rewalkShip = next;
                _rewalkFaction = FactionOfSlot(slot);
                _rewalkChanged = 0;
                Transform root = next.transform;
                RewalkLeft.Add(root);
                ShipHull hull = next.Hull;
                Transform hullRoot = hull != null ? hull.transform : null;
                if (hullRoot != null && !hullRoot.IsChildOf(root)) RewalkLeft.Add(hullRoot);
                List<CellModule> modules = next.GetAllModules();
                if (modules != null)
                {
                    foreach (CellModule module in modules)
                    {
                        if (module == null) continue;
                        Transform place = module.transform;
                        if (place.IsChildOf(root) || (hullRoot != null && place.IsChildOf(hullRoot))) continue;
                        RewalkLeft.Add(place);
                    }
                }
            }
            if (_rewalkShip == null)
            {
                // The ship is gone: so is the rest of the walk.
                RewalkLeft.Clear();
                return;
            }
            for (int n = 0; n < RewalkPerTick && RewalkLeft.Count > 0; n++)
            {
                int last = RewalkLeft.Count - 1;
                Transform node = RewalkLeft[last];
                RewalkLeft.RemoveAt(last);
                if (node == null) continue;
                node.GetComponents(Parts);
                _rewalkChanged += Apply(Parts, _rewalkFaction);
                for (int c = node.childCount - 1; c >= 0; c--) RewalkLeft.Add(node.GetChild(c));
            }
            Parts.Clear();
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
                module.GetComponentsInChildren(true, Parts);
                int changed = Apply(Parts, faction);
                Parts.Clear();
                if (changed > 0) Log.Verbose("PvP: a module of " + FleetShips.Describe(FleetShips.OwnerOf(__instance)) + " is now faction " + faction + " (" + changed + " parts).");
            }
            catch (Exception e)
            {
                Log.Failure("Giving a new module its team", e);
            }
        }

        /// <summary>Player-side parts take the team's faction; neutral parts stay neutral and characters go by their team.</summary>
        private static int Apply(List<OrbitObject> parts, int faction)
        {
            int changed = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                OrbitObject part = parts[i];
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
                    if (!shield.Valid() || ImpactDamageReceiverCall == null) return false;
                    float modifier = localWeaponDamageModifier * LockBonus(__instance.SourceWeapon, shooter, target);
                    _shieldShot = shield;
                    _shieldShotDamage = modifier * __instance.Damage;
                    _shieldShotWeapon = __instance.SourceWeapon;
                    try
                    {
                        ImpactDamageReceiverCall(__instance, shield, hit.point, hit.normal, modifier);
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
        /// The game applies whatever any player reports, so the host checks each report first (a refused entry's damage is
        /// set to nothing):
        /// - the ship that fired must be on the reporting player's team (when RpcSender knows who sent it);
        /// - a player ship (its hull, a part or a shield) only takes damage from a ship of the other team;
        /// - anything else (NPCs, asteroids) is taken as before.
        /// The amount isn't capped. What one report holds depends on the weapon (a beam's damage grows the longer it holds
        /// on, by curves in its data; multi-shot and burst barrels fire several rounds at once), on its stats after
        /// enhancements and mods, the helm's lock bonus and the damage type against the target's defence, and the game sends
        /// reports in batches; nothing in the game's data bounds all of that without cutting real hits, so the host checks
        /// who hits whom, not how hard.
        /// </summary>
        [GamePatch(Feature, "ProjectileDamageDistributor", "SendDamageData", PatchKind.Prefix)]
        private static void SendDamagePrefix(ProjectileDamageDistributor __instance, int[] targets, float[] damage, int sourceId, out List<EnemyHit> __state)
        {
            __state = null;
            if (!Active || !PhotonNetwork.IsMasterClient || targets == null || damage == null) return;
            try
            {
                OrbitObject attacker = ObjectRegistry.FindByPhotonView<OrbitObject>(sourceId);
                PlayerControlledShip attackerShip = attacker != null ? FleetShips.OwnerOf(attacker) : null;
                int sender;
                bool known = RpcSender.Of(__instance.photonView, out sender) && sender != FleetShips.LocalActor;
                // A report naming a ship of another team (or from a player with no team) as the one that fired: all of it.
                bool refuseAll = known && attackerShip != null && FleetShips.TeamOfActor(sender) != FleetShips.TeamOfSlot(FleetShips.Slot(attackerShip));
                int refused = 0;
                for (int i = 0; i < targets.Length && i < damage.Length; i++)
                {
                    if (damage[i] <= 0f) continue;
                    PhotonView view = PhotonView.Find(targets[i]);
                    PlayerControlledShip targetShip = ShipOfTarget(view);
                    if (refuseAll || (targetShip != null && !Enemies(attackerShip, targetShip)))
                    {
                        damage[i] = 0f;
                        refused++;
                        continue;
                    }
                    if (targetShip == null) continue;
                    var shield = view.GetComponent<ShieldDamagable>();
                    if (shield != null)
                    {
                        shield.ApplyHitDamage(damage[i], attacker, ShotDamageType(attacker), shield.transform.position, Quaternion.identity);
                        damage[i] = 0f;
                        continue;
                    }
                    var ship = view.GetComponent<PlayerControlledShip>();
                    if (ship == null) continue;
                    if (__state == null) __state = new List<EnemyHit>();
                    __state.Add(new EnemyHit { Ship = ship, Damage = damage[i], Attacker = attacker });
                }
                if (refused > 0)
                {
                    string why = refuseAll
                        ? "it named " + FleetShips.Describe(attackerShip) + ", not a ship of its team, as the one that fired"
                        : "the firing ship (" + (attackerShip != null ? FleetShips.Describe(attackerShip) : "none") + ") can't hurt those ships";
                    string what = "PvP: ignored " + refused + (refused == 1 ? " hit" : " hits") + " reported by player " + (known ? sender.ToString() : "?") + ": " + why;
                    Log.InfoOnce("pvp refused damage " + (known ? sender : -1), what + "; further ones are only logged in verbose mode.");
                    Log.Verbose(what + ".");
                }
            }
            catch (Exception e)
            {
                Log.Failure("Applying an enemy gunner's damage", e);
            }
        }

        /// <summary>The player ship a reported target belongs to (the ship itself, its shield or one of its parts), or null.</summary>
        private static PlayerControlledShip ShipOfTarget(PhotonView view)
        {
            if (view == null) return null;
            var part = view.GetComponent<OrbitObject>();
            return FleetShips.OwnerOf(part != null ? (Component)part : view);
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
        /// In PvP a shot the host fired without a launcher for a ship (a thrown payload's missile, or what a dying projectile
        /// spawns: FireProjectilePrefix) belongs to that ship the same way.
        /// </summary>
        [GamePatch(FleetShips.Feature, "CG.Space.Projectiles.SyncedProjectilesSynchronizer", "RegisterNpcProjectile", PatchKind.Prefix)]
        private static void RegisterProjectilePrefix(SyncedProjectile projectile)
        {
            try
            {
                if (projectile == null || !FleetShips.Active) return;
                if (_arrivingSlot >= 0)
                {
                    // The same ship and faction on every client: the host named the ship's slot.
                    PlayerControlledShip given = FleetShips.BySlot(_arrivingSlot);
                    if (given != null) projectile.Source = given;
                    projectile.Faction = FactionOfSlot(_arrivingSlot);
                    return;
                }
                var missile = projectile as Missile;
                if (missile == null || missile.Launcher == null) return;
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
        /// Every client: a payload let go of (dropped or thrown) remembers who held it. Primed by hand and let go in space,
        /// it fires its missiles a moment later, and they are that player's team's (PayloadTargetsPrefix, PayloadSpawnPrefix).
        /// Every client sees the drop, as the carrier's item is synced to everyone.
        /// </summary>
        [GamePatch(Feature, "Payload", "OnBeingDropped", PatchKind.Prefix, Optional = true)]
        private static void PayloadDroppedPrefix(Payload __instance, ICarrier previousCarrier)
        {
            try
            {
                var player = previousCarrier as Player;
                if (player == null || !Active) return;
                PayloadThrowers[__instance.GetInstanceID()] = player.ActorId;
            }
            catch (Exception e)
            {
                Log.Failure("Noting who let go of a payload", e);
            }
        }

        /// <summary>The team faction of the player who last let go of a payload; 0 when not known.</summary>
        private static int ThrowerFaction(Payload payload)
        {
            int actor;
            if (payload == null || !PayloadThrowers.TryGetValue(payload.GetInstanceID(), out actor)) return 0;
            int team = FleetShips.TeamOfActor(actor);
            return team >= 0 ? FactionOfTeam(team) : 0;
        }

        /// <summary>The ship a thrown payload's missiles belong to: the thrower's own ship, else their team's first living ship.</summary>
        private static PlayerControlledShip ThrowerShip(Payload payload)
        {
            int actor;
            if (payload == null || !PayloadThrowers.TryGetValue(payload.GetInstanceID(), out actor)) return null;
            PlayerControlledShip ship = FleetShips.ShipOfActor(actor);
            if (ship != null && !ship.StartedDeath) return ship;
            int team = FleetShips.TeamOfActor(actor);
            if (team < 0) return null;
            foreach (int slot in FleetShips.AliveSlotsOfTeam(team))
            {
                ship = FleetShips.BySlot(slot);
                if (ship != null) return ship;
            }
            return null;
        }

        /// <summary>
        /// Every client: a payload with no launcher (primed by hand) looks for its missiles' targets as the thrower's team.
        /// The game looks as faction 1, BLUE: RED's thrown missiles went for RED's own ships.
        /// </summary>
        [GamePatch(Feature, "PayloadMissileGameplayEffect", "FindPossibleTargets", PatchKind.Prefix, Optional = true)]
        private static void PayloadTargetsPrefix(PayloadMissileGameplayEffect __instance)
        {
            _seekFaction = 0;
            if (!Active) return;
            try
            {
                Payload payload = __instance.Payload;
                if (payload != null && payload.Launcher == null) _seekFaction = ThrowerFaction(payload);
            }
            catch (Exception e)
            {
                Log.Failure("Choosing a thrown payload's targets", e);
            }
        }

        [GamePatch(Feature, "PayloadMissileGameplayEffect", "FindPossibleTargets", PatchKind.Finalizer, Optional = true)]
        private static void PayloadTargetsFinalizer()
        {
            _seekFaction = 0;
        }

        /// <summary>Host: a payload with no launcher fires its missiles for the thrower's ship (FireProjectilePrefix).</summary>
        [GamePatch(Feature, "PayloadMissileGameplayEffect", "SpawnObject", PatchKind.Prefix, Optional = true)]
        private static void PayloadSpawnPrefix(PayloadMissileGameplayEffect __instance)
        {
            _thrownShip = null;
            if (!PhotonNetwork.IsMasterClient || !Active) return;
            try
            {
                Payload payload = __instance.Payload;
                if (payload != null && payload.Launcher == null) _thrownShip = ThrowerShip(payload);
            }
            catch (Exception e)
            {
                Log.Failure("Giving a thrown payload's missile its team", e);
            }
        }

        [GamePatch(Feature, "PayloadMissileGameplayEffect", "SpawnObject", PatchKind.Finalizer, Optional = true)]
        private static void PayloadSpawnFinalizer()
        {
            _thrownShip = null;
        }

        /// <summary>Host: what a player ship's dying projectile spawns belongs to that projectile's ship (FireProjectilePrefix).</summary>
        [GamePatch(Feature, "CG.Space.Projectiles.SyncedProjectile", "TrySpawnProjectilesOnDeath", PatchKind.Prefix, Optional = true)]
        private static void DyingProjectilePrefix(SyncedProjectile __instance)
        {
            _dyingProjectile = null;
            try
            {
                if (__instance.Source is PlayerControlledShip && PhotonNetwork.IsMasterClient && Active) _dyingProjectile = __instance;
            }
            catch (Exception e)
            {
                Log.Failure("Giving a dying projectile's shots its ship", e);
            }
        }

        [GamePatch(Feature, "CG.Space.Projectiles.SyncedProjectile", "TrySpawnProjectilesOnDeath", PatchKind.Finalizer, Optional = true)]
        private static void DyingProjectileFinalizer()
        {
            _dyingProjectile = null;
        }

        /// <summary>
        /// Host: a shot fired without a launcher for a ship (a thrown payload's missile, or what a dying projectile spawns)
        /// names that ship in the launcher's place, as a number no network object has. Every client then gives the shot the
        /// same ship and faction (FireProjectileInternalPrefix); the game gave it each client's own ship.
        /// </summary>
        [GamePatch(Feature, "PlayerSyncedProjectilesShooter", "FireProjectile", PatchKind.Prefix, Optional = true)]
        private static void FireProjectilePrefix(ref int launcherId)
        {
            if (launcherId != -1 || ((object)_thrownShip == null && (object)_dyingProjectile == null)) return;
            try
            {
                PlayerControlledShip ship = (object)_thrownShip != null ? _thrownShip : _dyingProjectile.Source as PlayerControlledShip;
                int slot = FleetShips.Slot(ship);
                if (slot >= 0 && slot < MaxSlots) launcherId = ShipInLauncher - slot;
            }
            catch (Exception e)
            {
                Log.Failure("Naming the ship of a shot without a launcher", e);
            }
        }

        /// <summary>
        /// Every client: a shot whose launcher is a ship's slot (FireProjectilePrefix) gets no launcher, as the game expects,
        /// and that ship as it registers (RegisterProjectilePrefix).
        /// </summary>
        [GamePatch(Feature, "PlayerSyncedProjectilesShooter", "FireProjectileInternal", PatchKind.Prefix, Optional = true)]
        private static void FireProjectileInternalPrefix(ref int launcherId)
        {
            _arrivingSlot = -1;
            if (launcherId > ShipInLauncher || launcherId <= ShipInLauncher - MaxSlots) return;
            _arrivingSlot = ShipInLauncher - launcherId;
            launcherId = -1;
        }

        [GamePatch(Feature, "PlayerSyncedProjectilesShooter", "FireProjectileInternal", PatchKind.Finalizer, Optional = true)]
        private static void FireProjectileInternalFinalizer()
        {
            _arrivingSlot = -1;
        }

        /// <summary>
        /// Host: a missile with no launcher (a thrown payload's, or one a dying projectile spawned) that looks for a new
        /// target looks as its own team. The game's missiles look as faction 1 unless their launcher's ship has target locks.
        /// </summary>
        [GamePatch(Feature, "Missile", "GetTarget", PatchKind.Prefix, Optional = true)]
        private static void MissileSeekPrefix(Missile __instance)
        {
            _seekFaction = 0;
            try
            {
                if (__instance.Launcher == null && __instance.Source is PlayerControlledShip && Active) _seekFaction = __instance.Faction;
            }
            catch (Exception e)
            {
                Log.Failure("Choosing a missile's new target", e);
            }
        }

        [GamePatch(Feature, "Missile", "GetTarget", PatchKind.Finalizer, Optional = true)]
        private static void MissileSeekFinalizer()
        {
            _seekFaction = 0;
        }

        /// <summary>
        /// The targets a ship's missiles may pick: the game passes faction 1 (the players) and compares locked targets
        /// with faction 1; in PvP each ship uses its own team's faction, and a missile or payload with no launcher (so no
        /// target locks) the faction it looks as (MissileSeekPrefix, PayloadTargetsPrefix).
        /// </summary>
        [GamePatch(Feature, "CG.Ship.Modules.Weapons.ProjectileUtils", "GetPossibleMissileTargets", PatchKind.Prefix)]
        private static bool MissileTargetsPrefix(Vector3 firePosition, float maxMissileRange, TargetLockComponent targetLockComponent, ref int faction,
            ref List<OrbitObject> __result)
        {
            if (!Active) return true;
            if (targetLockComponent == null)
            {
                if (_seekFaction != 0) faction = _seekFaction;
                return true;
            }
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
                IList<PlayerControlledShip> ships = FleetShips.All;
                Photon.Realtime.Room room = PhotonNetwork.CurrentRoom;
                int signature = ShipsSignature(ships);
                if (signature != _hostilityShips)
                {
                    // Players in the room now are told by SetAggressiveTowards itself; only later joiners need the message.
                    _hostilityShips = signature;
                    HostilityToldTo.Clear();
                    if (room != null)
                    {
                        foreach (Photon.Realtime.Player player in room.Players.Values) HostilityToldTo.Add(player.ActorNumber);
                    }
                }
                for (int i = 0; i < ships.Count; i++)
                {
                    for (int j = 0; j < ships.Count; j++)
                    {
                        PlayerControlledShip a = ships[i];
                        PlayerControlledShip b = ships[j];
                        if (HostilePair(a, b) && !a.IsHostile(b)) a.SetAggressiveTowards(b);
                    }
                }
                if (room == null) return;
                foreach (Photon.Realtime.Player player in room.Players.Values)
                {
                    if (player == null || player.IsLocal || !HostilityToldTo.Add(player.ActorNumber)) continue;
                    for (int i = 0; i < ships.Count; i++)
                    {
                        for (int j = 0; j < ships.Count; j++)
                        {
                            if (HostilePair(ships[i], ships[j])) ships[i].photonView.RPC("NotifyAggressiveTowards", player, ships[j].ViewID);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Failure("Making the teams' ships enemies", e);
            }
        }

        /// <summary>Two ships kept hostile to each other: of different teams and factions, and neither dying.</summary>
        private static bool HostilePair(PlayerControlledShip a, PlayerControlledShip b)
        {
            return Enemies(a, b) && a.faction != b.faction && !a.StartedDeath && !b.StartedDeath;
        }

        /// <summary>Which ships there are, as one number that doesn't depend on their order.</summary>
        private static int ShipsSignature(IList<PlayerControlledShip> ships)
        {
            int signature = 17 + ships.Count;
            for (int i = 0; i < ships.Count; i++) signature = unchecked(signature + FleetShips.ViewId(ships[i]) * SignatureMix);
            return signature;
        }

        /// <summary>Spreads view IDs over the whole int range (Knuth's multiplier), so different ships sum to different numbers.</summary>
        private const int SignatureMix = unchecked((int)2654435761u);
    }
}
