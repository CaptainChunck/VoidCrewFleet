using Photon.Pun;

namespace VoidCrewFleet
{
    /// <summary>
    /// The Fleet Pilgrimage's extra enemies. Two-ship missions already set the game's difficulty for one ship's crew; the
    /// Fleet Pilgrimage then multiplies (Settings > Fleet Pilgrimage > EnemyMultiplier):
    /// - the size of every enemy group the game allows to scale (scripted encounters and waves);
    /// - how many units the game's AI Director keeps in the fight (each unit counts for less of its "intensity" budget).
    /// Spawning only happens on the host, so this only acts there.
    /// </summary>
    internal static class FleetDifficulty
    {
        public const string Feature = "Fleet Pilgrimage enemies";

        private static bool Scaling
        {
            get
            {
                FleetPlan plan = FleetSession.Current;
                return plan != null && plan.ScaleEnemies && PhotonNetwork.IsMasterClient;
            }
        }

        private static float Multiplier
        {
            get { return Settings.EnemyMultiplier != null ? Settings.EnemyMultiplier.Value : 1.6f; }
        }

        [GamePatch(Feature, "Gameplay.MissionDifficulty.DifficultyState", "GetSpawnGroupAmountMultiplier", PatchKind.Postfix)]
        private static void GroupSizePostfix(ref float __result)
        {
            if (Scaling) __result *= Multiplier;
        }

        [GamePatch(Feature, "Gameplay.NPC.AI.Spawner", "SpawnUnitEntry", PatchKind.Prefix)]
        private static void SpawnPrefix(float ___currentIntensity, out float __state)
        {
            __state = ___currentIntensity;
        }

        [GamePatch(Feature, "Gameplay.NPC.AI.Spawner", "SpawnUnitEntry", PatchKind.Postfix)]
        private static void SpawnPostfix(ref float ___currentIntensity, float __state)
        {
            if (Scaling) ___currentIntensity = __state + (___currentIntensity - __state) / Multiplier;
        }

        [GamePatch(Feature, "Gameplay.NPC.AI.Spawner", "TrackedUnitDestroyed", PatchKind.Prefix)]
        private static void DestroyedPrefix(float ___currentIntensity, out float __state)
        {
            __state = ___currentIntensity;
        }

        [GamePatch(Feature, "Gameplay.NPC.AI.Spawner", "TrackedUnitDestroyed", PatchKind.Postfix)]
        private static void DestroyedPostfix(ref float ___currentIntensity, float __state)
        {
            if (Scaling) ___currentIntensity = __state + (___currentIntensity - __state) / Multiplier;
        }
    }
}
