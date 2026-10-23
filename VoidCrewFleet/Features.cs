using System;

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
            Spectator.Feature,
            FleetReturn.Feature,
            HackTerminal.Feature,
            PvpArena.Feature
        };

        /// <summary>
        /// Registers every message handler and every piece of per-frame and periodic work, each on its own: one that fails
        /// (its class can't load after a game update, or its set-up throws) is logged, and the others still register.
        /// Each step is a lambda rather than a method group, so its code is only compiled when it runs, inside its guard.
        /// </summary>
        public static void Listen()
        {
            Register("Notice.Listen", () => Notice.Listen());
            Register("HubFleet.Listen", () => HubFleet.Listen());
            Register("PvpTeams.Listen", () => PvpTeams.Listen());
            Register("VoidJumpLink.Listen", () => VoidJumpLink.Listen());
            Register("PvpMatch.Listen", () => PvpMatch.Listen());
            Register("PvpSabotage.Listen", () => PvpSabotage.Listen());
            Register("PvpPunch.Listen", () => PvpPunch.Listen());
            Register("PvpTurrets.Listen", () => PvpTurrets.Listen());
            Register("PvpHack.Listen", () => PvpHack.Listen());

            Register("PvpPunch.Frame", () => FleetRuntime.Frame += PvpPunch.Frame);
            Register("PvpTurrets.Frame", () => FleetRuntime.Frame += PvpTurrets.Frame);
            Register("PvpHack.Frame", () => FleetRuntime.Frame += PvpHack.Frame);
            Register("PvpVoice.Frame", () => FleetRuntime.Frame += PvpVoice.Frame);
            Register("ShipResources.Frame", () => FleetRuntime.Frame += ShipResources.Frame);
            Register("Spectator.Frame", () => FleetRuntime.Frame += Spectator.Frame);
            Register("FistPose.Late", () => FleetRuntime.Late += FistPose.Late);

            Register("ShipResources.OnShipLost", () => ShipDeath.ShipLost += ShipResources.OnShipLost);
            Register("Spectator.OnShipLost", () => ShipDeath.ShipLost += Spectator.OnShipLost);
            Register("FleetReturn.OnShipLost", () => ShipDeath.ShipLost += FleetReturn.OnShipLost);
            Register("ShipMarkers.OnShipLost", () => ShipDeath.ShipLost += ShipMarkers.OnShipLost);
            Register("CloneTubeScreen.OnShipLost", () => ShipDeath.ShipLost += CloneTubeScreen.OnShipLost);
            Register("CloneTubes.HoldSpawns", () => CloneTubes.HoldSpawns = () => PvpTeams.Choosing);

            Register("HackTerminal.EnsureRegistered", () => FleetRuntime.Slow += HackTerminal.EnsureRegistered);
            Register("HubFleet.Poll", () => FleetRuntime.Slow += HubFleet.Poll);
            Register("PvpRoster.PublishLocalId", () => FleetRuntime.Slow += PvpRoster.PublishLocalId);
            Register("PvpRoster.Tick", () => FleetRuntime.Slow += PvpRoster.Tick);
            Register("CloneTubes.Recode", () => FleetRuntime.Slow += CloneTubes.Recode);
            Register("ShipDeath.Tick", () => FleetRuntime.Slow += ShipDeath.Tick);
            Register("PvpTeams.KeepHubTeamsComplete", () => FleetRuntime.Slow += PvpTeams.KeepHubTeamsComplete);
            Register("PvpTeamScreen.Tick", () => FleetRuntime.Slow += PvpTeamScreen.Tick);
            Register("CrewBadge.Tick", () => FleetRuntime.Slow += CrewBadge.Tick);
            Register("PvpCombat.KeepFactions", () => FleetRuntime.Slow += PvpCombat.KeepFactions);
            Register("PvpCombat.KeepHostility", () => FleetRuntime.Slow += PvpCombat.KeepHostility);
            Register("PvpMatch.Tick", () => FleetRuntime.Slow += PvpMatch.Tick);
            Register("PvpSabotage.Apply", () => FleetRuntime.Slow += PvpSabotage.Apply);
            Register("PvpHack.Tick", () => FleetRuntime.Slow += PvpHack.Tick);
            Register("PvpArena.Tick", () => FleetRuntime.Slow += PvpArena.Tick);
            Register("PvpMarkers.Tick", () => FleetRuntime.Slow += PvpMarkers.Tick);
            Register("ShipMarkers.Tick", () => FleetRuntime.Slow += ShipMarkers.Tick);
            Register("HelmIsolation.Tick", () => FleetRuntime.Slow += HelmIsolation.Tick);
            Register("ProgressionGuard.Tick", () => FleetRuntime.Slow += ProgressionGuard.Tick);
            Register("FleetApi.Tick", () => FleetRuntime.Slow += FleetApi.Tick);
            Register("AutoTest.Tick", () => FleetRuntime.Slow += AutoTest.Tick);
        }

        /// <summary>Runs one registration; a failure is logged in full and the next registrations still run.</summary>
        private static void Register(string what, Action register)
        {
            Plugin.Step("Registering " + what, register);
        }
    }
}
