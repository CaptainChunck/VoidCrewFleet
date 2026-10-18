using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace VoidCrewFleet
{
    /// <summary>The plugin's settings file (BepInEx\config\com.captainchunck.voidcrew.fleet.cfg).</summary>
    internal static class Settings
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<int> MaxPlayers;
        public static ConfigEntry<float> ShipSpacing;
        public static ConfigEntry<float> PvpStartDistance;
        public static ConfigEntry<float> EnemyMultiplier;
        public static ConfigEntry<Key> PunchKey;
        public static ConfigEntry<int> BreachChargeCost;
        public static ConfigEntry<bool> VerboseLogging;
        public static ConfigEntry<AutoTestMode> AutoTest;
        public static ConfigEntry<bool> AutoTestJump;
        public static ConfigEntry<string> AutoTestShip2;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("General", "Enabled", true,
                "Turn Void Crew Fleet on or off. When off, rooms you host are the game's own one-ship rooms and the game is untouched.");

            MaxPlayers = config.Bind("Lobby", "MaxPlayers", 16,
                new ConfigDescription("How many players a room you host can hold (8 per ship). The game's own limit is 6.",
                    new AcceptableValueRange<int>(4, 16)));

            ShipSpacing = config.Bind("Two ships", "ShipSpacing", 160f,
                new ConfigDescription("How far apart, in metres, the two ships start and arrive in each sector (centre to centre, side by side).",
                    new AcceptableValueRange<float>(90f, 1000f)));

            EnemyMultiplier = config.Bind("Fleet Pilgrimage", "EnemyMultiplier", 1.6f,
                new ConfigDescription("How many more enemies the Fleet Pilgrimage sends than the game would for one ship's crew: enemy group sizes and how many " +
                                      "units the AI keeps in the fight are both multiplied by this. Only the host's setting counts.",
                    new AcceptableValueRange<float>(1f, 4f)));

            PvpStartDistance = config.Bind("PvP", "StartDistance", 3600f,
                new ConfigDescription("How far apart, in metres, the two ships start in a PvP match (on opposite sides of the arena, facing each other).",
                    new AcceptableValueRange<float>(800f, 9000f)));

            PunchKey = config.Bind("PvP", "PunchKey", Key.V,
                "The key that throws a punch at an enemy player in front of you (PvP only).");

            BreachChargeCost = config.Bind("PvP", "BreachChargeCost", BreachCharge.DefaultCost,
                new ConfigDescription("How many alloys the Airlock Breaching Charge costs in the fabricator. Only the host's setting counts.",
                    new AcceptableValueRange<int>(1, 60)));

            VerboseLogging = config.Bind("Diagnostics", "VerboseLogging", false,
                "Write extra detail to the BepInEx log.");

            AutoTest = config.Bind("Diagnostics", "AutoTest", AutoTestMode.Off,
                "Developer self-test. Anything but Off makes the game host a private one-player room from the main menu by itself, start a mission " +
                "with that fleet mode, and log what the fleet looks like (with screenshots in BepInEx\\fleet-autotest). Leave it Off to play.");
            AutoTestJump = config.Bind("Diagnostics", "AutoTestJump", true,
                "With AutoTest = TwoShips or FleetPilgrimage: also make ship 1 void jump (debug charge) and log ship 2 following it.");
            AutoTestShip2 = config.Bind("Diagnostics", "AutoTestShip2", "",
                "With AutoTest on: the loadout GUID (32 hex digits) the self-test chooses for ship 2, e.g. a Ship Customizer CUSTOM loadout. " +
                "Empty: ship 2 is a copy of ship 1.");
        }
    }
}
