using System;
using System.Collections.Generic;
using System.Linq;
using VoidCrewFleet;

// Offline tests for the host's own copy of the hub's fleet choices (RoomChoices.cs, with RoomCopyCheck.cs) and for the crew
// list's players waiting for a place (CrewRules.cs), which have nothing of the game in them: they are compiled together with
// this file and run here. Run it with tests\build-and-test.ps1.
internal static class HubChecksTest
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

    private static int Main()
    {
        RoomChoicesChecks();
        WaitingChecks();
        Console.WriteLine(_failures == 0 ? "HUB CHECKS PASSED (" + _checks + ")" : "HUB CHECKS FAILED: " + _failures + " of " + _checks);
        return _failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- the host's copy of the fleet choices

    private static readonly string[] Keys = { "VCF_On", "VCF_Count", "VCF_Var", "VCF_Type1", "VCF_Load2" };

    /// <summary>Runs the host's check every 0.25 s from 'from' to 'to'; the choices it puts back each time go into the room.</summary>
    private static List<string> Run(RoomChoices host, Dictionary<string, object> room, float from, float to, bool held = false)
    {
        var all = new List<string>();
        for (float now = from; now < to; now += 0.25f)
        {
            List<string> back = host.PutBack(key => Value(room, key), now, held);
            foreach (string key in back)
            {
                room[key] = host.Value(key);
                all.Add(key);
            }
        }
        return all;
    }

    private static object Value(Dictionary<string, object> room, string key)
    {
        object value;
        return room.TryGetValue(key, out value) ? value : null;
    }

    private static void RoomChoicesChecks()
    {
        // A player who becomes host takes the room's choices as they are; choices the room doesn't have are none.
        var room = new Dictionary<string, object> { { "VCF_On", 6 }, { "VCF_Count", 2 }, { "VCF_Var", (byte)1 } };
        var host = new RoomChoices();
        host.Adopt("room A", Keys, key => Value(room, key), 100f);
        Equal("room A", host.Room, "adopted: the room the copy is for");
        object value;
        Check(host.TryGet("VCF_Count", out value) && Equals(value, 2), "adopted: the room's ship count");
        Check(!host.TryGet("VCF_Type1", out value) && value == null, "adopted: a choice the room doesn't have is none");
        Check(host.Knows("VCF_Type1"), "adopted: the copy knows every choice, none included");
        Check(!host.Knows("VCF_Other"), "adopted: not a key it wasn't given");
        Equal(0, Run(host, room, 100f, 130f).Count, "nobody changes anything: nothing is put back");

        // The host's own write: its copy has it at once, the room a round trip later.
        host.Write("VCF_Count", 3, 130f);
        Check(host.TryGet("VCF_Count", out value) && Equals(value, 3), "written: the host reads its own value at once");
        Equal(0, Run(host, room, 130f, 132f).Count, "written, the room not caught up yet: nothing is put back early");
        room["VCF_Count"] = 3;
        Equal(0, Run(host, room, 132f, 140f).Count, "the room caught up: nothing to put back");

        // Another player (a modified game) writes a choice: the host's goes back in once it has lasted, and the host never
        // goes by the forged one.
        room["VCF_Var"] = (byte)2;
        Check(host.TryGet("VCF_Var", out value) && Equals(value, (byte)1), "forged: the host still goes by its own mission");
        List<string> back = Run(host, room, 140f, 145f);
        Equal("VCF_Var", string.Join(",", back.ToArray()), "forged: the host's mission is put back, once");
        Check(Equals(Value(room, "VCF_Var"), (byte)1), "forged: the room has the host's mission again");

        // A forged number of another type (an int where the host wrote a byte) differs too.
        room["VCF_Var"] = 1;
        Equal("VCF_Var", string.Join(",", Run(host, room, 145f, 150f).ToArray()), "an int instead of the host's byte is put back");
        Check(Value(room, "VCF_Var") is byte, "the room has the host's byte again");

        // A choice someone adds where the host has none is taken out again (put back as none).
        room["VCF_Type1"] = "ffff";
        Equal("VCF_Type1", string.Join(",", Run(host, room, 150f, 155f).ToArray()), "an added choice is put back");
        Check(Value(room, "VCF_Type1") == null, "the added choice is gone again");

        // Removing a choice: the host writes none; the room's old value until it catches up isn't put back early, and once
        // the room shows none nothing more happens.
        host.Write("VCF_Load2", null, 160f);
        room["VCF_Load2"] = "abcd";
        Equal(0, Run(host, room, 160f, 162f).Count, "removed, the room not caught up yet: nothing early");
        room.Remove("VCF_Load2");
        Equal(0, Run(host, room, 162f, 170f).Count, "removed and caught up: nothing to put back");

        // While Photon holds the incoming messages (a loading screen), a difference waits.
        room["VCF_Count"] = 1;
        Equal(0, Run(host, room, 170f, 180f, true).Count, "messages held: nothing is put back");
        Equal("VCF_Count", string.Join(",", Run(host, room, 180f, 190f).ToArray()), "messages flowing again: the host's ship count goes back");

        // The host's write never came back (the room kept a stranger's value all along): put back only after the wait.
        host.Write("VCF_On", 6, 200f);
        room["VCF_On"] = 0;
        Equal(0, Run(host, room, 200f, 200f + RoomCopyCheck.NoEchoSeconds).Count, "the host's write not seen back: nothing for NoEchoSeconds");
        Equal("VCF_On", string.Join(",", Run(host, room, 200f + RoomCopyCheck.NoEchoSeconds, 230f).ToArray()), "after that: put back");

        // Forget: this player isn't the host any more; a later copy starts from the room again.
        host.Forget();
        Check(host.Room == null && !host.Knows("VCF_On"), "forgotten: no room, no choices");
        room["VCF_Count"] = 2;
        host.Adopt("room A", Keys, key => Value(room, key), 300f);
        Check(host.TryGet("VCF_Count", out value) && Equals(value, 2), "host again: the room's choices as they are now");
        Equal(0, Run(host, room, 300f, 320f).Count, "host again: nothing to put back");
    }

    // ---------------------------------------------------------------- players waiting for a place in the crew list

    private static void WaitingChecks()
    {
        // Fleet Battle in the hub, teams confirmed: players with no entry (they found every crew full) or without a team.
        var crews = new Dictionary<int, CrewEntry>
        {
            { 1, CrewEntry.Play(1, 0, 0) },
            { 2, CrewEntry.Play(2, 1, 1) },
            { 4, new CrewEntry { Actor = 4, Team = -1, Slot = -1, State = CrewState.Waiting } },
            { 5, new CrewEntry { Actor = 5, Team = 1, Slot = -1, State = CrewState.Waiting } },
        };
        Equal("3,4", string.Join(",", CrewRules.WithoutTeam(new List<int> { 1, 2, 3, 4, 5 }, crews).Select(a => a.ToString()).ToArray()),
            "without a team: no entry, or a team of -1 (a waiting player of RED is RED's)");
        Equal(3, CrewRules.WithoutTeam(new List<int> { 1, 2, 3 }, null).Count, "no crew list: everyone is on no team");
        Equal(0, CrewRules.WithoutTeam(new List<int>(), crews).Count, "nobody: none");

        // The rows a list shows: all while they fit, else 7 and the prompt; with a prompt anyway (players waiting), 7 at most.
        Equal(8, CrewRules.RowsShown(8, 8), "8 players fit 8 places");
        Equal(7, CrewRules.RowsShown(9, 8), "9 players: 7 and the prompt");
        Equal(8, CrewRules.RowsShown(8, 8, false), "no prompt asked for: as before");
        Equal(7, CrewRules.RowsShown(8, 8, true), "8 players with a prompt anyway: 7 and the prompt");
        Equal(7, CrewRules.RowsShown(16, 8, true), "16 players with a prompt anyway: 7 and the prompt");
        Equal(3, CrewRules.RowsShown(3, 8, true), "3 players with a prompt: all 3 and the prompt");
        Equal(0, CrewRules.RowsShown(0, 8, true), "nobody: no rows");
        Equal(0, CrewRules.RowsShown(5, 0, true), "no room for rows: none");
    }
}
