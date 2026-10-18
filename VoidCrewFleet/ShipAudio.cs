using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CG.Client.Audio;
using CG.Client.Quests.SectorTwists;
using CG.Game.Player;
using CG.Objects;
using CG.Ship.Status;
using CG.Space;
using Gameplay.Carryables;
using Gameplay.Ship.VoidJump;
using Gameplay.SpacePlatforms;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// The ship AI's voice lines and the ship sounds and effects stay with their own ship. The game has one voice-line
    /// controller, sets up one copy of its 102 ship status events for every ship that spawns, and reads "the player ship"
    /// while doing so; many of those events, and many sound and effect listeners, react to events that don't say which
    /// ship they come from. With two ships:
    /// - each copy of the status events belongs to the ship it was set up for, and speaks only when that ship is the one
    ///   the local player belongs to (co-op: aboard, else crew; PvP: their team's), and only for that ship's own events;
    /// - a ship's alarms, engine parts, diodes, lights and storm sounds react only to their own ship;
    /// - music follows the local player's ship; camera shakes and hit effects only reach players aboard the ship;
    /// - silent mode and the boost key act on their own ship only (the game applied them to both ships);
    /// - stepping aboard the other player ship is not "boarding a wreck".
    /// </summary>
    internal static class ShipAudio
    {
        public const string Feature = "Ship voice and sounds per ship";

        private static readonly ConditionalWeakTable<AbstractShipStatusEvent, PlayerControlledShip> EventShip =
            new ConditionalWeakTable<AbstractShipStatusEvent, PlayerControlledShip>();

        private static readonly List<PlayerControlledShip> StartingShips = new List<PlayerControlledShip>();
        private static readonly List<int> SetupCounts = new List<int>();

        private static readonly FieldInfo ActiveTwistField = GameMember.Field(typeof(CalamityAmbienceController), "_activeSectorTwist");
        private static readonly FieldInfo TimeChangedField = GameMember.Field(typeof(CalamityAmbienceController), "_timeChanged");

        private static PlayerControlledShip StartingShip
        {
            get { return StartingShips.Count > 0 ? StartingShips[StartingShips.Count - 1] : null; }
        }

        /// <summary>The player ship something belongs to: a ship, a player's crew ship, a carrier's ship or a part's ship.</summary>
        private static PlayerControlledShip ShipOf(object thing)
        {
            if (thing == null) return null;
            var ship = thing as PlayerControlledShip;
            if (ship != null) return ship;
            var player = thing as Player;
            if (player != null)
            {
                int actor = player.photonView != null && player.photonView.Owner != null ? player.photonView.Owner.ActorNumber : -1;
                PlayerControlledShip crew = FleetShips.ShipOfActor(actor);
                return crew != null ? crew : FleetShips.AboardShip(player);
            }
            var carrier = thing as ICarrier;
            if (carrier != null)
            {
                SpacePlatform platform = carrier.ZonedPlatform != null ? carrier.ZonedPlatform : carrier.Platform;
                PlayerControlledShip onPlatform = platform != null ? platform.GetComponent<PlayerControlledShip>() : null;
                if (onPlatform != null) return onPlatform;
            }
            var component = thing as Component;
            return component != null ? FleetShips.OwnerOf(component) : null;
        }

        // ---------------------------------------------------------------- one set of voice-line events per ship

        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "Start", PatchKind.Prefix)]
        private static void ShipStartPrefix(PlayerControlledShip __instance)
        {
            StartingShips.Add(__instance);
        }

        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "Start", PatchKind.Finalizer)]
        private static void ShipStartFinalizer()
        {
            if (StartingShips.Count > 0) StartingShips.RemoveAt(StartingShips.Count - 1);
        }

        /// <summary>The status events set up while a ship starts read that ship (they read "the player ship").</summary>
        [GamePatch(Feature, "CG.Ship.Status.ShipStatusNotificationController", "InitializeShipStatusEvents", PatchKind.Prefix)]
        private static void InitEventsPrefix(List<AbstractShipStatusEvent> ___activeEvents)
        {
            SetupCounts.Add(___activeEvents != null ? ___activeEvents.Count : 0);
            ShipRedirect.BeginOverride(StartingShip);
        }

        [GamePatch(Feature, "CG.Ship.Status.ShipStatusNotificationController", "InitializeShipStatusEvents", PatchKind.Finalizer)]
        private static void InitEventsFinalizer(List<AbstractShipStatusEvent> ___activeEvents)
        {
            ShipRedirect.EndOverride();
            int start = 0;
            if (SetupCounts.Count > 0)
            {
                start = SetupCounts[SetupCounts.Count - 1];
                SetupCounts.RemoveAt(SetupCounts.Count - 1);
            }
            try
            {
                PlayerControlledShip ship = StartingShip;
                if (ship == null && CG.Game.ClientGame.Current != null) ship = CG.Game.ClientGame.Current.PlayerShip;
                if (ship == null || ___activeEvents == null) return;
                for (int i = start; i < ___activeEvents.Count; i++)
                {
                    AbstractShipStatusEvent statusEvent = ___activeEvents[i];
                    if (statusEvent == null) continue;
                    EventShip.Remove(statusEvent);
                    EventShip.Add(statusEvent, ship);
                }
            }
            catch (Exception e)
            {
                Log.Failure("Tying the ship's voice lines to their ship", e);
            }
        }

        /// <summary>Whether a status event may speak: its own ship's events only, and only to that ship's players.</summary>
        private static bool StatusEventAllowed(AbstractShipStatusEvent statusEvent, PlayerControlledShip source)
        {
            PlayerControlledShip own;
            if (statusEvent == null || !EventShip.TryGetValue(statusEvent, out own)) return !ShipEvents.ForeignToLocal(source);
            if ((object)own != null && own == null) return false;
            if (source != null && source != own) return false;
            return !ShipEvents.ForeignToLocal(own);
        }

        private static PlayerControlledShip EventShipOf(AbstractShipStatusEvent statusEvent)
        {
            PlayerControlledShip own;
            return statusEvent != null && EventShip.TryGetValue(statusEvent, out own) && own != null ? own : null;
        }

        /// <summary>Every voice line and log line goes through here.</summary>
        [GamePatch(Feature, "CG.Ship.Status.ShipStatusNotificationController", "Fire", PatchKind.Prefix)]
        private static bool StatusFirePrefix(AbstractShipStatusEvent statusEvent)
        {
            try
            {
                bool sourced = statusEvent is GravityScoopEvent || statusEvent is PlayerShipExitVectorSetEvent || statusEvent is JumpTargetChangeEvent;
                return StatusEventAllowed(statusEvent, sourced ? ShipEvents.Current : null);
            }
            catch (Exception e)
            {
                Log.Failure("Choosing which ship a voice line belongs to", e);
                return true;
            }
        }

        // ---------------------------------------------------------------- who raised an event that carries no ship

        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "ChangeActiveStateByIndex", PatchKind.Prefix)]
        private static void JumpStatePrefix(VoidJumpSystem __instance)
        {
            ShipEvents.Begin(FleetShips.Active ? __instance.PlayerShip : null);
        }

        [GamePatch(Feature, "CG.Ship.Modules.GravityScoopModule", "AttractorSocketsFull", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Modules.GravityScoopModule", "AttractorItemCaptured", PatchKind.Prefix)]
        [GamePatch(Feature, "Gameplay.AstralMap.AstralMapPUN", "DestinationSet", PatchKind.Prefix)]
        [GamePatch(Feature, "SpaceObjectDangerDetector", "SetDangerFactor", PatchKind.Prefix)]
        [GamePatch(Feature, "VoidDriveEnginePart", "Update", PatchKind.Prefix)]
        [GamePatch(Feature, "Gameplay.Power.ProtectedPowerSystem", "NotifyForcedShutDown", PatchKind.Prefix)]
        [GamePatch(Feature, "SilentMode", "LocalOnlyStateSwitch", PatchKind.Prefix)]
        private static void PartEventPrefix(Component __instance)
        {
            ShipEvents.BeginFor(__instance);
        }

        [GamePatch(Feature, "ControllingHelm", "ActiveThrusters", PatchKind.Prefix)]
        private static void BoostKeyPrefix(CG.Ship.Modules.Helm ____helm)
        {
            ShipEvents.BeginFor(____helm);
        }

        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "ChangeActiveStateByIndex", PatchKind.Finalizer)]
        [GamePatch(Feature, "CG.Ship.Modules.GravityScoopModule", "AttractorSocketsFull", PatchKind.Finalizer)]
        [GamePatch(Feature, "CG.Ship.Modules.GravityScoopModule", "AttractorItemCaptured", PatchKind.Finalizer)]
        [GamePatch(Feature, "Gameplay.AstralMap.AstralMapPUN", "DestinationSet", PatchKind.Finalizer)]
        [GamePatch(Feature, "SpaceObjectDangerDetector", "SetDangerFactor", PatchKind.Finalizer)]
        [GamePatch(Feature, "VoidDriveEnginePart", "Update", PatchKind.Finalizer)]
        [GamePatch(Feature, "Gameplay.Power.ProtectedPowerSystem", "NotifyForcedShutDown", PatchKind.Finalizer)]
        [GamePatch(Feature, "SilentMode", "LocalOnlyStateSwitch", PatchKind.Finalizer)]
        [GamePatch(Feature, "ControllingHelm", "ActiveThrusters", PatchKind.Finalizer)]
        private static void PartEventFinalizer()
        {
            ShipEvents.End();
        }

        // ---------------------------------------------------------------- status events whose argument names the ship

        [GamePatch(Feature, "CG.Ship.Status.DoorEvent", "OnDoorChanged", PatchKind.Prefix)]
        private static bool DoorPrefix(AbstractShipStatusEvent __instance, CG.Ship.Hull.AbstractDoor obj)
        {
            return Allowed(__instance, obj);
        }

        [GamePatch(Feature, "CG.Ship.Status.CarrierActionEvent", "OnCarryablePickUp", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Status.CarrierActionEvent", "OnCarryableDropped", PatchKind.Prefix)]
        private static bool CarrierPrefix(AbstractShipStatusEvent __instance, ICarrier carrier, CarryableObject carryable)
        {
            PlayerControlledShip ship = ShipOf(carrier);
            return Allowed(__instance, ship != null ? (object)ship : carryable);
        }

        [GamePatch(Feature, "CG.Ship.Status.CarryableActionEvent", "OnCarryableInserted", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Status.CarryableActionEvent", "OnCarryableEjected", PatchKind.Prefix)]
        private static bool SocketPrefix(AbstractShipStatusEvent __instance, ICarryablesSocket socket)
        {
            return Allowed(__instance, socket);
        }

        [GamePatch(Feature, "CG.Ship.Status.CarryableConsumedEvent", "OnCarryableConsumed", PatchKind.Prefix)]
        private static bool ConsumedPrefix(AbstractShipStatusEvent __instance, CarryableObject orbitObject)
        {
            return Allowed(__instance, orbitObject);
        }

        [GamePatch(Feature, "CG.Ship.Status.CruisingModeEvent", "OnCruisingModeStatusChanged", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Status.ImpulseTravelEvent", "OnPlayerShipVoidJumpStateChanged", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Status.PlayerShipJumpStateChangedEvent", "OnPlayerShipVoidJumpStateChanged", PatchKind.Prefix)]
        private static bool ShipArgumentPrefix(AbstractShipStatusEvent __instance, PlayerControlledShip playerShip)
        {
            return Allowed(__instance, playerShip);
        }

        [GamePatch(Feature, "CG.Ship.Status.LifeformStateEvent", "OnLifeformStateChanged", PatchKind.Prefix)]
        private static bool LifeformPrefix(AbstractShipStatusEvent __instance, CG.Ship.Object.ILifeform lifeform)
        {
            return Allowed(__instance, lifeform);
        }

        [GamePatch(Feature, "CG.Ship.Status.PlayerEnteringLeavingShipEvent", "OnObjectEnteredSpace", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Status.PlayerEnteringLeavingShipEvent", "OnObjectLeftSpace", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Status.ObjectSpaceTransitionEvent", "OnObjectEnteredSpace", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Status.ObjectSpaceTransitionEvent", "OnObjectLeftSpace", PatchKind.Prefix)]
        private static bool SpaceTransitionPrefix(AbstractShipStatusEvent __instance, CG.Ship.IEnvironmentalObject environmentalObject)
        {
            return Allowed(__instance, environmentalObject);
        }

        private static bool Allowed(AbstractShipStatusEvent statusEvent, object thing)
        {
            try
            {
                if (!FleetShips.Active) return true;
                return StatusEventAllowed(statusEvent, ShipOf(thing));
            }
            catch (Exception e)
            {
                Log.Failure("Choosing which ship a voice line belongs to", e);
                return true;
            }
        }

        // ---------------------------------------------------------------- status events that read "the player ship" when they fire

        [GamePatch(Feature, "CG.Ship.Status.ShipHullEvent", "OnHitpointsChanged", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Status.PowerBreakerEvent", "BreakerFlipDown", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Status.PowerBreakerEvent", "OnPowerDrainChanged", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Status.PowerBreakerEvent", "SystemForcedShutdown", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Status.PowerBreakerEvent", "OnDisable", PatchKind.Prefix)]
        private static void OwnShipPrefix(AbstractShipStatusEvent __instance)
        {
            ShipRedirect.BeginOverride(FleetShips.Active ? EventShipOf(__instance) : null);
        }

        [GamePatch(Feature, "CG.Ship.Status.ShipHullEvent", "OnHitpointsChanged", PatchKind.Finalizer)]
        [GamePatch(Feature, "CG.Ship.Status.PowerBreakerEvent", "BreakerFlipDown", PatchKind.Finalizer)]
        [GamePatch(Feature, "CG.Ship.Status.PowerBreakerEvent", "OnPowerDrainChanged", PatchKind.Finalizer)]
        [GamePatch(Feature, "CG.Ship.Status.PowerBreakerEvent", "SystemForcedShutdown", PatchKind.Finalizer)]
        [GamePatch(Feature, "CG.Ship.Status.PowerBreakerEvent", "OnDisable", PatchKind.Finalizer)]
        private static void OwnShipFinalizer()
        {
            ShipRedirect.EndOverride();
        }

        // ---------------------------------------------------------------- a ship's own listeners react to their own ship only

        [GamePatch(Feature, "PlayerShipEffects", "OnPlayerShipDangerState", PatchKind.Prefix)]
        [GamePatch(Feature, "AudioAlarmTrigger", "OnDangerProximity", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Client.Ship.Hull.LightSourceController", "OnSilentModeChanged", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Modules.ThrusterBoosterController", "OnSilentModeChanged", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Modules.VoidDriveModule", "OnSilentModeChanged", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Scanning.ScanComponent", "OnSilentModeChanged", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Scanning.SignatureComponent", "SilentModeChange", PatchKind.Prefix)]
        [GamePatch(Feature, "Gameplay.Ship.ShipEngine", "SilentModeChange", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Modules.ThrusterBoosterController", "OnThrusterBoostActivatePressed", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Modules.Helm", "ThrustersActivated", PatchKind.Prefix)]
        private static bool OwnShipOnlyPrefix(Component __instance)
        {
            try
            {
                return !ShipEvents.ForeignTo(__instance);
            }
            catch (Exception e)
            {
                Log.Failure("Checking which ship an event belongs to", e);
                return true;
            }
        }

        /// <summary>An engine part spins up only for its own ship's drive (the game checks only the engine number).</summary>
        [GamePatch(Feature, "VoidDriveEnginePart", "OnImpulseDriveEngineStateChanged", PatchKind.Prefix)]
        private static bool EnginePartPrefix(CG.Ship.Modules.VoidDriveModule driveModule, CG.Ship.Modules.VoidDriveModule ____void)
        {
            return driveModule == null || ____void == null || driveModule == ____void;
        }

        [GamePatch(Feature, "CG.Client.Ship.Views.SyncChargeStateDiodeController", "OnImpulseSyncChanged", PatchKind.Prefix)]
        private static bool SyncDiodePrefix(CG.Ship.Modules.VoidDriveModule voidDriveModule, CG.Ship.Modules.VoidDriveModule ___voidDriveModule)
        {
            return voidDriveModule == null || ___voidDriveModule == null || voidDriveModule == ___voidDriveModule;
        }

        /// <summary>The void tunnel's state effects of a ship follow that ship's own jump (co-op: both ships jump).</summary>
        [GamePatch(Feature, "VoidJumpStateEvents", "OnPreviousStateRelevantJumpStateChanged", PatchKind.Prefix)]
        private static bool TunnelPrefix(PlayerControlledShip playerShip, VoidJumpSystem ___voidJumpSystem)
        {
            return ___voidJumpSystem == null || playerShip == null || playerShip == ___voidJumpSystem.PlayerShip;
        }

        // ---------------------------------------------------------------- the local player's ears and screen

        [GamePatch(Feature, "CG.Client.Audio.EncounterMusicTrigger", "OnPlayerShipVoidJumpPhaseChanged", PatchKind.Prefix)]
        private static bool EncounterMusicPrefix(PlayerControlledShip playerShip)
        {
            return !ShipEvents.ForeignToLocal(playerShip);
        }

        [GamePatch(Feature, "CG.Client.Audio.IdleMusicTrigger", "OnPlayerShipVoidJumpStateChanged", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Client.Audio.VoidJumpTransitionMusicTrigger", "OnPlayerShipVoidJumpStateChanged", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Client.Audio.VoidJumpTravelMusicTrigger", "OnPlayerShipVoidJumpStateChanged", PatchKind.Prefix)]
        private static bool JumpMusicPrefix(PlayerControlledShip playerControlledShip)
        {
            return !ShipEvents.ForeignToLocal(playerControlledShip);
        }

        /// <summary>The homunculus and ship computer "speak" visibly only on the ship whose voice line plays.</summary>
        [GamePatch(Feature, "HomunculusVisualEffect", "OnSpeakerEvent", PatchKind.Prefix)]
        private static bool HomunculusSpeakerPrefix(CarryableObject ___carryableObject)
        {
            return ___carryableObject == null || !ShipEvents.ForeignToLocal(FleetShips.OwnerOf(___carryableObject));
        }

        [GamePatch(Feature, "CentralShipComputerVoiceGizmo", "OnSpeakerEvent", PatchKind.Prefix)]
        private static bool ComputerSpeakerPrefix(Component __instance)
        {
            return !ShipEvents.ForeignToLocal(FleetShips.OwnerOf(__instance));
        }

        [GamePatch(Feature, "HomunculusVisualEffect", "OnSystemForcedShutdown", PatchKind.Prefix)]
        private static bool HomunculusShutdownPrefix(CarryableObject ___carryableObject)
        {
            return ___carryableObject == null || !ShipEvents.ForeignTo(___carryableObject);
        }

        /// <summary>Stepping aboard the other player ship is being aboard a ship, not boarding a wreck.</summary>
        [GamePatch(Feature, "CG.Client.Audio.SnapshotController", "OnPlatformSet", PatchKind.Prefix)]
        private static bool SnapshotPlatformPrefix(SnapshotController __instance, SpacePlatform newPlatform, ref SpacePlatform ___currentWreckPlatform)
        {
            try
            {
                if (!FleetShips.Active || GameSessionManager.InHub || newPlatform == null || newPlatform.GetComponent<PlayerControlledShip>() == null) return true;
                ___currentWreckPlatform = null;
                __instance.ExitBoarding();
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Choosing the sound mix aboard a ship", e);
                return true;
            }
        }

        /// <summary>
        /// Storm sounds: each ship's ambience plays its storm sounds only for the ship the local player belongs to, and turns
        /// them off when the storm ends (the game keeps one setting for all ships, so the other ship's stayed on).
        /// </summary>
        [GamePatch(Feature, "CG.Client.Audio.CalamityAmbienceController", "CheckConfig", PatchKind.Prefix)]
        private static bool CalamityPrefix(CalamityAmbienceController __instance, CalamityAudioConfig[] ___ConfigsPerSectorTwist)
        {
            try
            {
                if (!FleetShips.Active || ___ConfigsPerSectorTwist == null || ActiveTwistField == null || TimeChangedField == null) return true;
                PlayerControlledShip own = FleetShips.OwnerOf(__instance);
                if (own == null) return true;
                var twist = ActiveTwistField.GetValue(null) as SectorTwist;
                if (twist == null && Time.time - (float)TimeChangedField.GetValue(null) < 2f) return false;
                bool mine = !ShipEvents.ForeignToLocal(own);
                foreach (CalamityAudioConfig config in ___ConfigsPerSectorTwist)
                {
                    if (config == null) continue;
                    bool on = mine && twist != null && config.SectorTwist != null && config.SectorTwist.AssetGuid == twist.ContainerGuid;
                    SetActive(config.TransmitterGroups, on);
                    SetActive(config.ReceiverGroups, on);
                }
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Switching a ship's storm sounds", e);
                return true;
            }
        }

        private static void SetActive(GameObject[] groups, bool on)
        {
            if (groups == null) return;
            foreach (GameObject group in groups)
            {
                if (group != null && group.activeSelf != on) group.SetActive(on);
            }
        }

        // ---------------------------------------------------------------- shakes and hit effects for those aboard

        /// <summary>A shake raised by a ship's part (its engines charging) reaches only players aboard that ship.</summary>
        [GamePatch(Feature, "CameraShakeEffectController", "Shake", PatchKind.Prefix)]
        private static bool ShakePrefix()
        {
            PlayerControlledShip source = ShipEvents.Current;
            if (source == null || !FleetShips.Active) return true;
            PlayerControlledShip aboard = FleetShips.AboardShip(LocalPlayer.Instance);
            return aboard == null || aboard == source;
        }

        /// <summary>A shake meant for the crew inside a ship (hits, power loss) reaches only players aboard that ship.</summary>
        [GamePatch(Feature, "CameraShakeEffectTrigger", "TriggerCameraShake", PatchKind.Prefix, Arguments = new[] { "System.Single" })]
        private static bool ShakeTriggerPrefix(Component __instance, bool ___localPlayerMustBeInShip)
        {
            if (!FleetShips.Active || !___localPlayerMustBeInShip) return true;
            PlayerControlledShip own = FleetShips.OwnerOf(__instance);
            return own == null || FleetShips.AboardShip(LocalPlayer.Instance) == own;
        }

        [GamePatch(Feature, "VFX.PlayerShipHitEffectObserver", "OnPlayerShipHit", PatchKind.Prefix)]
        private static bool HitEffectPrefix(PlayerControlledShip ____playership)
        {
            if (!FleetShips.Active || ____playership == null) return true;
            PlayerControlledShip aboard = FleetShips.AboardShip(LocalPlayer.Instance);
            return aboard == null || aboard == ____playership;
        }
    }
}
