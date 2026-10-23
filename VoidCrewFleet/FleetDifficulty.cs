using Photon.Pun;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// The Fleet Pilgrimage's extra enemies. Fleet missions already set the game's difficulty for one ship's crew; the
    /// Fleet Pilgrimage then multiplies (Settings > Fleet Pilgrimage > EnemyMultiplier, set for two ships; each further ship
    /// adds as much again, so 1.6 becomes 2.2 with three ships):
    /// - the size of every enemy group the game allows to scale (scripted encounters and waves);
    /// - how many units the game's AI Director keeps in the fight (each unit counts for less of its "intensity" budget).
    /// Spawning only happens on the host, so this only acts there. The multiplier is fixed for each mission the first time
    /// the host needs it: a unit's intensity must be taken off at the rate it was added, or a setting changed mid-mission
    /// would leave phantom intensity behind (and fewer enemies for the rest of the mission).
    /// </summary>
    internal static class FleetDifficulty
    {
        public const string Feature = "Fleet Pilgrimage enemies";

        private static GameSession _session;
        private static float _multiplier = 1f;

        private static bool Scaling
        {
            get
            {
                FleetPlan plan = FleetSession.Current;
                return plan != null && plan.ScaleEnemies && PhotonNetwork.IsMasterClient;
            }
        }

        /// <summary>The running mission's multiplier, from the host's setting when the mission first needed it.</summary>
        private static float Multiplier
        {
            get
            {
                GameSession session = GameSessionManager.ActiveSession;
                if (session != null && ReferenceEquals(session, _session)) return _multiplier;
                float twoShips = Settings.EnemyMultiplier != null ? Settings.EnemyMultiplier.Value : 1.6f;
                int ships = Mathf.Max(2, FleetSession.ShipCount);
                _multiplier = 1f + (twoShips - 1f) * (ships - 1);
                _session = session;
                return _multiplier;
            }
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

        /// <summary>
        /// A new host (or a respawned unit) takes over live units at their full intensity; scale it like a spawn, or the
        /// scaled removals that follow leave phantom intensity behind and fewer enemies come for the rest of the mission.
        /// </summary>
        [GamePatch(Feature, "Gameplay.NPC.AI.Spawner", "EvaluateIfUnitIsMine", PatchKind.Prefix, Optional = true)]
        private static void TakeOverPrefix(float ___currentIntensity, out float __state)
        {
            __state = ___currentIntensity;
        }

        [GamePatch(Feature, "Gameplay.NPC.AI.Spawner", "EvaluateIfUnitIsMine", PatchKind.Postfix, Optional = true)]
        private static void TakeOverPostfix(ref float ___currentIntensity, float __state)
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
