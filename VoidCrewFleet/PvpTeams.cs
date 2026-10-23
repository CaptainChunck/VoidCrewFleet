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
    /// Players who join the hub after the confirmation go to the smallest crew (of the smaller team). In Fleet Battle a crew
    /// takes CrewRules.MaxCrew at most: who finds every crew full waits for a free place (in the hub, and in the match the
    /// roster gives them one).
    /// </summary>
    internal static class PvpTeams
    {
        public const string Feature = "PvP teams";

        private const string KeyLocked = "VCF_TeamsLocked";

        /// <summary>Orders players by actor number (join order), the one comparer for every sort.</summary>
        private sealed class ByActor : IComparer<Player>
        {
            public static readonly ByActor Instance = new ByActor();

            public int Compare(Player a, Player b)
            {
                return a.ActorNumber.CompareTo(b.ActorNumber);
            }
        }

        /// <summary>
        /// The room's players in join order (actor number), into a list the caller keeps. PhotonNetwork.PlayerList makes and
        /// sorts a new array on every read, which adds up in work done a few times a second.
        /// </summary>
        public static void RoomPlayers(List<Player> into)
        {
            into.Clear();
            Room room = PhotonNetwork.CurrentRoom;
            if (room == null || room.Players == null) return;
            foreach (Player player in room.Players.Values) into.Add(player);
            into.Sort(ByActor.Instance);
        }

        /// <summary>The actor numbers of everyone in the room in join order, into a list the caller keeps.</summary>
        public static void RoomActors(List<int> into)
        {
            into.Clear();
            Room room = PhotonNetwork.CurrentRoom;
            if (room == null || room.Players == null) return;
            foreach (int actor in room.Players.Keys) into.Add(actor);
            into.Sort();
        }

        /// <summary>The actor numbers of everyone in the room in join order (a new list).</summary>
        private static List<int> PlayerActors()
        {
            var actors = new List<int>();
            RoomActors(actors);
            return actors;
        }

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
                List<int> players = PlayerActors();
                string refusal = CrewRules.CanJoin(crews, players, sender, slot, ShipCount, teamMatch, ShipLabel, TeamName);
                if (refusal != null)
                {
                    Net.ToActor(sender, Msg.Notice, refusal);
                    return;
                }
                crews[sender] = CrewEntry.Play(sender, CrewRules.TeamOf(slot, teamMatch), slot);
                PublishPicks(crews, players);
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
                PublishPicks(crews, PlayerActors());
            });
        }

        /// <summary>Host: the picks of the players in the room (a player who has left keeps no entry, so no count includes them).</summary>
        private static void PublishPicks(Dictionary<int, CrewEntry> crews, ICollection<int> players)
        {
            DropAbsent(crews, players);
            FleetShips.PublishCrew(crews);
        }

        /// <summary>Takes out the entries of players who aren't in the room; true when there were any.</summary>
        private static bool DropAbsent(Dictionary<int, CrewEntry> crews, ICollection<int> players)
        {
            List<int> gone = null;
            foreach (int actor in crews.Keys)
            {
                if (players.Contains(actor)) continue;
                if (gone == null) gone = new List<int>();
                gone.Add(actor);
            }
            if (gone == null) return false;
            foreach (int actor in gone) crews.Remove(actor);
            return true;
        }

        /// <summary>Host only: everyone on a ship, then even teams (Fleet Battle) and even crews; players keep their pick where they can.</summary>
        public static void AutoBalance()
        {
            if (!PhotonNetwork.IsMasterClient || !PickingTeams) return;
            FleetShips.PublishCrew(CrewRules.Balance(FleetShips.Crews(), PlayerActors(), ShipCount, FleetApi.IsTeamMatch));
        }

        /// <summary>Null when the teams can be confirmed now; otherwise why not.</summary>
        public static string CheckTeams()
        {
            return CrewRules.CheckTeams(FleetShips.Crews(), PlayerActors(), ShipCount, ShipLabel);
        }

        /// <summary>Co-op: null when the crews can be confirmed now; otherwise why not.</summary>
        public static string CheckCrews()
        {
            return CrewRules.CheckCrews(FleetShips.Crews(), PlayerActors(), ShipCount, ShipLabel);
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
            List<int> actors = PlayerActors();
            Dictionary<int, CrewEntry> crews = FleetShips.Crews();
            PublishPicks(crews, actors);
            SetLocked(true);
            if (!teams)
            {
                Notice.ShowAll("Crews confirmed: " + string.Join(", ", Enumerable.Range(0, ShipCount).Select(s =>
                               {
                                   int crew = CrewRules.CrewOf(crews, actors, s);
                                   return crew + (crew == 1 ? " player on " : " players on ") + ShipLabel(s);
                               }).ToArray()) + ". Set up your ship at the loadout terminal, then sit in the ready room to start.");
                return;
            }
            // The players in the room only: someone who picked a ship and left isn't on a team any more.
            int blue = CrewRules.TeamSize(crews, actors, 0, false);
            int red = CrewRules.TeamSize(crews, actors, 1, false);
            Notice.ShowAll("Teams confirmed: " + TeamName(0) + " " + blue + " against " + TeamName(1) + " " + red + ". " +
                           (inHub
                               ? "Set up your ship at the loadout terminal (the FLEET panel lists every ship). The host starts the match by sitting in the ready room."
                               : "Destroy every enemy ship to win!"));
        }

        /// <summary>Self-test only: lock the teams as they are (one player can't crew every ship).</summary>
        public static void ForceConfirm()
        {
            if (!PhotonNetwork.IsMasterClient || !PickingTeams) return;
            PublishPicks(FleetShips.Crews(), PlayerActors());
            SetLocked(true);
            Log.Info("[autotest] Teams locked without the checks: " + (CheckTeams() ?? "they would have passed") + ".");
        }

        /// <summary>Hub, Fleet Battle teams confirmed: players who joined and found every crew full (actor numbers), told once.</summary>
        private static readonly HashSet<int> HubWaiting = new HashSet<int>();

        /// <summary>The room and its number of players at KeepHubTeamsComplete's last full look: a player who leaves may free a place.</summary>
        private static string _hubRoom;

        private static int _hubPlayers = -1;
        private static readonly List<Player> HubPlayers = new List<Player>();
        private static readonly List<int> HubActors = new List<int>();

        /// <summary>
        /// Host, a few times a second: in the hub, once the teams (or co-op crews) are confirmed, a player who joins later goes
        /// to the smallest crew (of the smaller team), as in a mission, so they can set up their ship without the host
        /// reopening the picking. In Fleet Battle a crew takes CrewRules.MaxCrew at most: a player who finds every crew full
        /// stays without a ship and is told why (the host too), gets the first place a leaving player frees, and otherwise
        /// waits in the match like a player who joins it then (the roster gives them a free place). Not while the hub loads:
        /// the last mission's crews and lock are only cleared once it has.
        /// </summary>
        public static void KeepHubTeamsComplete()
        {
            try
            {
                if (!PhotonNetwork.IsMasterClient || !HubFleetPlanned || HubShipManager.Instance == null || !Locked)
                {
                    if (HubWaiting.Count > 0) HubWaiting.Clear();
                    _hubPlayers = -1;
                    return;
                }
                Room room = PhotonNetwork.CurrentRoom;
                if (room == null) return;
                int ships = ShipCount;
                // Nearly always nothing to do: everyone has a ship (or already waits for one), and nobody left to free a place.
                if (room.Name == _hubRoom && room.PlayerCount == _hubPlayers && EveryoneAccountedFor(room, ships)) return;
                _hubRoom = room.Name;
                _hubPlayers = room.PlayerCount;

                bool teams = FleetApi.IsTeamMatch;
                Dictionary<int, CrewEntry> crews = FleetShips.Crews();
                RoomPlayers(HubPlayers);
                RoomActors(HubActors);
                HubWaiting.RemoveWhere(actor => !HubActors.Contains(actor));
                bool changed = DropAbsent(crews, HubActors);
                List<int> slots = Enumerable.Range(0, ships).ToList();
                foreach (Player player in HubPlayers)
                {
                    int actor = player.ActorNumber;
                    CrewEntry entry;
                    if (crews.TryGetValue(actor, out entry) && entry.Slot >= 0 && entry.Slot < ships) continue;
                    int slot = CrewRules.PlaceFor(crews, HubActors, slots, teams);
                    if (slot < 0)
                    {
                        if (HubWaiting.Add(actor)) TellCrewsFull(player);
                        continue;
                    }
                    HubWaiting.Remove(actor);
                    crews[actor] = CrewEntry.Play(actor, CrewRules.TeamOf(slot, teams), slot);
                    changed = true;
                    Net.ToActor(actor, Msg.Notice, teams
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

        /// <summary>Whether everyone in the room has a ship, or is known to wait for one (KeepHubTeamsComplete).</summary>
        private static bool EveryoneAccountedFor(Room room, int ships)
        {
            foreach (Player player in room.Players.Values)
            {
                if (HubWaiting.Contains(player.ActorNumber)) continue;
                CrewEntry entry;
                if (!FleetShips.TryGetEntry(player.ActorNumber, out entry) || entry.Slot < 0 || entry.Slot >= ships) return false;
            }
            return true;
        }

        /// <summary>Fleet Battle, teams confirmed: a player who joined found every crew full. They and the host learn why, and what helps.</summary>
        private static void TellCrewsFull(Player player)
        {
            Net.ToActor(player.ActorNumber, Msg.Notice, "Every crew is full (" + CrewRules.MaxCrew + " players a ship), so you wait for a free place: " +
                                                        "you get the first one that opens, here or in the match.");
            if (player.IsLocal) return;
            string name = string.IsNullOrEmpty(player.NickName) ? "Player " + player.ActorNumber : player.NickName;
            Notice.Show(name + " joined, but every crew is full (" + CrewRules.MaxCrew + " players a ship), so they wait for a free place. " +
                        "More ships per team (TEAMS & RULES on the FLEET panel) would give them one.");
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
