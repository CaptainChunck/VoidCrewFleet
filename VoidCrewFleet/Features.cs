namespace VoidCrewFleet
{
    /// <summary>The plugin's features, for the start-up report, and their message handlers and periodic work.</summary>
    internal static class Features
    {
        public static readonly string[] All =
        {
            PlayerCap.Feature,
            ModRegistration.Feature,
            ProgressionGuard.Feature,
            FleetSession.Feature,
            HubFleet.Feature,
            HubTerminalUi.Feature,
            GalaxyMapModes.Feature,
            FleetShips.Feature,
            ShipIsolation.Feature,
            ShipMarkers.Feature,
            ShipResources.Feature,
            ShipAudio.Feature,
            HelmIsolation.Feature,
            VoidJumpLink.Feature,
            FleetDifficulty.Feature,
            PvpCombat.Feature,
            PvpMatch.Feature,
            PvpBoarding.Feature,
            PvpSabotage.Feature,
            PvpMarkers.Feature,
            PvpVoice.Feature,
            PvpTurrets.Feature,
            BreachCharge.Feature
        };

        public static void Listen()
        {
            Notice.Listen();
            HubFleet.Listen();
            PvpTeams.Listen();
            PvpMatch.Listen();
            PvpSabotage.Listen();
            PvpPunch.Listen();
            PvpTurrets.Listen();
            PvpBreach.Listen();
            FleetRuntime.Frame += PvpPunch.Frame;
            FleetRuntime.Frame += PvpTurrets.Frame;
            FleetRuntime.Frame += PvpBreach.Frame;
            FleetRuntime.Frame += PvpVoice.Frame;
            FleetRuntime.Frame += ShipResources.Frame;
            ShipDeath.ShipLost += ShipResources.OnShipLost;
            CloneTubes.HoldSpawns = () => PvpTeams.Choosing;
            FleetRuntime.Slow += BreachCharge.EnsureRegistered;
            FleetRuntime.Slow += HubFleet.Poll;
            FleetRuntime.Slow += ShipSpawn.KeepCrewsUpToDate;
            FleetRuntime.Slow += PvpTeamScreen.Tick;
            FleetRuntime.Slow += PvpCombat.KeepFactions;
            FleetRuntime.Slow += PvpCombat.KeepHostility;
            FleetRuntime.Slow += PvpMatch.Tick;
            FleetRuntime.Slow += PvpSabotage.Apply;
            FleetRuntime.Slow += PvpBreach.Tick;
            FleetRuntime.Slow += PvpMarkers.Tick;
            FleetRuntime.Slow += ShipMarkers.Tick;
            FleetRuntime.Slow += HelmIsolation.Tick;
            FleetRuntime.Slow += AutoTest.Tick;
        }
    }
}
