using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CG.Game.Player;
using CG.Space;
using Gameplay.Utilities;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// The stat rules of items, weapons, perks, mutators and sector events that watch a ship ("while overdrawing power",
    /// "per flipped breaker", "per defect", "while boosting", "while the void drive charges", "per speed tier", "per N
    /// alloys"). The game sets each rule up once and reads "the player ship" while doing so, so with several ships every rule
    /// watched the ship the local player belonged to at that moment. In a fleet mission each rule watches its own ship:
    /// - while a rule is set up, "the player ship" is the ship whose stats it changes (the ship of the stat collection it is
    ///   applied to, through the collections it hangs under); else the ship of the item or weapon part it comes from; else,
    ///   for a perk, the ship its player is aboard, or their crew's;
    /// - a rule applied to another ship's stats than the one it was set up for is set up again for that ship when it has
    ///   moved there (an item mod put into another ship's weapon, a perk's seat bonus on another ship's helm);
    /// - a rule that stays applied to its first ship while it is applied to another one too (a mutator's bonus for every
    ///   ship, a sector event acting on every ship) gives that other ship its own copy, made from the rule as it was before
    ///   it was first set up; a copy's rules are removed with its ship, or at the end of the mission;
    /// - a speed rule keeps measuring its ship;
    /// - alloy and biomass rules count that ship's own resources (ShipResources).
    /// In a one-ship mission nothing changes.
    /// </summary>
    internal static class ShipStatRules
    {
        public const string Feature = FleetShips.Feature;

        /// <summary>The stat collections being changed right now, innermost last.</summary>
        private static readonly List<StatCollection> Changing = new List<StatCollection>();

        private static readonly ConditionalWeakTable<object, PlayerControlledShip> RuleShips = new ConditionalWeakTable<object, PlayerControlledShip>();

        /// <summary>Where a stat mod's rules were set up: the ship (null for none) and the stat collection it was applied to.</summary>
        private sealed class Home
        {
            public PlayerControlledShip Ship;
            public StatCollection Stats;
        }

        private static readonly ConditionalWeakTable<StatMod, Home> Homes = new ConditionalWeakTable<StatMod, Home>();

        /// <summary>A stat mod as it was before its rules were first set up (its sources left out), for copies of it.</summary>
        private sealed class Pristine
        {
            public StatMod Template;
            public IModifierSource Source;
            public IModifierSource Information;
        }

        private static readonly ConditionalWeakTable<StatMod, Pristine> Templates = new ConditionalWeakTable<StatMod, Pristine>();

        /// <summary>A ship's own copy of a stat mod that several ships use at once.</summary>
        private sealed class ShipCopy
        {
            public StatMod Original;
            public PlayerControlledShip Ship;
            public StatMod Copy;
        }

        private static readonly List<ShipCopy> Copies = new List<ShipCopy>();

        /// <summary>The game's assembly: stat mods and rules whose types come from it are the game's own (GameRules).</summary>
        private static readonly Assembly GameAssembly = typeof(ModDynamicCondition).Assembly;

        /// <summary>Marks the copies (they are never shared, so never copied or set up again).</summary>
        private static readonly ConditionalWeakTable<StatMod, object> CopyMarks = new ConditionalWeakTable<StatMod, object>();

        private static readonly object CopyMark = new object();

        /// <summary>The private setters of the rules' "set up" flags, so a rule can be set up again.</summary>
        private static readonly MethodInfo ConditionSetUp = Setter(GameMember.Property(typeof(ModDynamicCondition), "IsInitialized"));
        private static readonly MethodInfo ValueSetUp = Setter(GameMember.Property(typeof(ModDynamicValue), "IsInitialized"));

        private static MethodInfo Setter(PropertyInfo property)
        {
            try
            {
                return property != null ? property.GetSetMethod(true) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The ship a rule was set up for; null in a one-ship mission, for a rule of something not on a player ship, or once that ship is gone.</summary>
        public static PlayerControlledShip RuleShip(object rule)
        {
            PlayerControlledShip ship;
            return rule != null && RuleShips.TryGetValue(rule, out ship) && ship != null ? ship : null;
        }

        // ---------------------------------------------------------------- whose stats are being changed
        // Each prefix here tells its finalizer through __state that it pushed (onto Changing, or a "player ship"): they are
        // patched one by one and read the game's arguments by name, so a finalizer whose prefix isn't in pops nothing.

        [GamePatch(Feature, "Gameplay.Utilities.StatTagCollection", "ApplyModifier", PatchKind.Prefix, Optional = true)]
        private static void ApplyOnePrefix(StatTagCollection __instance, ref StatMod mod, out bool __state)
        {
            Changing.Add(__instance);
            __state = true;
            if (mod == null || !mod.IsDynamic || FleetSession.ShipCount < 2) return;
            try
            {
                PlayerControlledShip ship = null;
                bool shipKnown = false;
                mod = ForShip(mod, __instance, ref ship, ref shipKnown);
            }
            catch (Exception e)
            {
                Log.Failure("Giving a ship its own stat rule", e);
            }
        }

        [GamePatch(Feature, "Gameplay.Utilities.StatTagCollection", "ApplyModifiers", PatchKind.Prefix, Optional = true)]
        private static void ApplyManyPrefix(StatTagCollection __instance, ref IEnumerable<StatMod> mods, out bool __state)
        {
            Changing.Add(__instance);
            __state = true;
            if (mods == null || FleetSession.ShipCount < 2) return;
            try
            {
                mods = ForShip(mods, __instance);
            }
            catch (Exception e)
            {
                Log.Failure("Giving a ship its own stat rules", e);
            }
        }

        [GamePatch(Feature, "Gameplay.Utilities.StatTagCollection", "ApplyModifier", PatchKind.Finalizer, Optional = true)]
        [GamePatch(Feature, "Gameplay.Utilities.StatTagCollection", "ApplyModifiers", PatchKind.Finalizer, Optional = true)]
        private static void ApplyFinalizer(bool __state)
        {
            if (__state && Changing.Count > 0) Changing.RemoveAt(Changing.Count - 1);
        }

        // ---------------------------------------------------------------- a rule is set up for its own ship

        [GamePatch(Feature, "Gameplay.Utilities.ModDynamicCondition", "Init", PatchKind.Prefix, Optional = true)]
        private static void ConditionInitPrefix(ModDynamicCondition __instance, StatMod mod, out bool __state)
        {
            ShipRedirect.BeginOverride(__instance != null && !__instance.IsInitialized ? Choose(__instance, mod) : null);
            __state = true;
        }

        [GamePatch(Feature, "Gameplay.Utilities.ModDynamicValue", "Init", PatchKind.Prefix, Optional = true)]
        private static void ValueInitPrefix(ModDynamicValue __instance, StatMod statMod, out bool __state)
        {
            ShipRedirect.BeginOverride(__instance != null && !__instance.IsInitialized ? Choose(__instance, statMod) : null);
            __state = true;
        }

        [GamePatch(Feature, "Gameplay.Utilities.ModDynamicCondition", "Init", PatchKind.Finalizer, Optional = true)]
        [GamePatch(Feature, "Gameplay.Utilities.ModDynamicValue", "Init", PatchKind.Finalizer, Optional = true)]
        private static void InitFinalizer(bool __state)
        {
            if (__state) ShipRedirect.EndOverride();
        }

        /// <summary>A speed rule measures its own ship (the game reads "the player ship" every second).</summary>
        [GamePatch(Feature, "Gameplay.Utilities.ModValueShipSpeed", "SpeedCheck", PatchKind.Prefix, Optional = true)]
        private static void SpeedCheckPrefix(object __instance, out bool __state)
        {
            ShipRedirect.BeginOverride(FleetShips.Active ? RuleShip(__instance) : null);
            __state = true;
        }

        [GamePatch(Feature, "Gameplay.Utilities.ModValueShipSpeed", "SpeedCheck", PatchKind.Finalizer, Optional = true)]
        private static void SpeedCheckFinalizer(bool __state)
        {
            if (__state) ShipRedirect.EndOverride();
        }

        /// <summary>
        /// The ship a rule being set up watches, remembered for that rule (and where its stat mod was set up); null outside a
        /// fleet mission.
        /// </summary>
        private static PlayerControlledShip Choose(object rule, StatMod mod)
        {
            try
            {
                if (FleetSession.ShipCount < 2) return null;
                PlayerControlledShip ship = ShipOf(mod);
                if (mod != null)
                {
                    Homes.Remove(mod);
                    Homes.Add(mod, new Home { Ship = ship, Stats = Changing.Count > 0 ? Changing[Changing.Count - 1] : null });
                }
                if (ship == null) return null;
                RuleShips.Remove(rule);
                RuleShips.Add(rule, ship);
                return ship;
            }
            catch (Exception e)
            {
                Log.Failure("Choosing the ship of an item's stat rule", e);
                return null;
            }
        }

        private static PlayerControlledShip ShipOf(StatMod mod)
        {
            PlayerControlledShip ship = Changing.Count > 0 ? ShipOfTree(Changing[Changing.Count - 1]) : null;
            if (ship != null || mod == null || mod.Mod == null) return ship;
            ship = ShipOfSource(mod.Mod.InformationSource);
            return ship != null ? ship : ShipOfSource(mod.Mod.Source);
        }

        /// <summary>
        /// The ship of a stat collection: that of the collection at the top of the ones it hangs under (a module's or a part's
        /// collection is passed its ship's rules through its ship's), else that of its own holder.
        /// </summary>
        private static PlayerControlledShip ShipOfTree(StatCollection stats)
        {
            StatCollection root = stats;
            var tagged = stats as StatTagCollection;
            for (int depth = 0; tagged != null && tagged.ParentCollection != null && depth < 16; depth++)
            {
                tagged = tagged.ParentCollection;
                root = tagged;
            }
            PlayerControlledShip ship = ShipOfStats(root);
            return ship != null || ReferenceEquals(root, stats) ? ship : ShipOfStats(stats);
        }

        /// <summary>The ship of a stat collection's holder (a module, a ship, a player).</summary>
        private static PlayerControlledShip ShipOfStats(StatCollection stats)
        {
            IStatCollectionHolder holder = stats != null ? stats.Holder : null;
            if (holder == null) return null;
            Transform transform;
            try
            {
                transform = holder.HolderTransform;
            }
            catch
            {
                // A holder being torn down.
                return null;
            }
            if (transform == null) return null;
            Player player = transform.GetComponentInParent<Player>();
            return player != null ? ShipOfPlayer(player) : FleetShips.OwnerOf(transform);
        }

        /// <summary>The ship of what a rule comes from: an item, a weapon part or a module on a ship, or a player.</summary>
        private static PlayerControlledShip ShipOfSource(object source)
        {
            var player = source as Player;
            if (player != null) return ShipOfPlayer(player);
            var component = source as Component;
            return component != null ? FleetShips.OwnerOf(component) : null;
        }

        /// <summary>A player's ship for their perks: the one they are aboard (a seat's), else their crew's.</summary>
        private static PlayerControlledShip ShipOfPlayer(Player player)
        {
            PlayerControlledShip ship = FleetShips.AboardShip(player);
            if (ship != null) return ship;
            int actor = player.photonView != null && player.photonView.Owner != null ? player.photonView.Owner.ActorNumber : -1;
            return FleetShips.ShipOfActor(actor);
        }

        // ---------------------------------------------------------------- a rule applied to another ship

        /// <summary>The stat mods to apply to a collection, with a ship's own copy in place of a rule another ship still uses.</summary>
        private static IEnumerable<StatMod> ForShip(IEnumerable<StatMod> mods, StatCollection stats)
        {
            var list = mods as IList<StatMod>;
            if (list == null) return mods;
            List<StatMod> replaced = null;
            PlayerControlledShip ship = null;
            bool shipKnown = false;
            for (int i = 0; i < list.Count; i++)
            {
                StatMod mod = list[i];
                if (mod == null || !mod.IsDynamic) continue;
                StatMod use = ForShip(mod, stats, ref ship, ref shipKnown);
                if (ReferenceEquals(use, mod)) continue;
                if (replaced == null) replaced = new List<StatMod>(list);
                replaced[i] = use;
            }
            return replaced != null ? replaced : mods;
        }

        /// <summary>
        /// The stat mod to apply to a collection, for one with rules (in a fleet mission):
        /// - made with another mod's own kind of stat mod, modifier or rule: itself, left as that mod made it (GameRules);
        /// - not set up yet: itself, kept as it is now first, so copies can be made of it later;
        /// - set up for this collection's ship, applied again where it was set up, applied to a collection of no player ship,
        ///   or set up before the mission had several ships: itself;
        /// - set up for another ship (or for none) and no longer applied where it was set up (it moved): itself, set up again
        ///   for this ship (the game sets it up as it applies it);
        /// - still applied where it was set up (several ships use it at once): this ship's own copy.
        /// </summary>
        private static StatMod ForShip(StatMod mod, StatCollection stats, ref PlayerControlledShip ship, ref bool shipKnown)
        {
            object mark;
            if (CopyMarks.TryGetValue(mod, out mark)) return mod;
            if (!GameRules(mod)) return mod;
            if (!IsSetUp(mod))
            {
                KeepPristine(mod);
                return mod;
            }
            Home home;
            if (!Homes.TryGetValue(mod, out home) || ReferenceEquals(home.Stats, stats)) return mod;
            if (!shipKnown)
            {
                ship = ShipOfTree(stats);
                shipKnown = true;
            }
            if (ship == null) return mod;
            bool hadShip = (object)home.Ship != null;
            bool homeAlive = hadShip && home.Ship != null;
            bool held = (!hadShip || homeAlive) && Holds(home.Stats, mod);
            if (homeAlive && ReferenceEquals(home.Ship, ship))
            {
                // Another collection of its ship: where it is applied from now on, unless it still is where it was (or is
                // being applied there now, which passes it to that collection's own collections first).
                if (!held && !Changing.Contains(home.Stats)) home.Stats = stats;
                return mod;
            }
            if (held)
            {
                StatMod copy = CopyFor(mod, ship);
                return copy != null ? copy : mod;
            }
            SetUpAgain(mod);
            return mod;
        }

        /// <summary>
        /// Whether a stat mod is all the game's own: the mod itself, its modifier, its tag settings and its rules. Only those
        /// are kept, copied or set up again. Something another mod writes can hold live game objects before it is set up
        /// (Ectype Skirmisher's Get Out and Push rule keeps its player, their atmosphere tracker and the ship), and the game's
        /// deep copy then copies everything they lead to, recursing until the stack runs out: the game closed at once, with no
        /// crash report, as such a player arrived in a fleet mission. The copy goes through the whole stat mod, so a mod's own
        /// kind of stat mod or modifier is left alone too, not only its own rules.
        /// </summary>
        private static bool GameRules(StatMod mod)
        {
            return mod.GetType().Assembly == GameAssembly &&
                (mod.Mod == null || mod.Mod.GetType().Assembly == GameAssembly) &&
                (mod.TagConfiguration == null || mod.TagConfiguration.GetType().Assembly == GameAssembly) &&
                (mod.DynamicCondition == null || mod.DynamicCondition.GetType().Assembly == GameAssembly) &&
                (mod.DynamicValue == null || mod.DynamicValue.GetType().Assembly == GameAssembly);
        }

        private static bool IsSetUp(StatMod mod)
        {
            return (mod.DynamicCondition != null && mod.DynamicCondition.IsInitialized) || (mod.DynamicValue != null && mod.DynamicValue.IsInitialized);
        }

        /// <summary>Whether a stat collection still has a stat mod applied (active, or waiting for its condition).</summary>
        private static bool Holds(StatCollection stats, StatMod mod)
        {
            if (stats == null) return false;
            if (stats.ActiveModifiers != null && stats.ActiveModifiers.Contains(mod)) return true;
            var tagged = stats as StatTagCollection;
            return tagged != null && tagged.InactiveModifiers != null && tagged.InactiveModifiers.Contains(mod);
        }

        /// <summary>
        /// A stat mod moved to another ship's stats: its rules let go of the ship they watched (the game's own way, as when its
        /// item is destroyed) and count as not set up, so the game sets them up again as it applies the mod, for the new ship.
        /// </summary>
        private static void SetUpAgain(StatMod mod)
        {
            if ((mod.DynamicCondition != null && ConditionSetUp == null) || (mod.DynamicValue != null && ValueSetUp == null)) return;
            mod.DestroyDynamicRules();
            if (mod.DynamicCondition != null) ConditionSetUp.Invoke(mod.DynamicCondition, new object[] { false });
            if (mod.DynamicValue != null) ValueSetUp.Invoke(mod.DynamicValue, new object[] { false });
            Log.Verbose("A stat rule that moved to another ship's stats is set up again for that ship.");
        }

        /// <summary>
        /// Keeps a stat mod as it is before its rules are first set up, for copies: copied with the game's own deep copy (as it
        /// copies perks and mutators), its sources (an item, a player, a ship part) left out of the copy and kept aside.
        /// </summary>
        private static void KeepPristine(StatMod mod)
        {
            Pristine pristine;
            PrimitiveModifier primitive = mod.Mod;
            if (primitive == null || Templates.TryGetValue(mod, out pristine)) return;
            IModifierSource source = primitive.Source;
            IModifierSource information = primitive.InformationSource;
            ModifiablePrimitive affected = primitive.AffectedMod;
            StatMod template;
            primitive.Source = null;
            primitive.InformationSource = null;
            primitive.AffectedMod = null;
            try
            {
                template = DeepCopy(mod);
            }
            finally
            {
                primitive.Source = source;
                primitive.InformationSource = information;
                primitive.AffectedMod = affected;
            }
            if (template != null) Templates.Add(mod, new Pristine { Template = template, Source = source, Information = information });
        }

        private static StatMod DeepCopy(StatMod mod)
        {
            return global::Client.Utils.ObjectExtensions.DeepCopy(mod);
        }

        /// <summary>A ship's own copy of a stat mod several ships use at once (made once per ship); null when it can't be made.</summary>
        private static StatMod CopyFor(StatMod mod, PlayerControlledShip ship)
        {
            for (int i = 0; i < Copies.Count; i++)
            {
                if (ReferenceEquals(Copies[i].Original, mod) && ReferenceEquals(Copies[i].Ship, ship)) return Copies[i].Copy;
            }
            Pristine pristine;
            if (!Templates.TryGetValue(mod, out pristine)) return null;
            StatMod copy = DeepCopy(pristine.Template);
            if (copy == null || copy.Mod == null) return null;
            copy.Mod.Source = pristine.Source;
            copy.Mod.InformationSource = pristine.Information;
            CopyMarks.Add(copy, CopyMark);
            Copies.Add(new ShipCopy { Original = mod, Ship = ship, Copy = copy });
            Log.Verbose("A stat rule several ships use gets a copy of its own for " + FleetShips.Describe(ship) + ".");
            return copy;
        }

        // ---------------------------------------------------------------- the copies go with their ship

        /// <summary>A ship goes: its copies of shared stat rules let go of it (the original rules are the game's to remove).</summary>
        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "OnDestroy", PatchKind.Prefix, Optional = true)]
        private static void ShipDestroyPrefix(PlayerControlledShip __instance)
        {
            if (Copies.Count == 0 || (object)__instance == null) return;
            DropCopies(__instance);
        }

        /// <summary>The mission ends: the rest of the copies let go too, as the game does with its own shared rules.</summary>
        [GamePatch(Feature, "GameSession", "OnSessionEnd", PatchKind.Postfix, Optional = true)]
        private static void SessionEndPostfix()
        {
            if (Copies.Count > 0) DropCopies(null);
        }

        /// <summary>Destroys the rules of a ship's copies (every ship's with null) and forgets them.</summary>
        private static void DropCopies(PlayerControlledShip ship)
        {
            for (int i = Copies.Count - 1; i >= 0; i--)
            {
                ShipCopy entry = Copies[i];
                if ((object)ship != null && !ReferenceEquals(entry.Ship, ship)) continue;
                Copies.RemoveAt(i);
                try
                {
                    if (IsSetUp(entry.Copy)) entry.Copy.DestroyDynamicRules();
                }
                catch (Exception e)
                {
                    Log.Failure("Removing a ship's copy of a stat rule", e);
                }
            }
        }
    }
}
