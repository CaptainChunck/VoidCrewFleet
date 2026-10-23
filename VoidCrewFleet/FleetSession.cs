using System;
using System.Collections.Generic;
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

        /// <summary>Two or three player ships on the same side, any mission.</summary>
        Coop = 1,

        /// <summary>Two teams of one to three ships each, fighting each other.</summary>
        PvP = 2
    }

    /// <summary>One ship of a mission's plan.</summary>
    internal sealed class ShipPlan
    {
        /// <summary>0-based; slot 0 is the ship the game itself spawns (GameSession.LoadShip).</summary>
        public int Slot;

        /// <summary>0 BLUE, 1 RED in PvP; 0 in co-op.</summary>
        public int Team;

        /// <summary>The loadout (ShipLoadoutDataDef) GUID it spawns with; empty for slot 0 and for a copy of slot 0.</summary>
        public GUIDUnion Loadout;

        /// <summary>The loadout as the host named it (players may not know a CUSTOM loadout's GUID).</summary>
        public string Name;

        /// <summary>The homunculus of its loadout, when it has one of its own (empty: the mission's).</summary>
        public GUIDUnion Homunculus;

        /// <summary>The ship type the host forced for it (empty: any).</summary>
        public GUIDUnion Type;

        /// <summary>Host only: what it spawns with; null for slot 0, and for a copy of slot 0 (its loadout at spawn time).</summary>
        public ShipLoadout Built;

        /// <summary>
        /// Host only: its starting alloys. -2 = the same as ship 1 (a copy of it), -1 = the game's own amount (a loadout of its
        /// own), 0 or more = its CUSTOM loadout's starting alloys (Ship Customizer).
        /// </summary>
        public int StartAlloys = -2;

        public JObject ToJson()
        {
            return new JObject
            {
                ["slot"] = Slot,
                ["team"] = Team,
                ["loadout"] = Loadout.AsHex(),
                ["name"] = Name ?? "",
                ["homunculus"] = Homunculus.AsHex(),
                ["type"] = Type.AsHex()
            };
        }

        public static ShipPlan FromJson(JObject json)
        {
            var ship = new ShipPlan();
            JToken token;
            if (json.TryGetValue("slot", out token)) ship.Slot = token.Value<int>();
            if (json.TryGetValue("team", out token)) ship.Team = token.Value<int>();
            ship.Loadout = Guid(json, "loadout");
            if (json.TryGetValue("name", out token)) ship.Name = token.Value<string>();
            ship.Homunculus = Guid(json, "homunculus");
            ship.Type = Guid(json, "type");
            return ship;
        }

        private static GUIDUnion Guid(JObject json, string key)
        {
            JToken token;
            if (!json.TryGetValue(key, out token)) return GUIDUnion.Empty();
            string hex = token.Value<string>();
            if (string.IsNullOrEmpty(hex)) return GUIDUnion.Empty();
            try
            {
                return new GUIDUnion(hex);
            }
            catch
            {
                return GUIDUnion.Empty();
            }
        }
    }

    /// <summary>The fleet settings of one mission, fixed when the host starts it.</summary>
    internal sealed class FleetPlan
    {
        public const int Protocol = 6;

        public FleetMode Mode;

        /// <summary>The Fleet Pilgrimage: more enemies per sector.</summary>
        public bool ScaleEnemies;

        /// <summary>Every ship, slot 0 first. Slot 0 is the game's own ship (only its type and name are used).</summary>
        public readonly List<ShipPlan> Ships = new List<ShipPlan>();

        /// <summary>PvP: what the airlock hacking terminal costs in the fabricator (the host's setting, the same for everyone).</summary>
        public int HackCost = HackTerminal.DefaultCost;

        /// <summary>PvP: the arena (auto, Dz-A, Or-A, Oz-B, Defiance or off); the host's setting.</summary>
        public string Arena = "auto";

        /// <summary>PvP: how many times the game's hull hit points every ship has (the host's setting, the same for everyone).</summary>
        public float HullMultiplier = 1f;

        public int ShipCount
        {
            get { return Mathf.Max(1, Ships.Count); }
        }

        /// <summary>PvP: ships per team (1-3); 0 in co-op.</summary>
        public int ShipsPerTeam
        {
            get { return Mode == FleetMode.PvP ? Ships.Count / 2 : 0; }
        }

        /// <summary>A slot's plan, or null.</summary>
        public ShipPlan Ship(int slot)
        {
            return slot >= 0 && slot < Ships.Count ? Ships[slot] : null;
        }

        public JObject ToJson()
        {
            var ships = new JArray();
            foreach (ShipPlan ship in Ships) ships.Add(ship.ToJson());
            return new JObject
            {
                ["protocol"] = Protocol,
                ["mode"] = (int)Mode,
                ["scale"] = ScaleEnemies,
                ["ships"] = ships,
                ["hackCost"] = HackCost,
                ["arena"] = Arena ?? "auto",
                ["hull"] = HullMultiplier
            };
        }

        public static FleetPlan FromJson(JObject json)
        {
            var plan = new FleetPlan();
            JToken token;
            if (json.TryGetValue("mode", out token)) plan.Mode = (FleetMode)token.Value<int>();
            if (json.TryGetValue("scale", out token)) plan.ScaleEnemies = token.Value<bool>();
            if (json.TryGetValue("ships", out token) && token is JArray)
            {
                foreach (JToken ship in (JArray)token)
                {
                    if (ship is JObject && plan.Ships.Count < FleetSlots.MaxSlots) plan.Ships.Add(ShipPlan.FromJson((JObject)ship));
                }
                for (int i = 0; i < plan.Ships.Count; i++) plan.Ships[i].Slot = i;
            }
            if (json.TryGetValue("hackCost", out token)) plan.HackCost = token.Value<int>();
            if (json.TryGetValue("arena", out token)) plan.Arena = token.Value<string>();
            if (json.TryGetValue("hull", out token)) plan.HullMultiplier = Mathf.Clamp(token.Value<float>(), 1f, 10f);
            return plan;
        }

        public override string ToString()
        {
            switch (Mode)
            {
                case FleetMode.OneShip:
                    return "one ship";
                case FleetMode.PvP:
                    return "PvP, " + ShipsPerTeam + " v " + ShipsPerTeam;
                default:
                    return ShipCount + " ships" + (ScaleEnemies ? ", Fleet Pilgrimage enemy scaling" : "");
            }
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

        /// <summary>How many ships the loaded mission has (1 without a fleet plan).</summary>
        public static int ShipCount
        {
            get
            {
                FleetPlan plan = Current;
                return plan != null && plan.Mode != FleetMode.OneShip ? plan.ShipCount : 1;
            }
        }

        /// <summary>
        /// The player count the game's difficulty should be set for: with several ships, each ship's crew faces the
        /// enemies of a mission tuned for its own crew size (the Fleet Pilgrimage then adds its own extra enemies).
        /// </summary>
        public static int DifficultyPlayerCount(int roomPlayers)
        {
            int ships = ShipCount;
            if (ships < 2) return roomPlayers;
            return Mathf.Max(1, Mathf.CeilToInt(roomPlayers / (float)ships));
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
