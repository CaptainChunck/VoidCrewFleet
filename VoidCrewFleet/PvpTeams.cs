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
    /// PvP teams. Team 1 crews ship 1 and team 2 crews ship 2; a team is simply a crew (FleetShips' crew list). When a PvP
    /// mission starts nobody is on a team and nobody spawns: everyone sees the team screen (PvpTeamScreen), picks a side,
    /// and the host may move anyone, then confirms. Teams must be even: with an odd number of players one team has one
    /// extra. On confirm the host releases the spawn queue and each player is cloned aboard their team's ship. Players who
    /// join later go to the smaller team.
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
                if (!PhotonNetwork.IsMasterClient || !Choosing) return;
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
                if (!PhotonNetwork.IsMasterClient || !Choosing) return;
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
            if (!PhotonNetwork.IsMasterClient || !Choosing) return;
            Dictionary<int, int> teams = FleetShips.CrewMap();
            string why;
            if (!Balanced(teams, out why))
            {
                Notice.Show("Can't start yet: " + why);
                return;
            }
            FleetShips.PublishCrew(teams);
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyLocked, true } });
            Notice.ShowAll("Teams confirmed: " + TeamName(0) + " " + Count(teams, 0) + " against " + TeamName(1) + " " + Count(teams, 1) +
                           ". Destroy the enemy ship to win!");
        }

        /// <summary>Host, when a PvP match starts or the hub loads: teams are chosen again, and there is no result yet.</summary>
        public static void Reset()
        {
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyLocked, null } });
            PvpMatch.ResetRoom();
            PvpSabotage.ResetRoom();
            PvpBreach.ResetRoom();
        }
    }
}
