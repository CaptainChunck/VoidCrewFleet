using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CG.Client.Player.Interactions.Build;
using CG.Client.Quests.SectorTwists;
using CG.Game;
using CG.Game.Scenarios;
using CG.Game.SpaceObjects.Controllers;
using CG.Ship.Hull;
using CG.Ship.Modules;
using CG.Ship.Scanning;
using CG.Space;
using HarmonyLib;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// With two player ships, every ship part must find its own ship. The game finds "the" ship through the single
    /// ClientGame.PlayerShip, set by whichever ship woke last. This replaces those lookups, in the game's components that
    /// make them, with "the player ship this component is on, if any, otherwise the game's answer"; and the game's answer
    /// itself becomes the ship of the local player's crew. In co-op, things in space that pick a ship to go after (enemies
    /// aiming, scouts looking, scanners, drones, summoned escorts) get the living player ship nearest to them instead, and
    /// the mission's scenario rules (where they place what they spawn, which ship they damage) the one nearest to what set
    /// them off: otherwise they would all go after whatever ship the host is on. With one ship nothing changes: every answer
    /// is the game's own value.
    /// The method list was generated from the game's Assembly-CSharp (every component method that reads
    /// ClientGame.PlayerShip), leaving out the systems that get their own dedicated patches: spawning, sector changes,
    /// the playable area, the local player's own checks, EVA inertia zones, ship death, saves and combat; and below it, the
    /// ship parts that keep their ship themselves (the scanner, the signature) and the sector twists that spawn at a ship.
    /// </summary>
    internal static class ShipRedirect
    {
        public const string Feature = FleetShips.Feature;

        private static readonly MethodInfo PlayerShipGetter = Getter(GameMember.Property(typeof(ClientGame), "PlayerShip"));
        private static readonly MethodInfo SignatureShipGetter = Getter(GameMember.Property(typeof(SignatureComponent), "PlayerShip"));
        private static readonly FieldInfo ScannerShipField = GameMember.Field(typeof(ScanComponent), "_playerShip");
        private static readonly MethodInfo RedirectMethod = new Func<ClientGame, object, PlayerControlledShip>(Redirect).Method;
        private static readonly MethodInfo RedirectNearestMethod = new Func<ClientGame, object, PlayerControlledShip>(RedirectNearest).Method;
        private static readonly MethodInfo OwnShipMethod = new Func<object, PlayerControlledShip>(OwnShip).Method;
        private static readonly MethodInfo ScannerShipMethod = new Func<ClientGame, PlayerControlledShip, PlayerControlledShip>(ScannerShip).Method;
        private static readonly Dictionary<Type, FieldInfo> OuterThisFields = new Dictionary<Type, FieldInfo>();

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

        /// <summary>
        /// With two ships, "the player ship" of the local player's screen and logic is their crew's ship (FleetShips.ViewShip,
        /// worked out once a frame: the game reads this getter tens of thousands of times a second).
        /// </summary>
        [GamePatch(Feature, "CG.Game.ClientGame", "get_PlayerShip", PatchKind.Postfix)]
        private static void PlayerShipPostfix(ref PlayerControlledShip __result)
        {
            try
            {
                if (_override != null)
                {
                    __result = _override;
                    return;
                }
                if (!FleetShips.Active) return;
                PlayerControlledShip view = FleetShips.ViewShip;
                if (view != null) __result = view;
            }
            catch (Exception e)
            {
                Log.Failure("Choosing the player ship", e);
            }
        }

        /// <summary>A property's getter, or null when the property is missing.</summary>
        private static MethodInfo Getter(PropertyInfo property)
        {
            return property != null ? property.GetGetMethod(true) : null;
        }

        // ---------------------------------------------------------------- ship parts find their own ship

        /// <summary>
        /// Replaces ClientGame.Current.PlayerShip in a ship part's method with the part's own ship. The game's answer (the
        /// patched getter) is only read when the part is on no ship.
        /// </summary>
        public static PlayerControlledShip Redirect(ClientGame game, object context)
        {
            if (context != null && FleetShips.Active)
            {
                Component component = context as Component ?? OuterComponent(context);
                PlayerControlledShip own = component != null ? FleetShips.OwnerOf(component) : null;
                if (own != null) return own;
            }
            return GameAnswer(game);
        }

        /// <summary>
        /// The game's own answer, read as the game reads it. While a scene unloads, ClientGame is already destroyed but still
        /// answers with its last ship; a Unity null check here gave no ship instead, and the game's teardown code then threw
        /// (a ship's ambience unhooking from its power as the hub unloaded, before stopping its sounds).
        /// </summary>
        private static PlayerControlledShip GameAnswer(ClientGame game)
        {
            return ReferenceEquals(game, null) ? null : game.PlayerShip;
        }

        /// <summary>
        /// Replaces ClientGame.Current.PlayerShip in the method of something in space that picks a ship to go after: the player
        /// ship it is on, if any; else in co-op the living player ship nearest to it (unless a method has set the ship itself,
        /// BeginOverride); else the game's answer (in PvP, the local player's own ship, as for any other part).
        /// </summary>
        public static PlayerControlledShip RedirectNearest(ClientGame game, object context)
        {
            if (context != null && FleetShips.Active)
            {
                Component component = context as Component ?? OuterComponent(context);
                if (component != null)
                {
                    PlayerControlledShip own = FleetShips.OwnerOf(component);
                    if (own != null) return own;
                    if (_override == null && FleetSession.Mode == FleetMode.Coop)
                    {
                        PlayerControlledShip nearest;
                        try
                        {
                            nearest = NearestShip(component.transform.position);
                        }
                        catch
                        {
                            // Being torn down: the game's answer then.
                            nearest = null;
                        }
                        if (nearest != null) return nearest;
                    }
                }
            }
            return GameAnswer(game);
        }

        /// <summary>
        /// Put in where a ship part reads its ship through a static getter of its own (which has no part to go by): the part's
        /// own ship, else the game's answer.
        /// </summary>
        public static PlayerControlledShip OwnShip(object context)
        {
            return Redirect(ClientGame.Current, context);
        }

        /// <summary>Put in where a ship's scanner reads "the player ship": the ship it keeps in its own field, else the game's answer.</summary>
        public static PlayerControlledShip ScannerShip(ClientGame game, PlayerControlledShip own)
        {
            if (FleetShips.Active && own != null) return own;
            return GameAnswer(game);
        }

        /// <summary>The living player ship nearest to a point, or null when none is left.</summary>
        public static PlayerControlledShip NearestShip(Vector3 point)
        {
            PlayerControlledShip nearest = null;
            float best = float.MaxValue;
            List<PlayerControlledShip> ships = FleetShips.All;
            for (int i = 0; i < ships.Count; i++)
            {
                PlayerControlledShip ship = ships[i];
                if (ship == null || ship.StartedDeath || ship.Platform == null) continue;
                float distance = (ship.Platform.Position - point).sqrMagnitude;
                if (distance >= best) continue;
                best = distance;
                nearest = ship;
            }
            return nearest;
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
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.AsteroidRain", "UpdateConstantTwist", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.Interference", "GetModuleTargets", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.Interference", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.ModuleDisabler", "DeactivateAllModulesOfType", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.ModuleDisabler", "OnDisable", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.NearPlayerSpawner", "FixedUpdate", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.NearPlayerSpawner", "OnDrawGizmos", PatchKind.Transpiler)]
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
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.ImpulseJumping.NpcImpulseJumper", "JumpToPlayerSectorPosition", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.ImpulseJumping.NpcImpulseJumper", "TEST_SetCommand_PingPongToPlayerSector", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.ProximityMineController", "NetworkNotifyTargetSet", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.ReinforcementCalling.NpcReinforcementCaller", "OnEnable", PatchKind.Transpiler)]
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
            return ReplacePlayerShip(instructions, original, RedirectMethod);
        }

        /// <summary>Things in space that pick a player ship to go after: in co-op the living one nearest to them (RedirectNearest).</summary>
        [GamePatch(Feature, "CarryableSummonConsumptionEffect", "SummonUnits", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.AimForShipSpaceCraftController", "Start", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.ScanningEnemyController", "get_playerShip", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.Scout.ScoutController", "get_PlayerShip", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.SwarmController", "DroneShouldBreakAttack", PatchKind.Transpiler)]
        private static IEnumerable<CodeInstruction> NearestTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return ReplacePlayerShip(instructions, original, RedirectNearestMethod);
        }

        /// <summary>
        /// A ship's scanner keeps its own ship in a field: its checks read that, with no lookup at all. (The passive scan asks
        /// once per object in the sector and per player, every frame; a pilot's scan request is checked by the host against
        /// the scanning ship's own helm.)
        /// </summary>
        [GamePatch(Feature, "CG.Ship.Scanning.ScanComponent", "GetScanFlags", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Scanning.ScanComponent", "ScanObjectInternal", PatchKind.Transpiler)]
        private static IEnumerable<CodeInstruction> ScannerTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            // Without the field (a game update) the scanner finds its ship as any other part does.
            if (ScannerShipField == null) return ReplacePlayerShip(instructions, original, RedirectMethod);
            // Stack: [ClientGame] -> [ClientGame, this._playerShip] -> ScannerShip -> [PlayerControlledShip].
            return ReplaceCalls(instructions, original, PlayerShipGetter, ScannerShipMethod, "the player ship",
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, ScannerShipField));
        }

        /// <summary>
        /// A ship's signature (how far away enemies notice it) reached its ship through a static getter, which has no part to
        /// go by: every ship's signature component drove the local player's view ship, and the other ships kept their starting
        /// signature (on the host, which runs the enemies, moving or going silent did nothing for them). Its two methods read
        /// their own ship instead.
        /// </summary>
        [GamePatch(Feature, "CG.Ship.Scanning.SignatureComponent", "Start", PatchKind.Transpiler, Optional = true)]
        [GamePatch(Feature, "CG.Ship.Scanning.SignatureComponent", "Update", PatchKind.Transpiler, Optional = true)]
        private static IEnumerable<CodeInstruction> SignatureTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            // Stack: [] -> [this] -> OwnShip -> [PlayerControlledShip].
            return ReplaceCalls(instructions, original, SignatureShipGetter, OwnShipMethod, "its ship", new CodeInstruction(OpCodes.Ldarg_0));
        }

        private static IEnumerable<CodeInstruction> ReplacePlayerShip(IEnumerable<CodeInstruction> instructions, MethodBase original, MethodInfo replacement)
        {
            // Stack: [ClientGame] -> [ClientGame, this] -> Redirect -> [PlayerControlledShip].
            return ReplaceCalls(instructions, original, PlayerShipGetter, replacement, "the player ship",
                new CodeInstruction(original.IsStatic ? OpCodes.Ldnull : OpCodes.Ldarg_0));
        }

        /// <summary>
        /// Replaces each call of a getter with the given loads and a call of the replacement. Labels and exception blocks move
        /// to the first new instruction so branches (the ?. pattern) still land on it.
        /// </summary>
        private static IEnumerable<CodeInstruction> ReplaceCalls(IEnumerable<CodeInstruction> instructions, MethodBase original, MethodInfo getter,
            MethodInfo replacement, string what, params CodeInstruction[] loads)
        {
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (getter != null && (instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && instruction.operand is MethodInfo &&
                    (MethodInfo)instruction.operand == getter)
                {
                    for (int i = 0; i <= loads.Length; i++)
                    {
                        CodeInstruction added = i < loads.Length ? new CodeInstruction(loads[i].opcode, loads[i].operand) : new CodeInstruction(OpCodes.Call, replacement);
                        if (i == 0)
                        {
                            added.labels.AddRange(instruction.labels);
                            added.blocks.AddRange(instruction.blocks);
                        }
                        yield return added;
                    }
                    replaced++;
                    continue;
                }
                yield return instruction;
            }
            if (replaced == 0) Log.Warn("Two ships: " + original.DeclaringType.FullName + "." + original.Name + " no longer reads " + what + "; the game may have changed.");
        }

        // ---------------------------------------------------------------- sector twists and hazards act on every ship

        private static bool _everyShip;
        private static readonly FieldInfo BlackHolePlatformField = GameMember.Field("Gameplay.SectorGameplay.BlackHole", "_playerShipPlatform");

        /// <summary>The ship each black hole (by its instance ID) is pointed at, so its field is only set when that changes.</summary>
        private static readonly Dictionary<int, PlayerControlledShip> BlackHoleShips = new Dictionary<int, PlayerControlledShip>();

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
        /// host was on when it spawned (and damaged whichever ship the host was on at the time). The black hole's own field
        /// (the ship it pulls) is only set when the nearest ship changes.
        /// </summary>
        [GamePatch(Feature, "Gameplay.SectorGameplay.BlackHole", "FixedUpdate", PatchKind.Prefix)]
        private static void BlackHolePrefix(Component __instance, out bool __state)
        {
            __state = false;
            if (!FleetShips.Active || !Photon.Pun.PhotonNetwork.IsMasterClient || BlackHolePlatformField == null) return;
            try
            {
                PlayerControlledShip nearest = NearestShip(__instance.transform.position);
                if (nearest == null) return;
                int id = __instance.GetInstanceID();
                PlayerControlledShip pointed;
                if (!BlackHoleShips.TryGetValue(id, out pointed) || !ReferenceEquals(pointed, nearest))
                {
                    BlackHolePlatformField.SetValue(__instance, nearest.Platform);
                    // Black holes come one or a few a mission: the list only needs emptying now and then.
                    if (BlackHoleShips.Count >= 32 && !BlackHoleShips.ContainsKey(id)) BlackHoleShips.Clear();
                    BlackHoleShips[id] = nearest;
                }
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

        // ---------------------------------------------------------------- sector twists that spawn at a ship

        /// <summary>Each twist's turns so far (by its instance ID): its next spawn goes for the next living ship.</summary>
        private static readonly Dictionary<int, int> TwistTurns = new Dictionary<int, int>();

        private static readonly List<int> TwistSlots = new List<int>();

        private static readonly Func<OrbitObject, bool> Trackable = Bind<Func<OrbitObject, bool>>(GameMember.Method(typeof(SpawnedObjectTracker), "IsTrackable"));

        private static readonly Action<SpawnedObjectTracker, OrbitObject> DespawnTracked =
            Bind<Action<SpawnedObjectTracker, OrbitObject>>(GameMember.Method(typeof(SpawnedObjectTracker), "Despawn"));

        /// <summary>
        /// Host: a sector twist that isn't on a ship spawns at "the player ship": asteroid rain's rocks, and the enemies a
        /// near-player spawner brings. Each spawn goes for the next living ship in turn, so every ship of the fleet gets its
        /// share; the game sent them all at the ship the host was on. (Fleet Battle doesn't get these twists: the game only
        /// gives twists to a mission's objective sectors, and the match stays in the Pilgrimage's first sector. Were one there,
        /// it would take turns between the teams alike.)
        /// </summary>
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.AsteroidRain", "SpawnAsteroid", PatchKind.Prefix, Optional = true)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.NearPlayerSpawner", "SpawnConfiguredObjects", PatchKind.Prefix, Optional = true)]
        private static void TwistSpawnPrefix(Component __instance)
        {
            PlayerControlledShip ship = null;
            try
            {
                if (FleetShips.Active && __instance != null) ship = NextTwistShip(__instance.GetInstanceID());
            }
            catch (Exception e)
            {
                Log.Failure("Choosing the ship a sector twist spawns at", e);
            }
            BeginOverride(ship);
        }

        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.AsteroidRain", "SpawnAsteroid", PatchKind.Finalizer, Optional = true)]
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.NearPlayerSpawner", "SpawnConfiguredObjects", PatchKind.Finalizer, Optional = true)]
        private static void TwistSpawnFinalizer()
        {
            EndOverride();
        }

        /// <summary>The living ship a twist's next spawn goes for (they take turns, lowest number first), or null.</summary>
        private static PlayerControlledShip NextTwistShip(int twist)
        {
            FleetShips.AliveSlots(TwistSlots);
            if (TwistSlots.Count == 0) return null;
            int turn;
            // Twists come a few a sector: the list only needs emptying now and then.
            if (!TwistTurns.TryGetValue(twist, out turn) && TwistTurns.Count >= 64) TwistTurns.Clear();
            TwistTurns[twist] = turn + 1;
            return FleetShips.BySlot(TwistSlots[(turn & int.MaxValue) % TwistSlots.Count]);
        }

        /// <summary>
        /// Host, each physics step: a near-player spawner sends what it spawned away (to the off-world sector) once it is out
        /// of range of the player ship. With several ships that is out of range of every ship, so the enemies that came for
        /// one ship stay while the host is on another (the game measured from the host's ship only). The game's own checks
        /// and its own send-off do the rest.
        /// </summary>
        [GamePatch(Feature, "CG.Client.Quests.SectorTwists.SpawnedObjectTracker", "UpdateTrackedObjects", PatchKind.Prefix, Optional = true)]
        private static bool TrackedObjectsPrefix(SpawnedObjectTracker __instance, OrbitObject referenceObject, Queue<OrbitObject> ___trackedObjects,
            float ___maximumDespawnRange)
        {
            if (!FleetShips.Active || !(referenceObject is PlayerControlledShip) || ___trackedObjects == null || Trackable == null || DespawnTracked == null)
                return true;
            float range = ___maximumDespawnRange * ___maximumDespawnRange;
            int count = ___trackedObjects.Count;
            for (int i = 0; i < count; i++)
            {
                OrbitObject tracked = ___trackedObjects.Dequeue();
                try
                {
                    // Gone, or sent away already: no longer tracked (as the game does).
                    if (!Trackable(tracked)) continue;
                    if (___maximumDespawnRange > 0f && !NearAnyShip(tracked.WorldPosition, range)) DespawnTracked(__instance, tracked);
                    else ___trackedObjects.Enqueue(tracked);
                }
                catch (Exception e)
                {
                    ___trackedObjects.Enqueue(tracked);
                    Log.Failure("Checking what a near-player spawner spawned", e);
                }
            }
            return false;
        }

        /// <summary>Whether a point is within range (squared) of any player ship.</summary>
        private static bool NearAnyShip(Vector3 point, float rangeSquared)
        {
            List<PlayerControlledShip> ships = FleetShips.All;
            for (int i = 0; i < ships.Count; i++)
            {
                PlayerControlledShip ship = ships[i];
                if (ship != null && (ship.WorldPosition - point).sqrMagnitude <= rangeSquared) return true;
            }
            return false;
        }

        /// <summary>A fast call to a private game method, or null when it is missing or has changed.</summary>
        private static T Bind<T>(MethodInfo method) where T : class
        {
            if (method == null) return null;
            try
            {
                return Delegate.CreateDelegate(typeof(T), method, false) as T;
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- the mission's scenario rules

        /// <summary>
        /// Co-op: the ship a scenario rule means by "the player ship". The living player ship nearest to what set the rule off
        /// (that ship itself when it was a player ship); for a rule nothing in space set off, the lead ship of the fleet's void
        /// jumps (the living ship with the lowest number until a jump says otherwise). Null outside co-op with several ships.
        /// </summary>
        private static PlayerControlledShip ScenarioShip(Classifier.ClassifierContext context)
        {
            if (!FleetShips.Active || FleetSession.Mode != FleetMode.Coop) return null;
            object trigger, position;
            if (context != null && context.TryGetValue(Classifier.ClassifierContextParameter.TriggeringOrbitObject, out trigger) && trigger != null &&
                context.TryGetValue(Classifier.ClassifierContextParameter.TriggeringOrbitObjectObjectPosition, out position) && position is Vector3)
            {
                PlayerControlledShip nearest = NearestShip((Vector3)position);
                if (nearest != null) return nearest;
            }
            return FleetShips.BySlot(VoidJumpLink.LeaderSlot);
        }

        /// <summary>
        /// A scenario rule that places what it spawns or sends relative to "the player ship", damages it, or sets NPCs on it:
        /// while it runs, that is the ship ScenarioShip picks, not whichever ship the host is on.
        /// </summary>
        [GamePatch(Feature, "CG.Game.Scenarios.Actions.DamageShip", "Invoke", PatchKind.Prefix, Optional = true)]
        [GamePatch(Feature, "CG.Game.Scenarios.Actions.NpcCommandMove", "Invoke", PatchKind.Prefix, Optional = true)]
        [GamePatch(Feature, "CG.Game.Scenarios.Actions.PositioningModeExtensions", "GetPosition", PatchKind.Prefix, Optional = true)]
        private static void ScenarioPrefix(Classifier.ClassifierContext context)
        {
            PlayerControlledShip ship = null;
            try
            {
                ship = ScenarioShip(context);
            }
            catch (Exception e)
            {
                Log.Failure("Choosing the ship a scenario rule acts on", e);
            }
            BeginOverride(ship);
        }

        /// <summary>Something a scenario rule spawns "pointing at the player ship" points at the living ship nearest to it (co-op).</summary>
        [GamePatch(Feature, "CG.Game.Scenarios.Actions.OrientationModeExtensions", "GetRotation", PatchKind.Prefix, Optional = true)]
        private static void PointAtPrefix(Vector3 spawnPosition)
        {
            PlayerControlledShip ship = null;
            try
            {
                if (FleetShips.Active && FleetSession.Mode == FleetMode.Coop) ship = NearestShip(spawnPosition);
            }
            catch (Exception e)
            {
                Log.Failure("Choosing the ship a spawned object points at", e);
            }
            BeginOverride(ship);
        }

        [GamePatch(Feature, "CG.Game.Scenarios.Actions.DamageShip", "Invoke", PatchKind.Finalizer, Optional = true)]
        [GamePatch(Feature, "CG.Game.Scenarios.Actions.NpcCommandMove", "Invoke", PatchKind.Finalizer, Optional = true)]
        [GamePatch(Feature, "CG.Game.Scenarios.Actions.PositioningModeExtensions", "GetPosition", PatchKind.Finalizer, Optional = true)]
        [GamePatch(Feature, "CG.Game.Scenarios.Actions.OrientationModeExtensions", "GetRotation", PatchKind.Finalizer, Optional = true)]
        private static void ScenarioFinalizer()
        {
            EndOverride();
        }

        // ---------------------------------------------------------------- building and taking apart modules

        /// <summary>
        /// Taking a module apart or upgrading it starts by looking for the module's socket, among one ship's sockets: the
        /// game's one build controller keeps the sockets of whichever ship the host was on the first time anyone took a
        /// module apart, and the game's own checks look at "the player ship". On any other ship the module had no socket
        /// there, so the host marked it as being taken apart (switched off, and kept from switching on again) and then
        /// failed: the module stayed stuck in its slot for the rest of the mission, neither working nor coming out, and the
        /// slot with it. When the ship asked has no socket holding the module, the module's own ship answers.
        /// </summary>
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.BuildSocketController", "GetSocketForModule", PatchKind.Postfix, Optional = true)]
        private static void SocketForModulePostfix(CellModule module, ref BuildSocket __result)
        {
            if (__result != null || module == null || !FleetShips.Active) return;
            try
            {
                PlayerControlledShip own = FleetShips.OwnerOf(module);
                BuildSocketController sockets = own != null ? own.GetComponent<BuildSocketController>() : null;
                if (sockets == null || sockets.Sockets == null) return;
                foreach (BuildSocket socket in sockets.Sockets)
                {
                    if (socket != null && socket.InstalledModule == module)
                    {
                        __result = socket;
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Failure("Finding a module's socket on its own ship", e);
            }
        }

        /// <summary>
        /// The host's work on a socket (building a module in it, taking its module apart, upgrading it) treats the socket's
        /// own ship as "the player ship": the one-per-ship check, the "anything in the way" check and taking the old module
        /// off the ship's list of modules all go to that ship, not to whichever ship the host is on.
        /// </summary>
        [GamePatch(Feature, "CG.Client.Player.Interactions.Build.BuildProcess", "Run", PatchKind.Prefix, Optional = true)]
        private static void BuildProcessPrefix(BuildProcess __instance)
        {
            PlayerControlledShip ship = null;
            try
            {
                if (FleetShips.Active && __instance != null && __instance.Socket != null) ship = FleetShips.OwnerOf(__instance.Socket);
            }
            catch (Exception e)
            {
                Log.Failure("Choosing the ship a module is built on", e);
            }
            BeginOverride(ship);
        }

        [GamePatch(Feature, "CG.Client.Player.Interactions.Build.BuildProcess", "Run", PatchKind.Finalizer, Optional = true)]
        private static void BuildProcessFinalizer()
        {
            EndOverride();
        }
    }
}
