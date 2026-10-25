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
            CrewList.Feature,
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
        /// Work that acts on what its feature's patches change is only registered while that feature is on: a feature switched
        /// off at start-up has its patches taken out again (Patcher), and its work would act without them. This runs after
        /// every start-up patch, and the patches that wait for the game to load are all optional (the offline checker makes
        /// sure), so no feature is switched off later.
        /// </summary>
        public static void Listen()
        {
            Register("Notice.Listen", () => Notice.Listen());
            Register("HubFleet.Listen", () => HubFleet.Listen());
            Register("PvpTeams.Listen", () => PvpTeams.Listen());
            Register("VoidJumpLink.Listen", VoidJumpLink.Feature, () => VoidJumpLink.Listen());
            Register("PvpMatch.Listen", PvpMatch.Feature, () => PvpMatch.Listen());
            Register("PvpSabotage.Listen", PvpSabotage.Feature, () => PvpSabotage.Listen());
            Register("PvpPunch.Listen", PvpCombat.Feature, () => PvpPunch.Listen());
            Register("PvpTurrets.Listen", PvpTurrets.Feature, () => PvpTurrets.Listen());
            Register("PvpHack.Listen", HackTerminal.Feature, () => PvpHack.Listen());

            Register("PvpPunch.Frame", PvpCombat.Feature, () => FleetRuntime.Frame += PvpPunch.Frame);
            Register("PvpTurrets.Frame", PvpTurrets.Feature, () => FleetRuntime.Frame += PvpTurrets.Frame);
            Register("PvpHack.Frame", HackTerminal.Feature, () => FleetRuntime.Frame += PvpHack.Frame);
            Register("PvpVoice.Frame", PvpVoice.Feature, () => FleetRuntime.Frame += PvpVoice.Frame);
            Register("ShipResources.Frame", ShipResources.Feature, () => FleetRuntime.Frame += ShipResources.Frame);
            Register("Spectator.Frame", Spectator.Feature, () => FleetRuntime.Frame += Spectator.Frame);
            // The crew list works without its two (optional) patches: it is never left out.
            Register("CrewList.Frame", () => FleetRuntime.Frame += CrewList.Frame);
            Register("FistPose.Late", PvpCombat.Feature, () => FleetRuntime.Late += FistPose.Late);

            Register("ShipResources.OnShipLost", ShipResources.Feature, () => ShipDeath.ShipLost += ShipResources.OnShipLost);
            Register("Spectator.OnShipLost", Spectator.Feature, () => ShipDeath.ShipLost += Spectator.OnShipLost);
            Register("FleetReturn.OnShipLost", FleetReturn.Feature, () => ShipDeath.ShipLost += FleetReturn.OnShipLost);
            Register("ShipMarkers.OnShipLost", ShipMarkers.Feature, () => ShipDeath.ShipLost += ShipMarkers.OnShipLost);
            Register("CloneTubeScreen.OnShipLost", CloneTubeScreen.Feature, () => ShipDeath.ShipLost += CloneTubeScreen.OnShipLost);
            Register("CloneTubes.HoldSpawns", CloneTubes.Feature, () => CloneTubes.HoldSpawns = () => PvpTeams.Choosing);

            Register("HackTerminal.EnsureRegistered", HackTerminal.Feature, () => FleetRuntime.Slow += HackTerminal.EnsureRegistered);
            Register("HubFleet.Poll", () => FleetRuntime.Slow += HubFleet.Poll);
            Register("PvpRoster.PublishLocalId", () => FleetRuntime.Slow += PvpRoster.PublishLocalId);
            Register("PvpRoster.Tick", () => FleetRuntime.Slow += PvpRoster.Tick);
            Register("CloneTubes.Recode", CloneTubes.Feature, () => FleetRuntime.Slow += CloneTubes.Recode);
            Register("ShipDeath.Tick", ShipDeath.Feature, () => FleetRuntime.Slow += ShipDeath.Tick);
            Register("PvpTeams.KeepHubTeamsComplete", () => FleetRuntime.Slow += PvpTeams.KeepHubTeamsComplete);
            Register("PvpTeamScreen.Tick", () => FleetRuntime.Slow += PvpTeamScreen.Tick);
            Register("CrewBadge.Tick", () => FleetRuntime.Slow += CrewBadge.Tick);
            Register("CrewList.Tick", () => FleetRuntime.Slow += CrewList.Tick);
            Register("PvpCombat.KeepFactions", PvpCombat.Feature, () => FleetRuntime.Slow += PvpCombat.KeepFactions);
            Register("PvpCombat.KeepHostility", PvpCombat.Feature, () => FleetRuntime.Slow += PvpCombat.KeepHostility);
            Register("PvpMatch.Tick", PvpMatch.Feature, () => FleetRuntime.Slow += PvpMatch.Tick);
            Register("PvpSabotage.Apply", PvpSabotage.Feature, () => FleetRuntime.Slow += PvpSabotage.Apply);
            Register("PvpHack.Tick", HackTerminal.Feature, () => FleetRuntime.Slow += PvpHack.Tick);
            Register("PvpArena.Tick", PvpArena.Feature, () => FleetRuntime.Slow += PvpArena.Tick);
            Register("PvpMarkers.Tick", PvpMarkers.Feature, () => FleetRuntime.Slow += PvpMarkers.Tick);
            Register("ShipMarkers.Tick", ShipMarkers.Feature, () => FleetRuntime.Slow += ShipMarkers.Tick);
            Register("HelmIsolation.Tick", HelmIsolation.Feature, () => FleetRuntime.Slow += HelmIsolation.Tick);
            Register("ProgressionGuard.Tick", ProgressionGuard.Feature, () => FleetRuntime.Slow += ProgressionGuard.Tick);
            Register("FleetApi.Tick", () => FleetRuntime.Slow += FleetApi.Tick);
            Register("AutoTest.Tick", () => FleetRuntime.Slow += AutoTest.Tick);
        }

        /// <summary>Runs one registration; a failure is logged in full and the next registrations still run.</summary>
        private static void Register(string what, Action register)
        {
            Plugin.Step("Registering " + what, register);
        }

        /// <summary>Runs one registration of a feature's work, only while the feature is on (its start-up report line says why it is off).</summary>
        private static void Register(string what, string feature, Action register)
        {
            if (!Patcher.IsReady(feature))
            {
                Log.Info("Not running " + what + ": " + feature + " is off.");
                return;
            }
            Register(what, register);
        }
    }
}
