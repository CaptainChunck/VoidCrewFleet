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
    /// has ships, losing one only sends its crew to watch (ShipDeath). The host ends the game a few seconds after the last
    /// ship starts to explode, and each player's end screen says Victory or Defeat for their own team (the game sends one
    /// result for everyone). A team that has lost all its players forfeits after 30 seconds.
    /// </summary>
    internal static class PvpMatch
    {
        public const string Feature = "PvP match result";

        private const string KeyLoser = "VCF_LoserTeam";
        private const float EndDelaySeconds = 12f;
        private const float ForfeitSeconds = 30f;

        private static object _session;
        private static int _loserTeam = -1;
        private static bool _ending;
        private static readonly bool[] TeamHadPlayers = new bool[2];
        private static int _emptyTeam = -1;
        private static float _emptySince;

        public static void Listen()
        {
            ShipDeath.ShipLost += OnShipLost;
        }

        /// <summary>The losing team: from the host's room property, else from the ships this client saw explode.</summary>
        public static int LoserTeam
        {
            get
            {
                object value;
                if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(KeyLoser, out value) && value is int)
                    return (int)value;
                return _loserTeam;
            }
        }

        /// <summary>
        /// A ship starts dying. A team with another living ship fights on; the first team to lose its last ship (or forfeit)
        /// decides the match. The host announces every ship's death in turn, so every client sees the same order; a ship that
        /// starts dying after the match is decided changes nothing.
        /// </summary>
        private static void OnShipLost(PlayerControlledShip ship)
        {
            if (!FleetSession.IsPvP) return;
            int slot = FleetShips.Slot(ship);
            if (slot < 0) return;
            int team = FleetShips.TeamOfSlot(slot);
            int decided = LoserTeam;
            if (decided >= 0)
            {
                if (decided != team)
                    Log.Info("PvP: " + FleetSlots.Name(slot, true) + " is destroyed too, after the match was decided; the " + PvpTeams.TeamName(1 - decided) +
                             " team still wins.");
                return;
            }
            if (ShipDeath.TeamFightsOn(team, ship))
            {
                int left = FleetShips.AliveSlotsOfTeam(team).Count(s => s != slot);
                Notice.Show(FleetSlots.Name(slot, true) + " is destroyed! The " + PvpTeams.TeamName(team) + " team fights on with " + left + (left == 1 ? " ship." : " ships."));
                return;
            }
            _loserTeam = team;
            Notice.Show("The " + PvpTeams.TeamName(team) + " team has no ships left: the " + PvpTeams.TeamName(1 - team) + " team wins!");
            if (PhotonNetwork.IsMasterClient) EndSoon(team, EndDelaySeconds);
        }

        /// <summary>Host: record the loser for everyone, then end the game once the explosion has played.</summary>
        private static void EndSoon(int loser, float delay)
        {
            if (_ending || FleetRuntime.Instance == null) return;
            _ending = true;
            _loserTeam = loser;
            try
            {
                PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyLoser, loser } });
            }
            catch (Exception e)
            {
                Log.Failure("Recording the PvP result", e);
            }
            FleetRuntime.Instance.StartCoroutine(EndAfter(delay));
        }

        private static IEnumerator EndAfter(float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            try
            {
                ClientGame game = ClientGame.Current;
                if (game == null || !PhotonNetwork.IsMasterClient || !FleetSession.IsPvP || game.GameState == ClientGameState.Ended) yield break;
                Log.Info("PvP: the match is over; the " + PvpTeams.TeamName(1 - LoserTeam) + " team won.");
                game.EndGame((byte)QuestResult.Victory);
            }
            catch (Exception e)
            {
                Log.Failure("Ending the PvP match", e);
            }
        }

        /// <summary>
        /// A few times a second: a new match forgets the last one; every client notes which teams have had players; the host
        /// finishes a match a previous host had decided, and makes a team that has lost all its players forfeit.
        /// </summary>
        public static void Tick()
        {
            object session = GameSessionManager.ActiveSession;
            if (!ReferenceEquals(session, _session))
            {
                _session = session;
                _loserTeam = -1;
                _ending = false;
                TeamHadPlayers[0] = TeamHadPlayers[1] = false;
                _emptyTeam = -1;
            }
            if (!FleetSession.IsPvP || !PvpTeams.Locked || GameSessionManager.InHub) return;

            // Every client keeps this, not only the host: when the host leaves, the player who takes over still knows that a
            // team now empty had players, and declares its forfeit. Spectators count: a team watching its last ship's crew
            // play is still in the match, and a seat held for a player who lost connection counts too.
            int empty = -1;
            for (int team = 0; team < 2; team++)
            {
                int count = TeamPresence(team);
                if (count > 0) TeamHadPlayers[team] = true;
                else if (TeamHadPlayers[team]) empty = team;
            }

            if (!PhotonNetwork.IsMasterClient) return;
            ClientGame game = ClientGame.Current;
            if (game == null || game.GameState == ClientGameState.Ended || _ending) return;

            int decided = LoserTeam;
            if (decided >= 0)
            {
                EndSoon(decided, 3f);
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
            Notice.ShowAll("The " + PvpTeams.TeamName(empty) + " team forfeits: the " + PvpTeams.TeamName(1 - empty) + " team wins!");
            EndSoon(empty, 3f);
        }

        /// <summary>Players of a team in the room (spectators included), plus seats held for players who lost connection.</summary>
        private static int TeamPresence(int team)
        {
            var players = new HashSet<int>(ShipSpawn.PlayerActors());
            int count = 0;
            foreach (CrewEntry entry in FleetShips.Crews().Values)
            {
                if (entry.Team == team && players.Contains(entry.Actor)) count++;
            }
            return count + PvpRoster.HeldSeats(team);
        }

        private static QuestResult LocalResult(int loser)
        {
            int mine = FleetShips.TeamOfActor(FleetShips.LocalActor);
            return mine == loser ? QuestResult.Defeat : QuestResult.Victory;
        }

        /// <summary>The host's own end screen shows its own team's result.</summary>
        [GamePatch(Feature, "CG.Game.ClientGame", "EndGame", PatchKind.Prefix)]
        private static void EndGamePrefix(ref byte playerQuestResult)
        {
            try
            {
                int loser = LoserTeam;
                if (FleetSession.IsPvP && loser >= 0) playerQuestResult = (byte)LocalResult(loser);
            }
            catch (Exception e)
            {
                Log.Failure("Setting the PvP result", e);
            }
        }

        /// <summary>Every other player's end screen shows their own team's result, whatever result the host sent.</summary>
        [GamePatch(Feature, "CG.Game.ClientGame", "OnEvent", PatchKind.Prefix)]
        private static void GameEventPrefix(EventData photonEvent)
        {
            try
            {
                if (photonEvent == null || photonEvent.Code != 2 || !FleetSession.IsPvP) return;
                var data = photonEvent.CustomData as object[];
                if (data == null || data.Length < 2 || Convert.ToInt32(data[0]) != (int)ClientGameState.Ended) return;
                int loser = LoserTeam;
                if (loser >= 0) data[1] = (byte)LocalResult(loser);
            }
            catch (Exception e)
            {
                Log.Failure("Setting the PvP result", e);
            }
        }

        /// <summary>Host: a new match has no result yet.</summary>
        public static void ResetRoom()
        {
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyLoser, null } });
        }
    }
}
