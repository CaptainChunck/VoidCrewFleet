using System;
using System.Collections.Generic;
using System.Linq;
using VoidCrewFleet;

// Offline tests for the crew and reconnect rules (CrewRules.cs, RosterRules.cs), which have nothing of the game in them:
// they are compiled together with this file and run here. Run it with tests\build-and-test.ps1.
internal static class CrewRulesTest
{
    private static int _checks;
    private static int _failures;

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failures++;
        Console.WriteLine("  FAIL " + what);
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        Check(Equals(expected, actual), what + ": expected " + expected + ", got " + actual);
    }

    private static string Name(int slot)
    {
        return "ship " + (slot + 1);
    }

    private static string Team(int team)
    {
        return team == 0 ? "BLUE" : "RED";
    }

    private static Dictionary<int, CrewEntry> Crews(bool teams, params int[] actorSlot)
    {
        var crews = new Dictionary<int, CrewEntry>();
        for (int i = 0; i + 1 < actorSlot.Length; i += 2) crews[actorSlot[i]] = CrewEntry.Play(actorSlot[i], CrewRules.TeamOf(actorSlot[i + 1], teams), actorSlot[i + 1]);
        return crews;
    }

    private static List<int> Players(int count)
    {
        return Enumerable.Range(1, count).ToList();
    }

    private static int Main()
    {
        CrewRulesChecks();
        RosterChecks();
        Console.WriteLine(_failures == 0 ? "CREW RULES PASSED (" + _checks + ")" : "CREW RULES FAILED: " + _failures + " of " + _checks);
        return _failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- CrewRules

    private static void CrewRulesChecks()
    {
        Equal(3, CrewRules.MaxPerShip(5, 2, false), "co-op 5 players on 2 ships");
        Equal(8, CrewRules.MaxPerShip(24, 3, false), "co-op 24 players on 3 ships");
        Equal(1, CrewRules.MaxPerShip(2, 3, false), "co-op 2 players on 3 ships");
        Equal(2, CrewRules.MaxPerShip(8, 4, true), "PvP 8 players, 2 ships per team");
        Equal(4, CrewRules.MaxPerShip(24, 6, true), "PvP 24 players, 3 ships per team");
        Equal(3, CrewRules.MaxPerShip(5, 2, true), "PvP 5 players, 1 ship per team");
        Equal(12, CrewRules.MaxPerTeam(24), "24 players per team");

        // Co-op: a full ship refuses another player.
        List<int> four = Players(4);
        Dictionary<int, CrewEntry> crews = Crews(false, 1, 0, 2, 0);
        Check(CrewRules.CanJoin(crews, four, 3, 0, 2, false, Name, Team) != null, "co-op: ship 1 full at 2 of 4 players on 2 ships");
        Check(CrewRules.CanJoin(crews, four, 3, 1, 2, false, Name, Team) == null, "co-op: ship 2 has room");
        Check(CrewRules.CanJoin(crews, four, 1, 0, 2, false, Name, Team) == null, "co-op: a player already on ship 1 may pick it again");
        Check(CrewRules.CanJoin(crews, four, 3, 2, 2, false, Name, Team) != null, "co-op: a ship outside the mission is refused");

        // PvP: a full team refuses another player even when its other ship has room.
        crews = Crews(true, 1, 0, 2, 2);
        Check(CrewRules.CanJoin(crews, four, 3, 0, 4, true, Name, Team) != null, "PvP: BLUE full at 2 of 4 players");
        Check(CrewRules.CanJoin(crews, four, 3, 1, 4, true, Name, Team) == null, "PvP: RED has room");

        // Everyone placed: picks stay, the others go to the smallest crew.
        List<int> five = Players(5);
        Dictionary<int, CrewEntry> placed = CrewRules.PlaceEveryone(Crews(false, 1, 1), five, new List<int> { 0, 1, 2 }, false);
        Equal(1, placed[1].Slot, "co-op: a pick stays");
        Equal(5, placed.Count, "co-op: everyone placed");
        int[] sizes = Enumerable.Range(0, 3).Select(s => placed.Values.Count(e => e.Slot == s)).ToArray();
        Check(sizes.Max() - sizes.Min() <= 1, "co-op: crews even (" + string.Join("/", sizes) + ")");

        // PvP: a pick on a ship that isn't there goes to the team's other ship; the unplaced go to the smaller team.
        placed = CrewRules.PlaceEveryone(Crews(true, 1, 2, 2, 0, 3, 0), five, new List<int> { 0, 1, 3 }, true);
        Equal(0, placed[1].Slot, "PvP: a pick on BLUE 2 (gone) goes to BLUE 1");
        Equal(0, placed[1].Team, "PvP: and stays BLUE");
        Equal(1, placed[4].Team, "PvP: a newcomer joins the smaller team");
        Check(placed[5].Team == 1 || placed[5].Team == 0, "PvP: the fifth player is on a team");

        // Auto-balance: six players on BLUE 1 with two ships per team: 3 against 3, crews within one.
        List<int> six = Players(6);
        Dictionary<int, CrewEntry> balanced = CrewRules.Balance(Crews(true, 1, 0, 2, 0, 3, 0, 4, 0, 5, 0, 6, 0), six, 4, true);
        Equal(3, balanced.Values.Count(e => e.Team == 0), "auto-balance: BLUE 3");
        Equal(3, balanced.Values.Count(e => e.Team == 1), "auto-balance: RED 3");
        foreach (int team in new[] { 0, 1 })
        {
            int[] crew = CrewRules.SlotsOfTeam(team, 4, true).Select(s => CrewRules.CrewOf(balanced, six, s)).ToArray();
            Check(crew.Max() - crew.Min() <= 1, "auto-balance: " + Team(team) + " crews even (" + string.Join("/", crew) + ")");
        }
        Equal(0, balanced[1].Slot, "auto-balance: the first player keeps their ship");
        Check(CrewRules.CheckTeams(balanced, six, 4, Name) == null, "auto-balanced teams can be confirmed");

        // Confirm: every ship needs a crew; teams within one.
        Check(CrewRules.CheckTeams(Crews(true, 1, 0, 2, 1), Players(2), 4, Name) != null, "two players can't confirm two ships per team");
        Check(CrewRules.CheckTeams(Crews(true, 1, 0, 2, 0, 3, 1), Players(3), 2, Name) == null, "2 against 1 is fine");
        Check(CrewRules.CheckTeams(Crews(true, 1, 0, 2, 0, 3, 0, 4, 1), Players(4), 2, Name) != null, "3 against 1 is refused");
        Check(CrewRules.CheckTeams(Crews(true, 1, 0), Players(2), 2, Name) != null, "a player without a ship blocks confirming");

        // A ship lost (co-op or PvP): its crew is dead (no respawn) and stays with it; nobody else moves.
        Dictionary<int, CrewEntry> lost = CrewRules.LoseShip(Crews(false, 1, 0, 2, 0, 3, 1, 4, 2), 0);
        Equal(CrewState.Dead, lost[1].State, "ship lost: its crew is dead");
        Equal(0, lost[2].Slot, "ship lost: and keeps its ship");
        Equal(CrewState.Playing, lost[3].State, "ship lost: another ship's crew plays on");
        Equal(1, lost[3].Slot, "ship lost: and stays on its ship");
        Equal(0, CrewRules.CrewOf(lost, Players(4), 0), "ship lost: nobody crews it any more");

        // Co-op confirm: everyone on a ship; every ship crewed when there are enough players.
        Check(CrewRules.CheckCrews(Crews(false, 1, 0, 2, 1, 3, 2), Players(3), 3, Name) == null, "co-op: three players on three ships can confirm");
        Check(CrewRules.CheckCrews(Crews(false, 1, 0, 2, 1), Players(3), 3, Name) != null, "co-op: a player without a ship blocks confirming");
        Check(CrewRules.CheckCrews(Crews(false, 1, 0, 2, 0, 3, 1), Players(3), 3, Name) != null, "co-op: an empty ship with enough players is refused");
        Check(CrewRules.CheckCrews(Crews(false, 1, 1), Players(1), 2, Name) == null, "co-op: one player may fly two ships' mission (one flies empty)");
        Equal(0, CrewRules.EmptyShips(Crews(false, 1, 1), Players(1), 2).Single(), "co-op: the empty ship is ship 1 (slot 0)");
        Check(CrewRules.CheckCrews(Crews(false, 1, 0, 2, 0, 3, 0, 4, 0, 5, 0, 6, 0, 7, 0, 8, 0, 9, 0), Players(9), 2, Name) != null,
            "co-op: nine on one ship is refused");
    }

    // ---------------------------------------------------------------- RosterRules

    private const long Hold = 180000;

    private static RosterSeat Seat(List<RosterSeat> seats, string id)
    {
        return seats.First(s => s.Id == id);
    }

    private static void RosterChecks()
    {
        // A 2 v 2 match with one ship per team: A, B BLUE (ship 1), C, D RED (ship 2); two seats per team.
        var alive = new List<int> { 0, 1 };
        var seats = new List<RosterSeat>
        {
            new RosterSeat { Id = "A", Actor = 1, Team = 0, Slot = 0, State = CrewState.Playing },
            new RosterSeat { Id = "B", Actor = 2, Team = 0, Slot = 0, State = CrewState.Playing },
            new RosterSeat { Id = "C", Actor = 3, Team = 1, Slot = 1, State = CrewState.Playing },
            new RosterSeat { Id = "D", Actor = 4, Team = 1, Slot = 1, State = CrewState.Playing }
        };
        long now = 1000;

        RosterRules.Leave(seats, "A", now);
        Equal(1, RosterRules.Held(seats, 0), "A's seat is held");
        RosterSeat e = RosterRules.Arrive(seats, "E", 5, "E", alive, true, 2, now + 1000);
        Equal(CrewState.Waiting, e.State, "a newcomer waits while every seat is taken or held");
        Equal(-1, e.Team, "and isn't on a team yet");
        RosterSeat a = RosterRules.Arrive(seats, "A", 6, "A", alive, true, 2, now + 60000);
        Equal(CrewState.Playing, a.State, "A, back within the hold, plays");
        Equal(0, a.Slot, "on their own ship");
        Equal(6, a.Actor, "under their new actor number");

        // A leaves again and the hold runs out: E takes the seat; A, back too late, waits on BLUE.
        RosterRules.Leave(seats, "A", now + 70000);
        Check(!RosterRules.ExpireHolds(seats, now + 70000 + Hold - 1, Hold), "the hold holds until three minutes");
        Check(RosterRules.ExpireHolds(seats, now + 70000 + Hold, Hold), "then it runs out");
        Check(RosterRules.FillSeats(seats, alive, 2), "the free seat is given out");
        Equal(CrewState.Playing, Seat(seats, "E").State, "the waiting newcomer gets it");
        Equal(0, Seat(seats, "E").Team, "on BLUE");
        a = RosterRules.Arrive(seats, "A", 7, "A", alive, true, 2, now + 300000);
        Equal(CrewState.Waiting, a.State, "A, back too late, waits");
        Equal(0, a.Team, "on their own team");

        // B leaves and the hold runs out: A (waiting on BLUE) gets it before a team-less waiter who came first.
        RosterSeat f = RosterRules.Arrive(seats, "F", 8, "F", alive, true, 2, now + 290000);
        Equal(CrewState.Waiting, f.State, "another newcomer waits");
        RosterRules.Leave(seats, "B", now + 310000);
        RosterRules.ExpireHolds(seats, now + 310000 + Hold, Hold);
        RosterRules.FillSeats(seats, alive, 2);
        Equal(CrewState.Playing, Seat(seats, "A").State, "BLUE's own waiter gets BLUE's free seat first");
        Equal(CrewState.Waiting, Seat(seats, "F").State, "the team-less waiter still waits");

        // Two ships per team: RED 1 (slot 1) is destroyed. Its crew watch, and their seats stay taken.
        var alive2 = new List<int> { 0, 1, 2, 3 };
        var seats2 = new List<RosterSeat>
        {
            new RosterSeat { Id = "A", Actor = 1, Team = 0, Slot = 0, State = CrewState.Playing },
            new RosterSeat { Id = "B", Actor = 2, Team = 0, Slot = 2, State = CrewState.Playing },
            new RosterSeat { Id = "C", Actor = 3, Team = 1, Slot = 1, State = CrewState.Playing },
            new RosterSeat { Id = "D", Actor = 4, Team = 1, Slot = 3, State = CrewState.Playing }
        };
        RosterRules.Leave(seats2, "D", now);
        RosterRules.ShipLost(seats2, 1);
        alive2.Remove(1);
        Equal(CrewState.Dead, Seat(seats2, "C").State, "RED 1's crew watch");
        Equal(CrewState.Playing, Seat(seats2, "D").State, "RED 2's held player is untouched");
        Equal(0, RosterRules.FreeSeats(seats2, 1, 2), "a dead player's seat stays taken");
        RosterRules.Leave(seats2, "C", now + 1000);
        RosterRules.ExpireHolds(seats2, now + 1000 + Hold * 2, Hold);
        // D's hold has run out (one seat free); C's seat stays taken although C left long ago.
        Equal(1, RosterRules.FreeSeats(seats2, 1, 2), "even after the dead player leaves for good, only the expired hold's seat is free");
        RosterSeat c = RosterRules.Arrive(seats2, "C", 9, "C", alive2, true, 2, now + 2000 + Hold * 2);
        Equal(CrewState.Dead, c.State, "a dead player who comes back still watches");

        // A held player whose ship is lost while they are away watches when they come back.
        var alive3 = new List<int> { 0, 1, 2, 3 };
        var seats3 = new List<RosterSeat>
        {
            new RosterSeat { Id = "A", Actor = 1, Team = 0, Slot = 0, State = CrewState.Playing },
            new RosterSeat { Id = "B", Actor = 2, Team = 0, Slot = 2, State = CrewState.Playing }
        };
        RosterRules.Leave(seats3, "A", now);
        RosterRules.ShipLost(seats3, 0);
        alive3.Remove(0);
        RosterSeat back = RosterRules.Arrive(seats3, "A", 3, "A", alive3, true, 2, now + 5000);
        Equal(CrewState.Dead, back.State, "a player whose ship was lost while away watches");

        // A player whose hold ran out before their ship was lost: their seat stays free, but they come back dead.
        var seats5 = new List<RosterSeat>
        {
            new RosterSeat { Id = "A", Actor = 1, Team = 0, Slot = 0, State = CrewState.Playing },
            new RosterSeat { Id = "B", Actor = 2, Team = 0, Slot = 2, State = CrewState.Playing }
        };
        RosterRules.Leave(seats5, "A", now);
        RosterRules.ExpireHolds(seats5, now + Hold, Hold);
        Equal(1, RosterRules.FreeSeats(seats5, 0, 2), "the expired hold's seat is free");
        RosterRules.ShipLost(seats5, 0);
        Equal(1, RosterRules.FreeSeats(seats5, 0, 2), "and stays free when the ship is lost");
        RosterSeat late = RosterRules.Arrive(seats5, "A", 3, "A", new List<int> { 2 }, true, 2, now + Hold + 5000);
        Equal(CrewState.Dead, late.State, "the player of a lost ship comes back dead, however late");

        // A 3 v 2 start (three seats per team): a newcomer joins RED.
        var seats4 = new List<RosterSeat>
        {
            new RosterSeat { Id = "A", Actor = 1, Team = 0, Slot = 0, State = CrewState.Playing },
            new RosterSeat { Id = "B", Actor = 2, Team = 0, Slot = 0, State = CrewState.Playing },
            new RosterSeat { Id = "C", Actor = 3, Team = 0, Slot = 0, State = CrewState.Playing },
            new RosterSeat { Id = "D", Actor = 4, Team = 1, Slot = 1, State = CrewState.Playing },
            new RosterSeat { Id = "E", Actor = 5, Team = 1, Slot = 1, State = CrewState.Playing }
        };
        RosterSeat g = RosterRules.Arrive(seats4, "G", 6, "G", new List<int> { 0, 1 }, true, 3, now);
        Equal(CrewState.Playing, g.State, "a newcomer gets RED's free seat");
        Equal(1, g.Team, "on RED");
        Equal(1, g.Slot, "on RED 1");

        // Co-op: a player who comes back returns to their ship; when it is lost, its crew (there or away) is dead.
        var coop = new List<RosterSeat>
        {
            new RosterSeat { Id = "A", Actor = 1, Team = 0, Slot = 2, State = CrewState.Playing },
            new RosterSeat { Id = "B", Actor = 2, Team = 0, Slot = 0, State = CrewState.Playing },
            new RosterSeat { Id = "C", Actor = 3, Team = 0, Slot = 1, State = CrewState.Playing },
            new RosterSeat { Id = "D", Actor = 4, Team = 0, Slot = 2, State = CrewState.Playing }
        };
        RosterRules.Leave(coop, "A", now);
        Equal(2, RosterRules.Arrive(coop, "A", 5, "A", new List<int> { 0, 1, 2 }, false, 0, now + 1000).Slot, "co-op: back on their own ship");
        RosterRules.Leave(coop, "A", now + 2000);
        RosterRules.ExpireHolds(coop, now + 2000 + Hold, Hold);
        RosterRules.ShipLost(coop, 2);
        Equal(CrewState.Dead, Seat(coop, "D").State, "co-op: the lost ship's crew is dead");
        Equal(CrewState.Playing, Seat(coop, "B").State, "co-op: another ship's crew plays on");
        RosterSeat coopBack = RosterRules.Arrive(coop, "A", 6, "A", new List<int> { 0, 1 }, false, 0, now + 3000 + Hold);
        Equal(CrewState.Dead, coopBack.State, "co-op: a player whose ship was lost while away comes back dead");
        Equal(2, coopBack.Slot, "co-op: and keeps their lost ship");
        RosterSeat h = RosterRules.Arrive(coop, "H", 7, "H", new List<int> { 0, 1 }, false, 0, now + 4000 + Hold);
        Equal(CrewState.Playing, h.State, "co-op: a newcomer plays at once");
        Check(h.Slot == 0 || h.Slot == 1, "co-op: on a living ship (" + h.Slot + ")");

        // The crew list from the roster: away players aren't in it; a waiting player has no ship.
        Dictionary<int, CrewEntry> list = RosterRules.Crews(seats);
        Check(!list.ContainsKey(2), "an away player isn't in the crew list");
        Equal(-1, list[8].Slot, "a waiting player has no ship");
        Equal(CrewState.Waiting, list[8].State, "and is waiting");
    }
}
