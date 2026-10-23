using System;
using System.Collections.Generic;
using System.Linq;

namespace VoidCrewFleet
{
    /// <summary>What a player is doing in a mission (or in the hub, for the ship they picked).</summary>
    internal enum CrewState : byte
    {
        /// <summary>Crews their ship (or picked it, in the hub).</summary>
        Playing = 0,

        /// <summary>PvP: alive but without a seat yet; spawns when one opens on their team.</summary>
        Waiting = 1,

        /// <summary>Their ship was destroyed: no respawn this mission (they play on while their character lives, then watch).</summary>
        Dead = 2
    }

    /// <summary>One player's place: their team, their ship (slot) and what they are doing.</summary>
    internal struct CrewEntry
    {
        public int Actor;

        /// <summary>0 BLUE, 1 RED in PvP (0 in co-op); -1 for a waiting player not yet on a team.</summary>
        public int Team;

        /// <summary>The ship they crew (a dead spectator keeps their lost ship's); -1 when waiting.</summary>
        public int Slot;

        public CrewState State;

        public bool IsSpectator
        {
            get { return State != CrewState.Playing; }
        }

        public static CrewEntry Play(int actor, int team, int slot)
        {
            return new CrewEntry { Actor = actor, Team = team, Slot = slot, State = CrewState.Playing };
        }

        public override string ToString()
        {
            return Actor + ":" + Team + "/" + Slot + (State == CrewState.Playing ? "" : "/" + State);
        }
    }

    /// <summary>
    /// The rules for who crews which ship, with nothing of the game, Unity or Photon in them (they are tested offline).
    /// Ships are slots. In a team match a slot's team is slot % 2 and each team has the same number of ships; otherwise every
    /// ship is on team 0. The players passed in are the ones in the room; entries of anyone else are ignored.
    /// </summary>
    internal static class CrewRules
    {
        /// <summary>The most players one ship's crew may have.</summary>
        public const int MaxCrew = 8;

        public static int TeamOf(int slot, bool teamMatch)
        {
            return slot < 0 ? -1 : (teamMatch ? slot % 2 : 0);
        }

        /// <summary>The slots of a team (every slot outside a team match).</summary>
        public static List<int> SlotsOfTeam(int team, int ships, bool teamMatch)
        {
            var slots = new List<int>();
            for (int slot = 0; slot < ships; slot++)
            {
                if (!teamMatch || slot % 2 == team) slots.Add(slot);
            }
            return slots;
        }

        /// <summary>Players crewing a ship (spectators not counted).</summary>
        public static int CrewOf(IDictionary<int, CrewEntry> crews, ICollection<int> players, int slot, int except = int.MinValue)
        {
            int count = 0;
            foreach (CrewEntry entry in crews.Values)
            {
                if (entry.Actor != except && entry.State == CrewState.Playing && entry.Slot == slot && players.Contains(entry.Actor)) count++;
            }
            return count;
        }

        /// <summary>Players on a team: crews, and spectators too when asked.</summary>
        public static int TeamSize(IDictionary<int, CrewEntry> crews, ICollection<int> players, int team, bool withSpectators, int except = int.MinValue)
        {
            int count = 0;
            foreach (CrewEntry entry in crews.Values)
            {
                if (entry.Actor == except || entry.Team != team || !players.Contains(entry.Actor)) continue;
                if (entry.State == CrewState.Playing || withSpectators) count++;
            }
            return count;
        }

        /// <summary>A team match: the most players a team may have, half the room rounded up.</summary>
        public static int MaxPerTeam(int players)
        {
            return Math.Max(1, (players + 1) / 2);
        }

        /// <summary>The most players one ship may have while picking, so crews stay even.</summary>
        public static int MaxPerShip(int players, int ships, bool teamMatch)
        {
            if (ships < 1) return MaxCrew;
            if (!teamMatch) return Math.Min(MaxCrew, Math.Max(1, (players + ships - 1) / ships));
            int perTeam = Math.Max(1, ships / 2);
            return Math.Min(MaxCrew, Math.Max(1, (MaxPerTeam(players) + perTeam - 1) / perTeam));
        }

        /// <summary>Null when a player may pick this ship now; otherwise why not.</summary>
        public static string CanJoin(IDictionary<int, CrewEntry> crews, ICollection<int> players, int actor, int slot, int ships, bool teamMatch,
            Func<int, string> shipName, Func<int, string> teamName)
        {
            if (slot < 0 || slot >= ships) return "That ship isn't in this mission.";
            int max = MaxPerShip(players.Count, ships, teamMatch);
            if (CrewOf(crews, players, slot, actor) >= max)
                return shipName(slot) + " is full: crews stay even (" + max + (max == 1 ? " player" : " players") + " each). Pick another ship.";
            if (teamMatch)
            {
                int team = TeamOf(slot, true);
                if (TeamSize(crews, players, team, true, actor) >= MaxPerTeam(players.Count))
                    return teamName(team) + " is full: teams stay even. Pick a " + teamName(1 - team) + " ship, or ask the host to move someone.";
            }
            return null;
        }

        /// <summary>
        /// The ship with the smallest crew among these slots (lowest slot on ties), so a ship with a full crew (MaxCrew) is
        /// only picked when none of the others has room. Ships whose crew has reached the limit are left out. -1 when there
        /// is none.
        /// </summary>
        public static int SmallestCrew(IDictionary<int, CrewEntry> crews, ICollection<int> players, IEnumerable<int> slots, int limit = int.MaxValue)
        {
            int best = -1;
            int bestCount = int.MaxValue;
            foreach (int slot in slots)
            {
                int count = CrewOf(crews, players, slot);
                if (count < bestCount && count < limit)
                {
                    best = slot;
                    bestCount = count;
                }
            }
            return best;
        }

        /// <summary>A team match: the team a newcomer joins, the smaller one (BLUE on ties).</summary>
        public static int SmallerTeam(IDictionary<int, CrewEntry> crews, ICollection<int> players)
        {
            return TeamSize(crews, players, 1, true) < TeamSize(crews, players, 0, true) ? 1 : 0;
        }

        /// <summary>
        /// Where a player without a ship goes: in a team match the smaller team (or the team given), then its smallest crew
        /// among the living ships that has room (MaxCrew), and on a tie the other team's when the smaller one is full;
        /// otherwise the smallest crew. -1 when no ship is left, or in a team match when no crew has room for them without
        /// putting the teams more than one apart (they then wait for a free place, as in a match).
        /// </summary>
        public static int PlaceFor(IDictionary<int, CrewEntry> crews, ICollection<int> players, ICollection<int> aliveSlots, bool teamMatch, int team = -1)
        {
            if (!teamMatch) return SmallestCrew(crews, players, aliveSlots);
            if (team >= 0) return SmallestCrew(crews, players, aliveSlots.Where(s => s % 2 == team), MaxCrew);
            int smaller = SmallerTeam(crews, players);
            int slot = SmallestCrew(crews, players, aliveSlots.Where(s => s % 2 == smaller), MaxCrew);
            if (slot < 0 && TeamSize(crews, players, 0, true) == TeamSize(crews, players, 1, true))
                slot = SmallestCrew(crews, players, aliveSlots.Where(s => s % 2 != smaller), MaxCrew);
            return slot;
        }

        /// <summary>
        /// Everyone in the room on a ship: picks on living ships stay, the rest (and anyone whose ship isn't there) go to the
        /// smallest crew (in a team match, their team's when they have one, else the smaller team's). Spectators stay as they are.
        /// In a team match nobody is put on a full crew (MaxCrew): who finds no room stays without a ship, and the roster has
        /// them wait for a free place. A ship picked by more than a full crew (players who joined while every ship was full)
        /// hands its latest joiners to the smallest crew with room, of their team in a team match: a ship the mission added
        /// for them, for example.
        /// </summary>
        public static Dictionary<int, CrewEntry> PlaceEveryone(IDictionary<int, CrewEntry> picks, IList<int> players, ICollection<int> aliveSlots, bool teamMatch)
        {
            var crews = new Dictionary<int, CrewEntry>();
            foreach (int actor in players)
            {
                CrewEntry entry;
                if (!picks.TryGetValue(actor, out entry)) continue;
                if (entry.IsSpectator || (entry.Slot >= 0 && aliveSlots.Contains(entry.Slot))) crews[actor] = entry;
            }
            foreach (int slot in aliveSlots)
            {
                while (CrewOf(crews, players, slot) > MaxCrew)
                {
                    int to = SmallestCrew(crews, players, aliveSlots.Where(s => s != slot && (!teamMatch || s % 2 == slot % 2)));
                    if (to < 0 || CrewOf(crews, players, to) >= MaxCrew || !MoveLatest(crews, players, slot, to, teamMatch)) break;
                }
            }
            foreach (int actor in players.OrderBy(a => a))
            {
                if (crews.ContainsKey(actor)) continue;
                CrewEntry pick;
                int team = teamMatch && picks.TryGetValue(actor, out pick) && pick.Team >= 0 ? pick.Team : -1;
                int slot = PlaceFor(crews, players, aliveSlots, teamMatch, team);
                if (slot < 0 && team >= 0) slot = PlaceFor(crews, players, aliveSlots, true);
                if (slot >= 0) crews[actor] = CrewEntry.Play(actor, TeamOf(slot, teamMatch), slot);
            }
            return crews;
        }

        /// <summary>Null when the picks can start a team match: everyone placed, teams even, every ship crewed; otherwise why not.</summary>
        public static string CheckTeams(IDictionary<int, CrewEntry> crews, ICollection<int> players, int ships, Func<int, string> shipName)
        {
            int unplaced = players.Count(a => !crews.ContainsKey(a) || crews[a].State != CrewState.Playing || crews[a].Slot < 0 || crews[a].Slot >= ships);
            if (unplaced > 0)
            {
                // More players than all the crews take together: only more ships give the last ones a place.
                if (players.Count > ships * MaxCrew)
                    return players.Count + " players are more than the ships' crews take (" + MaxCrew + " a ship): more ships per team give everyone a place.";
                return unplaced + (unplaced == 1 ? " player hasn't" : " players haven't") + " picked a ship yet.";
            }
            int blue = TeamSize(crews, players, 0, false);
            int red = TeamSize(crews, players, 1, false);
            if (Math.Abs(blue - red) > 1) return "The teams are " + blue + " against " + red + "; they may differ by one at most.";
            for (int slot = 0; slot < ships; slot++)
            {
                int crew = CrewOf(crews, players, slot);
                if (crew == 0) return shipName(slot) + " has no crew yet: every ship needs at least one player.";
                if (crew > MaxCrew) return shipName(slot) + " has " + crew + " players; a ship takes " + MaxCrew + " at most.";
            }
            return null;
        }

        /// <summary>
        /// Null when the picks can start a co-op mission: everyone placed and no crew over the limit; with at least as many
        /// players as ships, every ship crewed (with fewer players a ship may fly empty). Otherwise why not.
        /// </summary>
        public static string CheckCrews(IDictionary<int, CrewEntry> crews, ICollection<int> players, int ships, Func<int, string> shipName)
        {
            int unplaced = players.Count(a => !crews.ContainsKey(a) || crews[a].State != CrewState.Playing || crews[a].Slot < 0 || crews[a].Slot >= ships);
            if (unplaced > 0) return unplaced + (unplaced == 1 ? " player hasn't" : " players haven't") + " picked a ship yet.";
            for (int slot = 0; slot < ships; slot++)
            {
                int crew = CrewOf(crews, players, slot);
                if (crew == 0 && players.Count >= ships) return shipName(slot) + " has no crew yet: every ship needs at least one player.";
                if (crew > MaxCrew) return shipName(slot) + " has " + crew + " players; a ship takes " + MaxCrew + " at most.";
            }
            return null;
        }

        /// <summary>The ships nobody crews (co-op with fewer players than ships).</summary>
        public static List<int> EmptyShips(IDictionary<int, CrewEntry> crews, ICollection<int> players, int ships)
        {
            var empty = new List<int>();
            for (int slot = 0; slot < ships; slot++)
            {
                if (CrewOf(crews, players, slot) == 0) empty.Add(slot);
            }
            return empty;
        }

        /// <summary>
        /// Even crews: everyone placed, then (in a team match) teams evened out, then the crews of each team (or of the fleet).
        /// The players who move are the latest to join (highest actor number); everyone else keeps their pick.
        /// </summary>
        public static Dictionary<int, CrewEntry> Balance(IDictionary<int, CrewEntry> picks, IList<int> players, int ships, bool teamMatch)
        {
            var alive = new List<int>();
            for (int slot = 0; slot < ships; slot++) alive.Add(slot);
            Dictionary<int, CrewEntry> crews = PlaceEveryone(picks, players, alive, teamMatch);
            int guard = 64;
            if (teamMatch)
            {
                while (guard-- > 0)
                {
                    int blue = TeamSize(crews, players, 0, false);
                    int red = TeamSize(crews, players, 1, false);
                    if (Math.Abs(blue - red) <= 1) break;
                    int big = blue > red ? 0 : 1;
                    int from = LargestCrew(crews, players, SlotsOfTeam(big, ships, true));
                    int to = SmallestCrew(crews, players, SlotsOfTeam(1 - big, ships, true));
                    if (!MoveLatest(crews, players, from, to, true)) break;
                }
            }
            for (int team = 0; team < (teamMatch ? 2 : 1); team++)
            {
                List<int> slots = SlotsOfTeam(team, ships, teamMatch);
                while (guard-- > 0)
                {
                    int from = LargestCrew(crews, players, slots);
                    int to = SmallestCrew(crews, players, slots);
                    if (from < 0 || to < 0 || CrewOf(crews, players, from) - CrewOf(crews, players, to) <= 1) break;
                    if (!MoveLatest(crews, players, from, to, teamMatch)) break;
                }
            }
            return crews;
        }

        private static int LargestCrew(IDictionary<int, CrewEntry> crews, ICollection<int> players, IEnumerable<int> slots)
        {
            int best = -1;
            int bestCount = -1;
            foreach (int slot in slots)
            {
                int count = CrewOf(crews, players, slot);
                if (count > bestCount)
                {
                    best = slot;
                    bestCount = count;
                }
            }
            return best;
        }

        private static bool MoveLatest(IDictionary<int, CrewEntry> crews, ICollection<int> players, int from, int to, bool teamMatch)
        {
            if (from < 0 || to < 0 || from == to) return false;
            int actor = int.MinValue;
            foreach (CrewEntry entry in crews.Values)
            {
                if (entry.State == CrewState.Playing && entry.Slot == from && players.Contains(entry.Actor) && entry.Actor > actor) actor = entry.Actor;
            }
            if (actor == int.MinValue) return false;
            crews[actor] = CrewEntry.Play(actor, TeamOf(to, teamMatch), to);
            return true;
        }

        /// <summary>A ship was lost: everyone crewing it is DEAD from now on (no respawn; they watch once their character dies).</summary>
        public static Dictionary<int, CrewEntry> LoseShip(IDictionary<int, CrewEntry> crews, int lostSlot)
        {
            var result = new Dictionary<int, CrewEntry>(crews);
            foreach (CrewEntry entry in crews.Values)
            {
                if (entry.Slot == lostSlot && entry.State == CrewState.Playing)
                    result[entry.Actor] = new CrewEntry { Actor = entry.Actor, Team = entry.Team, Slot = lostSlot, State = CrewState.Dead };
            }
            return result;
        }
    }
}
