using CG.Ship.Shield;
using CG.Space;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Shields on a ship that isn't at the world origin. The game creates a ship's four shield segments at the origin and
    /// keeps that world position when it attaches them to the ship: right only for the one ship of a normal game, which
    /// always starts at the origin. With two ships (one 80 m to the side in co-op, both 1.8 km out in PvP) every ship's
    /// shields hung that far away and protected nothing. The segments are modelled in the ship's own space, so attaching
    /// one puts it at the ship's origin. Runs on every client (clients attach them through the same call).
    /// </summary>
    internal static class ShipShields
    {
        public const string Feature = FleetShips.Feature;

        [GamePatch(Feature, "CG.Space.OrbitObject", "SetSpatialParent", PatchKind.Postfix)]
        private static void SpatialParentPostfix(OrbitObject __instance, OrbitObject newParent)
        {
            if (!(__instance is ShieldDamagable) || !(newParent is PlayerControlledShip) || __instance.transform.parent != newParent.transform) return;
            if (__instance.transform.localPosition.sqrMagnitude < 0.01f && __instance.transform.localRotation == Quaternion.identity) return;
            __instance.transform.localPosition = Vector3.zero;
            __instance.transform.localRotation = Quaternion.identity;
            Log.Verbose("A shield segment of " + FleetShips.Describe((PlayerControlledShip)newParent) + " is back on its ship.");
        }
    }
}
