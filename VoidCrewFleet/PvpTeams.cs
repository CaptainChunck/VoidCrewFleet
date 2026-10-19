using System;
using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using ResourceAssets;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Who crews which ship, picked in the hub on the lobby screen (PvpTeamScreen), and the PvP teams that come with it. A
    /// player's ship is decided there for the whole mission: they always spawn on it, and if it is destroyed they aren't
    /// re-cloned again (ShipDeath).
    /// - Co-op with several ships: the screen opens for everyone; players pick the ship they crew, crews stay even. The host
    ///   may move anyone and auto-balance, then confirms the crews; the mission starts the usual way once they are.
    /// - Fleet Battle: each team has one to three ships; a player picks a ship, and with it the team (BLUE has ships 1, 3
    ///   and 5, RED ships 2, 4 and 6). Teams must be even (with an odd number of players one team has one extra), crews stay
    ///   even and every ship needs a crew. The host may move anyone, then confirms. The host starts the match by sitting in
    ///   the ready room, and everyone spawns aboard their ship. Without teams from the hub (an older host flow), the team
    ///   screen shows at the start of the match instead and holds every spawn until confirmed. Players who join later go to
    ///   the smaller team.
    /// Players who join the hub after the confirmation go to the smallest crew (of the smaller team).
    /// </summary>
    internal static class PvpTeams
    {
        public const string Feature = "PvP teams";

        private const string KeyLocked = "VCF_TeamsLocked";

        /// <summary>
        /// The host's own latest confirm or reopen, for a few seconds: the room's property only changes here once the server
        /// has sent it back, and the host acts on the lock right after changing it (late joiners, ship requests).
        /// </summary>
        private static bool _hostLocked;
        private static float _hostLockedAt = -100f;
        private static string _hostLockedRoom;

        /// <summary>True once the host has confirmed the teams (Fleet Battle) or the crews (co-op) for this mission.</summary>
        public static bool Locked
        {
            get
            {
                if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom == null) return false;
                if (PhotonNetwork.IsMasterClient && Time.unscaledTime - _hostLockedAt < 3f && PhotonNetwork.CurrentRoom.Name == _hostLockedRoom) return _hostLocked;
                object value;
                return PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(KeyLocked, out value) && value is bool && (bool)value;
            }
        }

        /// <summary>Host: confirm (or reopen) the picks: here at once, in the room after a round trip.</summary>
        private static void SetLocked(bool locked)
        {
            _hostLocked = locked;
            _hostLockedAt = Time.unscaledTime;
            _hostLockedRoom = PhotonNetwork.CurrentRoom.Name;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyLocked, locked ? (object)true : null } });
        }

        /// <summary>While true (a PvP match before the teams are confirmed) the host holds every spawn request.</summary>
        public static bool Choosing
        {
            get { return FleetSession.IsPvP && !Locked && !GameSessionManager.InHub; }
        }

        /// <summary>The hub plans a mission with several ships (FLEET BATTLE, or co-op with two or three ships).</summary>
        public static bool HubFleetPlanned
        {
            get
            {
                try
                {
                    return GameSessionManager.InHub && HubFleet.HostHasFleet && HubFleet.PlannedMode != FleetMode.OneShip;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>The hub plans a fleet mission whose crews (and teams) aren't confirmed yet: everyone picks a ship.</summary>
        public static bool HubChoosing
        {
            get { return HubFleetPlanned && !Locked; }
        }

        /// <summary>Ships are being picked, in the hub (teams or co-op crews) or at the start of a match (teams).</summary>
        public static bool PickingTeams
        {
            get { return Choosing || HubChoosing; }
        }

        /// <summary>How many ships players pick from: the hub's planned ships, or the running mission's.</summary>
        public static int ShipCount
        {
            get { return GameSessionManager.InHub ? HubFleet.PlannedShips : FleetSession.ShipCount; }
        }

        public static string TeamName(int team)
        {
            return team == 0 ? "BLUE" : (team == 1 ? "RED" : "NO TEAM");
        }

        /// <summary>A ship's loadout as "Frigate - Rookie" (no "METEM"): in the hub as chosen now, in a mission as it spawned.</summary>
        public static string ShipName(int slot)
        {
            try
            {
                if (GameSessionManager.InHub)
                {
                    if (HubFleet.CopiesShip1(slot)) return "as ship 1 (" + HubFleet.DescribeLoadout(HubFleet.ShipADef).Replace("METEM ", "") + ")";
                    return HubFleet.ShipDescription(slot).Replace("METEM ", "");
                }
                FleetPlan plan = FleetSession.Current;
                ShipPlan ship = plan != null ? plan.Ship(slot) : null;
                if (ship != null && !string.IsNullOrEmpty(ship.Name)) return ship.Name.Replace("METEM ", "");
                GameSession session = GameSessionManager.ActiveSession;
                return HubFleet.DescribeLoadout(HubFleet.LoadoutDef(session != null ? session.ToLoadShipGuid : GUIDUnion.Empty())).Replace("METEM ", "");
            }
            catch
            {
                return "ship " + (slot + 1);
            }
        }

        // ---------------------------------------------------------------- requests

        /// <summary>Asks the host to crew a ship (in Fleet Battle that picks the team too).</summary>
        public static void Join(int slot)
        {
            Net.ToHost(Msg.CrewRequest, slot);
        }

        /// <summary>Host only: put a player on a ship (or take them off every ship with -1), whatever they picked.</summary>
        public static void Move(int actor, int slot)
        {
            Net.ToHost(Msg.CrewMove, actor, slot);
        }

        private static string ShipLabel(int slot)
        {
            return FleetSlots.Name(slot, FleetApi.IsTeamMatch);
        }

        public static void Listen()
        {
            Net.On(Msg.CrewRequest, (sender, data) =>
            {
                if (!PhotonNetwork.IsMasterClient || !PickingTeams) return;
                int slot = Net.Get(data, 0, -1);
                Dictionary<int, CrewEntry> crews = FleetShips.Crews();
                CrewEntry current;
                if (crews.TryGetValue(sender, out current) && current.Slot == slot && current.State == CrewState.Playing) return;
                bool teamMatch = FleetApi.IsTeamMatch;
                string refusal = CrewRules.CanJoin(crews, ShipSpawn.PlayerActors(), sender, slot, ShipCount, teamMatch, ShipLabel, TeamName);
                if (refusal != null)
                {
                    Net.ToActor(sender, Msg.Notice, refusal);
                    return;
                }
                crews[sender] = CrewEntry.Play(sender, CrewRules.TeamOf(slot, teamMatch), slot);
                FleetShips.PublishCrew(crews);
            });
            Net.On(Msg.CrewMove, (sender, data) =>
            {
                if (!PhotonNetwork.IsMasterClient || !PickingTeams) return;
                if (PhotonNetwork.LocalPlayer == null || sender != PhotonNetwork.LocalPlayer.ActorNumber) return;
                int actor = Net.Get(data, 0, -1);
                int slot = Net.Get(data, 1, -1);
                Dictionary<int, CrewEntry> crews = FleetShips.Crews();
                if (slot >= 0 && slot < ShipCount) crews[actor] = CrewEntry.Play(actor, CrewRules.TeamOf(slot, FleetApi.IsTeamMatch), slot);
                else crews.Remove(actor);
                FleetShips.PublishCrew(crews);
            });
        }

        /// <summary>Host only: everyone on a ship, then even teams (Fleet Battle) and even crews; players keep their pick where they can.</summary>
        public static void AutoBalance()
        {
            if (!PhotonNetwork.IsMasterClient || !PickingTeams) return;
            FleetShips.PublishCrew(CrewRules.Balance(FleetShips.Crews(), ShipSpawn.PlayerActors(), ShipCount, FleetApi.IsTeamMatch));
        }

        /// <summary>Null when the teams can be confirmed now; otherwise why not.</summary>
        public static string CheckTeams()
        {
            return CrewRules.CheckTeams(FleetShips.Crews(), ShipSpawn.PlayerActors(), ShipCount, ShipLabel);
        }

        /// <summary>Co-op: null when the crews can be confirmed now; otherwise why not.</summary>
        public static string CheckCrews()
        {
            return CrewRules.CheckCrews(FleetShips.Crews(), ShipSpawn.PlayerActors(), ShipCount, ShipLabel);
        }

        /// <summary>Null when the picks can be confirmed now (teams or co-op crews); otherwise why not.</summary>
        public static string CheckPicks()
        {
            return FleetApi.IsTeamMatch ? CheckTeams() : CheckCrews();
        }

        /// <summary>Host only: lock the teams (or co-op crews), so everyone spawns aboard their ship.</summary>
        public static void Confirm()
        {
            if (!PhotonNetwork.IsMasterClient || !PickingTeams) return;
            bool inHub = HubChoosing;
            bool teams = FleetApi.IsTeamMatch;
            string why = CheckPicks();
            if (why != null)
            {
                Notice.Show("Can't confirm yet: " + why);
                return;
            }
            Dictionary<int, CrewEntry> crews = FleetShips.Crews();
            FleetShips.PublishCrew(crews);
            SetLocked(true);
            if (!teams)
            {
                List<int> actors = ShipSpawn.PlayerActors();
                Notice.ShowAll("Crews confirmed: " + string.Join(", ", Enumerable.Range(0, ShipCount).Select(s =>
                               {
                                   int crew = CrewRules.CrewOf(crews, actors, s);
                                   return crew + (crew == 1 ? " player on " : " players on ") + ShipLabel(s);
                               }).ToArray()) + ". Set up your ship at the loadout terminal, then sit in the ready room to start.");
                return;
            }
            int blue = crews.Values.Count(e => e.Team == 0);
            int red = crews.Values.Count(e => e.Team == 1);
            Notice.ShowAll("Teams confirmed: " + TeamName(0) + " " + blue + " against " + TeamName(1) + " " + red + ". " +
                           (inHub
                               ? "Set up your ship at the loadout terminal (the FLEET panel lists every ship). The host starts the match by sitting in the ready room."
                               : "Destroy every enemy ship to win!"));
        }

        /// <summary>Self-test only: lock the teams as they are (one player can't crew every ship).</summary>
        public static void ForceConfirm()
        {
            if (!PhotonNetwork.IsMasterClient || !PickingTeams) return;
            FleetShips.PublishCrew(FleetShips.Crews());
            SetLocked(true);
            Log.Info("[autotest] Teams locked without the checks: " + (CheckTeams() ?? "they would have passed") + ".");
        }

        /// <summary>
        /// Host, a few times a second: in the hub, once the teams (or co-op crews) are confirmed, a player who joins later goes
        /// to the smallest crew (of the smaller team), as in a mission, so they can set up their ship without the host
        /// reopening the picking. Not while the hub loads: the last mission's crews and lock are only cleared once it has.
        /// </summary>
        public static void KeepHubTeamsComplete()
        {
            try
            {
                if (!PhotonNetwork.IsMasterClient || !HubFleetPlanned || HubShipManager.Instance == null || !Locked) return;
                bool teams = FleetApi.IsTeamMatch;
                Dictionary<int, CrewEntry> crews = FleetShips.Crews();
                List<int> players = ShipSpawn.PlayerActors();
                var ships = Enumerable.Range(0, ShipCount).ToList();
                bool changed = false;
                foreach (Player player in PhotonNetwork.PlayerList.OrderBy(p => p.ActorNumber))
                {
                    CrewEntry entry;
                    if (crews.TryGetValue(player.ActorNumber, out entry) && entry.Slot >= 0 && entry.Slot < ShipCount) continue;
                    int slot = CrewRules.PlaceFor(crews, players, ships, teams);
                    if (slot < 0) continue;
                    crews[player.ActorNumber] = CrewEntry.Play(player.ActorNumber, CrewRules.TeamOf(slot, teams), slot);
                    changed = true;
                    Net.ToActor(player.ActorNumber, Msg.Notice, teams
                        ? "You joined the " + TeamName(slot % 2) + " team on " + FleetSlots.Name(slot, true) + " (the teams were already confirmed). Set it up at the loadout terminal."
                        : "You fly " + FleetSlots.Name(slot, false) + " (the crews were already confirmed). Set it up at the loadout terminal.");
                }
                if (changed) FleetShips.PublishCrew(crews);
            }
            catch (Exception e)
            {
                Log.Failure("Giving a late joiner a ship", e);
            }
        }

        /// <summary>
        /// Host, when the hub loads, the fleet changes (FLEET BATTLE chosen or left, another ship count) or a match starts: no
        /// result, sabotage or hack from an earlier match, and the ships are picked again (unless a match starts with the
        /// teams confirmed in the hub).
        /// </summary>
        public static void Reset(bool keepTeams = false)
        {
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            if (!keepTeams) SetLocked(false);
            PvpMatch.ResetRoom();
            PvpSabotage.ResetRoom();
            PvpHack.ResetRoom();
        }
    }
}
