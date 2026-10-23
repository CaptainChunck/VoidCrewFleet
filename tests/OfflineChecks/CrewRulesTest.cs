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

        // A full ship (8) only takes another player when no ship has room.
        List<int> fifteen = Players(15);
        Dictionary<int, CrewEntry> eightSeven = Crews(false, 1, 0, 2, 0, 3, 0, 4, 0, 5, 0, 6, 0, 7, 0, 8, 0, 9, 1, 10, 1, 11, 1, 12, 1, 13, 1, 14, 1, 15, 1);
        Equal(1, CrewRules.SmallestCrew(eightSeven, fifteen, new[] { 0, 1 }), "the ship with room, not the full one");
        Equal(1, CrewRules.PlaceFor(eightSeven, fifteen, new List<int> { 0, 1 }, false), "a late joiner goes to the ship with room");
        eightSeven[16] = CrewEntry.Play(16, 0, 1);
        Equal(0, CrewRules.SmallestCrew(eightSeven, Players(16), new[] { 0, 1 }), "with every ship full, the lowest");

        // Players who joined when every ship was full: nine picked ship 1, and the mission added ship 3 for them.
        var picks = new Dictionary<int, CrewEntry>();
        for (int actor = 1; actor <= 9; actor++) picks[actor] = CrewEntry.Play(actor, 0, 0);
        for (int actor = 10; actor <= 17; actor++) picks[actor] = CrewEntry.Play(actor, 0, 1);
        List<int> seventeen = Players(17);
        Dictionary<int, CrewEntry> spread = CrewRules.PlaceEveryone(picks, seventeen, new List<int> { 0, 1, 2 }, false);
        Equal(8, CrewRules.CrewOf(spread, seventeen, 0), "an overfull pick keeps a full crew");
        Equal(2, spread[9].Slot, "its latest joiner goes to the added ship");
        Equal(1, CrewRules.CrewOf(spread, seventeen, 2), "which doesn't fly empty");
        Equal(9, CrewRules.CrewOf(CrewRules.PlaceEveryone(picks, seventeen, new List<int> { 0, 1 }, false), seventeen, 0),
            "with no room anywhere, the picks stay");

        // PvP: an overfull ship hands players only to its own team's ships.
        var teamPicks = new Dictionary<int, CrewEntry>();
        for (int actor = 1; actor <= 9; actor++) teamPicks[actor] = CrewEntry.Play(actor, 0, 0);
        teamPicks[10] = CrewEntry.Play(10, 1, 1);
        List<int> ten = Players(10);
        Equal(9, CrewRules.CrewOf(CrewRules.PlaceEveryone(teamPicks, ten, new List<int> { 0, 1 }, true), ten, 0), "PvP: never to the other team");
        Dictionary<int, CrewEntry> teamSpread = CrewRules.PlaceEveryone(teamPicks, ten, new List<int> { 0, 1, 2, 3 }, true);
        Equal(2, teamSpread[9].Slot, "PvP: to its team's other ship");
        Equal(0, teamSpread[9].Team, "PvP: and stays BLUE");
    }

    // ---------------------------------------------------------------- RosterRules

    private const int Hold = 180000;

    private static RosterSeat Seat(List<RosterSeat> seats, string id)
    {
        return seats.First(s => s.Id == id);
    }

    private static RosterSeat Playing(string id, int actor, int team, int slot)
    {
        return new RosterSeat { Id = id, Actor = actor, Team = team, Slot = slot, State = CrewState.Playing };
    }

    private static RosterSeat Waiting(string id, int actor, int team, int since)
    {
        return new RosterSeat { Id = id, Actor = actor, Team = team, Slot = -1, State = CrewState.Waiting, WaitingSince = since };
    }

    /// <summary>A ship's crew of count players, named prefix0, prefix1 and so on.</summary>
    private static List<RosterSeat> Crew(string prefix, int team, int slot, int count, int firstActor)
    {
        var crew = new List<RosterSeat>();
        for (int i = 0; i < count; i++) crew.Add(Playing(prefix + i, firstActor + i, team, slot));
        return crew;
    }

    private static void RosterChecks()
    {
        int now = 1000;

        // A 2 v 2 match with one ship per team: A, B BLUE (ship 1), C, D RED (ship 2).
        var alive = new List<int> { 0, 1 };
        var seats = new List<RosterSeat> { Playing("A", 1, 0, 0), Playing("B", 2, 0, 0), Playing("C", 3, 1, 1), Playing("D", 4, 1, 1) };

        RosterRules.Leave(seats, "A", now);
        Equal(1, RosterRules.Held(seats, 0), "A's place is held");
        Equal(2, RosterRules.Active(seats, 0), "and still counts for BLUE");

        // Players who join mid-match play at once while there is room, on the team with fewer active players.
        RosterSeat e = RosterRules.Arrive(seats, "E", 5, "E", alive, true, now + 1000);
        Equal(CrewState.Playing, e.State, "a newcomer plays at once mid-match");
        Equal(0, e.Team, "on BLUE (2 against 2: BLUE)");
        Equal(0, e.Slot, "on BLUE's ship");
        RosterSeat f = RosterRules.Arrive(seats, "F", 6, "F", alive, true, now + 2000);
        Equal(CrewState.Playing, f.State, "the next newcomer plays too");
        Equal(1, f.Team, "on RED, the smaller team");

        RosterSeat a = RosterRules.Arrive(seats, "A", 7, "A", alive, true, now + 60000);
        Equal(CrewState.Playing, a.State, "A, back within the hold, plays");
        Equal(0, a.Slot, "on their own ship");
        Equal(7, a.Actor, "under their new actor number");

        // A leaves again and the hold runs out: BLUE has 2 active (B, E), RED 3, so A, back too late, plays on BLUE again.
        RosterRules.Leave(seats, "A", now + 70000);
        Check(!RosterRules.ExpireHolds(seats, now + 70000 + Hold - 1, Hold), "the hold holds until three minutes");
        Check(RosterRules.ExpireHolds(seats, now + 70000 + Hold, Hold), "then it runs out");
        Equal(2, RosterRules.Active(seats, 0), "an expired hold no longer counts");
        a = RosterRules.Arrive(seats, "A", 8, "A", alive, true, now + 300000);
        Equal(CrewState.Playing, a.State, "A, back too late, plays: their team has a free place");
        Equal(0, a.Team, "on BLUE");

        // 3 against 3. B's hold runs out and two newcomers arrive: G evens BLUE up, H joins BLUE on the tie: 4 against 3.
        RosterRules.Leave(seats, "B", now + 310000);
        RosterRules.ExpireHolds(seats, now + 310000 + Hold, Hold);
        RosterSeat g = RosterRules.Arrive(seats, "G", 9, "G", alive, true, now + 310000 + Hold);
        Equal(0, g.Team, "G joins BLUE, the smaller team");
        RosterSeat h = RosterRules.Arrive(seats, "H", 10, "H", alive, true, now + 310000 + Hold + 1);
        Equal(0, h.Team, "H joins BLUE on the tie");
        // B comes back too late to a team that is ahead: they wait on BLUE.
        RosterSeat b = RosterRules.Arrive(seats, "B", 11, "B", alive, true, now + 320000 + Hold);
        Equal(CrewState.Waiting, b.State, "back too late to the larger team, B waits");
        Equal(0, b.Team, "on their own team");
        // A newcomer can't take BLUE's next place ahead of B, but RED has a free place: they join it, 4 against 4.
        RosterSeat i = RosterRules.Arrive(seats, "I", 12, "I", alive, true, now + 320000 + Hold + 1);
        Equal(1, i.Team, "a newcomer joins RED while B waits for BLUE");
        Equal(CrewState.Playing, i.State, "and plays");
        // Now BLUE may take one more: B gets it.
        Check(RosterRules.FillSeats(seats, alive, now + 320000 + Hold + 2), "the free place is given out");
        Equal(CrewState.Playing, Seat(seats, "B").State, "B, waiting for BLUE, plays");
        Equal(5, RosterRules.Active(seats, 0), "BLUE 5");
        Equal(4, RosterRules.Active(seats, 1), "RED 4: never more than one apart");

        // Two ships per team: RED 1 is destroyed. Its crew watch and no longer count: a newcomer joins RED, on RED 2.
        var alive2 = new List<int> { 0, 1, 2, 3 };
        var seats2 = new List<RosterSeat> { Playing("A", 1, 0, 0), Playing("B", 2, 0, 2), Playing("C", 3, 1, 1), Playing("D", 4, 1, 3) };
        RosterRules.ShipLost(seats2, 1);
        alive2.Remove(1);
        Equal(CrewState.Dead, Seat(seats2, "C").State, "RED 1's crew watch");
        Equal(1, RosterRules.Active(seats2, 1), "and don't count as RED's active players");
        RosterSeat j = RosterRules.Arrive(seats2, "J", 5, "J", alive2, true, now);
        Equal(1, j.Team, "a newcomer joins RED, which has fewer active players");
        Equal(3, j.Slot, "on RED 2, its living ship");
        RosterRules.Leave(seats2, "C", now + 1000);
        RosterSeat c = RosterRules.Arrive(seats2, "C", 9, "C", alive2, true, now + 2000 + Hold * 2);
        Equal(CrewState.Dead, c.State, "a dead player who comes back still watches");

        // A held player whose ship is lost while they are away watches when they come back.
        var alive3 = new List<int> { 0, 1, 2, 3 };
        var seats3 = new List<RosterSeat> { Playing("A", 1, 0, 0), Playing("B", 2, 0, 2) };
        RosterRules.Leave(seats3, "A", now);
        RosterRules.ShipLost(seats3, 0);
        alive3.Remove(0);
        RosterSeat back = RosterRules.Arrive(seats3, "A", 3, "A", alive3, true, now + 5000);
        Equal(CrewState.Dead, back.State, "a player whose ship was lost while away watches");

        // A player whose hold ran out before their ship was lost comes back dead all the same.
        var seats5 = new List<RosterSeat> { Playing("A", 1, 0, 0), Playing("B", 2, 0, 2) };
        RosterRules.Leave(seats5, "A", now);
        RosterRules.ExpireHolds(seats5, now + Hold, Hold);
        RosterRules.ShipLost(seats5, 0);
        RosterSeat late = RosterRules.Arrive(seats5, "A", 3, "A", new List<int> { 2 }, true, now + Hold + 5000);
        Equal(CrewState.Dead, late.State, "the player of a lost ship comes back dead, however late");

        // Every ship's crew is full (8 a ship): a newcomer waits without a team.
        var full = new List<RosterSeat>();
        full.AddRange(Crew("b", 0, 0, 8, 100));
        full.AddRange(Crew("r", 1, 1, 8, 200));
        RosterSeat w = RosterRules.Arrive(full, "W", 1, "W", alive, true, now);
        Equal(CrewState.Waiting, w.State, "with every crew full, a newcomer waits");
        Equal(-1, w.Team, "without a team yet");

        // Co-op: a player who comes back returns to their ship; when it is lost, its crew (there or away) is dead.
        var coop = new List<RosterSeat> { Playing("A", 1, 0, 2), Playing("B", 2, 0, 0), Playing("C", 3, 0, 1), Playing("D", 4, 0, 2) };
        RosterRules.Leave(coop, "A", now);
        Equal(2, RosterRules.Arrive(coop, "A", 5, "A", new List<int> { 0, 1, 2 }, false, now + 1000).Slot, "co-op: back on their own ship");
        RosterRules.Leave(coop, "A", now + 2000);
        RosterRules.ExpireHolds(coop, now + 2000 + Hold, Hold);
        RosterRules.ShipLost(coop, 2);
        Equal(CrewState.Dead, Seat(coop, "D").State, "co-op: the lost ship's crew is dead");
        Equal(CrewState.Playing, Seat(coop, "B").State, "co-op: another ship's crew plays on");
        RosterSeat coopBack = RosterRules.Arrive(coop, "A", 6, "A", new List<int> { 0, 1 }, false, now + 3000 + Hold);
        Equal(CrewState.Dead, coopBack.State, "co-op: a player whose ship was lost while away comes back dead");
        Equal(2, coopBack.Slot, "co-op: and keeps their lost ship");
        RosterSeat coopNew = RosterRules.Arrive(coop, "H", 7, "H", new List<int> { 0, 1 }, false, now + 4000 + Hold);
        Equal(CrewState.Playing, coopNew.State, "co-op: a newcomer plays at once");
        Check(coopNew.Slot == 0 || coopNew.Slot == 1, "co-op: on a living ship (" + coopNew.Slot + ")");

        // The crew list from the roster: away players aren't in it; a waiting player has no ship.
        var listed = new List<RosterSeat> { Playing("A", 1, 0, 0), Waiting("W", 2, -1, now), Playing("X", 3, 1, 1) };
        RosterRules.Leave(listed, "X", now);
        Dictionary<int, CrewEntry> list = RosterRules.Crews(listed);
        Check(!list.ContainsKey(3), "an away player isn't in the crew list");
        Equal(-1, list[2].Slot, "a waiting player has no ship");
        Equal(CrewState.Waiting, list[2].State, "and is waiting");

        QueueChecks(now);
    }

    private static void QueueChecks(int now)
    {
        var alive = new List<int> { 0, 1 };

        // Free places on both teams (BLUE 2, RED 1): waiters without a team go one at a time to the team with fewer active
        // players, the longest waiting first. 3 against 2, not 4 against 1.
        var spread = new List<RosterSeat>
        {
            Playing("A", 1, 0, 0), Playing("B", 2, 0, 0), Playing("C", 3, 1, 1), Waiting("W1", 4, -1, now), Waiting("W2", 5, -1, now + 10)
        };
        Check(RosterRules.FillSeats(spread, alive, now + 20), "both teams' free places are given out");
        Equal(1, Seat(spread, "W1").Team, "the longest waiting goes to the smaller team (RED)");
        Equal(0, Seat(spread, "W2").Team, "the next to BLUE (a tie: BLUE)");
        Equal(3, RosterRules.Active(spread, 0), "BLUE 3");
        Equal(2, RosterRules.Active(spread, 1), "RED 2");

        // Full crews (8 a ship), W waiting: a place opens on RED and a newcomer arrives then: they get in line behind W.
        var queue = new List<RosterSeat>();
        queue.AddRange(Crew("b", 0, 0, 8, 100));
        queue.AddRange(Crew("r", 1, 1, 8, 200));
        queue.Add(Waiting("W", 1, -1, now));
        RosterRules.Leave(queue, "r0", now);
        RosterRules.ExpireHolds(queue, now + Hold, Hold);
        RosterSeat n = RosterRules.Arrive(queue, "N", 2, "N", alive, true, now + Hold);
        Equal(CrewState.Waiting, n.State, "a newcomer doesn't take a free place someone waits for");
        Check(RosterRules.FillSeats(queue, alive, now + Hold), "the free place is given out");
        Equal(CrewState.Playing, Seat(queue, "W").State, "to the waiting player");
        Equal(1, Seat(queue, "W").Team, "on RED, where the place is");
        Equal(CrewState.Waiting, n.State, "the newcomer waits behind them");

        // One place on BLUE: BLUE's own waiter gets it before a waiter without a team who waited longer.
        var own = new List<RosterSeat>();
        own.AddRange(Crew("b", 0, 0, 7, 100));
        own.AddRange(Crew("r", 1, 1, 8, 200));
        own.Add(Waiting("T", 1, -1, now));
        own.Add(Waiting("Q", 2, 0, now + 50));
        RosterRules.FillSeats(own, alive, now + 100);
        Equal(CrewState.Playing, Seat(own, "Q").State, "BLUE's own waiter gets BLUE's place first");
        Equal(CrewState.Waiting, Seat(own, "T").State, "the waiter without a team still waits");

        // Back too late while their team's waiter is in line for its free place: they wait behind them.
        var lateQueue = new List<RosterSeat>();
        lateQueue.AddRange(Crew("b", 0, 0, 8, 100));
        lateQueue.AddRange(Crew("r", 1, 1, 8, 200));
        lateQueue.Add(Waiting("Q", 1, 0, now));
        RosterRules.Leave(lateQueue, "b7", now);
        RosterRules.ExpireHolds(lateQueue, now + Hold, Hold);
        RosterSeat back = RosterRules.Arrive(lateQueue, "b7", 2, "b7", alive, true, now + Hold + 1);
        Equal(CrewState.Waiting, back.State, "back too late with a teammate in line: they wait");
        Equal(0, back.Team, "on their team");
        RosterRules.FillSeats(lateQueue, alive, now + Hold + 1);
        Equal(CrewState.Playing, Seat(lateQueue, "Q").State, "the teammate in line gets the place");
        Equal(CrewState.Waiting, back.State, "and they keep waiting");

        // A waiting player who drops out and comes back keeps their place in line.
        var keep = new List<RosterSeat>();
        keep.AddRange(Crew("b", 0, 0, 8, 100));
        keep.AddRange(Crew("r", 1, 1, 8, 200));
        keep.Add(Waiting("X", 1, -1, now));
        keep.Add(Waiting("Y", 2, -1, now + 100));
        RosterRules.Leave(keep, "X", now + 200);
        RosterSeat x = RosterRules.Arrive(keep, "X", 3, "X", alive, true, now + 300);
        Equal(CrewState.Waiting, x.State, "a waiting player who comes back waits again");
        Equal(now, x.WaitingSince, "back within the hold time, their place in line is kept");
        RosterRules.Leave(keep, "r0", now + 300);
        RosterRules.ExpireHolds(keep, now + 300 + Hold, Hold);
        RosterRules.FillSeats(keep, alive, now + 300 + Hold);
        Equal(CrewState.Playing, Seat(keep, "X").State, "and they get the next place before those who came after them");
        Equal(CrewState.Waiting, Seat(keep, "Y").State, "who keep waiting");

        // The server's clock wraps around (an int of milliseconds): holds and the queue stay right across the wrap.
        int edge = int.MaxValue - 5;
        var hold = new List<RosterSeat> { Playing("A", 1, 0, 0) };
        RosterRules.Leave(hold, "A", edge);
        Check(!RosterRules.ExpireHolds(hold, unchecked(edge + Hold - 1), Hold), "across the wrap, a hold holds until three minutes");
        Check(RosterRules.ExpireHolds(hold, unchecked(edge + Hold), Hold), "and then runs out");
        var wrap = new List<RosterSeat>();
        wrap.AddRange(Crew("b", 0, 0, 8, 100));
        wrap.AddRange(Crew("r", 1, 1, 7, 200));
        wrap.Add(Waiting("Late", 1, -1, unchecked(edge + 10)));
        wrap.Add(Waiting("Early", 2, -1, edge));
        RosterRules.FillSeats(wrap, alive, unchecked(edge + 20));
        Equal(CrewState.Playing, Seat(wrap, "Early").State, "across the wrap, who waited longest gets the place");
        Equal(CrewState.Waiting, Seat(wrap, "Late").State, "and the later one waits");

        // Waiting players gone for good are forgotten; dead players and expired holds stay (their ship, their team).
        var gone = new List<RosterSeat>
        {
            Waiting("W", 1, -1, now), Waiting("V", 2, 0, now), new RosterSeat { Id = "D", Actor = 3, Team = 0, Slot = 0, State = CrewState.Dead },
            Playing("P", 4, 1, 1)
        };
        foreach (string id in new[] { "W", "D", "P" }) RosterRules.Leave(gone, id, now);
        RosterRules.Leave(gone, "V", now + Hold);
        Check(!RosterRules.DropGone(gone, now + Hold - 1, Hold), "a waiting player away for less than the hold time is kept");
        RosterRules.ExpireHolds(gone, now + Hold, Hold);
        Check(RosterRules.DropGone(gone, now + Hold, Hold), "a waiting player away for the hold time is forgotten");
        Check(gone.All(s => s.Id != "W"), "W is forgotten");
        Check(gone.Any(s => s.Id == "V"), "one who left later is kept");
        Check(gone.Any(s => s.Id == "D"), "a dead player is kept (they come back dead)");
        Check(gone.Any(s => s.Id == "P") && Seat(gone, "P").Expired, "an expired hold is kept");
    }
}
