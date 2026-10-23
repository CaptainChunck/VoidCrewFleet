using System;
using System.Collections.Generic;
using System.Linq;

namespace VoidCrewFleet
{
    /// <summary>A player's place in a fleet mission, kept by account ID so it survives a lost connection.</summary>
    internal sealed class RosterSeat
    {
        /// <summary>The player's stable ID (PvpRoster.IdOf).</summary>
        public string Id;

        public string Name;

        /// <summary>Their actor number in the room now (it changes when they come back).</summary>
        public int Actor;

        /// <summary>0 BLUE, 1 RED in PvP (0 in co-op); -1 for a waiting player not yet on a team.</summary>
        public int Team;

        /// <summary>Their ship (a dead spectator keeps their lost ship's); -1 while waiting.</summary>
        public int Slot;

        public CrewState State;

        /// <summary>Not in the room right now.</summary>
        public bool Away;

        /// <summary>When they left (server milliseconds; see RosterRules.Elapsed).</summary>
        public int AwaySince;

        /// <summary>Their place was given up: they were away too long (a waiting player gives none up).</summary>
        public bool Expired;

        /// <summary>When they started waiting for a place (server milliseconds): the longest waiting gets the next place.</summary>
        public int WaitingSince;

        public RosterSeat Clone()
        {
            return (RosterSeat)MemberwiseClone();
        }

        public override string ToString()
        {
            return Id + "(" + Actor + "):" + Team + "/" + Slot + "/" + State + (Away ? "/away" : "") + (Expired ? "/expired" : "");
        }
    }

    /// <summary>
    /// The rules for who plays where when players join, leave and come back, with nothing of the game, Unity or Photon in
    /// them (they are tested offline). Both kinds of mission:
    /// - a player's ship is the one they were given in the lobby (or when they joined); when it is destroyed, everyone given
    ///   that ship is DEAD for the rest of the mission, there or away: they play on while their character lives, then watch;
    /// - a player who comes back returns to their ship.
    /// PvP (a team match) also, so that anyone who joins while the room has space plays, and the teams stay fair:
    /// - a team's ACTIVE players are those on a living ship, in the room or away for less than the hold time (dead
    ///   spectators fight no more and don't count);
    /// - a team has a free place while it has no more active players than the other team, and one of its living ships has
    ///   fewer than CrewRules.MaxCrew players: a player joining it never makes the teams more than one apart;
    /// - a player who leaves keeps their place for the hold time; back in time, they return to their team and ship;
    /// - free places go to the WAITING players in the room first: each team's own waiters, then those without a team, one at
    ///   a time to the team with fewer active players (BLUE on a tie); the longest waiting first;
    /// - a newcomer gets in line behind waiting players who could take a free place; otherwise they join the team with a free
    ///   place and fewer active players (BLUE on a tie), on its ship with the smallest crew, or wait when neither has one;
    /// - a player who comes back too late plays again on their own team when it has a free place none of their team waits
    ///   for (on their own ship when it has room, else on the team's ship with the smallest crew), else waits on their team;
    /// - a waiting player who leaves keeps their place in line for the hold time; after that they are forgotten.
    /// Co-op: nobody waits; a newcomer joins the living ship with the smallest crew.
    /// Times are the server's clock in milliseconds, an int that wraps around: they are only ever compared by their
    /// difference (Elapsed), which stays right across the wrap.
    /// </summary>
    internal static class RosterRules
    {
        /// <summary>Milliseconds from one server time to a later one, right across the clock's wrap.</summary>
        public static int Elapsed(int now, int then)
        {
            return unchecked(now - then);
        }

        /// <summary>A team's active players: on a living ship, in the room or away with their place still held.</summary>
        public static int Active(IEnumerable<RosterSeat> seats, int team)
        {
            return seats.Count(s => s.Team == team && s.State == CrewState.Playing && !s.Expired);
        }

        /// <summary>Players on a ship: on it now, or away with their place on it held.</summary>
        private static int CrewOf(IEnumerable<RosterSeat> seats, int slot)
        {
            return seats.Count(s => s.Slot == slot && s.State == CrewState.Playing && !s.Expired);
        }

        /// <summary>
        /// Whether a team has a free place for one more player: it has no more active players than the other team, and a
        /// living ship with room in its crew. Players in except (one being placed) are left out of the counts.
        /// </summary>
        public static bool HasPlace(IEnumerable<RosterSeat> seats, int team, IList<int> aliveSlots, RosterSeat except = null)
        {
            if (team < 0 || team > 1) return false;
            List<RosterSeat> others = seats.Where(s => s != except).ToList();
            if (Active(others, team) > Active(others, 1 - team)) return false;
            return SmallestCrew(others, aliveSlots, true, team, CrewRules.MaxCrew) >= 0;
        }

        /// <summary>Holds that ran out: those places are free now. True when anything changed.</summary>
        public static bool ExpireHolds(List<RosterSeat> seats, int now, int holdMs)
        {
            bool changed = false;
            foreach (RosterSeat seat in seats)
            {
                if (!seat.Away || seat.Expired || seat.State != CrewState.Playing || Elapsed(now, seat.AwaySince) < holdMs) continue;
                seat.Expired = true;
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// Waiting players who left and stayed away for the hold time hold nothing (no place, no ship): they are forgotten,
        /// so the roster doesn't keep everyone who ever passed through. Dead players and expired holds stay: a player who
        /// comes back must still find their lost ship, or their team. True when anyone was forgotten.
        /// </summary>
        public static bool DropGone(List<RosterSeat> seats, int now, int holdMs)
        {
            return seats.RemoveAll(s => s.Away && s.State == CrewState.Waiting && Elapsed(now, s.AwaySince) >= holdMs) > 0;
        }

        /// <summary>Holds left for a team: players away whose place is still theirs.</summary>
        public static int Held(IEnumerable<RosterSeat> seats, int team)
        {
            return seats.Count(s => s.Team == team && s.Away && !s.Expired && s.State == CrewState.Playing);
        }

        /// <summary>
        /// The living ship with the smallest crew, among a team's ships in a team match (every ship in co-op), leaving out
        /// ships whose crew has reached the limit; -1 when there is none.
        /// </summary>
        public static int SmallestCrew(IEnumerable<RosterSeat> seats, IList<int> aliveSlots, bool teamMatch, int team, int limit = int.MaxValue)
        {
            int best = -1;
            int bestCount = int.MaxValue;
            foreach (int slot in aliveSlots)
            {
                if (teamMatch && slot % 2 != team) continue;
                int count = CrewOf(seats, slot);
                if (count >= limit || count >= bestCount) continue;
                best = slot;
                bestCount = count;
            }
            return best;
        }

        /// <summary>
        /// Puts a player on a living ship of their team: their own when it lives (unless their place on it was given up and,
        /// in a team match, its crew is full), else the one with the smallest crew (with room in a team match). False when
        /// there is none.
        /// </summary>
        private static bool Seat(List<RosterSeat> seats, RosterSeat seat, IList<int> aliveSlots, bool teamMatch)
        {
            int limit = teamMatch ? CrewRules.MaxCrew : int.MaxValue;
            List<RosterSeat> others = seats.Where(s => s != seat).ToList();
            // A player back too late finds their place given away: their own ship only when it still has room.
            bool own = seat.Slot >= 0 && aliveSlots.Contains(seat.Slot) && (!teamMatch || seat.Slot % 2 == seat.Team) &&
                       (!seat.Expired || CrewOf(others, seat.Slot) < limit);
            int slot = own ? seat.Slot : SmallestCrew(others, aliveSlots, teamMatch, seat.Team, limit);
            if (slot < 0) return false;
            seat.Slot = slot;
            seat.State = CrewState.Playing;
            seat.Expired = false;
            return true;
        }

        /// <summary>
        /// The team a player without a team joins: of those with a free place, the one with fewer active players (BLUE on a
        /// tie); -1 when neither has one.
        /// </summary>
        private static int BestTeam(List<RosterSeat> seats, IList<int> aliveSlots, RosterSeat except)
        {
            bool blue = HasPlace(seats, 0, aliveSlots, except);
            bool red = HasPlace(seats, 1, aliveSlots, except);
            if (blue && red) return Active(seats.Where(s => s != except), 1) < Active(seats.Where(s => s != except), 0) ? 1 : 0;
            return blue ? 0 : (red ? 1 : -1);
        }

        /// <summary>Whether a waiting player in the room could take a free place now (their team's, or any if they have none).</summary>
        private static bool CouldPlay(List<RosterSeat> seats, RosterSeat seat, IList<int> aliveSlots)
        {
            if (seat.Away || seat.State != CrewState.Waiting) return false;
            return seat.Team >= 0 ? HasPlace(seats, seat.Team, aliveSlots, seat) : BestTeam(seats, aliveSlots, seat) >= 0;
        }

        /// <summary>The player in the room who has waited longest for a place on this team (-1: those without a team), or null.</summary>
        private static RosterSeat LongestWaiting(List<RosterSeat> seats, int team, int now)
        {
            RosterSeat best = null;
            int bestWait = 0;
            foreach (RosterSeat seat in seats)
            {
                if (seat.Away || seat.State != CrewState.Waiting || (team < 0 ? seat.Team >= 0 : seat.Team != team)) continue;
                int wait = Elapsed(now, seat.WaitingSince);
                if (best != null && wait <= bestWait) continue;
                best = seat;
                bestWait = wait;
            }
            return best;
        }

        /// <summary>
        /// A player is in the room (joined, came back, or was already there): their place, created or updated. The changes go
        /// into the list; returns the player's place.
        /// </summary>
        public static RosterSeat Arrive(List<RosterSeat> seats, string id, int actor, string name, IList<int> aliveSlots, bool teamMatch, int now)
        {
            RosterSeat seat = seats.FirstOrDefault(s => s.Id == id);
            if (seat != null && !seat.Away)
            {
                seat.Actor = actor;
                seat.Name = name;
                return seat;
            }
            if (seat == null)
            {
                seat = new RosterSeat { Id = id, Actor = actor, Name = name, Team = -1, Slot = -1, State = CrewState.Waiting, WaitingSince = now };
                seats.Add(seat);
                if (!teamMatch)
                {
                    seat.Team = 0;
                    if (!Seat(seats, seat, aliveSlots, false)) seat.State = CrewState.Waiting;
                    return seat;
                }
                // A newcomer gets in line behind the waiting players who could take a free place (FillSeats gives it to them).
                if (seats.Any(s => s != seat && CouldPlay(seats, s, aliveSlots))) return seat;
                int team = BestTeam(seats, aliveSlots, seat);
                if (team < 0) return seat;
                seat.Team = team;
                if (Seat(seats, seat, aliveSlots, true)) return seat;
                seat.Team = -1;
                return seat;
            }

            // Coming back.
            seat.Actor = actor;
            seat.Name = name;
            seat.Away = false;
            if (seat.State == CrewState.Dead) return seat;
            if (!teamMatch)
            {
                if (!Seat(seats, seat, aliveSlots, false)) seat.State = CrewState.Waiting;
                return seat;
            }
            // Back in line, where they were: FillSeats gives them a place in their turn.
            if (seat.State == CrewState.Waiting) return seat;
            bool held = !seat.Expired;
            bool ownWaiting = seats.Any(s => s != seat && !s.Away && s.State == CrewState.Waiting && s.Team == seat.Team);
            if (held || (seat.Team >= 0 && !ownWaiting && HasPlace(seats, seat.Team, aliveSlots, seat)))
            {
                if (Seat(seats, seat, aliveSlots, true)) return seat;
                // Their team has no ship left (the match is ending): they watch.
                seat.State = CrewState.Dead;
                seat.Expired = false;
                return seat;
            }
            // Their place went to someone else (or others of their team wait for the next one): they wait on their team.
            seat.State = CrewState.Waiting;
            seat.Slot = -1;
            seat.Expired = false;
            seat.WaitingSince = now;
            return seat;
        }

        /// <summary>A player left the room: their place is held (PvP) or kept for their return (co-op).</summary>
        public static void Leave(List<RosterSeat> seats, string id, int now)
        {
            RosterSeat seat = seats.FirstOrDefault(s => s.Id == id);
            if (seat == null || seat.Away) return;
            seat.Away = true;
            seat.AwaySince = now;
        }

        /// <summary>
        /// A ship was destroyed: everyone given that ship is DEAD from now on, in the room or away. One whose place was already
        /// given up (a PvP hold that ran out) comes back DEAD all the same.
        /// </summary>
        public static void ShipLost(List<RosterSeat> seats, int slot)
        {
            foreach (RosterSeat seat in seats)
            {
                if (seat.Slot == slot && seat.State == CrewState.Playing) seat.State = CrewState.Dead;
            }
        }

        /// <summary>
        /// Free places go to the waiting players in the room: each team's own waiters first, then the waiters without a team
        /// one at a time, each to the team with fewer active players (BLUE on a tie); the longest waiting first. True when
        /// anyone got a place.
        /// </summary>
        public static bool FillSeats(List<RosterSeat> seats, IList<int> aliveSlots, int now)
        {
            bool changed = false;
            for (int team = 0; team < 2; team++)
            {
                while (true)
                {
                    RosterSeat next = LongestWaiting(seats, team, now);
                    if (next == null || !HasPlace(seats, team, aliveSlots, next) || !Seat(seats, next, aliveSlots, true)) break;
                    changed = true;
                }
            }
            while (true)
            {
                RosterSeat next = LongestWaiting(seats, -1, now);
                if (next == null) break;
                int team = BestTeam(seats, aliveSlots, next);
                if (team < 0) break;
                next.Team = team;
                if (!Seat(seats, next, aliveSlots, true))
                {
                    next.Team = -1;
                    break;
                }
                changed = true;
            }
            return changed;
        }

        /// <summary>The crew entries of the players in the room (actor number to entry).</summary>
        public static Dictionary<int, CrewEntry> Crews(IEnumerable<RosterSeat> seats)
        {
            var crews = new Dictionary<int, CrewEntry>();
            foreach (RosterSeat seat in seats)
            {
                if (seat.Away) continue;
                crews[seat.Actor] = new CrewEntry { Actor = seat.Actor, Team = seat.Team, Slot = seat.State == CrewState.Waiting ? -1 : seat.Slot, State = seat.State };
            }
            return crews;
        }
    }
}
