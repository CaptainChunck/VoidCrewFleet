using System;
using System.Text;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// For other plugins, reached through reflection (plugin GUID com.captainchunck.voidcrew.fleet), so neither needs the
    /// other. Ship Customizer uses it to let only a ship's own team change that ship's loadout in Fleet Battle (PvP).
    /// Ships are numbered 1 and 2; teams are 0 (BLUE, crews ship 1) and 1 (RED, crews ship 2). Every member can be
    /// called at any time and never throws.
    /// </summary>
    public static class FleetApi
    {
        /// <summary>Raised when a member changes in a way older callers don't understand.</summary>
        public const int ApiVersion = 1;

        private static readonly Color Blue = new Color(0.24f, 0.55f, 1f, 1f);
        private static readonly Color Red = new Color(1f, 0.33f, 0.27f, 1f);

        private static string _signature;

        /// <summary>
        /// Raised on every client when the room's team match starts or ends, or a player's team or the teams' lock
        /// changes (checked a few times a second).
        /// </summary>
        public static event Action TeamsChanged;

        /// <summary>
        /// True: at the start of a run Fleet gives ship 2 its own starting alloys (its CUSTOM loadout's, through Ship
        /// Customizer's GetShip2Alloys, or else the game's own amount) unless ship 2 is a copy of ship 1.
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

        /// <summary>The team of a player: 0 BLUE, 1 RED, -1 none (or not a team match).</summary>
        public static int TeamOf(int actorNumber)
        {
            try
            {
                return IsTeamMatch ? FleetShips.CrewSlot(actorNumber) : -1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>The team that owns a ship: ship 1 BLUE (0), ship 2 RED (1); -1 when it isn't a team match.</summary>
        public static int TeamOfShip(int ship)
        {
            return IsTeamMatch && (ship == 1 || ship == 2) ? ship - 1 : -1;
        }

        /// <summary>
        /// Whether a player may change a ship's loadout: always outside a team match; in one, only the ship's own team
        /// (someone without a team can't change either ship).
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

        /// <summary>A few times a second: raise TeamsChanged when the picture changed.</summary>
        internal static void Tick()
        {
            string signature;
            try
            {
                var text = new StringBuilder();
                text.Append(IsTeamMatch ? '1' : '0').Append(PvpTeams.Locked ? 'L' : '-');
                if (IsTeamMatch && PhotonNetwork.InRoom)
                {
                    foreach (Photon.Realtime.Player player in PhotonNetwork.PlayerList)
                        text.Append(player.ActorNumber).Append(':').Append(FleetShips.CrewSlot(player.ActorNumber)).Append(',');
                }
                signature = text.ToString();
            }
            catch
            {
                return;
            }
            if (signature == _signature) return;
            _signature = signature;
            Action handlers = TeamsChanged;
            if (handlers == null) return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action)handler)();
                }
                catch (Exception e)
                {
                    Log.Failure("Telling another plugin that the teams changed", e);
                }
            }
        }
    }
}
