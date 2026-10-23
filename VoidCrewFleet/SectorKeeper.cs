using System;
using System.Collections.Generic;
using System.Reflection;
using CG.Space;

namespace VoidCrewFleet
{
    /// <summary>
    /// A session has one active sector. When the ships jump out of it, the game takes its one player ship out of the
    /// sector and the host then destroys everything else in it: with two ships, the second would be destroyed. Here every
    /// player ship is taken out before a sector unloads, and put into the new sector when it is entered. The sector's play
    /// area (which pulls a ship that strays too far back towards the centre) holds every ship, not only "the player ship".
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

        private static bool _pullingShips;
        private static readonly object[] PullArguments = new object[1];

        /// <summary>
        /// Each physics step every client pulls "the player ship" back into the sector's play area when it strays outside. With
        /// several ships each client pulled only the ship it looks at, so a ship could fly off for good while the client that
        /// runs its movement (the host, or its pilot) looked at another. The game's own pull now runs for every ship in the
        /// sector on every client, each in turn as "the player ship".
        /// </summary>
        [GamePatch(Feature, "PlayableAreaController", "CheckPlayableAreaPlayership", PatchKind.Prefix, Optional = true)]
        private static bool PlayAreaPrefix(PlayableAreaController __instance, PlayableArea area, MethodBase __originalMethod)
        {
            if (_pullingShips || !FleetShips.Active) return true;
            _pullingShips = true;
            try
            {
                IList<PlayerControlledShip> ships = FleetShips.All;
                for (int i = 0; i < ships.Count; i++)
                {
                    PlayerControlledShip ship = ships[i];
                    // A ship still on its way through the void (a moment behind the lead ship) isn't in the sector yet.
                    if (ship == null || VoidJumpLink.IsTravelling(ship)) continue;
                    ShipRedirect.BeginOverride(ship);
                    try
                    {
                        PullArguments[0] = area;
                        __originalMethod.Invoke(__instance, PullArguments);
                    }
                    catch (Exception e)
                    {
                        Log.WarnOnce("play area pull", "Pulling a ship back into the play area failed: " + e.Message);
                    }
                    finally
                    {
                        PullArguments[0] = null;
                        ShipRedirect.EndOverride();
                    }
                }
            }
            finally
            {
                _pullingShips = false;
            }
            return false;
        }
    }
}
