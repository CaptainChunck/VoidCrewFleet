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

        /// <summary>When they left (server milliseconds).</summary>
        public long AwaySince;

        /// <summary>Their seat was given up: they were away too long (a waiting player gives none up).</summary>
        public bool Expired;

        /// <summary>When they started waiting for a seat (server milliseconds): the first waiter gets the next seat.</summary>
        public long WaitingSince;

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
    /// The rules for who plays where when players leave and come back, with nothing of the game, Unity or Photon in them (they
    /// are tested offline). Both kinds of mission:
    /// - a player's ship is the one they were given in the lobby (or when they joined); when it is destroyed, everyone given
    ///   that ship is DEAD for the rest of the mission, there or away: they play on while their character lives, then watch;
    /// - a player who comes back returns to their ship.
    /// PvP (a team match) also:
    /// - each team has a number of seats, fixed when the match starts (the larger team's size);
    /// - a seat is taken by a player on a living ship (in the room, or away for less than the hold time), and for good by a
    ///   player whose ship was destroyed (a DEAD spectator);
    /// - a player who leaves keeps their seat for the hold time; back in time, they return to their team and ship;
    /// - when a hold runs out, the seat is free: a WAITING player of that team gets it first (the longest waiting first), then
    ///   a waiting player without a team, then anyone joining;
    /// - a player who comes back too late plays again if their team has a free seat, else waits on their team;
    /// - a newcomer joins the team with a free seat and fewer players, else waits for any seat.
    /// Co-op: nobody waits; a newcomer joins the living ship with the smallest crew.
    /// </summary>
    internal static class RosterRules
    {
        /// <summary>
        /// Seats a team uses: players on a living ship (present or held), and dead spectators (unless the seat was already
        /// given up when the ship was lost).
        /// </summary>
        public static int Occupied(IEnumerable<RosterSeat> seats, int team)
        {
            return seats.Count(s => s.Team == team && !s.Expired && (s.State == CrewState.Dead || s.State == CrewState.Playing));
        }

        public static int FreeSeats(IEnumerable<RosterSeat> seats, int team, int seatsPerTeam)
        {
            return Math.Max(0, seatsPerTeam - Occupied(seats, team));
        }

        /// <summary>Holds that ran out: those seats are free now. True when anything changed.</summary>
        public static bool ExpireHolds(List<RosterSeat> seats, long now, long holdMs)
        {
            bool changed = false;
            foreach (RosterSeat seat in seats)
            {
                if (!seat.Away || seat.Expired || seat.State != CrewState.Playing || now - seat.AwaySince < holdMs) continue;
                seat.Expired = true;
                changed = true;
            }
            return changed;
        }

        /// <summary>Holds left for a team: players away whose seat is still theirs.</summary>
        public static int Held(IEnumerable<RosterSeat> seats, int team)
        {
            return seats.Count(s => s.Team == team && s.Away && !s.Expired && s.State == CrewState.Playing);
        }

        /// <summary>The smallest crew among a team's living ships (every living ship in co-op); -1 when none.</summary>
        public static int SmallestCrew(IEnumerable<RosterSeat> seats, IList<int> aliveSlots, bool teamMatch, int team)
        {
            int best = -1;
            int bestCount = int.MaxValue;
            foreach (int slot in aliveSlots)
            {
                if (teamMatch && slot % 2 != team) continue;
                int count = seats.Count(s => s.Slot == slot && s.State == CrewState.Playing && !s.Expired);
                if (count < bestCount)
                {
                    best = slot;
                    bestCount = count;
                }
            }
            return best;
        }

        /// <summary>Puts a player on a living ship of their team: their own when it lives. False when the team has none.</summary>
        private static bool Seat(List<RosterSeat> seats, RosterSeat seat, IList<int> aliveSlots, bool teamMatch)
        {
            int slot = seat.Slot >= 0 && aliveSlots.Contains(seat.Slot) && (!teamMatch || seat.Slot % 2 == seat.Team)
                ? seat.Slot
                : SmallestCrew(seats.Where(s => s != seat), aliveSlots, teamMatch, seat.Team);
            if (slot < 0) return false;
            seat.Slot = slot;
            seat.State = CrewState.Playing;
            seat.Expired = false;
            return true;
        }

        /// <summary>
        /// A player is in the room (joined, came back, or was already there): their seat, created or updated. The changes go into
        /// the list; returns the player's seat.
        /// </summary>
        public static RosterSeat Arrive(List<RosterSeat> seats, string id, int actor, string name, IList<int> aliveSlots, bool teamMatch, int seatsPerTeam, long now)
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
                // A newcomer: the team with a free seat and fewer players (BLUE on ties), else wait for any seat.
                int blueFree = FreeSeats(seats, 0, seatsPerTeam);
                int redFree = FreeSeats(seats, 1, seatsPerTeam);
                int team = -1;
                if (blueFree > 0 && redFree > 0) team = Occupied(seats, 1) < Occupied(seats, 0) ? 1 : 0;
                else if (blueFree > 0) team = 0;
                else if (redFree > 0) team = 1;
                if (team >= 0)
                {
                    seat.Team = team;
                    if (Seat(seats, seat, aliveSlots, true)) return seat;
                }
                seat.Team = team;
                seat.State = CrewState.Waiting;
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
            bool held = seat.State == CrewState.Playing && !seat.Expired;
            if (held || (seat.Team >= 0 && FreeSeats(seats.Where(s => s != seat), seat.Team, seatsPerTeam) > 0))
            {
                if (Seat(seats, seat, aliveSlots, true)) return seat;
                // Their team has no ship left (the match is ending): they watch.
                seat.State = CrewState.Dead;
                seat.Expired = false;
                return seat;
            }
            // Their seat went to someone else: they wait on their team for the next one.
            seat.State = CrewState.Waiting;
            seat.Slot = -1;
            seat.Expired = false;
            seat.WaitingSince = now;
            return seat;
        }

        /// <summary>A player left the room: their seat is held (PvP) or kept for their return (co-op).</summary>
        public static void Leave(List<RosterSeat> seats, string id, long now)
        {
            RosterSeat seat = seats.FirstOrDefault(s => s.Id == id);
            if (seat == null || seat.Away) return;
            seat.Away = true;
            seat.AwaySince = now;
        }

        /// <summary>
        /// A ship was destroyed: everyone given that ship is DEAD from now on, in the room or away. Their seat stays taken; one
        /// already given up (a PvP hold that ran out) stays free, but its player comes back DEAD all the same.
        /// </summary>
        public static void ShipLost(List<RosterSeat> seats, int slot)
        {
            foreach (RosterSeat seat in seats)
            {
                if (seat.Slot == slot && seat.State == CrewState.Playing) seat.State = CrewState.Dead;
            }
        }

        /// <summary>
        /// Free seats go to waiting players in the room: a team's own waiters first (longest waiting first), then waiters
        /// without a team. True when anyone got a seat.
        /// </summary>
        public static bool FillSeats(List<RosterSeat> seats, IList<int> aliveSlots, int seatsPerTeam)
        {
            bool changed = false;
            for (int team = 0; team < 2; team++)
            {
                while (FreeSeats(seats, team, seatsPerTeam) > 0)
                {
                    RosterSeat next = seats.Where(s => !s.Away && s.State == CrewState.Waiting && s.Team == team).OrderBy(s => s.WaitingSince).FirstOrDefault() ??
                                      seats.Where(s => !s.Away && s.State == CrewState.Waiting && s.Team < 0).OrderBy(s => s.WaitingSince).FirstOrDefault();
                    if (next == null) break;
                    int previousTeam = next.Team;
                    next.Team = team;
                    if (!Seat(seats, next, aliveSlots, true))
                    {
                        next.Team = previousTeam;
                        break;
                    }
                    changed = true;
                }
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
