using System;
using System.Collections.Generic;
using VoidCrewFleet;

// Offline tests for the Fleet Pilgrimage's enemy budget (SpawnBudget.cs), which has nothing of the game in it: it is
// compiled together with this file and run here. Run it with tests\build-and-test.ps1.
// The game's spawner is modelled as Unity runs its code: Mono there is started with -O=-float32 (UnityPlayer.dll passes it),
// so float sums are worked out in double precision and rounded to float only when stored. This test runs on .NET, which
// may work float sums out at another precision, so the model says the precision of every step itself.
internal static class SpawnBudgetTest
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
        MultiplierChecks();
        ArithmeticChecks();
        CruiserChecks();
        ExactFitSweep();
        SameDecisionsChecks();
        Console.WriteLine(_failures == 0 ? "SPAWN BUDGET PASSED (" + _checks + ")" : "SPAWN BUDGET FAILED: " + _failures + " of " + _checks);
        return _failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- the game's spawner, as Unity runs it

    private enum Bookkeeping
    {
        /// <summary>No Fleet Pilgrimage scaling: the game's own spawner.</summary>
        Game,

        /// <summary>Fleet 0.4.4: the count itself scaled as units were sent and died.</summary>
        ScaledCount,

        /// <summary>Now: the spawner decides on the scaled count; its own count stays the game's (SpawnBudget).</summary>
        ScaledDecision
    }

    /// <summary>One of a spawner's unit entries (UnitEntry with its limiters).</summary>
    private sealed class Entry
    {
        public string Name;
        public int Intensity;
        public bool Group;
        public int MaxAlive;
        public int Cooldown;
        public int Alive;
        public double LastDeath = double.NegativeInfinity;

        public bool Limited(double now)
        {
            if (MaxAlive > 0 && Alive >= MaxAlive) return true;
            return Cooldown > 0 && now < LastDeath + Cooldown;
        }
    }

    /// <summary>Gameplay.NPC.AI.Spawner: DirectorTick, SpawnUnitEntry, GetSpawnableUnitEntry and TrackedUnitDestroyed.</summary>
    private sealed class Spawner
    {
        public readonly int Target;
        public readonly bool FitGroupsFirst;
        public readonly List<Entry> Entries = new List<Entry>();
        public readonly Bookkeeping Books;
        public readonly float Multiplier;
        public float Count;
        public double Now;

        public Spawner(int target, bool fitGroupsFirst, Bookkeeping books, float multiplier)
        {
            Target = target;
            FitGroupsFirst = fitGroupsFirst;
            Books = books;
            Multiplier = multiplier;
        }

        public Entry Add(string name, int intensity, bool group = false, int maxAlive = 0, int cooldown = 0)
        {
            var entry = new Entry { Name = name, Intensity = intensity, Group = group, MaxAlive = maxAlive, Cooldown = cooldown };
            Entries.Add(entry);
            return entry;
        }

        /// <summary>The AI Director's turn for this spawner; the entry it sent, or null.</summary>
        public Entry Tick()
        {
            float before = Count;
            if (Books == Bookkeeping.ScaledDecision) Count = SpawnBudget.Scaled(before, Multiplier);
            Entry sent = null;
            // if (spawnHandle != null && (float)targetIntensity > currentIntensity) SpawnUnitEntry();
            if ((double)Target > Count)
            {
                float spawnBefore = Count;
                sent = Pick();
                // currentIntensity += spawnableUnitEntry.CombinedIntensity;
                if (sent != null)
                {
                    sent.Alive++;
                    Count = (float)((double)Count + sent.Intensity);
                }
                // Fleet 0.4.4's postfix on SpawnUnitEntry.
                if (Books == Bookkeeping.ScaledCount) Count = (float)(spawnBefore + ((double)Count - spawnBefore) / Multiplier);
            }
            if (Books == Bookkeeping.ScaledDecision) Count = SpawnBudget.Unscaled(before, SpawnBudget.Scaled(before, Multiplier), Count);
            return sent;
        }

        private Entry Pick()
        {
            var fits = new List<Entry>();
            Entry bestGroup = null;
            foreach (Entry entry in Entries)
            {
                // if (!(currentIntensity + (float)item.CombinedIntensity > (float)targetIntensity) && !item.HasActiveLimiter())
                if ((double)Count + entry.Intensity > Target || entry.Limited(Now)) continue;
                fits.Add(entry);
                if (FitGroupsFirst && entry.Group && (bestGroup == null || entry.Intensity > bestGroup.Intensity)) bestGroup = entry;
            }
            if (bestGroup != null) return bestGroup;
            // The game picks at random; the model takes the first, which every check here allows.
            return fits.Count > 0 ? fits[0] : null;
        }

        /// <summary>A unit of the entry dies (the whole group's intensity for a group).</summary>
        public void Kill(Entry entry)
        {
            Kill(entry, entry.Intensity, true);
        }

        /// <summary>One unit of an entry dies, counting the intensity it took; the last one of what was sent starts its cooldown.</summary>
        public void Kill(Entry entry, int intensity, bool last)
        {
            float before = Count;
            // currentIntensity -= DataTable<IntensityTable>.Instance.GetIntensity(unit.assetGuid);
            Count = (float)((double)Count - intensity);
            // Fleet 0.4.4's postfix on TrackedUnitDestroyed.
            if (Books == Bookkeeping.ScaledCount) Count = (float)(before + ((double)Count - before) / Multiplier);
            if (!last) return;
            entry.Alive--;
            entry.LastDeath = Now;
        }
    }

    // ---------------------------------------------------------------- checks

    private static void MultiplierChecks()
    {
        Equal(1.6f, SpawnBudget.Multiplier(1.6f, 2), "two ships, EnemyMultiplier 1.6");
        Equal(2.2f, SpawnBudget.Multiplier(1.6f, 3), "three ships, EnemyMultiplier 1.6");
        Equal(1.6f, SpawnBudget.Multiplier(1.6f, 1), "fewer than two ships count as two");
        Equal(1f, SpawnBudget.Multiplier(1f, 3), "EnemyMultiplier 1 adds nothing");
        Equal(7f, SpawnBudget.Multiplier(4f, 3), "the highest setting with three ships");
    }

    private static void ArithmeticChecks()
    {
        Equal(50f, SpawnBudget.Scaled(80f, 1.6f), "a HOLLOW Cruiser Elite (80) counts 50 with two ships");
        Equal(0f, SpawnBudget.Scaled(0f, 2.2f), "nothing out counts nothing");
        Equal(80f, SpawnBudget.Scaled(80f, 0f), "a broken multiplier leaves the count alone");
        Equal(80f, SpawnBudget.Unscaled(80f, 50f, 50f), "a turn that sent nothing leaves the count as it was");
        Equal(160f, SpawnBudget.Unscaled(80f, 50f, 130.00002f), "a turn that sent 80 adds exactly 80");
        float scaled = SpawnBudget.Scaled(40f, 2.2f);
        Equal(120f, SpawnBudget.Unscaled(40f, scaled, (float)((double)scaled + 80)), "with three ships too");
        Equal(10.5f, SpawnBudget.Unscaled(10f, 3f, 3.5f), "a change that isn't a whole number is kept as it is");
    }

    /// <summary>The Fleet Battle mission's capital-ship spawner: budget 80, one HOLLOW Cruiser Elite (80), 30 s cooldown.</summary>
    private static Spawner Cruisers(Bookkeeping books, float multiplier, out Entry cruiser)
    {
        var spawner = new Spawner(80, false, books, multiplier);
        cruiser = spawner.Add("HOLLOW Cruiser Elite", 80, cooldown: 30);
        return spawner;
    }

    private static void CruiserChecks()
    {
        float two = SpawnBudget.Multiplier(1.6f, 2);

        // The model itself: with the game's own spawner the next cruiser comes once the cooldown is over...
        Entry cruiser;
        Spawner game = Cruisers(Bookkeeping.Game, 1f, out cruiser);
        Check(game.Tick() == cruiser, "game: the first cruiser comes");
        Check(game.Tick() == null, "game: one cruiser at a time");
        game.Kill(cruiser);
        game.Now += 15;
        Check(game.Tick() == null, "game: not during the 30 s cooldown");
        game.Now += 15;
        Check(game.Tick() == cruiser, "game: the next cruiser comes after the cooldown");

        // ... and with Fleet 0.4.4's scaled count it never does again: what the playtest's Fleet Battle showed.
        Spawner old = Cruisers(Bookkeeping.ScaledCount, two, out cruiser);
        Check(old.Tick() == cruiser, "0.4.4: the first cruiser comes");
        old.Kill(cruiser);
        Equal(7.4505806E-07f, old.Count, "0.4.4: the remainder left after the cruiser died");
        for (int turn = 1; turn <= 20; turn++)
        {
            old.Now += 15;
            Check(old.Tick() == null, "0.4.4: no next cruiser, turn " + turn + " (the model must show the playtest's fault)");
        }

        // Now: the next cruiser comes as with one ship, every time.
        foreach (int ships in new[] { 2, 3 })
        {
            float multiplier = SpawnBudget.Multiplier(1.6f, ships);
            Spawner now = Cruisers(Bookkeeping.ScaledDecision, multiplier, out cruiser);
            for (int round = 1; round <= 5; round++)
            {
                string what = ships + " ships, cruiser " + round;
                Check(now.Tick() == cruiser, what + ": it comes");
                Equal(80f, now.Count, what + ": the spawner's own count while it is out");
                Check(now.Tick() == null, what + ": one at a time");
                now.Kill(cruiser);
                Equal(0f, now.Count, what + ": nothing counted once it died");
                now.Now += 15;
                Check(now.Tick() == null, what + ": not during the cooldown");
                now.Now += 15;
            }
        }
    }

    /// <summary>
    /// Every unit that takes its spawner's whole budget comes back after it dies, for every multiplier setting and fleet:
    /// the spawner's own count is back to exactly nothing.
    /// </summary>
    private static void ExactFitSweep()
    {
        int blocked = 0, cases = 0;
        string first = null;
        for (int step = 0; step <= 60; step++)
        {
            float setting = 1f + step * 0.05f;
            for (int ships = 2; ships <= 3; ships++)
            {
                float multiplier = SpawnBudget.Multiplier(setting, ships);
                for (int intensity = 1; intensity <= 250; intensity++)
                {
                    cases++;
                    var spawner = new Spawner(intensity, false, Bookkeeping.ScaledDecision, multiplier);
                    Entry unit = spawner.Add("unit", intensity, cooldown: 30);
                    bool ok = true;
                    for (int round = 0; round < 4 && ok; round++)
                    {
                        ok = spawner.Tick() == unit;
                        spawner.Kill(unit);
                        ok = ok && spawner.Count == 0f;
                        spawner.Now += 30;
                    }
                    if (ok) continue;
                    blocked++;
                    if (first == null) first = "setting " + setting + ", " + ships + " ships, intensity " + intensity;
                }
            }
        }
        Check(blocked == 0, "a whole-budget unit comes back in every case (" + blocked + " of " + cases + " didn't, first: " + first + ")");
    }

    /// <summary>The bigger budget itself is as before: the same units fit as with Fleet 0.4.4's scaled count.</summary>
    private static void SameDecisionsChecks()
    {
        float two = SpawnBudget.Multiplier(1.6f, 2);
        foreach (Bookkeeping books in new[] { Bookkeeping.ScaledCount, Bookkeeping.ScaledDecision })
        {
            string name = books == Bookkeeping.ScaledCount ? "0.4.4" : "now";

            // The Fleet Battle's METEM allies (budget 45: three Vanguards together, or one): the playtest had three, then a fourth.
            var allies = new Spawner(45, true, books, two);
            Entry group = allies.Add("3 Vanguards", 45, group: true);
            Entry single = allies.Add("Vanguard", 15, cooldown: 30);
            Check(allies.Tick() == group, name + ": the allies' group of three comes first");
            Check(allies.Tick() == single, name + ": a fourth Vanguard fits beside them");
            Check(allies.Tick() == null, name + ": not a fifth");

            // The Fleet Battle's HOLLOW swarms (budget 80, two Large swarms of 40): two out, a third doesn't fit.
            var swarms = new Spawner(80, false, books, two);
            Entry swarm = swarms.Add("Swarm Large", 40, maxAlive: 3);
            Check(swarms.Tick() == swarm && swarms.Tick() == swarm, name + ": two swarms");
            Check(swarms.Tick() == null, name + ": not a third");
            swarms.Kill(swarm);
            Check(swarms.Tick() == swarm, name + ": another once one is gone");

            // Small units: five of 20 in a budget of 80 (the game itself keeps four); the gate counts them scaled too.
            var small = new Spawner(80, false, books, two);
            Entry fighter = small.Add("Fighter", 20);
            int sent = 0;
            for (int turn = 0; turn < 8; turn++)
            {
                if (small.Tick() != null) sent++;
            }
            Equal(5, sent, name + ": units of 20 in a budget of 80");
        }

        // A swarm's drones die one by one, each taking its share of the swarm's intensity. In 0.4.4 the last share left a
        // remainder too (as Unity runs it), so a swarm that takes its spawner's whole budget never came again either.
        foreach (Bookkeeping books in new[] { Bookkeeping.ScaledCount, Bookkeeping.ScaledDecision })
        {
            var drones = new Spawner(40, false, books, two);
            Entry pack = drones.Add("Swarm", 40, maxAlive: 1);
            Check(drones.Tick() == pack, books + ": a swarm of 40 comes");
            for (int drone = 0; drone < 8; drone++) drones.Kill(pack, 5, drone == 7);
            if (books == Bookkeeping.ScaledCount)
            {
                Check(drones.Count > 0f, "0.4.4: a remainder once the swarm's 8 drones died (the model must show it), got " + drones.Count.ToString("R"));
                Check(drones.Tick() == null, "0.4.4: so no next swarm");
            }
            else
            {
                Equal(0f, drones.Count, "now: nothing counted once the swarm's 8 drones died");
                Check(drones.Tick() == pack, "now: the next swarm comes");
            }
        }

        // The game's own spawner keeps four of those small units (the check above is the Fleet Pilgrimage's fifth).
        var vanilla = new Spawner(80, false, Bookkeeping.Game, 1f);
        vanilla.Add("Fighter", 20);
        int game = 0;
        for (int turn = 0; turn < 8; turn++)
        {
            if (vanilla.Tick() != null) game++;
        }
        Equal(4, game, "game: units of 20 in a budget of 80");
    }
}
