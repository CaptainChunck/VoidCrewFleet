using System;
using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using Newtonsoft.Json.Linq;
using Photon.Pun;
using Photon.Realtime;

namespace VoidCrewFleet
{
    /// <summary>
    /// Every player of a fleet mission by account ID, so a player who loses their connection gets their place back
    /// (RosterRules). The host keeps the list as a room property, so a player who becomes host carries on with it, and
    /// turns it into the crew list (FleetShips) a few times a second:
    /// - Both: a player who comes back returns to their ship; everyone given a ship that is destroyed is DEAD for the rest of
    ///   the mission (no respawn; they watch once their character dies), even if they were away when it happened.
    /// - PvP: a player who leaves keeps their seat for 3 minutes; a waiting player gets the next free seat of their team;
    ///   one without a seat waits (WAITING).
    /// - Co-op: a newcomer joins the living ship with the smallest crew.
    /// A player's ID is their PlayFab account ID, which the game itself publishes; without one, their Steam ID or an ID this
    /// plugin keeps in its settings, published as a player property.
    /// </summary>
    internal static class PvpRoster
    {
        public const string Feature = "PvP reconnects";

        private const string KeyRoster = "VCF_Roster";
        private const string KeyId = "VCF_Pid";

        /// <summary>How long a seat is held for a player who lost their connection.</summary>
        public const int HoldSeconds = 180;

        private static List<RosterSeat> _seats;
        private static int _seatsPerTeam;
        private static object _session;
        private static string _published;
        private static string _localId;

        // ---------------------------------------------------------------- player IDs

        /// <summary>This player's stable ID: the Steam ID when Steam runs, else one kept in the settings.</summary>
        public static string LocalId
        {
            get
            {
                if (!string.IsNullOrEmpty(_localId)) return _localId;
                try
                {
                    if (Steamworks.SteamAPI.IsSteamRunning()) _localId = "steam:" + Steamworks.SteamUser.GetSteamID().m_SteamID;
                }
                catch
                {
                    // not on Steam
                }
                if (string.IsNullOrEmpty(_localId) && Settings.PlayerId != null)
                {
                    if (string.IsNullOrEmpty(Settings.PlayerId.Value)) Settings.PlayerId.Value = Guid.NewGuid().ToString("N");
                    _localId = "id:" + Settings.PlayerId.Value;
                }
                return _localId;
            }
        }

        /// <summary>A few times a second: the local player's ID is on their Photon player, for the host to read.</summary>
        public static void PublishLocalId()
        {
            try
            {
                Player local = PhotonNetwork.LocalPlayer;
                if (local == null || !PhotonNetwork.InRoom) return;
                object value;
                string id = LocalId;
                if (string.IsNullOrEmpty(id) || (local.CustomProperties.TryGetValue(KeyId, out value) && value as string == id)) return;
                local.SetCustomProperties(new Hashtable { { KeyId, id } });
            }
            catch (Exception e)
            {
                Log.Failure("Publishing this player's ID", e);
            }
        }

        /// <summary>
        /// A player's stable ID: their PlayFab account (the game publishes it on every player, and it comes from their Steam
        /// login, so it is the same each time they join), else this plugin's own; null until their client has published one.
        /// </summary>
        public static string IdOf(Player player)
        {
            if (player == null || player.CustomProperties == null) return null;
            object value;
            if (player.CustomProperties.TryGetValue("PLAYFAB_ID", out value) && value is string && ((string)value).Length > 0) return "pf:" + value;
            if (player.CustomProperties.TryGetValue(KeyId, out value) && value is string && ((string)value).Length > 0) return (string)value;
            return player.IsLocal ? LocalId : null;
        }

        private static long Now
        {
            get { return (uint)PhotonNetwork.ServerTimestamp; }
        }

        // ---------------------------------------------------------------- what others read

        /// <summary>PvP: seats a team still holds for players who lost their connection (they count as on the team).</summary>
        public static int HeldSeats(int team)
        {
            List<RosterSeat> seats = Seats();
            return seats != null ? RosterRules.Held(seats, team) : 0;
        }

        /// <summary>The roster as the host last published it (the host's own copy on the host).</summary>
        public static List<RosterSeat> Seats()
        {
            if (PhotonNetwork.IsMasterClient && _seats != null && ReferenceEquals(_session, GameSessionManager.ActiveSession)) return _seats;
            int perTeam;
            return Read(out perTeam);
        }

        // ---------------------------------------------------------------- the host

        /// <summary>The mission's plan when the roster runs: a fleet mission (PvP once its teams are confirmed), on the host.</summary>
        private static FleetPlan ActivePlan()
        {
            if (!PhotonNetwork.IsMasterClient || !PhotonNetwork.InRoom || GameSessionManager.InHub) return null;
            FleetPlan plan = FleetSession.Current;
            if (plan == null || plan.Mode == FleetMode.OneShip || FleetShips.All.Count == 0) return null;
            // PvP before the teams are confirmed: the players pick on the team screen.
            if (plan.Mode == FleetMode.PvP && !PvpTeams.Locked) return null;
            return plan;
        }

        /// <summary>Host, a few times a second during a fleet mission: the roster follows the room, and the crew list follows the roster.</summary>
        public static void Tick()
        {
            try
            {
                FleetPlan plan = ActivePlan();
                if (plan == null) return;
                bool pvp = plan.Mode == FleetMode.PvP;
                if (!Ready(pvp)) return;

                List<int> alive = FleetShips.AliveSlots();
                long now = Now;
                bool changed = RosterRules.ExpireHolds(_seats, now, HoldSeconds * 1000L);
                var present = new HashSet<string>();
                foreach (Player player in PhotonNetwork.PlayerList.OrderBy(p => p.ActorNumber))
                {
                    string id = IdOf(player);
                    if (string.IsNullOrEmpty(id)) continue;
                    present.Add(id);
                    RosterSeat before = _seats.FirstOrDefault(s => s.Id == id);
                    RosterSeat was = before != null ? before.Clone() : null;
                    RosterSeat seat = RosterRules.Arrive(_seats, id, player.ActorNumber, NameOf(player), alive, pvp, _seatsPerTeam, now);
                    if (was != null && seat.ToString() == was.ToString()) continue;
                    changed = true;
                    if (pvp || seat.State == CrewState.Dead) Announce(player.ActorNumber, seat.Name, was, seat);
                }
                foreach (RosterSeat seat in _seats)
                {
                    if (seat.Away || present.Contains(seat.Id)) continue;
                    RosterRules.Leave(_seats, seat.Id, now);
                    changed = true;
                    if (pvp && seat.State == CrewState.Playing)
                        Notice.ShowAll(seat.Name + " lost their connection: their place on the " + PvpTeams.TeamName(seat.Team) + " team is held for " + HoldSeconds / 60 + " minutes.");
                }
                if (pvp)
                {
                    List<RosterSeat> waiting = _seats.Where(s => !s.Away && s.State == CrewState.Waiting).Select(s => s.Clone()).ToList();
                    if (RosterRules.FillSeats(_seats, alive, _seatsPerTeam))
                    {
                        changed = true;
                        foreach (RosterSeat was in waiting)
                        {
                            RosterSeat seat = _seats.First(s => s.Id == was.Id);
                            if (seat.State == CrewState.Playing) Announce(seat.Actor, seat.Name, was, seat);
                        }
                    }
                }
                if (changed) Publish();
                Apply();
            }
            catch (Exception e)
            {
                Log.Failure("Keeping the fleet's roster", e);
            }
        }

        /// <summary>The roster of this mission exists (inherited, or made from the crews as they are).</summary>
        private static bool Ready(bool pvp)
        {
            object session = GameSessionManager.ActiveSession;
            if (ReferenceEquals(session, _session) && _seats != null) return true;
            _session = session;
            _published = null;
            _seats = null;
            return Start(pvp);
        }

        /// <summary>Host: the crew list says what the roster says.</summary>
        private static void Apply()
        {
            Dictionary<int, CrewEntry> crews = RosterRules.Crews(_seats);
            if (!SameCrews(crews, FleetShips.Crews())) FleetShips.PublishCrew(crews);
        }

        /// <summary>The first time the host looks at a mission: the roster it has inherited, else one from the crews as they are.</summary>
        private static bool Start(bool pvp)
        {
            int perTeam;
            List<RosterSeat> inherited = Read(out perTeam);
            if (inherited != null)
            {
                _seats = inherited;
                _seatsPerTeam = perTeam;
                Log.Info("Fleet roster taken over: " + string.Join(", ", _seats.Select(s => s.ToString()).ToArray()) + ".");
                return true;
            }
            Dictionary<int, CrewEntry> crews = FleetShips.Crews();
            if (crews.Count == 0) return false;
            _seats = new List<RosterSeat>();
            long now = Now;
            foreach (Player player in PhotonNetwork.PlayerList)
            {
                CrewEntry entry;
                string id = IdOf(player);
                if (string.IsNullOrEmpty(id) || !crews.TryGetValue(player.ActorNumber, out entry)) continue;
                _seats.Add(new RosterSeat
                {
                    Id = id, Actor = player.ActorNumber, Name = NameOf(player), Team = entry.Team, Slot = entry.Slot, State = entry.State, WaitingSince = now
                });
            }
            int blue = _seats.Count(s => s.Team == 0);
            int red = _seats.Count(s => s.Team == 1);
            _seatsPerTeam = pvp ? Math.Max(1, Math.Max(blue, red)) : 0;
            Log.Info("Fleet roster: " + _seats.Count + " players" + (pvp ? ", " + _seatsPerTeam + " seats per team" : "") + ".");
            Publish();
            return true;
        }

        private static void Announce(int actor, string name, RosterSeat before, RosterSeat seat)
        {
            if (seat.State == CrewState.Dead)
            {
                if (before != null && before.Away)
                    Net.ToActor(actor, Msg.Notice, "You're back. Your ship was destroyed in this mission, so you watch the rest of it.");
                return;
            }
            if (seat.State == CrewState.Waiting)
            {
                Notice.ShowAll(name + " is waiting for a free place" + (seat.Team >= 0 ? " on the " + PvpTeams.TeamName(seat.Team) + " team." : "."));
                return;
            }
            if (before == null) Notice.ShowAll(name + " joins the " + PvpTeams.TeamName(seat.Team) + " team, on " + FleetSlots.Name(seat.Slot, true) + ".");
            else if (before.Away) Notice.ShowAll(name + " is back on the " + PvpTeams.TeamName(seat.Team) + " team, on " + FleetSlots.Name(seat.Slot, true) + ".");
            else if (before.State == CrewState.Waiting) Notice.ShowAll(name + " gets a free place on the " + PvpTeams.TeamName(seat.Team) + " team, on " + FleetSlots.Name(seat.Slot, true) + ".");
        }

        private static string NameOf(Player player)
        {
            return player == null ? "A player" : (string.IsNullOrEmpty(player.NickName) ? "Player " + player.ActorNumber : player.NickName);
        }

        /// <summary>
        /// Host: a ship is lost. Everyone given it (present or away) is DEAD from now on: no respawn, they watch once their
        /// character dies. True when the roster took care of it.
        /// </summary>
        public static bool OnShipLost(int slot)
        {
            FleetPlan plan = ActivePlan();
            if (plan == null) return false;
            if (!Ready(plan.Mode == FleetMode.PvP)) return false;
            RosterRules.ShipLost(_seats, slot);
            Publish();
            Apply();
            return true;
        }

        private static bool SameCrews(Dictionary<int, CrewEntry> a, Dictionary<int, CrewEntry> b)
        {
            if (a.Count != b.Count) return false;
            foreach (KeyValuePair<int, CrewEntry> pair in a)
            {
                CrewEntry other;
                if (!b.TryGetValue(pair.Key, out other) || other.ToString() != pair.Value.ToString()) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- the room property

        private static void Publish()
        {
            if (_seats == null || PhotonNetwork.CurrentRoom == null) return;
            var players = new JArray();
            foreach (RosterSeat seat in _seats)
            {
                players.Add(new JObject
                {
                    ["id"] = seat.Id, ["name"] = seat.Name ?? "", ["actor"] = seat.Actor, ["team"] = seat.Team, ["slot"] = seat.Slot, ["state"] = (int)seat.State,
                    ["away"] = seat.Away, ["since"] = seat.AwaySince, ["expired"] = seat.Expired, ["waiting"] = seat.WaitingSince
                });
            }
            GameSession session = GameSessionManager.ActiveSession;
            string json = new JObject
            {
                ["v"] = 1, ["session"] = session != null ? session.GameSessionID.ToString() : "", ["seats"] = _seatsPerTeam, ["players"] = players
            }.ToString(Newtonsoft.Json.Formatting.None);
            if (json == _published) return;
            _published = json;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyRoster, json } });
        }

        /// <summary>The roster in the room for this mission, or null.</summary>
        private static List<RosterSeat> Read(out int seatsPerTeam)
        {
            seatsPerTeam = 0;
            try
            {
                object value;
                if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom == null || !PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(KeyRoster, out value) || !(value is string))
                    return null;
                JObject json = JObject.Parse((string)value);
                GameSession session = GameSessionManager.ActiveSession;
                if (session == null || (string)json["session"] != session.GameSessionID.ToString()) return null;
                seatsPerTeam = (int)json["seats"];
                var seats = new List<RosterSeat>();
                foreach (JObject entry in (JArray)json["players"])
                {
                    seats.Add(new RosterSeat
                    {
                        Id = (string)entry["id"], Name = (string)entry["name"], Actor = (int)entry["actor"], Team = (int)entry["team"], Slot = (int)entry["slot"],
                        State = (CrewState)(int)entry["state"], Away = (bool)entry["away"], AwaySince = (long)entry["since"], Expired = (bool)entry["expired"],
                        WaitingSince = (long)entry["waiting"]
                    });
                }
                return seats;
            }
            catch (Exception e)
            {
                Log.Failure("Reading the fleet's roster", e);
                return null;
            }
        }

        /// <summary>Host, back in the hub or a one-ship mission: no roster.</summary>
        public static void ClearRoom()
        {
            _seats = null;
            _session = null;
            _published = null;
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyRoster, null } });
        }
    }
}
