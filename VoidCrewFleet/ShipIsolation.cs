using System;
using CG.Client.Ship.Hull;
using CG.Ship.Tracking;
using CG.Space;
using Gameplay.Power;

namespace VoidCrewFleet
{
    /// <summary>
    /// Keeps what happens on one ship on that ship. The game raises several ship events without saying which ship they
    /// come from; here the code raising them says which ship it is (ShipEvents), and each ship's own listeners ignore the
    /// other ship's events:
    /// - a power breaker tripping, and enemies starting to track the ship, flash only that ship's lights;
    /// - a ship's "danger" state (enemies around it) is worked out for that ship, and only its own lights follow it.
    /// </summary>
    internal static class ShipIsolation
    {
        public const string Feature = "Ships kept apart";

        // ---------------------------------------------------------------- who raises the event

        [GamePatch(Feature, "Gameplay.Power.ProtectedPowerSystem", "BreakNextBreaker", PatchKind.Prefix)]
        [GamePatch(Feature, "Gameplay.Power.ProtectedPowerSystem", "OnPhotonSerializeView", PatchKind.Prefix)]
        private static void PowerSystemPrefix(ProtectedPowerSystem __instance)
        {
            ShipEvents.BeginFor(__instance);
        }

        [GamePatch(Feature, "CG.Ship.Tracking.TrackingComponent", "HandleReceivingTrackersList", PatchKind.Prefix)]
        private static void TrackingPrefix(TrackingComponent __instance)
        {
            ShipEvents.BeginFor(__instance);
        }

        [GamePatch(Feature, "Gameplay.Power.ProtectedPowerSystem", "BreakNextBreaker", PatchKind.Finalizer)]
        [GamePatch(Feature, "Gameplay.Power.ProtectedPowerSystem", "OnPhotonSerializeView", PatchKind.Finalizer)]
        [GamePatch(Feature, "CG.Ship.Tracking.TrackingComponent", "HandleReceivingTrackersList", PatchKind.Finalizer)]
        private static void EventFinalizer()
        {
            ShipEvents.End();
        }

        /// <summary>
        /// Each ship works out its own danger state ("enemies are after this ship"): while it does, "the player ship" is
        /// that ship, and the state it announces belongs to it.
        /// </summary>
        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "UpdateDangerState", PatchKind.Prefix)]
        private static void DangerStatePrefix(PlayerControlledShip __instance)
        {
            bool two = FleetShips.Active;
            ShipRedirect.BeginOverride(two ? __instance : null);
            ShipEvents.Begin(two ? __instance : null);
        }

        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "UpdateDangerState", PatchKind.Finalizer)]
        private static void DangerStateFinalizer()
        {
            ShipEvents.End();
            ShipRedirect.EndOverride();
        }

        // ---------------------------------------------------------------- each ship's own homunculus

        private static int _dispensingSlot = -1;

        /// <summary>A ship's homunculus dispenser spawns that ship's homunculus (the game takes ship 1's loadout's).</summary>
        [GamePatch(Feature, "CG.Ship.Hull.HomunculusAndBiomassSocket", "DispenseHomunculusNow", PatchKind.Prefix)]
        private static void DispensePrefix(UnityEngine.Component __instance)
        {
            _dispensingSlot = FleetShips.Active ? FleetShips.Slot(FleetShips.OwnerOf(__instance)) : -1;
        }

        [GamePatch(Feature, "CG.Ship.Hull.HomunculusAndBiomassSocket", "DispenseHomunculusNow", PatchKind.Finalizer)]
        private static void DispenseFinalizer()
        {
            _dispensingSlot = -1;
        }

        [GamePatch(Feature, "GameSession", "get_Homunculus", PatchKind.Postfix)]
        private static void HomunculusPostfix(GameSession __instance, ref ResourceAssets.CarryableRef __result)
        {
            if (_dispensingSlot != 1) return;
            FleetPlan plan = FleetSession.Current;
            if (plan == null) return;
            // Ship 2: its own loadout's homunculus, else the mission's own override, else the default one. The session copies
            // ship 1's loadout homunculus into its override, which isn't the mission's: ship 2 doesn't take that one.
            if (!plan.ShipBHomunculus.IsNull) __result = new ResourceAssets.CarryableRef(plan.ShipBHomunculus);
            else if (!__instance.HomunculusOverride.IsNull && !IsShipAHomunculus(__instance)) __result = new ResourceAssets.CarryableRef(__instance.HomunculusOverride);
            else __result = DataTable<DefaultAssetTable>.Instance.DefaultHomunculus;
        }

        private static bool IsShipAHomunculus(GameSession session)
        {
            try
            {
                var loadout = session.ToLoadShipData;
                return loadout != null && loadout.HomunculusOverride != null && !loadout.HomunculusOverride.IsNull &&
                       loadout.HomunculusOverride.AssetGuid == session.HomunculusOverride;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- each ship's lights

        [GamePatch(Feature, "CG.Client.Ship.Hull.LightSourceController", "OnPowerBreakerFlipped", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Client.Ship.Hull.LightSourceController", "OnPlayerShipStartedBeingTracked", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Client.Ship.Hull.LightSourceController", "OnPlayerShipDangerState", PatchKind.Prefix)]
        private static bool LightsPrefix(LightSourceController __instance)
        {
            try
            {
                return !ShipEvents.ForeignTo(__instance);
            }
            catch (Exception e)
            {
                Log.Failure("Checking which ship a light event belongs to", e);
                return true;
            }
        }
    }
}
