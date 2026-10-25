using System;
using System.Collections.Generic;
using System.Reflection;
using CG.Space;

namespace VoidCrewFleet
{
    /// <summary>
    /// A session has one active sector. When the ships jump out of it, the game takes its one player ship out of the
    /// sector and the host then destroys everything else in it: with several ships, the others would be destroyed. Here
    /// every player ship is taken out before a sector unloads, and put into the new sector when it is entered. The sector's
    /// play area (which pulls a ship that strays too far back towards the centre) holds every ship, not only "the player
    /// ship".
    /// </summary>
    internal static class SectorKeeper
    {
        public const string Feature = FleetShips.Feature;

        /// <summary>The game's own pull of "the player ship" into the play area, called directly (no reflection call per ship).</summary>
        private static readonly Action<PlayableAreaController, PlayableArea> PullShip =
            ShipRedirect.Bind<Action<PlayableAreaController, PlayableArea>>(GameMember.Method(typeof(PlayableAreaController), "CheckPlayableAreaPlayership"));

        [GamePatch(Feature, "GameSessionSector", "Unload", PatchKind.Prefix)]
        private static void UnloadPrefix(GameSessionSector __instance)
        {
            List<PlayerControlledShip> ships;
            try
            {
                if (FleetShips.All.Count < 2) return;
                ships = new List<PlayerControlledShip>(FleetShips.All);
            }
            catch (Exception e)
            {
                Log.Failure("Keeping the player ships out of an unloading sector", e);
                return;
            }
            // Each ship on its own: one that fails must not leave the others to be destroyed with the sector.
            foreach (PlayerControlledShip ship in ships)
            {
                try
                {
                    if (ship != null && __instance.OrbitObjects.Contains(ship)) __instance.RemoveOrbitObject(ship);
                }
                catch (Exception e)
                {
                    Log.Failure("Keeping a player ship out of an unloading sector", e);
                }
            }
        }

        /// <summary>Every ship belongs to the sector the session enters (the jumping ship adds itself; this adds the others).</summary>
        [GamePatch(Feature, "GameSessionSectorManager", "EnterSectorInternal", PatchKind.Postfix)]
        private static void EnterSectorPostfix()
        {
            GameSessionSector active;
            List<PlayerControlledShip> ships;
            try
            {
                active = GameSessionManager.ActiveSector;
                if (active == null || FleetShips.All.Count < 2) return;
                ships = new List<PlayerControlledShip>(FleetShips.All);
            }
            catch (Exception e)
            {
                Log.Failure("Adding the player ships to the new sector", e);
                return;
            }
            foreach (PlayerControlledShip ship in ships)
            {
                try
                {
                    if (ship == null || ship.StartedDeath || ship.Sector == active) continue;
                    // A ship still on its way (mid-jump) joins when its own jump arrives.
                    if (VoidJumpLink.IsTravelling(ship)) continue;
                    active.AddOrbitObject(ship);
                }
                catch (Exception e)
                {
                    Log.Failure("Adding a player ship to the new sector", e);
                }
            }
        }

        private static bool _pullingShips;
        private static readonly object[] PullArguments = new object[1];

        /// <summary>
        /// Each physics step every client pulls "the player ship" back into the sector's play area when it strays outside. With
        /// several ships each client pulled only the ship it looks at, so a ship could fly off for good while the client that
        /// runs its movement (the host, or its pilot) looked at another. The game's own pull now runs for every ship in the
        /// sector on every client, each in turn as "the player ship". A ship inside the area is skipped at once (the game's pull
        /// does nothing for it), so the usual step costs a distance check per ship.
        /// </summary>
        [GamePatch(Feature, "PlayableAreaController", "CheckPlayableAreaPlayership", PatchKind.Prefix, Optional = true)]
        private static bool PlayAreaPrefix(PlayableAreaController __instance, PlayableArea area, MethodBase __originalMethod)
        {
            if (_pullingShips || !FleetShips.Active || area == null) return true;
            _pullingShips = true;
            try
            {
                List<PlayerControlledShip> ships = FleetShips.All;
                for (int i = 0; i < ships.Count; i++)
                {
                    PlayerControlledShip ship = ships[i];
                    try
                    {
                        if (ship == null) continue;
                        // Inside the area: nothing to pull (the game's own test).
                        if ((area.Center - ship.transform.position).magnitude < area.Radius) continue;
                        // A ship still on its way through the void (a moment behind the lead ship) isn't in the sector yet.
                        if (VoidJumpLink.IsTravelling(ship)) continue;
                        Pull(__instance, area, ship, __originalMethod);
                    }
                    catch (Exception e)
                    {
                        Log.WarnOnce("play area pull", "Pulling a ship back into the play area failed: " + e.Message);
                    }
                }
            }
            finally
            {
                _pullingShips = false;
            }
            return false;
        }

        /// <summary>The game's pull for one ship, as "the player ship".</summary>
        private static void Pull(PlayableAreaController controller, PlayableArea area, PlayerControlledShip ship, MethodBase original)
        {
            ShipRedirect.BeginOverride(ship);
            try
            {
                if (PullShip != null)
                {
                    PullShip(controller, area);
                    return;
                }
                // The method has changed so it can't be called directly: through reflection then.
                PullArguments[0] = area;
                original.Invoke(controller, PullArguments);
            }
            finally
            {
                PullArguments[0] = null;
                ShipRedirect.EndOverride();
            }
        }
    }
}
