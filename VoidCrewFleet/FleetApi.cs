using System;
using System.Linq;
using System.Text;
using Photon.Pun;
using ResourceAssets;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// For other plugins, reached through reflection (plugin GUID com.captainchunck.voidcrew.fleet), so neither needs the
    /// other. Ship Customizer uses it to let only a ship's own team change that ship's loadout in Fleet Battle (PvP), to
    /// follow the host's ship rules there, and to show each ship's CUSTOM for its forced type. Ships are numbered 1-6: ship 1
    /// is the game's own selection; co-op has ships 1-3; in Fleet Battle odd ships are BLUE (team 0) and even ships RED
    /// (team 1), so ship 1 is BLUE 1, ship 2 RED 1, ship 3 BLUE 2 and so on. Every member can be called at any time and
    /// never throws.
    /// </summary>
    public static class FleetApi
    {
        /// <summary>Raised when a member changes in a way older callers don't understand.</summary>
        public const int ApiVersion = 1;

        private static readonly Color Blue = new Color(0.24f, 0.55f, 1f, 1f);
        private static readonly Color Red = new Color(1f, 0.33f, 0.27f, 1f);

        private static string _signature;
        private static string _rulesSignature;
        private static string _shipsSignature;

        /// <summary>
        /// Raised on every client when the room's ship count or any ship's forced type changes (checked a few times a second).
        /// Added in 0.4.0.
        /// </summary>
        public static event Action ShipsChanged;

        /// <summary>
        /// How many ships the room plans for its next mission (in the hub), or the running mission has; 1 when there is no
        /// fleet. Added in 0.4.0: its presence means this Fleet knows ships 3-6.
        /// </summary>
        public static int ShipCount
        {
            get
            {
                try
                {
                    return GameSessionManager.InHub ? HubFleet.PlannedShips : FleetSession.ShipCount;
                }
                catch
                {
                    return 1;
                }
            }
        }

        /// <summary>
        /// The ship type the host forces for a ship (1-6): the ShipGuid of the loadouts of that type
        /// (ShipLoadoutDataDef.ShipGuid), or an empty GUID when any type is allowed. Added in 0.4.0.
        /// </summary>
        public static GUIDUnion HullOfShip(int ship)
        {
            try
            {
                if (ship < 1 || ship > ShipCount) return GUIDUnion.Empty();
                if (GameSessionManager.InHub) return HubFleet.ForcedType(ship - 1);
                FleetPlan plan = FleetSession.Current;
                ShipPlan entry = plan != null ? plan.Ship(ship - 1) : null;
                return entry != null ? entry.Type : GUIDUnion.Empty();
            }
            catch
            {
                return GUIDUnion.Empty();
            }
        }

        /// <summary>A ship's name for players: "SHIP 3" in co-op, "BLUE 2" or "RED 1" in Fleet Battle. Added in 0.4.0.</summary>
        public static string ShipName(int ship)
        {
            try
            {
                return FleetSlots.Name(ship - 1, IsTeamMatch);
            }
            catch
            {
                return "SHIP " + ship;
            }
        }

        /// <summary>The ship a player crews (1-6), or 0 when none (not placed yet, or a spectator). Added in 0.4.0.</summary>
        public static int ShipOfPlayer(int actorNumber)
        {
            try
            {
                int slot = FleetShips.CrewSlot(actorNumber);
                return slot >= 0 ? slot + 1 : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Raised on every client when the room's team match starts or ends, or a player's team or the teams' lock
        /// changes (checked a few times a second).
        /// </summary>
        public static event Action TeamsChanged;

        /// <summary>
        /// Raised on every client when the host's ship rules change, or start or stop applying (checked a few times a second).
        /// </summary>
        public static event Action ShipRulesChanged;

        /// <summary>
        /// True in a team match (Fleet Battle) while the host's ship rules restrict anything a CUSTOM loadout may use. The
        /// members below only refuse anything while it is true.
        /// </summary>
        public static bool ShipRulesActive
        {
            get
            {
                try
                {
                    return IsTeamMatch && ShipRules.Current.Restricts;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Whether an MK tier (1, 2 or 3) may be fitted; tier 0 (untiered) is always allowed.</summary>
        public static bool IsTierAllowed(int tier)
        {
            try
            {
                return !ShipRulesActive || ShipRules.Current.TierAllowed(tier);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>The highest fabricator starting level allowed: 0 is MK I, 2 is MK III (the highest allowed tier, less one).</summary>
        public static int FabricatorMaxLevel
        {
            get
            {
                try
                {
                    return ShipRulesActive ? ShipRules.Current.HighestTier - 1 : 2;
                }
                catch
                {
                    return 2;
                }
            }
        }

        /// <summary>Whether one of Ship Customizer's categories (its own upper-case names, e.g. "WEAPONS") may be used.</summary>
        public static bool IsCategoryAllowed(string category)
        {
            try
            {
                return !ShipRulesActive || ShipRules.Current.CategoryAllowed(category);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Whether one of Ship Customizer's options (its code: "m:", "w:", "i:" or "c:" and the GUID's hex) may be used.</summary>
        public static bool IsItemAllowed(string code)
        {
            try
            {
                return !ShipRulesActive || ShipRules.Current.ItemAllowed(code);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>The most starting alloys a CUSTOM loadout may set: int.MaxValue no limit, -1 only the game's own amount.</summary>
        public static int MaxStartAlloys
        {
            get
            {
                try
                {
                    return ShipRulesActive ? ShipRules.Current.MaxAlloys : int.MaxValue;
                }
                catch
                {
                    return int.MaxValue;
                }
            }
        }

        /// <summary>
        /// True: at the start of a run Fleet gives every ship 2-6 its own starting alloys (its CUSTOM loadout's, through Ship
        /// Customizer's GetShipAlloys or GetShip2Alloys, or else the game's own amount) unless it is a copy of ship 1.
        /// </summary>
        public static bool ReadsShip2Alloys
        {
            get { return true; }
        }

        /// <summary>True while the room's mission is FLEET BATTLE (picked in the hub), or a PvP match is running.</summary>
        public static bool IsTeamMatch
        {
            get
            {
                try
                {
                    if (FleetSession.IsPvP) return true;
                    return GameSessionManager.InHub && HubFleet.HostHasFleet && HubFleet.Variant == HubVariant.FleetBattle;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>The team of a player: 0 BLUE, 1 RED, -1 none (or not a team match). Spectators keep their team.</summary>
        public static int TeamOf(int actorNumber)
        {
            try
            {
                return IsTeamMatch ? FleetShips.TeamOfActor(actorNumber) : -1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>The team that owns a ship (1-6): odd ships BLUE (0), even ships RED (1); -1 when it isn't a team match or the ship isn't planned.</summary>
        public static int TeamOfShip(int ship)
        {
            if (!IsTeamMatch || ship < 1 || ship > Math.Max(2, ShipCount)) return -1;
            return (ship - 1) % 2;
        }

        /// <summary>
        /// Whether a player may change a ship's loadout: always outside a team match; in one, only the ship's own team
        /// (someone without a team can't change any ship).
        /// </summary>
        public static bool CanEditShip(int actorNumber, int ship)
        {
            if (!IsTeamMatch) return true;
            int team = TeamOf(actorNumber);
            return team >= 0 && team == TeamOfShip(ship);
        }

        /// <summary>The ship's team colour in a team match, otherwise Color.clear.</summary>
        public static Color ShipColor(int ship)
        {
            int team = TeamOfShip(ship);
            return team == 0 ? Blue : (team == 1 ? Red : Color.clear);
        }

        internal static Color TeamColor(int team)
        {
            return team == 0 ? Blue : (team == 1 ? Red : Color.clear);
        }

        /// <summary>
        /// A few times a second: raise TeamsChanged, ShipsChanged and ShipRulesChanged when what they describe changed.
        /// TeamsChanged only looks at the team match, the teams' lock and, in a team match, each player's team: a player
        /// moving between ships (or any co-op crew change) doesn't change who may edit which ship.
        /// </summary>
        internal static void Tick()
        {
            string signature;
            try
            {
                var text = new StringBuilder();
                bool teamMatch = IsTeamMatch;
                text.Append(teamMatch ? '1' : '0').Append(PvpTeams.Locked ? 'L' : '-');
                if (teamMatch && PhotonNetwork.InRoom)
                {
                    foreach (CrewEntry entry in FleetShips.Crews().Values.OrderBy(e => e.Actor)) text.Append(entry.Actor).Append(':').Append(entry.Team).Append(',');
                }
                signature = text.ToString();
            }
            catch
            {
                return;
            }
            if (signature != _signature)
            {
                _signature = signature;
                Raise(TeamsChanged, "the teams changed");
            }

            string ships;
            try
            {
                int count = ShipCount;
                var text = new StringBuilder().Append(count);
                for (int ship = 1; ship <= count; ship++) text.Append('|').Append(HullOfShip(ship).AsHex());
                ships = text.ToString();
            }
            catch
            {
                return;
            }
            if (ships != _shipsSignature)
            {
                _shipsSignature = ships;
                Raise(ShipsChanged, "the ships changed");
            }

            string rules;
            try
            {
                rules = (ShipRulesActive ? "1" : "0") + ShipRules.CurrentJson;
            }
            catch
            {
                return;
            }
            if (rules == _rulesSignature) return;
            _rulesSignature = rules;
            Raise(ShipRulesChanged, "the ship rules changed");
        }

        private static void Raise(Action handlers, string what)
        {
            if (handlers == null) return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action)handler)();
                }
                catch (Exception e)
                {
                    Log.Failure("Telling another plugin that " + what, e);
                }
            }
        }
    }
}
