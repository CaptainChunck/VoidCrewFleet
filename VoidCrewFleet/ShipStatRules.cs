using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CG.Game.Player;
using CG.Space;
using Gameplay.Utilities;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// The stat rules of items, weapons and perks that watch a ship ("while overdrawing power", "per flipped breaker",
    /// "per defect", "while boosting", "while the void drive charges", "per speed tier", "per N alloys"). The game sets each
    /// rule up once and reads "the player ship" while doing so, so with several ships every rule watched the ship the local
    /// player belonged to at that moment. In a fleet mission each rule watches its own ship:
    /// - while a rule is set up, "the player ship" is the ship whose stats it changes (a module's or a ship's); else the
    ///   ship of the item or weapon part it comes from; else, for a perk, the ship its player is aboard, or their crew's;
    /// - a speed rule keeps measuring that ship;
    /// - alloy and biomass rules count that ship's own resources (ShipResources).
    /// In a one-ship mission nothing changes.
    /// </summary>
    internal static class ShipStatRules
    {
        public const string Feature = FleetShips.Feature;

        /// <summary>The stat collections being changed right now, innermost last.</summary>
        private static readonly List<StatCollection> Changing = new List<StatCollection>();

        private static readonly ConditionalWeakTable<object, PlayerControlledShip> RuleShips = new ConditionalWeakTable<object, PlayerControlledShip>();

        /// <summary>The ship a rule was set up for; null in a one-ship mission, for a rule of something not on a player ship, or once that ship is gone.</summary>
        public static PlayerControlledShip RuleShip(object rule)
        {
            PlayerControlledShip ship;
            return rule != null && RuleShips.TryGetValue(rule, out ship) && ship != null ? ship : null;
        }

        // ---------------------------------------------------------------- whose stats are being changed

        [GamePatch(Feature, "Gameplay.Utilities.StatTagCollection", "ApplyModifier", PatchKind.Prefix, Optional = true)]
        [GamePatch(Feature, "Gameplay.Utilities.StatTagCollection", "ApplyModifiers", PatchKind.Prefix, Optional = true)]
        private static void ApplyPrefix(StatCollection __instance)
        {
            Changing.Add(__instance);
        }

        [GamePatch(Feature, "Gameplay.Utilities.StatTagCollection", "ApplyModifier", PatchKind.Finalizer, Optional = true)]
        [GamePatch(Feature, "Gameplay.Utilities.StatTagCollection", "ApplyModifiers", PatchKind.Finalizer, Optional = true)]
        private static void ApplyFinalizer()
        {
            if (Changing.Count > 0) Changing.RemoveAt(Changing.Count - 1);
        }

        // ---------------------------------------------------------------- a rule is set up for its own ship

        [GamePatch(Feature, "Gameplay.Utilities.ModDynamicCondition", "Init", PatchKind.Prefix, Optional = true)]
        private static void ConditionInitPrefix(ModDynamicCondition __instance, StatMod mod)
        {
            ShipRedirect.BeginOverride(__instance != null && !__instance.IsInitialized ? Choose(__instance, mod) : null);
        }

        [GamePatch(Feature, "Gameplay.Utilities.ModDynamicValue", "Init", PatchKind.Prefix, Optional = true)]
        private static void ValueInitPrefix(ModDynamicValue __instance, StatMod statMod)
        {
            ShipRedirect.BeginOverride(__instance != null && !__instance.IsInitialized ? Choose(__instance, statMod) : null);
        }

        [GamePatch(Feature, "Gameplay.Utilities.ModDynamicCondition", "Init", PatchKind.Finalizer, Optional = true)]
        [GamePatch(Feature, "Gameplay.Utilities.ModDynamicValue", "Init", PatchKind.Finalizer, Optional = true)]
        private static void InitFinalizer()
        {
            ShipRedirect.EndOverride();
        }

        /// <summary>A speed rule measures its own ship (the game reads "the player ship" every second).</summary>
        [GamePatch(Feature, "Gameplay.Utilities.ModValueShipSpeed", "SpeedCheck", PatchKind.Prefix, Optional = true)]
        private static void SpeedCheckPrefix(object __instance)
        {
            ShipRedirect.BeginOverride(FleetShips.Active ? RuleShip(__instance) : null);
        }

        [GamePatch(Feature, "Gameplay.Utilities.ModValueShipSpeed", "SpeedCheck", PatchKind.Finalizer, Optional = true)]
        private static void SpeedCheckFinalizer()
        {
            ShipRedirect.EndOverride();
        }

        /// <summary>The ship a rule being set up watches, remembered for that rule; null outside a fleet mission.</summary>
        private static PlayerControlledShip Choose(object rule, StatMod mod)
        {
            try
            {
                if (FleetSession.ShipCount < 2) return null;
                PlayerControlledShip ship = ShipOf(mod);
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
            PlayerControlledShip ship = Changing.Count > 0 ? ShipOfStats(Changing[Changing.Count - 1]) : null;
            if (ship != null || mod == null || mod.Mod == null) return ship;
            ship = ShipOfSource(mod.Mod.InformationSource);
            return ship != null ? ship : ShipOfSource(mod.Mod.Source);
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
    }
}
