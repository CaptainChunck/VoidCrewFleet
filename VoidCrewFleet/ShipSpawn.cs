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
    /// Puts the second player ship into the world. The host spawns ship 1 as the game does (moved to its side), then ship 2
    /// through the same ShipLoadout.InitializeShip the game uses, so ship 2 is a room object cached for late joiners like
    /// ship 1. Co-op ships start side by side; PvP ships start on opposite sides facing each other. The host then
    /// publishes which ship is which and who crews which (FleetShips).
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
            FleetShips.Register(__instance);
        }

        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "OnDestroy", PatchKind.Prefix)]
        private static void ShipDestroyPrefix(PlayerControlledShip __instance)
        {
            FleetShips.Unregister(__instance);
        }

        // ---------------------------------------------------------------- one physics scene per ship

        /// <summary>
        /// Every player ship builds its interior physics scene named after its GameObject, and every player ship is
        /// called "PlayerShip", so a second ship's scene would clash with the first one. Give the scene a unique name.
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

        [GamePatch(Feature, "Gameplay.SpacePlatforms.MovingSpacePlatform", "SetUpSimulationScene", PatchKind.Postfix)]
        private static void SimulationScenePostfix(MovingSpacePlatform __instance, string __state)
        {
            if (__state != null && __instance != null) __instance.gameObject.name = __state;
        }

        // ---------------------------------------------------------------- spawning

        /// <summary>Where the two ships start, centred on where the game puts its one ship.</summary>
        private static void StartPositions(FleetPlan plan, out Vector3 a, out Quaternion ra, out Vector3 b, out Quaternion rb)
        {
            if (plan.Mode == FleetMode.PvP)
            {
                float half = Settings.PvpStartDistance.Value * 0.5f;
                a = new Vector3(0f, 0f, -half);
                ra = Quaternion.identity;
                b = new Vector3(0f, 0f, half);
                rb = Quaternion.Euler(0f, 180f, 0f);
                return;
            }
            float side = Settings.ShipSpacing.Value * 0.5f;
            a = new Vector3(-side, 0f, 0f);
            ra = Quaternion.identity;
            b = new Vector3(side, 0f, 0f);
            rb = Quaternion.identity;
        }

        [GamePatch(Feature, "GameSession", "LoadShip", PatchKind.Prefix)]
        private static void LoadShipPrefix()
        {
            _spawningShipA = false;
            try
            {
                FleetPlan plan = FleetSession.Current;
                if (!PhotonNetwork.IsMasterClient || plan == null || plan.Mode == FleetMode.OneShip || plan.ShipBLoadout == null) return;
                Vector3 b;
                Quaternion rb;
                StartPositions(plan, out _positionA, out _rotationA, out b, out rb);
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

        /// <summary>Host: right after ship 1, spawn ship 2 and say which is which and who crews which.</summary>
        [GamePatch(Feature, "GameSession", "LoadShip", PatchKind.Postfix)]
        private static void LoadShipPostfix(PlayerControlledShip __result)
        {
            _spawningShipA = false;
            if (!PhotonNetwork.IsMasterClient) return;
            FleetPlan plan = FleetSession.Current;
            if (plan == null || plan.Mode == FleetMode.OneShip || plan.ShipBLoadout == null)
            {
                FleetShips.ClearRoom();
                return;
            }
            if (__result == null)
            {
                Log.Warn("Ship 1 did not spawn, so ship 2 is not spawned either.");
                return;
            }
            try
            {
                Vector3 a, b;
                Quaternion ra, rb;
                StartPositions(plan, out a, out ra, out b, out rb);
                PlayerControlledShip shipB = plan.ShipBLoadout.InitializeShip(b, rb);
                if (shipB == null)
                {
                    Notice.ShowAll("Ship 2 could not be spawned; everyone plays on ship 1.");
                    return;
                }
                // Teams confirmed in the hub stay: nobody waits on a team screen, everyone spawns aboard their team's ship.
                bool hubTeams = plan.Mode == FleetMode.PvP && PvpTeams.Locked;
                if (plan.Mode == FleetMode.PvP) PvpTeams.Reset(hubTeams);
                FleetShips.PublishShips(__result, shipB);
                Dictionary<int, int> crews = InitialCrews(plan, hubTeams);
                FleetShips.PublishCrew(crews);
                if (hubTeams)
                    Notice.ShowAll("Fleet Battle: " + PvpTeams.TeamName(0) + " " + crews.Count(p => p.Value == 0) + " against " + PvpTeams.TeamName(1) + " " +
                                   crews.Count(p => p.Value == 1) + ". Destroy the enemy ship to win!");
                Log.Info("Ship 2 is in the world, " + Vector3.Distance(a, b).ToString("0") + " m from ship 1 (" + plan + ").");
            }
            catch (Exception e)
            {
                Log.Failure("Spawning ship 2", e);
                Notice.ShowAll("Ship 2 could not be spawned; everyone plays on ship 1.");
            }
        }

        /// <summary>
        /// The starting crews: players split as evenly as possible, alternating in join order. In PvP the teams from the
        /// hub (anyone without one joins the smaller team); without them nobody is on a team yet, and the players choose
        /// on the team screen before anyone spawns.
        /// </summary>
        private static Dictionary<int, int> InitialCrews(FleetPlan plan, bool hubTeams)
        {
            var crews = new Dictionary<int, int>();
            if (plan.Mode == FleetMode.PvP)
            {
                if (!hubTeams) return crews;
                Dictionary<int, int> picked = FleetShips.CrewMap();
                foreach (Photon.Realtime.Player player in PhotonNetwork.PlayerList.OrderBy(p => p.ActorNumber))
                {
                    int team;
                    if (picked.TryGetValue(player.ActorNumber, out team) && (team == 0 || team == 1)) crews[player.ActorNumber] = team;
                }
                foreach (Photon.Realtime.Player player in PhotonNetwork.PlayerList.OrderBy(p => p.ActorNumber))
                {
                    if (crews.ContainsKey(player.ActorNumber)) continue;
                    crews[player.ActorNumber] = crews.Count(p => p.Value == 1) < crews.Count(p => p.Value == 0) ? 1 : 0;
                }
                return crews;
            }
            int slot = 0;
            foreach (Photon.Realtime.Player player in PhotonNetwork.PlayerList.OrderBy(p => p.ActorNumber))
            {
                crews[player.ActorNumber] = slot;
                slot = 1 - slot;
            }
            return crews;
        }

        /// <summary>
        /// Host, a few times a second during a two-ship mission: everyone in the room has a ship. Players who joined
        /// late go to the ship with the smaller crew (in PvP, the smaller team, once the teams are confirmed: before that
        /// the players choose); players who left are taken off the list.
        /// </summary>
        public static void KeepCrewsUpToDate()
        {
            if (!PhotonNetwork.IsMasterClient || !PhotonNetwork.InRoom || !FleetShips.Active) return;
            FleetPlan plan = FleetSession.Current;
            if (plan == null || plan.Mode == FleetMode.OneShip) return;
            bool pvp = plan.Mode == FleetMode.PvP;
            bool assign = !pvp || PvpTeams.Locked;
            Dictionary<int, int> crews = FleetShips.CrewMap();
            bool changed = false;
            var present = new HashSet<int>();
            foreach (Photon.Realtime.Player player in PhotonNetwork.PlayerList)
            {
                present.Add(player.ActorNumber);
                if (!assign || crews.ContainsKey(player.ActorNumber)) continue;
                int countA = crews.Count(p => p.Value == 0 && InRoom(p.Key));
                int countB = crews.Count(p => p.Value == 1 && InRoom(p.Key));
                crews[player.ActorNumber] = countB < countA ? 1 : 0;
                changed = true;
                string name = string.IsNullOrEmpty(player.NickName) ? "Player " + player.ActorNumber : player.NickName;
                if (pvp) Notice.ShowAll(name + " joins the " + PvpTeams.TeamName(crews[player.ActorNumber]) + " team.");
                else Log.Info(name + " joins the crew of ship " + (crews[player.ActorNumber] + 1) + ".");
            }
            foreach (int actor in crews.Keys.ToList())
            {
                if (present.Contains(actor)) continue;
                crews.Remove(actor);
                changed = true;
            }
            if (changed) FleetShips.PublishCrew(crews);
        }

        private static bool InRoom(int actor)
        {
            return PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.GetPlayer(actor) != null;
        }
    }
}
