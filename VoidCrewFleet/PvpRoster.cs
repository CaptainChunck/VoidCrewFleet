using System;
using System.Collections.Generic;
using System.Linq;
using CG.GameLoopStateMachine;
using CG.GameLoopStateMachine.GameStates;
using ExitGames.Client.Photon;
using Newtonsoft.Json.Linq;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Every player of a fleet mission by account ID, so a player who loses their connection gets their place back
    /// (RosterRules). The host keeps the list as a room property, so a player who becomes host carries on with it, and
    /// turns it into the crew list (FleetShips) a few times a second:
    /// - Both: a player who comes back returns to their ship; everyone given a ship that is destroyed is DEAD for the rest of
    ///   the mission (no respawn; they watch once their character dies), even if they were away when it happened.
    /// - PvP: anyone who joins while there is room plays: they join the team with fewer active players, on its ship with the
    ///   smallest crew, so the teams stay at most one apart. A player who leaves keeps their place for 3 minutes. Free places
    ///   go to the waiting players (WAITING), each team's own first, before anyone who joins.
    /// - Co-op: a newcomer joins the living ship with the smallest crew while one has room (CrewRules.MaxCrew); when every
    ///   crew is full they wait (WAITING) and get the first free place, as in PvP.
    /// A player's ID is their PlayFab account ID, which the game itself publishes; without one, their Steam ID or an ID this
    /// plugin keeps in its settings, published as a player property. An ID is whatever a player's game publishes, so a
    /// place stays with the player who has it while they are in the room, even when someone else shows the same ID.
    /// </summary>
    internal static class PvpRoster
    {
        private const string KeyRoster = "VCF_Roster";
        private const string KeyId = "VCF_Pid";

        /// <summary>How long a seat is held for a player who lost their connection.</summary>
        public const int HoldSeconds = 180;

        private const int HoldMs = HoldSeconds * 1000;

        /// <summary>How often the crew list is compared with the roster when the roster itself didn't change (seconds).</summary>
        private const float ApplyCheckSeconds = 2f;

        private static List<RosterSeat> _seats;
        private static object _session;
        private static string _published;
        private static string _localId;
        private static float _nextApplyCheck;

        /// <summary>Host, during a tick: the seats by player ID, the IDs in the room, and the waiting players before a fill.</summary>
        private static readonly Dictionary<string, RosterSeat> ById = new Dictionary<string, RosterSeat>();

        private static readonly HashSet<string> Present = new HashSet<string>();
        private static readonly List<RosterSeat> WaitingBefore = new List<RosterSeat>();

        /// <summary>Host, during a tick: the room's players in join order, and the living ships' slots.</summary>
        private static readonly List<Player> Players = new List<Player>();

        private static readonly List<int> Alive = new List<int>();

        /// <summary>Host: each player's "pf:" ID with the PlayFab ID it was made from, so it isn't made again every tick.</summary>
        private static readonly Dictionary<int, KeyValuePair<object, string>> PlayFabIds = new Dictionary<int, KeyValuePair<object, string>>();

        /// <summary>Host: players refused a place another player in the room holds under the same ID (logged once a mission).</summary>
        private static readonly HashSet<int> Refused = new HashSet<int>();

        // The room's roster as last read: the property's value and the session it was read for (it is parsed again only when
        // either changes), and what it held. Shared with every reader, so nobody changes it.
        private static object _readValue;
        private static object _readSession;
        private static List<RosterSeat> _readSeats;

        // ---------------------------------------------------------------- player IDs

        /// <summary>
        /// This player's stable ID: from the Steam ID when Steam runs (a one-way hash of it, so the Steam ID itself is never
        /// published), else one kept in the settings.
        /// </summary>
        public static string LocalId
        {
            get
            {
                if (!string.IsNullOrEmpty(_localId)) return _localId;
                try
                {
                    if (Steamworks.SteamAPI.IsSteamRunning()) _localId = "steam:" + Hashed(Steamworks.SteamUser.GetSteamID().m_SteamID.ToString());
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

        /// <summary>The first 16 bytes of a salted SHA-256 of a text, as hex: the same every time, and it can't be turned back.</summary>
        private static string Hashed(string raw)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("VoidCrewFleet|" + raw));
                var text = new System.Text.StringBuilder(32);
                for (int i = 0; i < 16; i++) text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }

        /// <summary>A few times a second, in a Fleet room only: the local player's ID is on their Photon player, for the host to read.</summary>
        public static void PublishLocalId()
        {
            try
            {
                Player local = PhotonNetwork.LocalPlayer;
                if (local == null || !PhotonNetwork.InRoom || !ModRegistration.IsFleetRoom) return;
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
            if (player.CustomProperties.TryGetValue("PLAYFAB_ID", out value) && value is string && ((string)value).Length > 0)
            {
                // The same property value gives the same ID: made once, not on every tick.
                KeyValuePair<object, string> made;
                if (PlayFabIds.TryGetValue(player.ActorNumber, out made) && ReferenceEquals(made.Key, value)) return made.Value;
                string id = "pf:" + value;
                PlayFabIds[player.ActorNumber] = new KeyValuePair<object, string>(value, id);
                return id;
            }
            if (player.CustomProperties.TryGetValue(KeyId, out value) && value is string && ((string)value).Length > 0) return (string)value;
            return player.IsLocal ? LocalId : null;
        }

        /// <summary>The server's clock in milliseconds (it wraps around: the rules compare times by their difference).</summary>
        private static int Now
        {
            get { return PhotonNetwork.ServerTimestamp; }
        }

        // ---------------------------------------------------------------- what others read

        /// <summary>The roster as the host last published it (the host's own copy on the host). Only to read.</summary>
        public static List<RosterSeat> Seats()
        {
            if (PhotonNetwork.IsMasterClient && _seats != null && ReferenceEquals(_session, GameSessionManager.ActiveSession)) return _seats;
            return Read();
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
                if (!PhotonNetwork.IsMasterClient && _seats != null)
                {
                    // No longer the host: should this player become host again, they take over the roster as it is then, not
                    // their own old copy.
                    _seats = null;
                    _session = null;
                    _published = null;
                }
                FleetPlan plan = ActivePlan();
                if (plan == null) return;
                bool pvp = plan.Mode == FleetMode.PvP;
                if (!Ready(pvp)) return;

                // Into a list kept for it: this runs a few times a second.
                List<int> alive = Alive;
                FleetShips.AliveSlots(alive);
                int now = Now;
                bool changed = RosterRules.ExpireHolds(_seats, now, HoldMs);
                if (RosterRules.DropGone(_seats, now, HoldMs)) changed = true;
                if (LoseGoneShips(alive)) changed = true;
                ById.Clear();
                foreach (RosterSeat seat in _seats) ById[seat.Id] = seat;
                PvpTeams.RoomPlayers(Players);
                Present.Clear();
                foreach (Player player in Players)
                {
                    string id = IdOf(player);
                    if (!string.IsNullOrEmpty(id)) Present.Add(id);
                }

                // Who left goes first: a waiting player who just left must not be given a free place (and hold it for minutes).
                foreach (RosterSeat seat in _seats)
                {
                    if (seat.Away || Present.Contains(seat.Id)) continue;
                    RosterRules.Leave(_seats, seat.Id, now);
                    changed = true;
                    if (pvp && seat.State == CrewState.Playing)
                        Notice.ShowAll(seat.Name + " lost their connection: their place on the " + PvpTeams.TeamName(seat.Team) + " team is held for " + HoldSeconds / 60 + " minutes.");
                }
                // Seats freed since the last look go to the players already waiting, before anyone who arrives now.
                if (FillSeats(alive, now, pvp)) changed = true;

                // In join order (actor number): of two players new to the roster with one ID, the first one gets the place.
                foreach (Player player in Players)
                {
                    string id = IdOf(player);
                    if (string.IsNullOrEmpty(id)) continue;
                    string name = NameOf(player);
                    RosterSeat seat;
                    if (ById.TryGetValue(id, out seat) && !seat.Away)
                    {
                        if (seat.Actor != player.ActorNumber)
                        {
                            // Someone else with this ID. While the player who has the place is in the room it stays theirs;
                            // once they have left, this is them back after a lost connection, and they take it over.
                            if (InRoomNow(seat.Actor))
                            {
                                RefuseCopy(seat, player);
                                continue;
                            }
                            seat.Actor = player.ActorNumber;
                            seat.Name = name;
                            changed = true;
                            continue;
                        }
                        // Still here: only their name can change.
                        if (seat.Name == name) continue;
                        seat.Name = name;
                        changed = true;
                        continue;
                    }
                    RosterSeat was = seat != null ? seat.Clone() : null;
                    seat = RosterRules.Arrive(_seats, id, player.ActorNumber, name, alive, pvp, now);
                    ById[id] = seat;
                    changed = true;
                    if (pvp || seat.State != CrewState.Playing) Announce(player.ActorNumber, seat.Name, was, seat);
                }
                if (FillSeats(alive, now, pvp)) changed = true;
                if (changed) Publish();
                Apply(changed);
            }
            catch (Exception e)
            {
                Log.Failure("Keeping the fleet's roster", e);
            }
        }

        /// <summary>Whether a player (by actor number) is in the room now and connected.</summary>
        private static bool InRoomNow(int actor)
        {
            Room room = PhotonNetwork.CurrentRoom;
            Player player;
            return room != null && room.Players != null && room.Players.TryGetValue(actor, out player) && player != null && !player.IsInactive;
        }

        /// <summary>A second player shows the ID of one who has a place: they get none while both are here (logged once a mission).</summary>
        private static void RefuseCopy(RosterSeat seat, Player player)
        {
            if (!Refused.Add(player.ActorNumber)) return;
            Log.Warn(NameOf(player) + " (" + player.ActorNumber + ") shows the same player ID as " + seat.Name + " (" + seat.Actor + "), who has that place: " +
                     NameOf(player) + " gets none while " + seat.Name + " is in the room.");
        }

        /// <summary>
        /// Host: everyone still playing on a ship this mission started with that is no longer alive is DEAD, as the host does
        /// when the ship starts dying (OnShipLost). A player who becomes host during a ship's death never saw that happen, and
        /// the ship's crew would otherwise stay on the roster as players of a ship that is gone. Only once the host is in the
        /// game with a ship left (a host still loading the mission doesn't have every ship yet); a ship that never spawned
        /// doesn't count. True when anyone changed.
        /// </summary>
        private static bool LoseGoneShips(List<int> alive)
        {
            if (alive.Count == 0 || !InGame()) return false;
            // The ships as the host published them (FleetShips: on the host its own list, not the room's copy).
            int ships = FleetShips.PublishedCount;
            if (ships < 2) return false;
            bool changed = false;
            for (int slot = 0; slot < ships; slot++)
            {
                if (!FleetShips.IsLost(slot) || !PlayingOn(slot)) continue;
                RosterRules.ShipLost(_seats, slot);
                changed = true;
                Log.Info(FleetSlots.Name(slot, FleetSession.IsPvP) + " is lost but its crew was still playing on the roster (a new host?): they are out (DEAD) now.");
            }
            return changed;
        }

        /// <summary>Whether anyone on the roster still plays on this ship (there or away).</summary>
        private static bool PlayingOn(int slot)
        {
            foreach (RosterSeat seat in _seats)
            {
                if (seat.Slot == slot && seat.State == CrewState.Playing) return true;
            }
            return false;
        }

        /// <summary>Whether this player is in the mission itself (past loading and spawning).</summary>
        private static bool InGame()
        {
            GameStateMachine machine = GameStateMachine.Instance;
            return machine != null && machine.CurrentState is GSIngame;
        }

        /// <summary>Free seats to the waiting players in the room (RosterRules.FillSeats), with a notice for each who got one.</summary>
        private static bool FillSeats(List<int> alive, int now, bool pvp)
        {
            WaitingBefore.Clear();
            foreach (RosterSeat seat in _seats)
            {
                if (!seat.Away && seat.State == CrewState.Waiting) WaitingBefore.Add(seat.Clone());
            }
            if (WaitingBefore.Count == 0 || !RosterRules.FillSeats(_seats, alive, now, pvp)) return false;
            foreach (RosterSeat was in WaitingBefore)
            {
                RosterSeat seat;
                if (ById.TryGetValue(was.Id, out seat) && seat.State == CrewState.Playing) Announce(seat.Actor, seat.Name, was, seat);
            }
            return true;
        }

        /// <summary>The roster of this mission exists (inherited, or made from the crews as they are).</summary>
        private static bool Ready(bool pvp)
        {
            object session = GameSessionManager.ActiveSession;
            if (ReferenceEquals(session, _session) && _seats != null) return true;
            _session = session;
            _published = null;
            _seats = null;
            // A new roster (a new mission, or this player took over as host) is compared with the crew list at once.
            _nextApplyCheck = 0f;
            Refused.Clear();
            return Start(pvp);
        }

        /// <summary>
        /// Host: the crew list says what the roster says. Both are only made and compared when the roster changed, and every
        /// few seconds besides in case anything else wrote the crew list (making them on every tick showed up). This is the
        /// host's own crew list; the room's copy, which every other player reads, is kept equal to it by
        /// FleetShips.KeepRoomCopies, so a crew list someone else wrote into the room is put back too.
        /// </summary>
        private static void Apply(bool changed)
        {
            if (!changed && Time.unscaledTime < _nextApplyCheck) return;
            _nextApplyCheck = Time.unscaledTime + ApplyCheckSeconds;
            Dictionary<int, CrewEntry> crews = RosterRules.Crews(_seats);
            if (!SameCrews(crews, FleetShips.Crews())) FleetShips.PublishCrew(crews);
        }

        /// <summary>The first time the host looks at a mission: the roster it has inherited, else one from the crews as they are.</summary>
        private static bool Start(bool pvp)
        {
            List<RosterSeat> inherited = Read();
            if (inherited != null)
            {
                // Its own copy: the roster as read is shared with every reader. The room holds it as published (KeepRoomCopy).
                _seats = inherited.Select(s => s.Clone()).ToList();
                _published = _readValue as string;
                _publishedAt = Time.realtimeSinceStartup;
                Log.Info("Fleet roster taken over: " + string.Join(", ", _seats.Select(s => s.ToString()).ToArray()) + ".");
                return true;
            }
            Dictionary<int, CrewEntry> crews = FleetShips.Crews();
            if (crews.Count == 0) return false;
            _seats = new List<RosterSeat>();
            int now = Now;
            PvpTeams.RoomPlayers(Players);
            foreach (Player player in Players)
            {
                CrewEntry entry;
                string id = IdOf(player);
                if (string.IsNullOrEmpty(id) || !crews.TryGetValue(player.ActorNumber, out entry)) continue;
                RosterSeat first = _seats.FirstOrDefault(s => s.Id == id);
                if (first != null)
                {
                    // One place per ID: the first player in the room with it has it.
                    RefuseCopy(first, player);
                    continue;
                }
                _seats.Add(new RosterSeat
                {
                    Id = id, Actor = player.ActorNumber, Name = NameOf(player), Team = entry.Team, Slot = entry.Slot, State = entry.State, WaitingSince = now
                });
            }
            Log.Info("Fleet roster: " + _seats.Count + " players" + (pvp ? ", " + RosterRules.Active(_seats, 0) + " against " + RosterRules.Active(_seats, 1) : "") + ".");
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
            bool pvp = FleetSession.IsPvP;
            if (seat.State == CrewState.Waiting)
            {
                Notice.ShowAll(name + " is waiting for a free place" + (!pvp
                    ? ": every ship's crew is full (" + CrewRules.MaxCrew + " a ship)."
                    : seat.Team >= 0 ? " on the " + PvpTeams.TeamName(seat.Team) + " team." : "."));
                return;
            }
            if (!pvp)
            {
                // Co-op: a newcomer or a player back on their ship is no news; a waiting player who gets a place is.
                if (before != null && before.State == CrewState.Waiting) Notice.ShowAll(name + " gets a free place, on " + FleetSlots.Name(seat.Slot, false) + ".");
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
            Apply(true);
            return true;
        }

        private static bool SameCrews(Dictionary<int, CrewEntry> a, Dictionary<int, CrewEntry> b)
        {
            if (a.Count != b.Count) return false;
            foreach (KeyValuePair<int, CrewEntry> pair in a)
            {
                CrewEntry other;
                if (!b.TryGetValue(pair.Key, out other)) return false;
                CrewEntry mine = pair.Value;
                if (other.Actor != mine.Actor || other.Team != mine.Team || other.Slot != mine.Slot || other.State != mine.State) return false;
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
                ["v"] = 2, ["session"] = session != null ? session.GameSessionID.ToString() : "", ["players"] = players
            }.ToString(Newtonsoft.Json.Formatting.None);
            if (json == _published) return;
            _published = json;
            _publishedAt = Time.realtimeSinceStartup;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyRoster, json } });
        }

        /// <summary>When the host last wrote the roster (the room's copy changes a round trip later).</summary>
        private static float _publishedAt = -100f;

        private static readonly RoomCopyCheck RosterCopy = new RoomCopyCheck();

        /// <summary>
        /// Host, a few times a second (PvpTeams.KeepRoomCopies): the room's roster stays the one the host last published. Any
        /// player can write a room property; a roster that keeps differing once the host's own has had time to come back
        /// (RoomCopyCheck) was written by someone else and is put back, so a player who takes over as host carries on with the
        /// real one.
        /// </summary>
        public static void KeepRoomCopy()
        {
            Room room = PhotonNetwork.CurrentRoom;
            bool ours = PhotonNetwork.IsMasterClient && room != null && _seats != null && _published != null && ReferenceEquals(_session, GameSessionManager.ActiveSession);
            object value = null;
            if (ours) room.CustomProperties.TryGetValue(KeyRoster, out value);
            if (!RosterCopy.PutBack(ours && value as string != _published, _publishedAt, Time.realtimeSinceStartup, !PhotonNetwork.IsMessageQueueRunning)) return;
            _publishedAt = Time.realtimeSinceStartup;
            room.SetCustomProperties(new Hashtable { { KeyRoster, _published } });
            Log.WarnOnce("room roster " + room.Name, "The room's fleet roster differed from the host's (another player changed it?); the host put its own back.");
        }

        /// <summary>
        /// The roster in the room for this mission, or null. It is parsed again only when the room property or the session
        /// changes, and the list is shared: only to read.
        /// </summary>
        private static List<RosterSeat> Read()
        {
            object value = null;
            if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null) PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(KeyRoster, out value);
            GameSession session = GameSessionManager.ActiveSession;
            if (!ReferenceEquals(value, _readValue) || !ReferenceEquals(session, _readSession))
            {
                _readValue = value;
                _readSession = session;
                _readSeats = Parse(value as string, session);
            }
            return _readSeats;
        }

        /// <summary>The roster in a room property's text when it is this session's, or null.</summary>
        private static List<RosterSeat> Parse(string text, GameSession session)
        {
            if (text == null || session == null) return null;
            try
            {
                JObject json = JObject.Parse(text);
                if ((string)json["session"] != session.GameSessionID.ToString()) return null;
                var seats = new List<RosterSeat>();
                foreach (JObject entry in (JArray)json["players"])
                {
                    seats.Add(new RosterSeat
                    {
                        Id = (string)entry["id"], Name = (string)entry["name"], Actor = (int)entry["actor"], Team = (int)entry["team"], Slot = (int)entry["slot"],
                        State = (CrewState)(int)entry["state"], Away = (bool)entry["away"], AwaySince = ServerTime(entry["since"]), Expired = (bool)entry["expired"],
                        WaitingSince = ServerTime(entry["waiting"])
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

        /// <summary>A server time in the roster: an int, or (as 0.4.0 wrote it) the same milliseconds as an unsigned number.</summary>
        private static int ServerTime(JToken token)
        {
            return unchecked((int)(long)token);
        }

        /// <summary>Host, back in the hub or a one-ship mission: no roster.</summary>
        public static void ClearRoom()
        {
            _seats = null;
            _session = null;
            _published = null;
            PlayFabIds.Clear();
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyRoster, null } });
        }
    }
}
