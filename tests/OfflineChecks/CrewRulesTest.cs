using System;
using System.Collections.Generic;
using System.Linq;
using VoidCrewFleet;

// Offline tests for the crew and reconnect rules (CrewRules.cs, RosterRules.cs) and for when the host puts its crews and
// lock back into the room (RoomCopyCheck.cs), which have nothing of the game in them: they are compiled together with
// this file and run here. Run it with tests\build-and-test.ps1.
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
        CrewListChecks();
        RosterChecks();
        RoomCopyChecks();
        Console.WriteLine(_failures == 0 ? "CREW RULES PASSED (" + _checks + ")" : "CREW RULES FAILED: " + _failures + " of " + _checks);
        return _failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- the HUD's crew list

    private static string Join(IEnumerable<int> values)
    {
        return string.Join(",", values.Select(v => v.ToString()).ToArray());
    }

    private static void CrewListChecks()
    {
        const int rowsLimit = CrewRules.MaxCrew - 1;
        List<int> others = Enumerable.Range(2, 23).ToList();

        // Co-op, 24 players on three ships, the local player (1) on ship 1: their 7 crewmates, in the list's order.
        var coop = new Dictionary<int, CrewEntry>();
        for (int actor = 1; actor <= 24; actor++) coop[actor] = CrewEntry.Play(actor, 0, (actor - 1) % 3);
        Equal("4,7,10,13,16,19,22", Join(CrewRules.Listed(others, coop, 1, rowsLimit)), "co-op, 24 players: the local player's crew");

        // Fleet Battle 3v3, 24 players, the local player (3) on BLUE 2: BLUE 2's crew only, never RED.
        var pvp = new Dictionary<int, CrewEntry>();
        for (int actor = 1; actor <= 24; actor++)
        {
            int slot = (actor - 1) % 6;
            pvp[actor] = CrewEntry.Play(actor, CrewRules.TeamOf(slot, true), slot);
        }
        List<int> everyone = Enumerable.Range(1, 24).ToList();
        Equal("9,15,21", Join(CrewRules.Listed(everyone, pvp, 3, rowsLimit)), "Fleet Battle: the local player's ship only, never themselves");

        // A lost ship's crew still list each other (they keep its slot); a waiting player (no slot) and an unplaced one don't show.
        var lost = new Dictionary<int, CrewEntry>
        {
            { 1, new CrewEntry { Actor = 1, Team = 0, Slot = 1, State = CrewState.Dead } },
            { 2, new CrewEntry { Actor = 2, Team = 0, Slot = 1, State = CrewState.Dead } },
            { 3, CrewEntry.Play(3, 0, 0) },
            { 4, new CrewEntry { Actor = 4, Team = -1, Slot = -1, State = CrewState.Waiting } },
        };
        Equal("2", Join(CrewRules.Listed(new List<int> { 2, 3, 4, 5 }, lost, 1, rowsLimit)), "a lost ship's crew: its other members");

        // Without a ship: the first 7 others in the list's order (waiting, the hub before picking, a room without crews).
        Equal("5,6,7,8,9,10,11", Join(CrewRules.Listed(Enumerable.Range(4, 20).ToList(), lost, 4, rowsLimit)), "waiting: the first 7 others");
        Equal("2,3,4,5,6,7,8", Join(CrewRules.Listed(others, new Dictionary<int, CrewEntry>(), 1, rowsLimit)), "no crews: the first 7");
        Equal("2,3,4,5,6,7,8", Join(CrewRules.Listed(others, null, 1, rowsLimit)), "no crew list at all: the first 7");
        Equal("2,3", Join(CrewRules.Listed(new List<int> { 2, 3 }, null, 1, rowsLimit)), "a small room: everyone");
        // A crew bigger than the list (a hub with too many picks for one ship): still 7.
        var crowded = new Dictionary<int, CrewEntry>();
        for (int actor = 1; actor <= 12; actor++) crowded[actor] = CrewEntry.Play(actor, 0, 0);
        Equal(rowsLimit, CrewRules.Listed(Enumerable.Range(2, 11).ToList(), crowded, 1, rowsLimit).Count, "never more than 7 rows");

        // The other ships on the local player's side.
        Equal("1,2", Join(CrewRules.OtherShipsOfSide(0, 3, false)), "co-op ship 1 of 3: ships 2 and 3");
        Equal("0", Join(CrewRules.OtherShipsOfSide(1, 2, false)), "co-op ship 2 of 2: ship 1");
        Equal("2,4", Join(CrewRules.OtherShipsOfSide(0, 6, true)), "BLUE 1 in 3v3: BLUE 2 and BLUE 3");
        Equal("1,5", Join(CrewRules.OtherShipsOfSide(3, 6, true)), "RED 2 in 3v3: RED 1 and RED 3");
        Equal("", Join(CrewRules.OtherShipsOfSide(0, 2, true)), "1v1: no other ship on the side");
        Equal("", Join(CrewRules.OtherShipsOfSide(-1, 3, false)), "no ship: none");

        // The other team's ships, for the enemy cards: only in a team match.
        Equal("1,3,5", Join(CrewRules.EnemyShips(0, 6, true)), "BLUE 1 in 3v3: RED 1, RED 2, RED 3");
        Equal("0,2", Join(CrewRules.EnemyShips(3, 4, true)), "RED 2 in 2v2: BLUE 1 and BLUE 2");
        Equal("", Join(CrewRules.EnemyShips(0, 3, false)), "co-op: no enemy ships");
        Equal("", Join(CrewRules.EnemyShips(-1, 6, true)), "no ship: none");

        // The teams' lists once teams are picked: each team's players in the list's order; unplaced players on neither.
        var picked = new Dictionary<int, CrewEntry>(pvp) { { 30, new CrewEntry { Actor = 30, Team = -1, Slot = -1, State = CrewState.Waiting } } };
        List<int> withUnplaced = Enumerable.Range(2, 23).Concat(new[] { 30, 31 }).ToList();
        Equal("2,4,6,8,10,12,14,16,18,20,22,24", Join(CrewRules.OfTeam(withUnplaced, picked, 1)), "RED's players in order");
        Equal(11, CrewRules.OfTeam(withUnplaced, picked, 0).Count, "BLUE's other players (the local player has no row)");
        Equal(0, CrewRules.OfTeam(withUnplaced, null, 0).Count, "no crew list: nobody is on a team");

        // A list shows every player while they fit; with more, one row fewer and the key prompt in its place.
        Equal(5, CrewRules.RowsShown(5, 8), "5 players, 8 rows: all 5");
        Equal(8, CrewRules.RowsShown(8, 8), "8 players, 8 rows: all 8");
        Equal(7, CrewRules.RowsShown(9, 8), "9 players, 8 rows: 7 and the prompt");
        Equal(7, CrewRules.RowsShown(23, 8), "23 players, 8 rows: 7 and the prompt");
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

        TeamCapChecks();
    }

    /// <summary>Players numbered first, first + 1, ... on a ship (team match: the team is the slot's).</summary>
    private static void Put(Dictionary<int, CrewEntry> crews, int slot, int count, int first)
    {
        for (int actor = first; actor < first + count; actor++) crews[actor] = CrewEntry.Play(actor, CrewRules.TeamOf(slot, true), slot);
    }

    /// <summary>Team matches: nobody is put on a full crew (8), and a team with two ships uses the one with room.</summary>
    private static void TeamCapChecks()
    {
        var twoPerTeam = new List<int> { 0, 1, 2, 3 };

        // Two living ships on BLUE: the smaller crew, and a full ship is passed over while the other has room.
        var crews = new Dictionary<int, CrewEntry>();
        Put(crews, 0, 2, 1);
        Put(crews, 2, 1, 3);
        Put(crews, 1, 2, 4);
        Put(crews, 3, 2, 6);
        List<int> players = Players(8);
        Equal(2, CrewRules.PlaceFor(crews, players, twoPerTeam, true), "two BLUE ships: the smaller crew (BLUE 2)");
        crews = new Dictionary<int, CrewEntry>();
        Put(crews, 0, 8, 1);
        Put(crews, 2, 5, 9);
        Put(crews, 1, 7, 14);
        Put(crews, 3, 7, 21);
        players = Players(28);
        Equal(2, CrewRules.PlaceFor(crews, players, twoPerTeam, true), "a full BLUE 1 is passed over for BLUE 2");
        Equal(-1, CrewRules.SmallestCrew(crews, players, new[] { 0 }, CrewRules.MaxCrew), "a full ship has no room under the limit");
        Equal(0, CrewRules.SmallestCrew(crews, players, new[] { 0 }), "without a limit it is still the smallest");

        // 8 against 8 on one ship each, and two more join: both teams are full, so they stay without a ship.
        crews = new Dictionary<int, CrewEntry>();
        Put(crews, 0, 8, 1);
        Put(crews, 1, 8, 9);
        List<int> eighteen = Players(18);
        var oneEach = new List<int> { 0, 1 };
        Equal(-1, CrewRules.PlaceFor(crews, eighteen, oneEach, true), "PvP: with both crews full there is no place");
        Dictionary<int, CrewEntry> placed = CrewRules.PlaceEveryone(crews, eighteen, oneEach, true);
        Check(!placed.ContainsKey(17) && !placed.ContainsKey(18), "PvP: players over the crews' limit stay without a ship");
        Equal(8, CrewRules.CrewOf(placed, eighteen, 0), "PvP: BLUE 1 keeps 8");
        Equal(8, CrewRules.CrewOf(placed, eighteen, 1), "PvP: RED 1 keeps 8");
        string why = CrewRules.CheckTeams(placed, eighteen, 2, Name);
        Check(why != null && why.Contains("more ships per team"), "PvP: confirming says more ships are needed (" + why + ")");

        // 8 against 7: the next player fills RED, the one after waits.
        crews = new Dictionary<int, CrewEntry>();
        Put(crews, 0, 8, 1);
        Put(crews, 1, 7, 9);
        List<int> seventeen = Players(17);
        placed = CrewRules.PlaceEveryone(crews, seventeen, oneEach, true);
        Equal(1, placed[16].Slot, "PvP: a newcomer fills RED's last place");
        Check(!placed.ContainsKey(17), "PvP: the next one gets none");

        // A BLUE pick on BLUE 2, which didn't spawn, while BLUE 1 is full: 8 against 8, so RED's ship with room takes them.
        crews = new Dictionary<int, CrewEntry>();
        Put(crews, 0, 8, 1);
        Put(crews, 1, 4, 9);
        Put(crews, 3, 4, 13);
        crews[17] = CrewEntry.Play(17, 0, 2);
        placed = CrewRules.PlaceEveryone(crews, seventeen, new List<int> { 0, 1, 3 }, true);
        Equal(1, placed[17].Slot, "PvP: a pick on a lost ship of a full team goes to the other team on a tie");
        Equal(8, CrewRules.CrewOf(placed, seventeen, 0), "PvP: and BLUE 1 isn't overfilled");

        // BLUE (8, its only ship full) is behind RED (9): RED may not take one more, so a newcomer has no place.
        crews = new Dictionary<int, CrewEntry>();
        Put(crews, 0, 8, 1);
        Put(crews, 1, 5, 9);
        Put(crews, 3, 4, 14);
        Equal(-1, CrewRules.PlaceFor(crews, Players(18), new List<int> { 0, 1, 3 }, true), "PvP: no place when the smaller team is full");

        // Co-op has the same limit: a full ship takes nobody else (who finds every crew full waits for a free place).
        crews = new Dictionary<int, CrewEntry>();
        Put(crews, 0, 8, 1);
        Equal(-1, CrewRules.PlaceFor(crews, Players(9), new List<int> { 0 }, false), "co-op: a full ship takes nobody else");
        Equal(1, CrewRules.PlaceFor(crews, Players(9), new List<int> { 0, 1 }, false), "co-op: the ship with room does");

        // PvP as before: a 3v3 with 48 players seats everyone; 17 players in a 1v1 still need more ships per team.
        List<int> fortyEight = Players(48);
        Dictionary<int, CrewEntry> full3v3 = CrewRules.Balance(new Dictionary<int, CrewEntry>(), fortyEight, 6, true);
        Equal(48, full3v3.Count, "PvP 3v3, 48 players: everyone has a ship");
        Check(CrewRules.CheckTeams(full3v3, fortyEight, 6, Name) == null, "PvP 3v3, 48 players: can be confirmed");

        CoopCapChecks();
    }

    /// <summary>Players numbered first, first + 1, ... on a co-op ship.</summary>
    private static void PutCoop(Dictionary<int, CrewEntry> crews, int slot, int count, int first)
    {
        for (int actor = first; actor < first + count; actor++) crews[actor] = CrewEntry.Play(actor, 0, slot);
    }

    /// <summary>
    /// Co-op: 8 a ship there too, and three ships seat 24 at most. Picking, AUTO-BALANCE, late joiners and the mission's
    /// added ships all follow it; with more players than the ships take, confirming is refused with what helps.
    /// </summary>
    private static void CoopCapChecks()
    {
        var three = new List<int> { 0, 1, 2 };
        Equal(24, CrewRules.CoopSeats, "co-op seats 24");

        // 36 players, nobody picked: AUTO-BALANCE fills the three ships with 8 each and leaves 12 without a ship.
        List<int> players = Players(36);
        Dictionary<int, CrewEntry> balanced = CrewRules.Balance(new Dictionary<int, CrewEntry>(), players, 3, false);
        foreach (int slot in three) Equal(8, CrewRules.CrewOf(balanced, players, slot), "co-op 36 players, auto-balanced: ship " + (slot + 1) + " has 8");
        Equal(24, balanced.Count, "co-op 36 players, auto-balanced: 24 have a ship");
        string why = CrewRules.CheckCrews(balanced, players, 3, Name);
        Check(why != null && why.Contains("FLEET BATTLE") && why.Contains("24"), "co-op 36 players: confirming says co-op takes 24 (" + why + ")");

        // 36 players, 24 picked as the lobby screen allows (8 a ship): AUTO-BALANCE moves nobody, and the 12 others wait.
        var picks = new Dictionary<int, CrewEntry>();
        for (int actor = 1; actor <= 24; actor++) picks[actor] = CrewEntry.Play(actor, 0, (actor - 1) % 3);
        Equal(8, CrewRules.MaxPerShip(36, 3, false), "co-op 36 players: a ship takes 8 picks");
        Check(CrewRules.CanJoin(picks, players, 25, 0, 3, false, Name, Team) != null, "co-op 36 players: a 25th pick is refused");
        Dictionary<int, CrewEntry> kept = CrewRules.Balance(picks, players, 3, false);
        Check(Enumerable.Range(1, 24).All(a => kept[a].Slot == picks[a].Slot), "co-op 36 players: the 24 picks stay");
        Check(Enumerable.Range(25, 12).All(a => !kept.ContainsKey(a)), "co-op 36 players: the 12 others wait");

        // Every room from 25 to 48 players (a one-ship mission adds three ships): 8 on every ship, the rest wait, and
        // confirming says why.
        for (int count = 25; count <= 48; count++)
        {
            List<int> room = Players(count);
            Dictionary<int, CrewEntry> crews = CrewRules.Balance(new Dictionary<int, CrewEntry>(), room, 3, false);
            Check(three.All(s => CrewRules.CrewOf(crews, room, s) == CrewRules.MaxCrew), "co-op " + count + " players: 8 on every ship");
            Equal(CrewRules.CoopSeats, crews.Count, "co-op " + count + " players: 24 have a ship");
            string reason = CrewRules.CheckCrews(crews, room, 3, Name);
            Check(reason != null && reason == CrewRules.CoopOverflow(count, 3), "co-op " + count + " players: confirming is refused with the reason");
            string waiting = CrewRules.CoopWaiting(count, 3);
            int over = count - CrewRules.CoopSeats;
            Check(waiting != null && waiting.Contains(over + (over == 1 ? " player waits" : " players wait")), "co-op " + count + " players: " + over + " wait (" + waiting + ")");
        }

        // 25 players with everyone on a ship, the host having moved 9 onto one: still the reason that helps, not "9 players".
        List<int> twentyFive = Players(25);
        var crowded = new Dictionary<int, CrewEntry>();
        PutCoop(crowded, 0, 9, 1);
        PutCoop(crowded, 1, 8, 10);
        PutCoop(crowded, 2, 8, 18);
        Equal(CrewRules.CoopOverflow(25, 3), CrewRules.CheckCrews(crowded, twentyFive, 3, Name), "co-op 25 players, one ship crowded: confirming says co-op takes 24");

        // 24 on three ships: everyone has a place and the crews can be confirmed.
        List<int> twentyFour = Players(24);
        Dictionary<int, CrewEntry> seated = CrewRules.Balance(new Dictionary<int, CrewEntry>(), twentyFour, 3, false);
        Equal(24, seated.Count, "co-op 24 players: everyone has a ship");
        Check(CrewRules.CheckCrews(seated, twentyFour, 3, Name) == null, "co-op 24 players: can be confirmed");
        Check(CrewRules.CoopOverflow(24, 3) == null && CrewRules.CoopWaiting(24, 3) == null, "co-op 24 players on 3 ships: nobody waits");

        // 17 on two ships: 16 have a place; confirming says a third ship helps. The mission's added ship seats everyone.
        List<int> seventeen = Players(17);
        Dictionary<int, CrewEntry> two = CrewRules.Balance(new Dictionary<int, CrewEntry>(), seventeen, 2, false);
        Equal(16, two.Count, "co-op 17 players on 2 ships: 16 have a ship");
        why = CrewRules.CheckCrews(two, seventeen, 2, Name);
        Check(why != null && why.Contains("SHIPS at 3"), "co-op 17 players on 2 ships: confirming says a third ship helps (" + why + ")");
        Dictionary<int, CrewEntry> added = CrewRules.Balance(two, seventeen, 3, false);
        Equal(17, added.Count, "co-op 17 players, a ship added: everyone has a ship");
        int[] sizes = three.Select(s => CrewRules.CrewOf(added, seventeen, s)).ToArray();
        Check(sizes.Max() - sizes.Min() <= 1 && sizes.Max() <= CrewRules.MaxCrew, "co-op 17 players, a ship added: crews even (" + string.Join("/", sizes) + ")");
        Check(CrewRules.CheckCrews(added, seventeen, 3, Name) == null, "co-op 17 players, a ship added: can be confirmed");

        // After the crews are confirmed: a late joiner waits while every crew is full, and gets the place a leaver frees.
        var confirmed = new Dictionary<int, CrewEntry>();
        PutCoop(confirmed, 0, 8, 1);
        PutCoop(confirmed, 1, 8, 9);
        PutCoop(confirmed, 2, 8, 17);
        Equal(-1, CrewRules.PlaceFor(confirmed, Players(25), three, false), "co-op: a late joiner finds every crew full");
        confirmed.Remove(12);
        List<int> afterLeave = Players(25).Where(a => a != 12).ToList();
        Equal(1, CrewRules.PlaceFor(confirmed, afterLeave, three, false), "co-op: a leaver's place goes to the late joiner");

        // A one-ship mission's 9 to 24 players: the added ships take them all, 8 a ship at most.
        for (int count = 9; count <= 24; count++)
        {
            List<int> room = Players(count);
            int ships = (count + CrewRules.MaxCrew - 1) / CrewRules.MaxCrew;
            Dictionary<int, CrewEntry> crews = CrewRules.Balance(new Dictionary<int, CrewEntry>(), room, ships, false);
            Check(crews.Count == count && Enumerable.Range(0, ships).All(s => CrewRules.CrewOf(crews, room, s) <= CrewRules.MaxCrew),
                "co-op " + count + " players on " + ships + " ships: everyone placed, 8 a ship at most");
        }
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

        CoopRosterChecks(now);
        QueueChecks(now);
    }

    /// <summary>Co-op in a mission: 8 a ship there too; who finds every crew full waits, and free places go to the longest waiting.</summary>
    private static void CoopRosterChecks(int now)
    {
        var alive = new List<int> { 0, 1, 2 };
        var seats = new List<RosterSeat>();
        seats.AddRange(Crew("a", 0, 0, 8, 100));
        seats.AddRange(Crew("b", 0, 1, 8, 200));
        seats.AddRange(Crew("c", 0, 2, 8, 300));

        // Three full crews: newcomers wait, on the fleet's one team, without a ship.
        RosterSeat w1 = RosterRules.Arrive(seats, "W1", 1, "W1", alive, false, now);
        Equal(CrewState.Waiting, w1.State, "co-op, every crew full: a newcomer waits");
        Equal(0, w1.Team, "co-op: a waiting player is on the fleet's team");
        Equal(-1, RosterRules.Crews(seats)[1].Slot, "co-op: a waiting player has no ship");
        RosterSeat w2 = RosterRules.Arrive(seats, "W2", 2, "W2", alive, false, now + 10);
        Equal(CrewState.Waiting, w2.State, "co-op: the next newcomer waits too");
        Check(alive.All(s => seats.Count(x => x.Slot == s && x.State == CrewState.Playing) == CrewRules.MaxCrew), "co-op: no crew goes past 8");

        // A player leaves: their place is held for the hold time, then it goes to who waited longest.
        RosterRules.Leave(seats, "b0", now + 100);
        Check(!RosterRules.FillSeats(seats, alive, now + 200, false), "co-op: a held place isn't given out");
        Check(RosterRules.ExpireHolds(seats, now + 100 + Hold, Hold), "co-op: the hold runs out");
        Check(RosterRules.FillSeats(seats, alive, now + 100 + Hold, false), "co-op: then the place is given out");
        Equal(CrewState.Playing, w1.State, "co-op: to who waited longest");
        Equal(1, w1.Slot, "co-op: on the ship with the free place");
        Equal(CrewState.Waiting, w2.State, "co-op: the other one keeps waiting");

        // Back too late, with every crew full again: they wait, behind the one already waiting.
        RosterSeat b0 = RosterRules.Arrive(seats, "b0", 3, "b0", alive, false, now + 200 + Hold);
        Equal(CrewState.Waiting, b0.State, "co-op: back too late to full crews, they wait");
        Equal(-1, b0.Slot, "co-op: without a ship");
        RosterRules.Leave(seats, "c0", now + 300 + Hold);
        RosterRules.ExpireHolds(seats, now + 300 + 2 * Hold, Hold);
        RosterSeat late = RosterRules.Arrive(seats, "N", 4, "N", alive, false, now + 300 + 2 * Hold);
        Equal(CrewState.Waiting, late.State, "co-op: a newcomer doesn't take a free place others wait for");
        RosterRules.FillSeats(seats, alive, now + 300 + 2 * Hold, false);
        Equal(CrewState.Playing, w2.State, "co-op: the free place goes to who waited longest");
        Equal(2, w2.Slot, "co-op: on the ship it is on");
        Equal(CrewState.Waiting, b0.State, "co-op: the others keep waiting");
        Equal(CrewState.Waiting, late.State, "co-op: the newcomer too");

        // Back in time: their own ship, whose place was kept, full or not.
        RosterRules.Leave(seats, "a0", now + 400 + 2 * Hold);
        RosterSeat a0 = RosterRules.Arrive(seats, "a0", 5, "a0", alive, false, now + 500 + 2 * Hold);
        Equal(CrewState.Playing, a0.State, "co-op: back within the hold, they play");
        Equal(0, a0.Slot, "co-op: on their own ship");

        // A waiting player who leaves and comes back keeps their place in line.
        RosterRules.Leave(seats, "b0", now + 600 + 2 * Hold);
        RosterSeat again = RosterRules.Arrive(seats, "b0", 6, "b0", alive, false, now + 700 + 2 * Hold);
        Equal(CrewState.Waiting, again.State, "co-op: a waiting player who comes back waits again");
        Equal(now + 200 + Hold, again.WaitingSince, "co-op: in their place in line");

        // Room on a ship: a newcomer plays at once (the smallest crew); a lost ship's crew stays dead, the waiting get
        // places only on the living ships.
        var room = new List<RosterSeat>();
        room.AddRange(Crew("a", 0, 0, 8, 100));
        room.AddRange(Crew("b", 0, 1, 5, 200));
        RosterSeat n = RosterRules.Arrive(room, "N", 1, "N", new List<int> { 0, 1 }, false, now);
        Equal(CrewState.Playing, n.State, "co-op, a ship with room: a newcomer plays at once");
        Equal(1, n.Slot, "co-op: on the ship with room");
        RosterRules.ShipLost(room, 1);
        RosterSeat m = RosterRules.Arrive(room, "M", 2, "M", new List<int> { 0 }, false, now + 10);
        Equal(CrewState.Waiting, m.State, "co-op, the only living ship full: a newcomer waits");
        Equal(CrewState.Dead, n.State, "co-op: the lost ship's crew stays dead");
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

        // Forgetting several at once keeps everyone else in their order (the roster's order is the order places go in), and
        // the holds left are counted per team.
        var mixed = new List<RosterSeat>
        {
            Waiting("W1", 1, -1, now), Playing("A", 2, 0, 0), Waiting("W2", 3, 1, now), Playing("B", 4, 1, 1), Waiting("W3", 5, 0, now), Playing("C", 6, 0, 0)
        };
        foreach (string id in new[] { "W1", "W2", "W3", "B", "C" }) RosterRules.Leave(mixed, id, now);
        Check(RosterRules.DropGone(mixed, now + Hold, Hold), "three waiting players gone for good are forgotten together");
        Equal("A,B,C", string.Join(",", mixed.Select(s => s.Id).ToArray()), "the others stay in their order");
        Equal(1, RosterRules.Held(mixed, 0), "BLUE holds C's place");
        Equal(1, RosterRules.Held(mixed, 1), "RED holds B's place");
        Check(!RosterRules.DropGone(mixed, now + Hold, Hold), "nothing more to forget");

        ReturnChecks(now);
    }

    /// <summary>Two ships per team, players coming back late, and a waiting player who was forgotten.</summary>
    private static void ReturnChecks(int now)
    {
        var alive = new List<int> { 0, 1, 2, 3 };

        // Two living ships on BLUE: a newcomer joins the smaller crew, and a full ship is passed over.
        var two = new List<RosterSeat>();
        two.AddRange(Crew("b", 0, 0, 2, 100));
        two.AddRange(Crew("c", 0, 2, 1, 110));
        two.AddRange(Crew("r", 1, 1, 2, 200));
        two.AddRange(Crew("s", 1, 3, 2, 210));
        RosterSeat n = RosterRules.Arrive(two, "N", 1, "N", alive, true, now);
        Equal(0, n.Team, "two ships a team: a newcomer joins BLUE (3 against 4)");
        Equal(2, n.Slot, "on BLUE 2, the smaller crew");
        var fullFirst = new List<RosterSeat>();
        fullFirst.AddRange(Crew("b", 0, 0, 8, 100));
        fullFirst.AddRange(Crew("c", 0, 2, 5, 110));
        fullFirst.AddRange(Crew("r", 1, 1, 7, 200));
        fullFirst.AddRange(Crew("s", 1, 3, 7, 210));
        n = RosterRules.Arrive(fullFirst, "N", 1, "N", alive, true, now);
        Equal(2, n.Slot, "BLUE 1 is full (8): the newcomer goes to BLUE 2");

        // Back too late, and their own ship is full by now: their team's other ship, not a ninth on their own.
        var full = new List<RosterSeat> { Playing("A", 1, 0, 0) };
        full.AddRange(Crew("b", 0, 0, 7, 100));
        full.AddRange(Crew("c", 0, 2, 2, 110));
        full.AddRange(Crew("r", 1, 1, 6, 200));
        full.AddRange(Crew("s", 1, 3, 6, 210));
        RosterRules.Leave(full, "A", now);
        RosterRules.ExpireHolds(full, now + Hold, Hold);
        full.Add(Playing("late", 2, 0, 0));
        RosterSeat a = RosterRules.Arrive(full, "A", 3, "A", alive, true, now + Hold + 1000);
        Equal(CrewState.Playing, a.State, "back too late with a free place on their team: plays");
        Equal(2, a.Slot, "on BLUE 2: BLUE 1 is full");
        Equal(8, full.Count(s => s.Slot == 0 && s.State == CrewState.Playing && !s.Expired), "BLUE 1 keeps 8");
        // Back too late while their own ship has room: their own ship.
        var room = new List<RosterSeat> { Playing("A", 1, 0, 0) };
        room.AddRange(Crew("b", 0, 0, 6, 100));
        room.AddRange(Crew("r", 1, 1, 7, 200));
        RosterRules.Leave(room, "A", now);
        RosterRules.ExpireHolds(room, now + Hold, Hold);
        Equal(0, RosterRules.Arrive(room, "A", 3, "A", new List<int> { 0, 1 }, true, now + Hold + 1000).Slot, "back too late, own ship with room: their own");
        // Back in time: their own ship, whose place was kept for them.
        var held = new List<RosterSeat> { Playing("A", 1, 0, 0) };
        held.AddRange(Crew("b", 0, 0, 7, 100));
        held.AddRange(Crew("c", 0, 2, 1, 110));
        held.AddRange(Crew("r", 1, 1, 8, 200));
        RosterRules.Leave(held, "A", now);
        Equal(0, RosterRules.Arrive(held, "A", 3, "A", alive, true, now + 1000).Slot, "back within the hold: their own ship");

        // A held player whose team has no living ship left (the match is ending) watches.
        var ending = new List<RosterSeat> { Playing("A", 1, 0, 0), Playing("R", 2, 1, 1) };
        RosterRules.Leave(ending, "A", now);
        RosterSeat end = RosterRules.Arrive(ending, "A", 3, "A", new List<int> { 1 }, true, now + 1000);
        Equal(CrewState.Dead, end.State, "held, but no living ship on their team: they watch");
        Check(!end.Expired, "and hold nothing");

        // A waiting player forgotten after the hold time is new when they come back: at the back of the line.
        var line = new List<RosterSeat>();
        line.AddRange(Crew("b", 0, 0, 8, 100));
        line.AddRange(Crew("r", 1, 1, 8, 200));
        line.Add(Waiting("W1", 1, -1, now));
        line.Add(Waiting("W2", 2, -1, now + 100));
        RosterRules.Leave(line, "W1", now + 200);
        Check(RosterRules.DropGone(line, now + 200 + Hold, Hold), "the waiting player who left is forgotten");
        RosterSeat w1 = RosterRules.Arrive(line, "W1", 3, "W1", new List<int> { 0, 1 }, true, now + 300 + Hold);
        Equal(CrewState.Waiting, w1.State, "back after being forgotten: waits");
        Equal(now + 300 + Hold, w1.WaitingSince, "from the time they came back");
        RosterRules.Leave(line, "r0", now + 400 + Hold);
        RosterRules.ExpireHolds(line, now + 400 + 2 * Hold, Hold);
        RosterRules.FillSeats(line, new List<int> { 0, 1 }, now + 400 + 2 * Hold);
        Equal(CrewState.Playing, Seat(line, "W2").State, "the free place goes to who waited longer");
        Equal(CrewState.Waiting, Seat(line, "W1").State, "the forgotten one waits behind them");
    }

    // ---------------------------------------------------------------- RoomCopyCheck

    /// <summary>When the host puts its own value of a room property back: only a difference that lasts, after its own write has come back.</summary>
    private static void RoomCopyChecks()
    {
        // The host wrote at 100 s; ticks every 0.25 s. While the copy matches, nothing.
        var check = new RoomCopyCheck();
        Check(!check.PutBack(false, 100f, 110f, false), "room copy: a copy that matches stays");

        // Its own write coming back: a difference within the echo time is no one else's.
        check = new RoomCopyCheck();
        bool early = false;
        for (float now = 100f; now < 100f + RoomCopyCheck.EchoSeconds; now += 0.25f) early |= check.PutBack(true, 100f, now, false);
        Check(!early, "room copy: no put-back while the host's own write may still be on its way");

        // Someone else's write long after the host's: put back once it has lasted a second, not at the first sight.
        check = new RoomCopyCheck();
        Check(!check.PutBack(true, 100f, 200f, false), "room copy: a difference first seen isn't acted on yet");
        Check(!check.PutBack(true, 100f, 200.5f, false), "room copy: nor half a second later");
        Check(check.PutBack(true, 100f, 201f, false), "room copy: a second later it is put back");
        // The host wrote its own again at 201 s: it waits for that write to come back before it looks again.
        Check(!check.PutBack(true, 201f, 202.5f, false), "room copy: after a put-back, the echo time again");

        // A long frame: the first look after it still sees the old copy, the next one the echo: nothing put back.
        check = new RoomCopyCheck();
        Check(!check.PutBack(true, 100f, 110f, false), "room copy: the first look after a long frame waits");
        Check(!check.PutBack(false, 100f, 110.25f, false), "room copy: the echo arrives; nothing to put back");
        Check(!check.PutBack(true, 100f, 110.5f, false), "room copy: a new difference starts its second again");

        // Photon holding incoming messages (the host moving to a mission): nothing while held, nor for the echo time after.
        check = new RoomCopyCheck();
        bool whileHeld = false;
        for (float now = 200f; now < 205f; now += 0.25f) whileHeld |= check.PutBack(true, 100f, now, true);
        Check(!whileHeld, "room copy: no put-back while Photon holds incoming messages");
        bool afterHeld = false;
        for (float now = 205f; now < 205f + RoomCopyCheck.EchoSeconds - 0.25f; now += 0.25f) afterHeld |= check.PutBack(true, 100f, now, false);
        Check(!afterHeld, "room copy: nor until the messages held up have had time to come in");
        Check(!check.PutBack(true, 100f, 208.25f, false), "room copy: then a lasting difference starts its second");
        Check(check.PutBack(true, 100f, 209.25f, false), "room copy: and is put back once it has lasted");

        // 0.4.6's 3v3 test: the host's own write took longer than the echo time to come back while six ships spawned. Until
        // the room has shown the host's value once, a difference is its write still on its way.
        check = new RoomCopyCheck();
        bool slowEcho = false;
        for (float now = 100f; now < 115f; now += 0.25f) slowEcho |= check.PutBack(true, 100f, now, false);
        Check(!slowEcho, "room copy: the host's own write coming back late is nobody else's");
        Check(!check.PutBack(false, 100f, 115f, false), "room copy: the late echo arrives; nothing to put back");
        // Once it has come back, someone else's write is put back after the echo time and a second.
        Check(!check.PutBack(true, 100f, 140f, false), "room copy: after the echo, another write is first seen");
        Check(check.PutBack(true, 100f, 141f, false), "room copy: and put back once it has lasted a second");

        // Its own write never showing in the room (someone wrote over it before it came back): put back after NoEchoSeconds.
        check = new RoomCopyCheck();
        bool beforeTimeout = false;
        for (float now = 300f; now < 300f + RoomCopyCheck.NoEchoSeconds; now += 0.25f) beforeTimeout |= check.PutBack(true, 300f, now, false);
        Check(!beforeTimeout, "room copy: no put-back while the host's write may still come back");
        Check(!check.PutBack(true, 300f, 300f + RoomCopyCheck.NoEchoSeconds + 0.25f, false), "room copy: after that, a lasting difference starts its second");
        Check(check.PutBack(true, 300f, 300f + RoomCopyCheck.NoEchoSeconds + 1.25f, false), "room copy: and is put back");

        // A new write by the host starts the wait for its echo again.
        check = new RoomCopyCheck();
        Check(!check.PutBack(false, 400f, 401f, false), "room copy: the host's write came back");
        Check(!check.PutBack(true, 450f, 454f, false), "room copy: a newer write of the host's isn't back yet: its own");
        Check(!check.PutBack(true, 450f, 455f, false), "room copy: still waiting for that one");
    }
}
