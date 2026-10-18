using System;
using CG.Space;

namespace VoidCrewFleet
{
    /// <summary>
    /// A session has one active sector. When the ships jump out of it, the game takes its one player ship out of the
    /// sector and the host then destroys everything else in it: with two ships, the second would be destroyed. Here every
    /// player ship is taken out before a sector unloads, and put into the new sector when it is entered.
    /// </summary>
    internal static class SectorKeeper
    {
        public const string Feature = FleetShips.Feature;

        [GamePatch(Feature, "GameSessionSector", "Unload", PatchKind.Prefix)]
        private static void UnloadPrefix(GameSessionSector __instance)
        {
            try
            {
                if (FleetShips.All.Count < 2) return;
                foreach (PlayerControlledShip ship in FleetShips.All)
                {
                    if (ship != null && __instance.OrbitObjects.Contains(ship)) __instance.RemoveOrbitObject(ship);
                }
            }
            catch (Exception e)
            {
                Log.Failure("Keeping the player ships out of an unloading sector", e);
            }
        }

        /// <summary>Both ships belong to the sector the session enters (the jumping ship adds itself; this adds the other).</summary>
        [GamePatch(Feature, "GameSessionSectorManager", "EnterSectorInternal", PatchKind.Postfix)]
        private static void EnterSectorPostfix()
        {
            try
            {
                GameSessionSector active = GameSessionManager.ActiveSector;
                if (active == null || FleetShips.All.Count < 2) return;
                foreach (PlayerControlledShip ship in FleetShips.All)
                {
                    if (ship == null || ship.StartedDeath || ship.Sector == active) continue;
                    // A ship still on its way (mid-jump) joins when its own jump arrives.
                    if (VoidJumpLink.IsTravelling(ship)) continue;
                    active.AddOrbitObject(ship);
                }
            }
            catch (Exception e)
            {
                Log.Failure("Adding the player ships to the new sector", e);
            }
        }
    }
}
