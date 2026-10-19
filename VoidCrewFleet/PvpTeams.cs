using System;
using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using ResourceAssets;

namespace VoidCrewFleet
{
    /// <summary>
    /// PvP teams. Team 1 (BLUE) crews ship 1 and team 2 (RED) crews ship 2; a team is simply a crew (FleetShips' crew
    /// list). The teams are picked in the hub: when the host chooses FLEET BATTLE, everyone sees the team screen
    /// (PvpTeamScreen), picks a side, and the host may move anyone, then confirms. Teams must be even: with an odd number
    /// of players one team has one extra. The host starts the match by sitting in the ready room, and everyone spawns
    /// aboard their team's ship. Without teams from the hub (an older host flow), the team screen shows at the start of
    /// the match instead and holds every spawn until confirmed. Players who join later go to the smaller team.
    /// </summary>
    internal static class PvpTeams
    {
        public const string Feature = "PvP teams";

        private const string KeyLocked = "VCF_TeamsLocked";

        /// <summary>True once the host has confirmed the teams for this match.</summary>
        public static bool Locked
        {
            get
            {
                object value;
                return PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(KeyLocked, out value) &&
                       value is bool && (bool)value;
            }
        }

        /// <summary>While true (a PvP match before the teams are confirmed) the host holds every spawn request.</summary>
        public static bool Choosing
        {
            get { return FleetSession.IsPvP && !Locked && !GameSessionManager.InHub; }
        }

        /// <summary>The hub's mission is FLEET BATTLE and the teams aren't confirmed yet: everyone picks a team.</summary>
        public static bool HubChoosing
        {
            get
            {
                try
                {
                    return GameSessionManager.InHub && HubFleet.HostHasFleet && HubFleet.Variant == HubVariant.FleetBattle && !Locked;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Teams are being picked, in the hub or at the start of a match.</summary>
        public static bool PickingTeams
        {
            get { return Choosing || HubChoosing; }
        }

        public static int PlayerCount
        {
            get { return PhotonNetwork.PlayerList.Length; }
        }

        /// <summary>The most players a team may have: half the room, rounded up.</summary>
        public static int MaxPerTeam
        {
            get { return Math.Max(1, (PlayerCount + 1) / 2); }
        }

        public static int Count(Dictionary<int, int> teams, int team)
        {
            int count = 0;
            foreach (Player player in PhotonNetwork.PlayerList)
            {
                int value;
                if (teams.TryGetValue(player.ActorNumber, out value) && value == team) count++;
            }
            return count;
        }

        /// <summary>Every player is on a team and the teams differ by at most one.</summary>
        public static bool Balanced(Dictionary<int, int> teams, out string why)
        {
            int unassigned = PhotonNetwork.PlayerList.Count(p => !teams.ContainsKey(p.ActorNumber));
            if (unassigned > 0)
            {
                why = unassigned + (unassigned == 1 ? " player hasn't" : " players haven't") + " picked a team yet.";
                return false;
            }
            int a = Count(teams, 0);
            int b = Count(teams, 1);
            if (Math.Abs(a - b) > 1)
            {
                why = "The teams are " + a + " against " + b + "; they may differ by one at most.";
                return false;
            }
            why = null;
            return true;
        }

        public static string TeamName(int team)
        {
            return team == 0 ? "BLUE" : "RED";
        }

        /// <summary>"Ship 1: Frigate - Rookie" from the mission's loadouts.</summary>
        public static string ShipName(int team)
        {
            try
            {
                if (GameSessionManager.InHub)
                {
                    // In the hub: the loadouts chosen at the terminal (ship 2 copies ship 1 until it has its own).
                    if (team == 1 && HubFleet.ChosenShipB.IsNull) return "as ship 1 (" + HubFleet.DescribeLoadout(HubFleet.ShipADef).Replace("METEM ", "") + ")";
                    return HubFleet.DescribeLoadout(team == 0 ? HubFleet.ShipADef : HubFleet.ShipBDef).Replace("METEM ", "");
                }
                GameSession session = GameSessionManager.ActiveSession;
                FleetPlan plan = FleetSession.Current;
                if (team == 1 && plan != null && !string.IsNullOrEmpty(plan.ShipBName)) return plan.ShipBName.Replace("METEM ", "");
                GUIDUnion guid = team == 0 ? (session != null ? session.ToLoadShipGuid : GUIDUnion.Empty()) : (plan != null ? plan.ShipB : GUIDUnion.Empty());
                return HubFleet.DescribeLoadout(HubFleet.LoadoutDef(guid)).Replace("METEM ", "");
            }
            catch
            {
                return "ship " + (team + 1);
            }
        }

        // ---------------------------------------------------------------- requests

        public static void Join(int team)
        {
            Net.ToHost(Msg.TeamRequest, team);
        }

        /// <summary>Host only: put a player on a team (or take them off it with -1), whatever they picked.</summary>
        public static void Move(int actor, int team)
        {
            Net.ToHost(Msg.TeamMove, actor, team);
        }

        public static void Listen()
        {
            Net.On(Msg.TeamRequest, (sender, data) =>
            {
                if (!PhotonNetwork.IsMasterClient || !PickingTeams) return;
                int team = Net.Get(data, 0, -1);
                if (team != 0 && team != 1) return;
                Dictionary<int, int> teams = FleetShips.CrewMap();
                int current;
                if (teams.TryGetValue(sender, out current) && current == team) return;
                if (Count(teams, team) >= MaxPerTeam)
                {
                    Net.ToActor(sender, Msg.Notice, TeamName(team) + " is full: teams stay even. Pick " + TeamName(1 - team) + ", or ask the host to move someone.");
                    return;
                }
                teams[sender] = team;
                FleetShips.PublishCrew(teams);
            });
            Net.On(Msg.TeamMove, (sender, data) =>
            {
                if (!PhotonNetwork.IsMasterClient || !PickingTeams) return;
                if (PhotonNetwork.LocalPlayer == null || sender != PhotonNetwork.LocalPlayer.ActorNumber) return;
                int actor = Net.Get(data, 0, -1);
                int team = Net.Get(data, 1, -1);
                Dictionary<int, int> teams = FleetShips.CrewMap();
                if (team == 0 || team == 1) teams[actor] = team;
                else teams.Remove(actor);
                FleetShips.PublishCrew(teams);
            });
        }

        /// <summary>Host only: everyone without a team joins the smaller one, then the larger team gives players away until even.</summary>
        public static void AutoBalance()
        {
            if (!PhotonNetwork.IsMasterClient) return;
            Dictionary<int, int> teams = FleetShips.CrewMap();
            foreach (Player player in PhotonNetwork.PlayerList.OrderBy(p => p.ActorNumber))
            {
                if (!teams.ContainsKey(player.ActorNumber)) teams[player.ActorNumber] = Count(teams, 0) <= Count(teams, 1) ? 0 : 1;
            }
            int guard = 32;
            while (Math.Abs(Count(teams, 0) - Count(teams, 1)) > 1 && guard-- > 0)
            {
                int big = Count(teams, 0) > Count(teams, 1) ? 0 : 1;
                Player last = PhotonNetwork.PlayerList.Where(p => teams[p.ActorNumber] == big).OrderByDescending(p => p.ActorNumber).First();
                teams[last.ActorNumber] = 1 - big;
            }
            FleetShips.PublishCrew(teams);
        }

        /// <summary>Host only: lock the teams and let everyone spawn aboard their team's ship.</summary>
        public static void Confirm()
        {
            if (!PhotonNetwork.IsMasterClient || !PickingTeams) return;
            bool inHub = HubChoosing;
            Dictionary<int, int> teams = FleetShips.CrewMap();
            string why;
            if (!Balanced(teams, out why))
            {
                Notice.Show("Can't start yet: " + why);
                return;
            }
            FleetShips.PublishCrew(teams);
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyLocked, true } });
            Notice.ShowAll("Teams confirmed: " + TeamName(0) + " " + Count(teams, 0) + " against " + TeamName(1) + " " + Count(teams, 1) + ". " +
                           (inHub
                               ? "Set up your team's ship at the loadout terminal (SHIP 1 is BLUE, SHIP 2 is RED). The host starts the match by sitting in the ready room."
                               : "Destroy the enemy ship to win!"));
        }

        /// <summary>
        /// Host, when the hub loads, FLEET BATTLE is chosen or a match starts: no result, sabotage or hack from an earlier
        /// match, and the teams are picked again (unless a match starts with the teams confirmed in the hub).
        /// </summary>
        public static void Reset(bool keepTeams = false)
        {
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            if (!keepTeams) PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyLocked, null } });
            PvpMatch.ResetRoom();
            PvpSabotage.ResetRoom();
            PvpHack.ResetRoom();
        }
    }
}
