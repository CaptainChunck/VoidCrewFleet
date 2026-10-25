using System;

namespace VoidCrewFleet
{
    /// <summary>
    /// The Fleet Pilgrimage's bigger enemy budget, as plain arithmetic (nothing of the game in it, so the offline tests run
    /// it as it is). Each of the game's spawners keeps a count of the "intensity" of the units it has out, and sends another
    /// unit while that unit still fits under its target. In a Fleet Pilgrimage every unit already out counts for
    /// 1/multiplier of its intensity, so more of them fit.
    /// The spawner's own count stays as the game keeps it, in whole intensity points, which add up and come off exactly.
    /// Only while it decides does the spawner see the scaled count; whatever it sent then goes back on its own count.
    /// Fleet used to scale the count itself as units came and went, and that left a remainder: Unity runs the game's sums
    /// at double precision, so a unit's share went on as one number and came off as another (80 / 1.6 went on as 50 and
    /// came off as 49.99999925). A spawner whose one unit takes its whole budget, like the Fleet Battle mission's HOLLOW
    /// Cruisers, then never had room for the next one.
    /// </summary>
    internal static class SpawnBudget
    {
        /// <summary>
        /// How close to a whole number a change of the count must be to be that whole number: the game adds a unit's intensity,
        /// a whole number, and the difference of two floats only carries their rounding (far less than this).
        /// </summary>
        public const double WholeTolerance = 0.01;

        /// <summary>The multiplier for a fleet of this many ships, from the setting for two (each further ship adds as much again).</summary>
        public static float Multiplier(float twoShips, int ships)
        {
            return (float)(1.0 + ((double)twoShips - 1.0) * (Math.Max(2, ships) - 1));
        }

        /// <summary>
        /// What a spawner decides on: its own count with every unit counting for 1/multiplier of its intensity. Worked out in
        /// double precision and rounded once, so it comes out the same whichever precision the game's code runs at.
        /// </summary>
        public static float Scaled(float count, float multiplier)
        {
            if (!(multiplier > 0f)) return count;
            return (float)(count / (double)multiplier);
        }

        /// <summary>
        /// The spawner's own count after a decision made on the scaled count (scaledBefore, which became scaledAfter while it
        /// decided): what it started from plus what the decision added, in whole points when it added a whole number.
        /// </summary>
        public static float Unscaled(float count, float scaledBefore, float scaledAfter)
        {
            double added = (double)scaledAfter - scaledBefore;
            double whole = Math.Round(added);
            if (Math.Abs(added - whole) < WholeTolerance) added = whole;
            return (float)(count + added);
        }
    }
}
