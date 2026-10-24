using BepInEx.Configuration;

namespace VoidCrewFleet
{
    /// <summary>The plugin's settings file (BepInEx\config\com.captainchunck.voidcrew.fleet.cfg).</summary>
    internal static class Settings
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<int> MaxPlayers;
        public static ConfigEntry<int> Ships;
        public static ConfigEntry<string> PlayerId;
        public static ConfigEntry<int> PvpShipsPerTeam;
        public static ConfigEntry<float> PvpHullMultiplier;
        public static ConfigEntry<string> PvpShipTypes;
        public static ConfigEntry<float> ShipSpacing;
        public static ConfigEntry<float> PvpStartDistance;
        public static ConfigEntry<float> EnemyMultiplier;
        public static ConfigEntry<int> HackTerminalCost;
        public static ConfigEntry<string> PvpArena;
        public static ConfigEntry<string> PvpShipRules;
        public static ConfigEntry<string> PlayerListKey;
        public static ConfigEntry<string> RedListKey;
        public static ConfigEntry<bool> VerboseLogging;
        public static ConfigEntry<AutoTestMode> AutoTest;
        public static ConfigEntry<bool> AutoTestJump;
        public static ConfigEntry<int> AutoTestShips;
        public static ConfigEntry<string> AutoTestLoadouts;
        public static ConfigEntry<string> AutoTestDestroy;
        public static ConfigEntry<int> AutoTestCrewShip;
        public static ConfigEntry<bool> AutoTestRespawn;
        public static ConfigEntry<bool> AutoTestReturn;
        public static ConfigEntry<bool> AutoTestFists;
        public static ConfigEntry<bool> AutoTestTour;
        public static ConfigEntry<bool> Profile;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("General", "Enabled", true,
                "Turn Void Crew Fleet on or off. When off, rooms you host are the game's own one-ship rooms and the game is untouched.");

            MaxPlayers = config.Bind("Lobby", "MaxPlayers", 24,
                new ConfigDescription("How many players a room you host can hold: up to 48 (8 per ship in a 3v3 Fleet Battle; co-op's three ships take 24). The game's own limit is 6.",
                    new AcceptableValueRange<int>(4, 48)));
            PlayerId = config.Bind("Lobby", "PlayerId", "",
                "When Steam isn't running: the ID that fleet rooms know you by, to give you your place back after a lost connection. Made by itself; leave it as it is.");
            Ships = config.Bind("Lobby", "Ships", 1,
                new ConfigDescription("How many ships the game's own missions start with in rooms you host (the SHIPS button on the FLEET panel at the loadout " +
                                      "terminal changes it, and this follows).",
                    new AcceptableValueRange<int>(1, FleetSlots.MaxCoopShips)));

            ShipSpacing = config.Bind("Two ships", "ShipSpacing", 160f,
                new ConfigDescription("How far apart, in metres, the ships start and arrive in each sector (centre to centre, side by side).",
                    new AcceptableValueRange<float>(90f, 1000f)));

            EnemyMultiplier = config.Bind("Fleet Pilgrimage", "EnemyMultiplier", 1.6f,
                new ConfigDescription("How many more enemies the Fleet Pilgrimage sends with two ships than the game would for one ship's crew: enemy group " +
                                      "sizes and how many units the AI keeps in the fight are both multiplied by this (a third ship adds as much again). " +
                                      "Only the host's setting counts; a change applies from the next mission.",
                    new AcceptableValueRange<float>(1f, 4f)));

            PvpStartDistance = config.Bind("PvP", "StartDistance", 3600f,
                new ConfigDescription("How far apart, in metres, the two teams start in a PvP match (on opposite sides of the arena, facing each other).",
                    new AcceptableValueRange<float>(800f, 9000f)));
            PvpHullMultiplier = config.Bind("PvP", "HullMultiplier", 4f,
                new ConfigDescription("How many times the game's own hull hit points every ship has in Fleet Battle. Only the host's setting counts.",
                    new AcceptableValueRange<float>(1f, 10f)));
            PvpShipsPerTeam = config.Bind("PvP", "ShipsPerTeam", 1,
                new ConfigDescription("Your last Fleet Battle ships per team (set on the lobby screen); a lobby you host starts with it.",
                    new AcceptableValueRange<int>(1, FleetSlots.MaxShipsPerTeam)));
            PvpShipTypes = config.Bind("PvP", "ShipTypes", "",
                "Your last Fleet Battle ship types (set on the lobby screen); a lobby you host starts with them.");

            PvpArena = config.Bind("PvP", "Arena", "auto",
                new ConfigDescription("Where Fleet Battle is fought: auto (an asteroid field of the run's solar system), Dz-A (ice field), Or-A (lava and asteroids), " +
                                      "Oz-B (asteroids), Defiance (the Survival arena), or off (the empty starting sector). Only the host's setting counts.",
                    new AcceptableValueList<string>("auto", "Dz-A", "Or-A", "Oz-B", "Defiance", "off")));

            HackTerminalCost = config.Bind("PvP", "HackTerminalCost", HackTerminal.DefaultCost,
                new ConfigDescription("How many alloys the Airlock Hacking Terminal costs in the fabricator. Only the host's setting counts.",
                    new AcceptableValueRange<int>(1, 60)));
            PvpShipRules = config.Bind("PvP", "ShipRules", "",
                "Your last Fleet Battle ship rules (set them on the lobby screen's SHIP RULES tab); a lobby you host starts with them.");

            PlayerListKey = config.Bind("Crew list", "PlayersKey", "<Keyboard>/leftBracket",
                "The key that opens the list of every player (before teams are picked), or of the BLUE team (once they are), when they don't all " +
                "fit in the crew list on the left. An Input System control path, such as <Keyboard>/leftBracket or <Keyboard>/f2. Tab is the " +
                "game's camera view.");
            RedListKey = config.Bind("Crew list", "RedTeamKey", "<Keyboard>/rightBracket",
                "The key that opens the list of the RED team once teams are picked, when they don't all fit in the list on the right.");

            VerboseLogging = config.Bind("Diagnostics", "VerboseLogging", false,
                "Write extra detail to the BepInEx log.");

            AutoTest = config.Bind("Diagnostics", "AutoTest", AutoTestMode.Off,
                "Developer self-test. Anything but Off makes the game host a private one-player room from the main menu by itself, start a mission " +
                "with that fleet mode, and log what the fleet looks like (with screenshots in BepInEx\\fleet-autotest). Leave it Off to play.");
            AutoTestJump = config.Bind("Diagnostics", "AutoTestJump", true,
                "With AutoTest = TwoShips or FleetPilgrimage: also make the lead ship void jump (debug charge) and log the other ships following it.");
            AutoTestShips = config.Bind("Diagnostics", "AutoTestShips", 2,
                new ConfigDescription("With AutoTest on: how many ships the self-test plans (co-op), or ships per team (PvP).", new AcceptableValueRange<int>(1, 3)));
            AutoTestLoadouts = config.Bind("Diagnostics", "AutoTestLoadouts", "",
                "With AutoTest on: the loadout GUIDs (32 hex digits each, comma-separated) the self-test chooses for ships 2, 3 and so on, e.g. a Ship " +
                "Customizer CUSTOM loadout. Empty entries keep the default.");
            AutoTestDestroy = config.Bind("Diagnostics", "AutoTestDestroy", "",
                "With AutoTest on: ship numbers (comma-separated) the self-test destroys one by one after its first reports, e.g. \"2\" or \"1,3\".");
            AutoTestCrewShip = config.Bind("Diagnostics", "AutoTestCrewShip", 1,
                new ConfigDescription("With AutoTest = TwoShips or FleetPilgrimage: the ship the test player picks on the crew screen.", new AcceptableValueRange<int>(1, 3)));
            AutoTestRespawn = config.Bind("Diagnostics", "AutoTestRespawn", false,
                "With AutoTest on: the test player dies once and is re-cloned, and the log says on which ship.");
            AutoTestReturn = config.Bind("Diagnostics", "AutoTestReturn", false,
                "With AutoTest on: after destroying ships, the host uses RETURN TO HUB and the test waits to be back in the hub.");
            AutoTestFists = config.Bind("Diagnostics", "AutoTestFists", false,
                "With AutoTest = PvP: the test player raises their fists and punches, with screenshots.");
            Profile = config.Bind("Diagnostics", "Profile", false,
                "Developer: times the plugin's own code (every patch method and periodic handler) and logs where its time went every 10 s. " +
                "Adds a little overhead of its own; leave it off when playing.");
            AutoTestTour = config.Bind("Diagnostics", "AutoTestTour", false,
                "With AutoTest = PvP: a free camera tours the arena (above it, behind each team, its centre, every ship, every salvage spot and a " +
                "supply drop called in for the tour), with a screenshot at each and the game's screens hidden.");
        }
    }
}
