using Photon.Pun;

namespace VoidCrewFleet
{
    /// <summary>
    /// The Fleet Pilgrimage's extra enemies. Fleet missions already set the game's difficulty for one ship's crew; the
    /// Fleet Pilgrimage then multiplies (Settings > Fleet Pilgrimage > EnemyMultiplier, set for two ships; each further ship
    /// adds as much again, so 1.6 becomes 2.2 with three ships):
    /// - the size of every enemy group the game allows to scale (scripted encounters and waves);
    /// - how many units the game's AI Director keeps in the fight: while a spawner decides whether to send a unit, each unit
    ///   it already has out counts for less of its "intensity" budget (SpawnBudget).
    /// Spawning only happens on the host, so this only acts there. The multiplier is fixed for each mission the first time
    /// the host needs it, so a setting changed mid-mission applies from the next one.
    /// </summary>
    internal static class FleetDifficulty
    {
        public const string Feature = "Fleet Pilgrimage enemies";

        private static GameSession _session;
        private static float _multiplier = 1f;

        /// <summary>True during a spawner's scaled turn, while its count is the scaled one.</summary>
        private static bool _inTurn;

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
                _multiplier = SpawnBudget.Multiplier(twoShips, FleetSession.ShipCount);
                _session = session;
                return _multiplier;
            }
        }

        [GamePatch(Feature, "Gameplay.MissionDifficulty.DifficultyState", "GetSpawnGroupAmountMultiplier", PatchKind.Postfix)]
        private static void GroupSizePostfix(ref float __result)
        {
            if (Scaling) __result *= Multiplier;
        }

        /// <summary>
        /// Host, a spawner's turn (the AI Director gives every spawner one each 15 s): whether it sends a unit, and which,
        /// is decided on the scaled count of the units it has out; its own count then gets back exactly what it sent, in the
        /// game's whole intensity points (the finalizer). Fleet used to scale the count itself as units came and went. That
        /// left a remainder of under a millionth of a point after a unit died (Unity runs the game's sums at double
        /// precision), and a spawner whose one unit takes its whole budget never had room for the next: the Fleet Battle
        /// mission sends its HOLLOW Cruisers one at a time, three to destroy, and after the first no other came. Its bombers
        /// had the same problem. Units a new host takes over (or a respawned one) go on the count at full intensity, as
        /// everything on it now does, so they need nothing of their own.
        /// </summary>
        [GamePatch(Feature, "Gameplay.NPC.AI.Spawner", "DirectorTick", PatchKind.Prefix)]
        private static void TickPrefix(ref float ___currentIntensity, out float __state)
        {
            // NaN: this turn isn't scaled (and the finalizer leaves the count alone).
            __state = float.NaN;
            if (!Scaling) return;
            __state = ___currentIntensity;
            ___currentIntensity = SpawnBudget.Scaled(__state, Multiplier);
            _inTurn = true;
        }

        /// <summary>The spawner's own count back, plus what it sent on its turn; a finalizer, so it comes back even if the turn failed.</summary>
        [GamePatch(Feature, "Gameplay.NPC.AI.Spawner", "DirectorTick", PatchKind.Finalizer)]
        private static void TickFinalizer(ref float ___currentIntensity, float __state)
        {
            _inTurn = false;
            if (float.IsNaN(__state)) return;
            ___currentIntensity = SpawnBudget.Unscaled(__state, SpawnBudget.Scaled(__state, Multiplier), ___currentIntensity);
        }

        /// <summary>
        /// What a spawner says it counts outside its turn is the scaled count too, as before: the host's bar on the
        /// Desecration objective shows it, and would otherwise run ahead with the Fleet Pilgrimage's extra enemies.
        /// </summary>
        [GamePatch(Feature, "Gameplay.NPC.AI.Spawner", "get_CurrentIntensity", PatchKind.Postfix, Optional = true)]
        private static void CountPostfix(ref float __result)
        {
            if (!_inTurn && Scaling) __result = SpawnBudget.Scaled(__result, Multiplier);
        }
    }
}
