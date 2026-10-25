using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CG.Client.UserData;
using CG.Game;
using CG.Space;
using ExitGames.Client.Photon;
using Hashtable = ExitGames.Client.Photon.Hashtable;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// How a PvP match ends: a team that has lost all its ships loses the match, and the other team wins. While a team still
    /// has ships, losing one only sends its crew to watch (ShipDeath). A team that has lost all its players forfeits after
    /// 30 seconds. The host decides the match and tells everyone (Msg.MatchResult): players can see the last ships of both
    /// teams start dying in a different order, so the winner they are told and their end screen always follow the host. A
    /// room property keeps a copy for players who join later, and for a player who takes over as host while the match ends
    /// (only when it checks out: any player can write a room property). The host ends the game a few seconds after the last
    /// ship starts to explode, and each player's end screen says Victory or Defeat for their own team (the game sends one
    /// result for everyone; a player who was never on a team gets Defeat). Nobody else can end a fleet mission through the
    /// game's EndGame network call or its "game ended" event (EndGamePrefix, GameEventPrefix).
    /// </summary>
    internal static class PvpMatch
    {
        public const string Feature = "PvP match result";

        private const string KeyLoser = "VCF_LoserTeam";
        private const float EndDelaySeconds = 12f;
        private const float ForfeitSeconds = 30f;

        private static object _session;

        /// <summary>
        /// The losing team once the match is decided, else -1: on the host its own decision, on every other player the host's
        /// (Msg.MatchResult).
        /// </summary>
        private static int _loserTeam = -1;

        /// <summary>Whether that team lost by forfeit (all its players left) rather than by losing its ships.</summary>
        private static bool _forfeit;

        /// <summary>
        /// The first team this player saw lose its last ship while not the host (-1 for none): a player who takes over as host
        /// before the last host's decision reached them decides from it.
        /// </summary>
        private static int _seenLoser = -1;

        /// <summary>The losing team of the result this player has announced (-1 for none), so each result is announced once.</summary>
        private static int _shownLoser = -1;

        private static bool _ending;

        /// <summary>
        /// Whether this player was host at the last tick, and whether it has been a guest at any time this match: together,
        /// whether it has just taken over as host (TakeOver).
        /// </summary>
        private static bool _hosting;

        private static bool _wasGuest;

        private static readonly bool[] TeamHadPlayers = new bool[2];
        private static readonly int[] Presence = new int[2];
        private static int _emptyTeam = -1;
        private static float _emptySince;

        public static void Listen()
        {
            ShipDeath.ShipLost += OnShipLost;
            Net.On(Msg.MatchResult, OnResult);
        }

        /// <summary>
        /// The losing team as this player knows it (-1 until decided): on the host its own decision; on every other player
        /// the host's message, else (a player who joined after it was sent) the room's copy.
        /// </summary>
        public static int LoserTeam
        {
            get
            {
                SyncSession();
                if (_loserTeam >= 0 || PhotonNetwork.IsMasterClient) return _loserTeam;
                return RoomLoser();
            }
        }

        /// <summary>
        /// The losing team in the room's copy of the result, or -1. Any player can write a room property, so it only stands
        /// in for the host's word on the end screen of a player who joined after the host sent it, and for a player who takes
        /// over as host while the match ends, once it checks out (TakeOver).
        /// </summary>
        private static int RoomLoser()
        {
            Photon.Realtime.Room room = PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom : null;
            object value;
            if (room == null || !room.CustomProperties.TryGetValue(KeyLoser, out value) || !(value is int)) return -1;
            int team = (int)value;
            return team == 0 || team == 1 ? team : -1;
        }

        /// <summary>
        /// A ship starts dying. A team with another living ship fights on; the first team to lose its last ship (or forfeit)
        /// loses the match. The host decides which team that is and tells the others (OnResult), who may have seen the last
        /// ships of both teams start dying in another order. A ship that starts dying after the match is decided changes
        /// nothing.
        /// </summary>
        private static void OnShipLost(PlayerControlledShip ship)
        {
            if (!FleetSession.IsPvP) return;
            SyncSession();
            int slot = FleetShips.Slot(ship);
            if (slot < 0) return;
            int team = FleetShips.TeamOfSlot(slot);
            if (_loserTeam >= 0)
            {
                if (_loserTeam != team)
                    Log.Info("PvP: " + FleetSlots.Name(slot, true) + " is destroyed too, after the match was decided; the " + PvpTeams.TeamName(1 - _loserTeam) +
                             " team still wins.");
                return;
            }
            if (ShipDeath.TeamFightsOn(team, ship))
            {
                int left = FleetShips.AliveSlotsOfTeam(team).Count(s => s != slot);
                Notice.Show(FleetSlots.Name(slot, true) + " is destroyed! The " + PvpTeams.TeamName(team) + " team fights on with " + left + (left == 1 ? " ship." : " ships."));
                return;
            }
            if (PhotonNetwork.IsMasterClient)
            {
                EndSoon(team, false, EndDelaySeconds);
                return;
            }
            // The host's decision follows in a moment (OnResult); a player who takes over as host before it arrives goes by this.
            if (_seenLoser < 0) _seenLoser = team;
            Log.Info("PvP: the " + PvpTeams.TeamName(team) + " team has no ships left here; the host announces the result.");
        }

        /// <summary>Every player but the host: the host has decided the match. Kept for the end screen, and announced once.</summary>
        private static void OnResult(int sender, object[] data)
        {
            if (!Net.FromHost(sender) || !FleetSession.IsPvP) return;
            int loser = Net.Get(data, 0, -1);
            if (loser != 0 && loser != 1) return;
            SyncSession();
            _loserTeam = loser;
            _forfeit = Net.Get(data, 1, false);
            Announce(loser, _forfeit);
        }

        /// <summary>The result as a notice, once per result.</summary>
        private static void Announce(int loser, bool forfeit)
        {
            if (_shownLoser == loser) return;
            _shownLoser = loser;
            Notice.Show("The " + PvpTeams.TeamName(loser) + (forfeit ? " team forfeits" : " team has no ships left") + ": the " + PvpTeams.TeamName(1 - loser) +
                        " team wins!");
        }

        /// <summary>
        /// Host: the match is decided. Everyone is told (a message, and the room's copy for players who join later or take over
        /// as host), and the game ends once the explosion has played.
        /// </summary>
        private static void EndSoon(int loser, bool forfeit, float delay)
        {
            if (_ending || FleetRuntime.Instance == null) return;
            _ending = true;
            _loserTeam = loser;
            _forfeit = forfeit;
            try
            {
                PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyLoser, loser } });
            }
            catch (Exception e)
            {
                Log.Failure("Recording the PvP result", e);
            }
            Net.ToOthers(Msg.MatchResult, loser, forfeit);
            Announce(loser, forfeit);
            FleetRuntime.Instance.StartCoroutine(EndAfter(delay));
        }

        private static IEnumerator EndAfter(float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            try
            {
                ClientGame game = ClientGame.Current;
                if (game == null || !PhotonNetwork.IsMasterClient || !FleetSession.IsPvP || game.GameState == ClientGameState.Ended) yield break;
                Log.Info("PvP: the match is over; the " + PvpTeams.TeamName(1 - _loserTeam) + " team won.");
                game.EndGame((byte)QuestResult.Victory);
            }
            catch (Exception e)
            {
                Log.Failure("Ending the PvP match", e);
            }
        }

        /// <summary>A new match (session) forgets the last one: its result, who was host, and which teams had players.</summary>
        private static void SyncSession()
        {
            object session = GameSessionManager.ActiveSession;
            if (ReferenceEquals(session, _session)) return;
            _session = session;
            _loserTeam = -1;
            _forfeit = false;
            _seenLoser = -1;
            _shownLoser = -1;
            _ending = false;
            _hosting = false;
            _wasGuest = false;
            TeamHadPlayers[0] = TeamHadPlayers[1] = false;
            _emptyTeam = -1;
        }

        /// <summary>
        /// A few times a second: every client notes which teams have had players; the host makes a team that has lost all its
        /// players forfeit, and a player who takes over as host finishes the match the last host had decided.
        /// </summary>
        public static void Tick()
        {
            SyncSession();
            bool host = PhotonNetwork.IsMasterClient;
            // Just taken over as host: the match may be ending on the last host's word (TakeOver, below).
            bool tookOver = host && !_hosting && _wasGuest;
            _hosting = host;
            if (!host) _wasGuest = true;
            if (!FleetSession.IsPvP || !PvpTeams.Locked || GameSessionManager.InHub) return;

            // Every client keeps this, not only the host: when the host leaves, the player who takes over still knows that a
            // team now empty had players, and declares its forfeit. Spectators count: a team watching its last ship's crew
            // play is still in the match, and a seat held for a player who lost connection counts too.
            CountPresence();
            int empty = -1;
            for (int team = 0; team < 2; team++)
            {
                if (Presence[team] > 0) TeamHadPlayers[team] = true;
                else if (TeamHadPlayers[team]) empty = team;
            }

            if (!host) return;
            ClientGame game = ClientGame.Current;
            if (game == null || game.GameState == ClientGameState.Ended || _ending) return;

            if (tookOver && _loserTeam < 0) TakeOver();
            if (_loserTeam >= 0)
            {
                // The last host's result (heard from it, or taken over): this host finishes the match.
                EndSoon(_loserTeam, _forfeit, 3f);
                return;
            }

            if (empty < 0)
            {
                _emptyTeam = -1;
                return;
            }
            if (_emptyTeam != empty)
            {
                _emptyTeam = empty;
                _emptySince = Time.unscaledTime;
                Notice.ShowAll("Everyone on the " + PvpTeams.TeamName(empty) + " team has left. If nobody joins it within " + (int)ForfeitSeconds +
                               " seconds, the " + PvpTeams.TeamName(1 - empty) + " team wins.");
                return;
            }
            if (Time.unscaledTime - _emptySince < ForfeitSeconds) return;
            EndSoon(empty, true, 3f);
        }

        /// <summary>
        /// A player who has just taken over as host, without the last host's result: the room's copy of it when it checks out
        /// (that team has no living ship, or it had players and has none now), else the team this player saw lose its last
        /// ship. Nothing else ever takes the room's copy: any player can write it.
        /// </summary>
        private static void TakeOver()
        {
            int loser = RoomLoser();
            bool forfeit;
            if (loser >= 0 && HasLost(loser, out forfeit))
            {
                Log.Info("PvP: taking over as host while the match ends; the last host's result stands: the " + PvpTeams.TeamName(loser) + " team lost.");
            }
            else
            {
                if (loser >= 0) Log.Warn("PvP: the room says the " + PvpTeams.TeamName(loser) + " team lost, but it hasn't; the new host ignores it.");
                loser = _seenLoser;
                if (loser < 0 || !HasLost(loser, out forfeit)) return;
                Log.Info("PvP: taking over as host; the " + PvpTeams.TeamName(loser) + " team has no ships left, so it loses.");
            }
            _loserTeam = loser;
            _forfeit = forfeit;
        }

        /// <summary>
        /// Whether a team has lost the match as this player sees it: it has no living ship (while this player has the
        /// match's ships), or (a forfeit) it had players and has none now.
        /// </summary>
        private static bool HasLost(int team, out bool forfeit)
        {
            forfeit = false;
            if (FleetShips.All.Count > 0 && FleetShips.AliveSlotsOfTeam(team).Count == 0) return true;
            forfeit = TeamHadPlayers[team] && Presence[team] == 0;
            return forfeit;
        }

        /// <summary>
        /// Each team's players in the room (spectators included), plus seats held for players who lost connection, into
        /// Presence (0 BLUE, 1 RED).
        /// </summary>
        private static void CountPresence()
        {
            Presence[0] = Presence[1] = 0;
            Photon.Realtime.Room room = PhotonNetwork.CurrentRoom;
            if (room != null)
            {
                foreach (int actor in room.Players.Keys)
                {
                    CrewEntry entry;
                    if (FleetShips.TryGetEntry(actor, out entry) && (entry.Team == 0 || entry.Team == 1)) Presence[entry.Team]++;
                }
            }
            List<RosterSeat> seats = PvpRoster.Seats();
            if (seats == null) return;
            Presence[0] += RosterRules.Held(seats, 0);
            Presence[1] += RosterRules.Held(seats, 1);
        }

        /// <summary>The local player's result: Defeat for the losing team, and for a player who was never on a team.</summary>
        private static QuestResult LocalResult(int loser)
        {
            int mine = FleetShips.TeamOfActor(FleetShips.LocalActor);
            return mine < 0 || mine == loser ? QuestResult.Defeat : QuestResult.Victory;
        }

        /// <summary>
        /// ClientGame.EndGame is a network call (a PunRPC) the game never sends itself: called on the host it ends the mission for
        /// everyone, called on another player it ends it for them. In a fleet mission only the host's own call counts (and the
        /// game event it sends everyone: GameEventPrefix): a call another player sent (a modified game, to end a Fleet Battle
        /// with the result it chooses), or one replayed from the room after its sender left, is refused on every player's
        /// game. When RpcSender isn't in, the call counts as the game would count it. In PvP the host's own end screen shows
        /// its own team's result (from its own decision, never the room's copy).
        /// </summary>
        [GamePatch(Feature, "CG.Game.ClientGame", "EndGame", PatchKind.Prefix)]
        private static bool EndGamePrefix(ClientGame __instance, ref byte playerQuestResult)
        {
            try
            {
                if (FleetSession.Mode == FleetMode.OneShip) return true;
                PhotonView view = __instance != null ? __instance.photonView : null;
                int actor;
                bool fromOther = RpcSender.Of(view, out actor) ? actor != FleetShips.LocalActor && !Net.FromHost(actor) : RpcSender.FromAbsentPlayer(view);
                if (fromOther)
                {
                    Log.WarnOnce("end game from " + actor, (actor >= 0 ? "Player " + actor : "A player who has left") +
                                                           " sent the end of the mission, which only the host's game may end; ignored.");
                    return false;
                }
                if (!FleetSession.IsPvP) return true;
                int loser = LoserTeam;
                if (loser >= 0) playerQuestResult = (byte)LocalResult(loser);
            }
            catch (Exception e)
            {
                Log.Failure("Setting the PvP result", e);
            }
            return true;
        }

        /// <summary>
        /// The game's state events (ready, running, ended) come from the host's game alone, and set the state of every other
        /// player's game. In a fleet mission one that another player in the room sent (a modified game, to end the mission for
        /// everyone) is ignored, on every player's game. Events the room keeps for players who join later, the host's (who may
        /// have left since), count as before. In PvP every other player's end screen shows their own team's result, whatever
        /// result the host sent: by the host's decision (its message, or the room's copy for a player who joined after it).
        /// </summary>
        [GamePatch(Feature, "CG.Game.ClientGame", "OnEvent", PatchKind.Prefix)]
        private static bool GameEventPrefix(EventData photonEvent)
        {
            try
            {
                if (photonEvent == null || photonEvent.Code != 2 || FleetSession.Mode == FleetMode.OneShip) return true;
                int sender = photonEvent.Sender;
                Photon.Realtime.Room room = PhotonNetwork.CurrentRoom;
                if (sender > 0 && !Net.FromHost(sender) && room != null && room.GetPlayer(sender) != null)
                {
                    Log.WarnOnce("game state from " + sender, "Player " + sender + " sent a change of the mission's state, which only the host's game sends; ignored.");
                    return false;
                }
                if (!FleetSession.IsPvP) return true;
                var data = photonEvent.CustomData as object[];
                if (data == null || data.Length < 2 || Convert.ToInt32(data[0]) != (int)ClientGameState.Ended) return true;
                int loser = LoserTeam;
                if (loser >= 0) data[1] = (byte)LocalResult(loser);
            }
            catch (Exception e)
            {
                Log.Failure("Setting the PvP result", e);
            }
            return true;
        }

        /// <summary>Host: a new match has no result yet.</summary>
        public static void ResetRoom()
        {
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyLoser, null } });
        }
    }
}
