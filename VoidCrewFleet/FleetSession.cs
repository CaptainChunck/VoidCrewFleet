using System;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using ResourceAssets;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>How the host has set up the room.</summary>
    internal enum FleetMode : byte
    {
        /// <summary>The game as it is: one ship (up to 8 players with this plugin).</summary>
        OneShip = 0,

        /// <summary>Two player ships on the same side, any mission.</summary>
        TwoShips = 1,

        /// <summary>Two player ships, one per team, fighting each other.</summary>
        PvP = 2
    }

    /// <summary>The fleet settings of one mission, fixed when the host starts it.</summary>
    internal sealed class FleetPlan
    {
        public const int Protocol = 2;

        public FleetMode Mode;

        /// <summary>The 2-ship Fleet Pilgrimage: more enemies and more objectives per sector.</summary>
        public bool ScaleEnemies;

        /// <summary>Loadout (ShipLoadoutDataDef) GUID of the second ship.</summary>
        public GUIDUnion ShipB;

        /// <summary>The second ship's loadout as the host named it (players may not know a CUSTOM loadout's GUID).</summary>
        public string ShipBName;

        /// <summary>The homunculus of the second ship's loadout, when it has one of its own (empty: the mission's).</summary>
        public GUIDUnion ShipBHomunculus;

        /// <summary>Host only: what the second ship spawns with. Not sent; clients receive the ship through Photon.</summary>
        public ShipLoadout ShipBLoadout;

        /// <summary>PvP: what the airlock hacking terminal costs in the fabricator (the host's setting, the same for everyone).</summary>
        public int HackCost = HackTerminal.DefaultCost;

        /// <summary>PvP: the arena (auto, Dz-A, Or-A, Oz-B, Defiance or off); the host's setting.</summary>
        public string Arena = "auto";

        public JObject ToJson()
        {
            return new JObject
            {
                ["protocol"] = Protocol,
                ["mode"] = (int)Mode,
                ["scale"] = ScaleEnemies,
                ["shipB"] = ShipB.AsHex(),
                ["shipBName"] = ShipBName ?? "",
                ["shipBHomunculus"] = ShipBHomunculus.AsHex(),
                ["hackCost"] = HackCost,
                ["arena"] = Arena ?? "auto"
            };
        }

        public static FleetPlan FromJson(JObject json)
        {
            var plan = new FleetPlan();
            JToken token;
            if (json.TryGetValue("mode", out token)) plan.Mode = (FleetMode)token.Value<int>();
            if (json.TryGetValue("scale", out token)) plan.ScaleEnemies = token.Value<bool>();
            if (json.TryGetValue("shipB", out token)) plan.ShipB = new GUIDUnion(token.Value<string>());
            if (json.TryGetValue("shipBName", out token)) plan.ShipBName = token.Value<string>();
            if (json.TryGetValue("shipBHomunculus", out token) && !string.IsNullOrEmpty(token.Value<string>()))
                plan.ShipBHomunculus = new GUIDUnion(token.Value<string>());
            if (json.TryGetValue("hackCost", out token)) plan.HackCost = token.Value<int>();
            if (json.TryGetValue("arena", out token)) plan.Arena = token.Value<string>();
            return plan;
        }

        public override string ToString()
        {
            return Mode == FleetMode.OneShip ? "one ship" : (Mode == FleetMode.PvP ? "PvP, two ships" : "two ships" + (ScaleEnemies ? ", Fleet Pilgrimage enemy scaling" : ""));
        }
    }

    /// <summary>
    /// The fleet plan of the session that is loaded right now (hub or mission), the same on every client. The host
    /// attaches the plan to the GameSession it starts, the plan travels inside the session data the host sends to every
    /// client (the game's event 9, cached for late joiners), and each client reads it back from there.
    /// </summary>
    internal static class FleetSession
    {
        public const string Feature = "Fleet session data";
        private const string JsonKey = "vcf";

        private static readonly ConditionalWeakTable<GameSession, FleetPlan> Plans = new ConditionalWeakTable<GameSession, FleetPlan>();

        private static GameSession _cachedSession;
        private static FleetPlan _cachedPlan;

        /// <summary>Host: the mission about to start uses this plan.</summary>
        public static void Attach(GameSession session, FleetPlan plan)
        {
            if (session == null) return;
            Plans.Remove(session);
            if (plan != null) Plans.Add(session, plan);
            _cachedSession = null;
        }

        /// <summary>The plan of the loaded session, or null for a normal one-ship session (and in the hub).</summary>
        public static FleetPlan Current
        {
            get
            {
                GameSession session;
                try
                {
                    session = GameSessionManager.ActiveSession;
                }
                catch
                {
                    return null;
                }
                if (session == null) return null;
                if (!ReferenceEquals(session, _cachedSession))
                {
                    FleetPlan plan;
                    _cachedPlan = Plans.TryGetValue(session, out plan) ? plan : null;
                    _cachedSession = session;
                }
                return _cachedPlan;
            }
        }

        public static FleetMode Mode
        {
            get
            {
                FleetPlan plan = Current;
                return plan != null ? plan.Mode : FleetMode.OneShip;
            }
        }

        public static bool IsPvP
        {
            get { return Mode == FleetMode.PvP; }
        }

        public static int ShipCount
        {
            get { return Mode == FleetMode.OneShip ? 1 : 2; }
        }

        /// <summary>
        /// The player count the game's difficulty should be set for: with two ships, each ship's crew faces the
        /// enemies of a mission tuned for its own crew size (the Fleet Pilgrimage then adds its own extra enemies).
        /// </summary>
        public static int DifficultyPlayerCount(int roomPlayers)
        {
            if (ShipCount < 2) return roomPlayers;
            return Mathf.Max(1, Mathf.CeilToInt(roomPlayers / 2f));
        }

        // ---------------------------------------------------------------- the session data sent to clients

        [GamePatch(Feature, "GameSession", "AsJObject", PatchKind.Postfix)]
        private static void AsJObjectPostfix(GameSession __instance, JObject __result)
        {
            try
            {
                FleetPlan plan;
                if (__result != null && Plans.TryGetValue(__instance, out plan)) __result[JsonKey] = plan.ToJson();
            }
            catch (Exception e)
            {
                Log.Failure("Adding the fleet plan to the session data", e);
            }
        }

        [GamePatch(Feature, "GameSession", "FromJObject", PatchKind.Postfix)]
        private static void FromJObjectPostfix(JObject jObj, GameSession __result)
        {
            try
            {
                JToken token;
                if (__result == null || jObj == null || !jObj.TryGetValue(JsonKey, out token) || token.Type != JTokenType.Object) return;
                FleetPlan plan = FleetPlan.FromJson((JObject)token);
                Attach(__result, plan);
                Log.Info("This mission is a fleet mission: " + plan + ".");
            }
            catch (Exception e)
            {
                Log.Failure("Reading the fleet plan from the session data", e);
            }
        }
    }
}
