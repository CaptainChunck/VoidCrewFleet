using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CG.Game;
using CG.Space;
using HarmonyLib;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// With two player ships, every ship part must find its own ship. The game finds "the" ship through the single
    /// ClientGame.PlayerShip, set by whichever ship woke last. This replaces those lookups, in the game's components that
    /// make them, with "the player ship this component is on, if any, otherwise the game's answer"; and the game's answer
    /// itself becomes the ship of the local player's crew. With one ship nothing changes: both are the game's own value.
    /// The method list was generated from the game's Assembly-CSharp (every component method that reads
    /// ClientGame.PlayerShip), leaving out the systems that get their own dedicated patches: spawning, sector changes,
    /// the playable area, the local player's own checks, EVA inertia zones, ship death, saves and combat.
    /// </summary>
    internal static class ShipRedirect
    {
        public const string Feature = FleetShips.Feature;

        private static readonly MethodInfo PlayerShipGetter = AccessTools.PropertyGetter(typeof(ClientGame), "PlayerShip");
        private static readonly MethodInfo RedirectMethod = AccessTools.Method(typeof(ShipRedirect), nameof(Redirect));
        private static readonly Dictionary<Type, FieldInfo> OuterThisFields = new Dictionary<Type, FieldInfo>();

        private static int _viewFrame = -1;
        private static PlayerControlledShip _viewShip;

        private static readonly List<PlayerControlledShip> Overrides = new List<PlayerControlledShip>();
        private static PlayerControlledShip _override;

        /// <summary>
        /// While a game method runs for a known ship (spawning a player on a ship's clone tube, a quest check for one
        /// crew), "the player ship" is that ship. Every call must be paired with <see cref="EndOverride"/> (in a
        /// finalizer); null pushes "no override" so nesting stays balanced.
        /// </summary>
        public static void BeginOverride(PlayerControlledShip ship)
        {
            Overrides.Add(ship);
            _override = ship;
        }

        public static void EndOverride()
        {
            if (Overrides.Count == 0) return;
            Overrides.RemoveAt(Overrides.Count - 1);
            _override = Overrides.Count > 0 ? Overrides[Overrides.Count - 1] : null;
        }

        // ---------------------------------------------------------------- the game's own "player ship"

        /// <summary>With two ships, "the player ship" of the local player's screen and logic is their crew's ship.</summary>
        [GamePatch(Feature, "CG.Game.ClientGame", "get_PlayerShip", PatchKind.Postfix)]
        private static void PlayerShipPostfix(ref PlayerControlledShip __result)
        {
            if (_override != null)
            {
                __result = _override;
                return;
            }
            if (!FleetShips.Active) return;
            int frame = Time.frameCount;
            if (frame != _viewFrame || _viewShip == null)
            {
                _viewFrame = frame;
                _viewShip = FleetShips.ViewShip;
            }
            if (_viewShip != null) __result = _viewShip;
        }

        // ---------------------------------------------------------------- ship parts find their own ship

        /// <summary>Replaces ClientGame.Current.PlayerShip in a ship part's method with the part's own ship.</summary>
        public static PlayerControlledShip Redirect(ClientGame game, object context)
        {
            PlayerControlledShip fallback = game != null ? game.PlayerShip : null;
            if (context == null || !FleetShips.Active) return fallback;
            Component component = context as Component ?? OuterComponent(context);
            if (component == null) return fallback;
            PlayerControlledShip own = FleetShips.OwnerOf(component);
            return own != null ? own : fallback;
        }

        /// <summary>For coroutine and lambda bodies: the component that created them.</summary>
        internal static Component OuterComponent(object context)
        {
            Type type = context.GetType();
            FieldInfo field;
            if (!OuterThisFields.TryGetValue(type, out field))
            {
                field = AccessTools.Field(type, "<>4__this");
                OuterThisFields[type] = field;
            }
            if (field == null) return null;
            try
            {
                return field.GetValue(context) as Component;
            }
            catch
            {
                return null;
            }
        }
        [GamePatch(Feature, "AbstractOrbitObjectEffect", "GetObjectOwner", PatchKind.Transpiler)]
        [GamePatch(Feature, "AreasOfEffectPlayershipMaterialApplier", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "AudioAlarmTrigger", "OnDangerProximity", PatchKind.Transpiler)]
        [GamePatch(Feature, "CameraShakeEffectTrigger", "TriggerCameraShake", PatchKind.Transpiler, Arguments = new string[] { "System.Single" })]
        [GamePatch(Feature, "CarryableSummonConsumptionEffect", "SummonUnits", PatchKind.Transpiler)]
        [GamePatch(Feature, "CentralComputerPoweredEvents", "OnDestroy", PatchKind.Transpiler)]
        [GamePatch(Feature, "CentralComputerPoweredEvents", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Audio.ShipAmbienceController", "OnDestroy", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Audio.SnapshotController", "OnPlatformSet", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Audio.VoiceController", "FindHomunculusCarryable", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Audio.VoiceController+<PlayMessageRoutine>d__23", "MoveNext", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Player.HUD.HUD_WeaponControlScreen", "Update", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Player.Interactions.Build.BuildProcessController", "get_BuildSocketController", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Player.Interactions.Build.BuildProcessController", "Update", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.ApplyPlayerShipModifier", "RemovePlayerShipModifier", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.ApplyPlayerShipModifier", "RunModifierToPlayerShip", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.AsteroidRain", "GetVolumetricNoiseDirection", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.AsteroidRain", "SetRandomSpawnDirection", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.AsteroidRain", "SpawnAsteroid", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.AsteroidRain", "UpdateConstantTwist", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.Interference", "GetModuleTargets", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.Interference", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.ModuleDisabler", "DeactivateAllModulesOfType", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.ModuleDisabler", "OnDisable", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.NearPlayerSpawner", "FixedUpdate", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.NearPlayerSpawner", "OnDrawGizmos", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.NearPlayerSpawner", "SpawnConfiguredObjects", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.NearPlayerSpawner", "TryGetSpawnPosition", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.ScorchingPulse", "RemovePlayerShipModifier", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.ScorchingPulse", "RunModifierToPlayerShip", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Ship.Airlock", "Awake", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Ship.AirlockButtonPanel", "Initialize", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Ship.Modules.Mediators.HelmMediator", "OnChairFreed", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Ship.Modules.Mediators.TacticalScannerMediator", "Awake", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Ship.Views.AbstractModuleDiodes", "get_playerShip", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Ship.Views.DefenceTerminalDiodes", "UpdateDiodes", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Ship.Views.WindowBlindsEffect", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.OrbitEffect.LimitedVerticalMovementOrbitEffect", "TriggerLowerLimitDamage", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.OrbitEffect.LimitedVerticalMovementOrbitEffect", "Update", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.OrbitEffect.NearSunOrbitEffect", "CheckAndTriggerTemperatureWarnings", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.OrbitEffect.NearSunOrbitEffect", "Update", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.Scenarios.WavesSpawnManager", "Update", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.AimForShipSpaceCraftController", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.ImpulseJumping.NpcImpulseJumper", "JumpToPlayerSectorPosition", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.ImpulseJumping.NpcImpulseJumper", "TEST_SetCommand_PingPongToPlayerSector", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.ProximityMineController", "NetworkNotifyTargetSet", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.ReinforcementCalling.NpcReinforcementCaller", "OnEnable", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.ScanningEnemyController", "get_playerShip", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.Scout.ScoutController", "get_PlayerShip", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.SwarmController", "DroneShouldBreakAttack", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Objects.AbstractEnvironmentalObject", "get_Ship", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Objects.CarryableObject", "get_Ship", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Objects.CarryableObject", "UpdateSector", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Hull.AbstractDoor", "Awake", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Hull.BuildSocket", "SetModule", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Hull.CarryablesSocket", "get_Ship", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Hull.CarryablesSocket", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.AutoMechanicModule", "Awake", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.CentralShipComputerModule", "EnterStateOff", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.CentralShipComputerModule", "EnterStateOn", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.Helm", "HandleAnalytics", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.JammerModule", "LaunchFromOwner", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.LifeSupportModule", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.Shield.ShieldModule", "ToggleShield", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.ThrusterBooster", "Awake", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.ThrusterBooster", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.ThrusterBoosterController", "OnThrusterBoostActivatePressed", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.ThrusterBoosterController", "UpdateBoosters", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.ThrusterBoosterControllerPun", "Awake", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.ThrusterStationModule", "get_IsInUse", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.VoidDriveModule", "GetReferences", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.VoidDriveModule", "UpdateSignatureAmount", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Object.InteractiveCarryableModuleUpgrader", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.PlayerShipCollider", "get_MyObject", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.PlayerShipCollider", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Scanning.EnemyScanComponent", "OnOrbitObjectArrivedAtDestination", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Scanning.ScanComponent", "GetScanFlags", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Scanning.ScanComponent", "ScanObjectInternal", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Scanning.SignatureComponent", "get_PlayerShip", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Shield.Effects.ShieldEmitterAnimatorEffect", "OnShieldDirectionChanged", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Status.ShipStatusNotificationController", "InitializeShipStatusEvents", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.TargetLocking.TargetLockComponent", "Awake", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.TargetLocking.TargetLockComponent", "CanBeTargetLocked", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.TargetLocking.TargetLockComponent", "IsTargetLockValidMissileTarget", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.TargetLocking.TargetLockComponent", "OnOrbitObjectScanChanged", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.TargetLocking.TargetLockComponent", "OnSpaceObjectDestroyed", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.TargetLocking.TargetLockComponent", "OnWarp", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Space.Bombs.TimedBombEffects", "Update", PatchKind.Transpiler)]
        [GamePatch(Feature, "Client.Player.Interactions.ModuleStatsContextModifierData", "GetTarget", PatchKind.Transpiler)]
        [GamePatch(Feature, "CoopBuddyStatsSync", "ChairFreed", PatchKind.Transpiler)]
        [GamePatch(Feature, "CoopBuddyStatsSync", "ChairTaken", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.AstralMap.AstralMapPUN", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.CompositeWeapons.CompositeWeaponModule", "get_Ship", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.Defects.PlayerShipDefectDamageController", "GetHullHealthFraction", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.Helm.ShipExternalCamera", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.Loot.LootManager", "SectorOrbitObjectAdded", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.NPC.AI.Spawner", "SpawnUnitEntry", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.SectorGameplay.BlackHole", "Awake", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.SectorGameplay.BlackHole", "FixedUpdate", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.SectorGameplay.BlackHole", "PointHasLOSToPlayerShip", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.SectorGameplay.BlackHoleCalamity", "FindPointWithLOS", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.SectorGameplay.VoidRift", "DamagePlayers", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.SectorGameplay.VoidRift", "OnDestroy", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.SectorGameplay.VoidRift", "OnTriggerEnter", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.Ship.ShipAutoTilt", "Awake", PatchKind.Transpiler)]
        [GamePatch(Feature, "ModuleUpgraderEffects", "StartBeingCarried", PatchKind.Transpiler)]
        [GamePatch(Feature, "MovementSpeedParticleScaler", "Update", PatchKind.Transpiler)]
        [GamePatch(Feature, "PlayerShipMovementAudio", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "PlayerShipMovementEffects", "GetModules", PatchKind.Transpiler)]
        [GamePatch(Feature, "PlayerShipMovementEffects", "Update", PatchKind.Transpiler)]
        [GamePatch(Feature, "RuntimeAssets.PlayerShipVisualsLoader", "OnPlayerShipSet", PatchKind.Transpiler)]
        [GamePatch(Feature, "RuntimeAssets.PlayerShipVisualsLoader", "TryLoadVisuals", PatchKind.Transpiler)]
        [GamePatch(Feature, "SilentMode", "Awake", PatchKind.Transpiler)]
        [GamePatch(Feature, "SpaceObjectDangerDetector", "Awake", PatchKind.Transpiler)]
        [GamePatch(Feature, "SpaceObjectDangerDetector", "OnSpaceObjectDestroyed", PatchKind.Transpiler)]
        [GamePatch(Feature, "SpaceObjectDangerDetector", "OnSpaceObjectSectorChanged", PatchKind.Transpiler)]
        [GamePatch(Feature, "SpaceObjectDangerDetector", "OnSpaceObjectSpawned", PatchKind.Transpiler)]
        [GamePatch(Feature, "SpaceObjectDangerDetector", "Update", PatchKind.Transpiler)]
        [GamePatch(Feature, "SpaceObjectDangerDetector", "UpdateDangerFactor", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.AimAssist.AimAssistMarkerViewer", "OnLocalWeaponControlToggled", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.AstralMap.AstralMapController", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.MainHUD.ShipInfoHUD", "OnEnable", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.MainHUD.ShipInfoHUD", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.MainHUD.ThreatPanelController", "CanDisplay", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.MainHUD.ThreatPanelController+<Start>d__20", "MoveNext", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.ObjectMarker.ExitMarkerProvider", "ExitVectorChanged", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.ObjectMarker.ExitMarkerProvider", "ShipCreated", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.ObjectMarker.ObjectMarkerViewer", "get_shouldShowLocksBasedOnPlatform", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.ObjectMarker.ObjectMarkerViewer", "ModeChanged", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.ObjectMarker.ObjectMarkerViewer", "OnLocalGameStateChanged", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.ObjectMarker.ObjectMarkerViewer+<Start>d__37", "MoveNext", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.ObjectMarker.UrgencyMarkerProvider", "Init", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.ObjectMarker.UrgencyMarkerProvider", "OnDisable", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.ObjectMarker.UrgencyMarkerProvider", "SubscribeToEvents", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.PilotHUD.LaunchersDisplayer+<Start>d__8", "MoveNext", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.PilotHUD.PilotHUD", "Awake", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.PilotHUD.ShieldAbsorptionDisplay", "OnEnable", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.PilotHUD.ShieldDisplayer+<Start>d__12", "MoveNext", PatchKind.Transpiler)]
        [GamePatch(Feature, "UI.Ping.PingableItem", "get_DisplayText", PatchKind.Transpiler)]
        [GamePatch(Feature, "VFX.Calamities.BlackHoleAudioController", "EnsureSharedLoopStarted", PatchKind.Transpiler)]
        [GamePatch(Feature, "VFX.Calamities.BlackHoleAudioController", "ResolveLocalIntensity", PatchKind.Transpiler)]
        [GamePatch(Feature, "VFX.Calamities.BlackHoleCameraShake", "Update", PatchKind.Transpiler)]
        [GamePatch(Feature, "VFX.ImpulsePreArrivalEffect", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "VFX.OrbitObjectDeathEffect", "OnTriggerDestroyEffects", PatchKind.Transpiler)]
        [GamePatch(Feature, "VFX.OrbitObjectSpawnSimpleDebrisOnDeath", "OnTriggerDestroyEffects", PatchKind.Transpiler)]
        [GamePatch(Feature, "VFX.PlayerShipHitEffectObserver", "SubscribeToPlayerShipLoop", PatchKind.Transpiler)]
        [GamePatch(Feature, "VFX.ThrusterEffectPlayerInput", "InitializeBoosterEffect", PatchKind.Transpiler)]
        [GamePatch(Feature, "VoidDriveEnginePart", "Awake", PatchKind.Transpiler)]
        [GamePatch(Feature, "VoidDriveEnginePart", "OnImpulseDriveEngineStateChanged", PatchKind.Transpiler)]
        [GamePatch(Feature, "VoidDriveEnginePart", "Update", PatchKind.Transpiler)]
        [GamePatch(Feature, "VoidJumpChargeMeter", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "VoidJumpFlow", "SetUp", PatchKind.Transpiler)]
        [GamePatch(Feature, "VolumetricEffectApplier", "GetVolumetricNoiseDirection", PatchKind.Transpiler)]
        // A ship's own death sequence destroys its own ship (ShipDeath decides which sequence plays).
        [GamePatch(Feature, "PlayerShipDeathSequence+<DeathRoutine>d__23", "MoveNext", PatchKind.Transpiler)]
        [GamePatch(Feature, "PlayerShipDeathSequence", "DisableShipShields", PatchKind.Transpiler)]
        private static IEnumerable<CodeInstruction> RedirectTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && instruction.operand is MethodInfo &&
                    (MethodInfo)instruction.operand == PlayerShipGetter)
                {
                    // Stack: [ClientGame] -> [ClientGame, this] -> Redirect -> [PlayerControlledShip]. Labels and exception
                    // blocks move to the first new instruction so branches (the ?. pattern) still land on it.
                    var context = new CodeInstruction(original.IsStatic ? OpCodes.Ldnull : OpCodes.Ldarg_0);
                    context.labels.AddRange(instruction.labels);
                    context.blocks.AddRange(instruction.blocks);
                    yield return context;
                    yield return new CodeInstruction(OpCodes.Call, RedirectMethod);
                    replaced++;
                    continue;
                }
                yield return instruction;
            }
            if (replaced == 0) Log.Warn("Two ships: " + original.DeclaringType.FullName + "." + original.Name + " no longer reads the player ship; the game may have changed.");
        }
        // ---------------------------------------------------------------- sector twists and hazards act on every ship

        private static bool _everyShip;
        private static readonly FieldInfo BlackHolePlatformField = GameMember.Field("Gameplay.SectorGameplay.BlackHole", "_playerShipPlatform");

        /// <summary>
        /// A world effect's method that acts on "the player ship" (a sector twist's stat modifiers, a module disabler, an
        /// interference event) runs once per player ship, each in turn as "the player ship", so every client changes every
        /// ship alike. Without this each client changed only the ship its own player was on (the stats are local to each
        /// client, and the host decides damage from them).
        /// </summary>
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.ApplyPlayerShipModifier", "RunModifierToPlayerShip", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.ApplyPlayerShipModifier", "RemovePlayerShipModifier", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.ScorchingPulse", "RunModifierToPlayerShip", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.ScorchingPulse", "RemovePlayerShipModifier", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.ModuleDisabler", "DeactivateAllModulesOfType", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.Interference", "ToggleRandomSystem", PatchKind.Prefix)]
        private static bool EveryShipPrefix(object __instance, MethodBase __originalMethod, object[] __args)
        {
            if (_everyShip || !FleetShips.Active) return true;
            var ships = new List<PlayerControlledShip>(FleetShips.All);
            _everyShip = true;
            try
            {
                foreach (PlayerControlledShip ship in ships)
                {
                    if (ship == null) continue;
                    BeginOverride(ship);
                    try
                    {
                        __originalMethod.Invoke(__instance, __args);
                    }
                    catch (Exception e)
                    {
                        Log.Failure("Running " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " for " + FleetShips.Describe(ship), e);
                    }
                    finally
                    {
                        EndOverride();
                    }
                }
            }
            finally
            {
                _everyShip = false;
            }
            return false;
        }

        /// <summary>An interference event's random defect hits every ship (the game kept the defect controller of one).</summary>
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.Interference", "ActivateRandomDefect", PatchKind.Prefix)]
        private static bool InterferenceDefectPrefix(Component __instance)
        {
            if (!FleetShips.Active) return true;
            try
            {
                var view = __instance.GetComponent<Photon.Pun.PhotonView>();
                if (view == null || !view.IsMine) return false;
                foreach (PlayerControlledShip ship in new List<PlayerControlledShip>(FleetShips.All))
                {
                    var defects = ship != null ? ship.GetComponent<Gameplay.Defects.PlayerShipDefectDamageController>() : null;
                    if (defects != null) defects.ActivateRandomDefect();
                }
            }
            catch (Exception e)
            {
                Log.Failure("Giving every ship an interference defect", e);
            }
            return false;
        }

        /// <summary>
        /// When a module disabler ends, the game clears the malfunction effect on one ship's modules; the other ships' are
        /// cleared here (the game's own pass then does the remaining ship).
        /// </summary>
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.ModuleDisabler", "OnDisable", PatchKind.Prefix)]
        private static void ModuleDisablerOffPrefix(object ___moduleTarget)
        {
            if (!FleetShips.Active || ___moduleTarget == null) return;
            try
            {
                ClientGame game = ClientGame.Current;
                PlayerControlledShip current = game != null ? game.PlayerShip : null;
                MethodInfo getModules = typeof(PlayerControlledShip).GetMethod("GetModules", new[] { ___moduleTarget.GetType() });
                if (getModules == null) return;
                foreach (PlayerControlledShip ship in new List<PlayerControlledShip>(FleetShips.All))
                {
                    if (ship == null || ship == current) continue;
                    var modules = getModules.Invoke(ship, new[] { ___moduleTarget }) as System.Collections.IEnumerable;
                    if (modules == null) continue;
                    foreach (object module in modules)
                    {
                        var component = module as Component;
                        var effect = component != null ? component.GetComponent<VFX.MalfunctionEffect>() : null;
                        if (effect != null) effect.ToggleMalfunctionEffect(false);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Failure("Clearing a module disabler's effect on every ship", e);
            }
        }

        /// <summary>
        /// Host: a black hole pulls and damages the player ship nearest to it, each physics step. The game kept the ship the
        /// host was on when it spawned (and damaged whichever ship the host was on at the time).
        /// </summary>
        [GamePatch(Feature, "Gameplay.SectorGameplay.BlackHole", "FixedUpdate", PatchKind.Prefix)]
        private static void BlackHolePrefix(Component __instance, out bool __state)
        {
            __state = false;
            if (!FleetShips.Active || !Photon.Pun.PhotonNetwork.IsMasterClient || BlackHolePlatformField == null) return;
            try
            {
                PlayerControlledShip nearest = null;
                float best = float.MaxValue;
                Vector3 here = __instance.transform.position;
                foreach (PlayerControlledShip ship in FleetShips.All)
                {
                    if (ship == null || ship.StartedDeath || ship.Platform == null) continue;
                    float distance = (ship.Platform.Position - here).sqrMagnitude;
                    if (distance >= best) continue;
                    best = distance;
                    nearest = ship;
                }
                if (nearest == null) return;
                BlackHolePlatformField.SetValue(__instance, nearest.Platform);
                BeginOverride(nearest);
                __state = true;
            }
            catch (Exception e)
            {
                Log.Failure("Pointing a black hole at the nearest ship", e);
            }
        }

        [GamePatch(Feature, "Gameplay.SectorGameplay.BlackHole", "FixedUpdate", PatchKind.Finalizer)]
        private static void BlackHoleFinalizer(bool __state)
        {
            if (__state) EndOverride();
        }
    }
}
