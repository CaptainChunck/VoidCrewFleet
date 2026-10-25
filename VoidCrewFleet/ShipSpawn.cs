using System;
using System.Collections.Generic;
using System.Linq;
using CG.Space;
using Gameplay.SpacePlatforms;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Puts ships 2-6 into the world. The host spawns ship 1 as the game does (moved to its place in the formation), then
    /// the other ships through the same ShipLoadout.InitializeShip the game uses, so they are room objects cached for late
    /// joiners like ship 1. Co-op ships start side by side; in PvP each team's ships start side by side on its own side of
    /// the arena, facing the other team. The host then publishes which ship is which and who crews which (FleetShips).
    /// </summary>
    internal static class ShipSpawn
    {
        public const string Feature = FleetShips.Feature;

        private static bool _spawningShipA;
        private static Vector3 _positionA;
        private static Quaternion _rotationA;

        // ---------------------------------------------------------------- the ship registry

        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "Awake", PatchKind.Postfix)]
        private static void ShipAwakePostfix(PlayerControlledShip __instance)
        {
            try
            {
                FleetShips.Register(__instance);
            }
            catch (Exception e)
            {
                Log.Failure("Registering a player ship", e);
            }
        }

        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "OnDestroy", PatchKind.Prefix)]
        private static void ShipDestroyPrefix(PlayerControlledShip __instance)
        {
            try
            {
                FleetShips.Unregister(__instance);
            }
            catch (Exception e)
            {
                Log.Failure("Unregistering a player ship", e);
            }
        }

        // ---------------------------------------------------------------- one physics scene per ship

        /// <summary>
        /// Every player ship builds its interior physics scene named after its GameObject, and every player ship is
        /// called "PlayerShip", so a second ship's scene would clash with the first one. Give the scene a unique name; the
        /// ship gets its own name back afterwards (in a finalizer, so also when the game's set-up fails).
        /// </summary>
        [GamePatch(Feature, "Gameplay.SpacePlatforms.MovingSpacePlatform", "SetUpSimulationScene", PatchKind.Prefix)]
        private static void SimulationScenePrefix(MovingSpacePlatform __instance, out string __state)
        {
            __state = null;
            try
            {
                if (__instance.GetComponent<PlayerControlledShip>() == null) return;
                __state = __instance.gameObject.name;
                __instance.gameObject.name = __state + "_" + __instance.gameObject.GetInstanceID();
            }
            catch (Exception e)
            {
                Log.Failure("Naming a ship's physics scene", e);
            }
        }

        [GamePatch(Feature, "Gameplay.SpacePlatforms.MovingSpacePlatform", "SetUpSimulationScene", PatchKind.Finalizer)]
        private static void SimulationSceneFinalizer(MovingSpacePlatform __instance, string __state)
        {
            try
            {
                if (__state != null && __instance != null) __instance.gameObject.name = __state;
            }
            catch (Exception e)
            {
                Log.Failure("Naming a ship's physics scene", e);
            }
        }

        // ---------------------------------------------------------------- spawning

        /// <summary>How far apart a PvP team's own ships start (side by side).</summary>
        public static float TeamSpacing
        {
            get { return Mathf.Max(Settings.ShipSpacing.Value, 250f); }
        }

        /// <summary>A place in a line of ships side by side: the offset of ship index (0-based) of count along "right".</summary>
        public static Vector3 LineOffset(int index, int count, Vector3 right, float spacing)
        {
            return right * ((index - (count - 1) * 0.5f) * spacing);
        }

        /// <summary>Where each ship starts, by slot, centred on where the game puts its one ship.</summary>
        private static void StartPoses(FleetPlan plan, out Vector3[] positions, out Quaternion[] rotations)
        {
            int count = plan.ShipCount;
            positions = new Vector3[count];
            rotations = new Quaternion[count];
            if (plan.Mode == FleetMode.PvP)
            {
                // Each team on its side of the arena's centre, facing the other (the origin without an arena), its ships side
                // by side.
                Vector3 blue, red;
                Quaternion blueRotation, redRotation;
                PvpArena.StartPositions(Settings.PvpStartDistance.Value, out blue, out blueRotation, out red, out redRotation);
                int perTeam = Mathf.Max(1, plan.ShipsPerTeam);
                for (int slot = 0; slot < count; slot++)
                {
                    bool isBlue = slot % 2 == 0;
                    Quaternion rotation = isBlue ? blueRotation : redRotation;
                    positions[slot] = (isBlue ? blue : red) + LineOffset(slot / 2, perTeam, rotation * Vector3.right, TeamSpacing);
                    rotations[slot] = rotation;
                }
                return;
            }
            for (int slot = 0; slot < count; slot++)
            {
                positions[slot] = LineOffset(slot, count, Vector3.right, Settings.ShipSpacing.Value);
                rotations[slot] = Quaternion.identity;
            }
        }

        [GamePatch(Feature, "GameSession", "LoadShip", PatchKind.Prefix)]
        private static void LoadShipPrefix()
        {
            _spawningShipA = false;
            try
            {
                FleetPlan plan = FleetSession.Current;
                if (!PhotonNetwork.IsMasterClient || plan == null || plan.Mode == FleetMode.OneShip || plan.ShipCount < 2) return;
                Vector3[] positions;
                Quaternion[] rotations;
                StartPoses(plan, out positions, out rotations);
                _positionA = positions[0];
                _rotationA = rotations[0];
                _spawningShipA = true;
            }
            catch (Exception e)
            {
                Log.Failure("Placing ship 1", e);
            }
        }

        /// <summary>Ship 1 starts on its side instead of the centre.</summary>
        [GamePatch(Feature, "ShipLoadout", "InitializeShip", PatchKind.Prefix)]
        private static void InitializeShipPrefix(ref Vector3 Position, ref Quaternion Rotation)
        {
            if (!_spawningShipA) return;
            _spawningShipA = false;
            Position = _positionA;
            Rotation = _rotationA;
        }

        /// <summary>Host: right after ship 1, spawn ships 2-6 and say which is which and who crews which.</summary>
        [GamePatch(Feature, "GameSession", "LoadShip", PatchKind.Postfix)]
        private static void LoadShipPostfix(GameSession __instance, PlayerControlledShip __result)
        {
            _spawningShipA = false;
            FleetPlan plan;
            try
            {
                if (!PhotonNetwork.IsMasterClient) return;
                plan = FleetSession.Current;
                if (plan == null || plan.Mode == FleetMode.OneShip || plan.ShipCount < 2)
                {
                    FleetShips.ClearRoom();
                    return;
                }
                if (__result == null)
                {
                    Log.Warn("Ship 1 did not spawn, so the other ships are not spawned either.");
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Failure("Reading the fleet plan", e);
                return;
            }
            try
            {
                Vector3[] positions;
                Quaternion[] rotations;
                StartPoses(plan, out positions, out rotations);
                var ships = new PlayerControlledShip[plan.ShipCount];
                ships[0] = __result;
                for (int slot = 1; slot < ships.Length; slot++)
                {
                    ShipPlan ship = plan.Ships[slot];
                    // A copy of ship 1 spawns from the very loadout ship 1 was just spawned from (Ship Customizer matches
                    // each spawned ship to its loadout by reference).
                    ShipLoadout loadout = ship.Built ?? __instance.ToLoadShipData;
                    try
                    {
                        ships[slot] = loadout != null ? loadout.InitializeShip(positions[slot], rotations[slot]) : null;
                    }
                    catch (Exception e)
                    {
                        Log.Failure("Spawning " + FleetSlots.Name(slot, plan.Mode == FleetMode.PvP), e);
                    }
                    if (ships[slot] == null)
                        Notice.ShowAll(FleetSlots.Name(slot, plan.Mode == FleetMode.PvP) + " could not be spawned; its crew plays on another ship with room (or waits for a free place).");
                }
                // Teams confirmed in the hub stay: nobody waits on a team screen, everyone spawns aboard their team's ship.
                bool pvp = plan.Mode == FleetMode.PvP;
                bool hubTeams = pvp && PvpTeams.Locked;
                if (pvp) PvpTeams.Reset(hubTeams);
                FleetShips.PublishShips(ships);
                var alive = new List<int>();
                for (int slot = 0; slot < ships.Length; slot++)
                {
                    if (ships[slot] != null) alive.Add(slot);
                }
                Dictionary<int, CrewEntry> crews = pvp && !hubTeams
                    ? new Dictionary<int, CrewEntry>()
                    : CrewRules.PlaceEveryone(FleetShips.Crews(), PlayerActors(), alive, pvp);
                FleetShips.PublishCrew(crews);
                if (hubTeams)
                    Notice.ShowAll("Fleet Battle: " + PvpTeams.TeamName(0) + " " + crews.Values.Count(e => e.Team == 0) + " against " + PvpTeams.TeamName(1) + " " +
                                   crews.Values.Count(e => e.Team == 1) + ". Destroy every enemy ship to win!");
                Log.Info(alive.Count + " ships are in the world (" + plan + "); crews " + string.Join(", ", crews.Values.Select(e => e.ToString()).ToArray()) + ".");
            }
            catch (Exception e)
            {
                Log.Failure("Spawning the fleet", e);
                Notice.ShowAll("The other ships could not be spawned; everyone plays on ship 1.");
            }
        }

        /// <summary>The actor numbers of everyone in the room, in join order.</summary>
        public static List<int> PlayerActors()
        {
            return PhotonNetwork.PlayerList.Select(p => p.ActorNumber).OrderBy(a => a).ToList();
        }

    }
}
