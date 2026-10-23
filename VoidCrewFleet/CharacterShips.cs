using System;
using System.Collections.Generic;
using CG.Game.Player;
using CG.Space;
using Gameplay.SpacePlatforms;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Characters and the ship they are with. Inside a ship's rooms the game already knows which ship that is; two checks
    /// still assume one ship and are fixed here when there are two:
    /// - floating in a ship's inertia zone (the bubble that carries EVA players along) attached you to "the" player ship,
    ///   whichever ship's zone it was; now it is the ship whose zone you are in;
    /// - "is the local player on the player ship" (oxygen alarm, being left behind at a void jump, interior sound and
    ///   culling) now means aboard either player ship.
    /// </summary>
    internal static class CharacterShips
    {
        public const string Feature = FleetShips.Feature;

        private static readonly Collider[] Hits = new Collider[16];

        [GamePatch(Feature, "CharacterAtmosphericDataTracker", "CheckForInertiaZones", PatchKind.Prefix)]
        private static bool InertiaZonePrefix(CharacterAtmosphericDataTracker __instance, Vector3? overridePosition, int ___gravityZoneMask, ref bool __result)
        {
            if (!FleetShips.Active) return true;
            try
            {
                Vector3 point = overridePosition ?? __instance.CollectionPoint.transform.position;
                int count = Physics.OverlapSphereNonAlloc(point, __instance.InInertiaZone ? 1f : 0.01f, Hits, ___gravityZoneMask);
                if (count == 0)
                {
                    __result = false;
                    return false;
                }
                MovingSpacePlatform best = null;
                float bestDistance = float.MaxValue;
                for (int i = 0; i < count; i++)
                {
                    Collider hit = Hits[i];
                    if (hit == null) continue;
                    MovingPlatformInertiaZone zone = hit.GetComponentInParent<MovingPlatformInertiaZone>();
                    MovingSpacePlatform platform = zone != null && zone.ConnectedPlatform != null ? zone.ConnectedPlatform : hit.GetComponentInParent<MovingSpacePlatform>();
                    if (platform == null) continue;
                    float distance = (hit.ClosestPoint(point) - point).sqrMagnitude;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = platform;
                    }
                }
                if (best == null)
                {
                    PlayerControlledShip view = FleetShips.ViewShip;
                    if (view == null) return true;
                    best = view.Platform;
                }
                __instance.InInertiaZone = true;
                __instance.ConnectedSpacePlatform = best;
                __result = true;
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Finding which ship's inertia zone a player is in", e);
                return true;
            }
        }

        /// <summary>Asked several hundred times a second: the zoned platform is compared with the ships' own (no component lookup).</summary>
        [GamePatch(Feature, "CG.Game.Player.LocalPlayer", "IsOnPlayerShip", PatchKind.Prefix)]
        private static bool OnPlayerShipPrefix(LocalPlayer __instance, ref bool __result)
        {
            if (!FleetShips.Active) return true;
            try
            {
                __result = IsShipPlatform(__instance.ZonedPlatform) && !__instance.Locomotion.AtmosphereTracker.InInertiaZone;
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking whether the local player is aboard a ship", e);
                return true;
            }
        }

        /// <summary>Whether a platform is a player ship's own (each ship's platform is on the ship's object).</summary>
        private static bool IsShipPlatform(SpacePlatform platform)
        {
            if (platform == null) return false;
            List<PlayerControlledShip> ships = FleetShips.All;
            for (int i = 0; i < ships.Count; i++)
            {
                PlayerControlledShip ship = ships[i];
                if (!ReferenceEquals(ship, null) && ReferenceEquals(ship.Platform, platform)) return true;
            }
            return false;
        }
    }
}
