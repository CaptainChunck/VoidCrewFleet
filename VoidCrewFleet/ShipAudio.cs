using System;
using System.Collections.Generic;
using System.Reflection;
using CG.Client.Audio;
using CG.Client.Quests.SectorTwists;
using CG.Game.Player;
using CG.Objects;
using CG.Ship.Status;
using CG.Space;
using Gameplay.Carryables;
using Gameplay.Ship.VoidJump;
using Gameplay.SpacePlatforms;
using HarmonyLib;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// The ship AI's voice lines and the ship sounds and effects stay with their own ship. The game has one voice-line
    /// controller, sets up one copy of its 102 ship status events for every ship that spawns, and reads "the player ship"
    /// while doing so; many of those events, and many sound and effect listeners, react to events that don't say which
    /// ship they come from. With several ships:
    /// - each copy of the status events belongs to the ship it was set up for, and speaks only when that ship is the one
    ///   the local player belongs to (co-op: aboard, else crew; PvP: their team's), and only for that ship's own events
    ///   (the other ships' copies return at once); a lost ship's copies go with it;
    /// - a picked-up or dropped item speaks on the ship its carrier is aboard (else the item's ship, else the carrier's
    ///   crew's);
    /// - a ship's alarms, engine parts, diodes, lights and storm sounds react only to their own ship;
    /// - music follows the local player's ship; camera shakes and hit effects only reach players aboard the ship;
    /// - silent mode and the boost key act on their own ship only (the game applied them to every ship);
    /// - stepping aboard the other player ship is not "boarding a wreck".
    /// </summary>
    internal static class ShipAudio
    {
        public const string Feature = "Ship voice and sounds per ship";

        /// <summary>
        /// The ship each copy of a status event belongs to, by the copy's instance ID (the copies are the game's own
        /// ScriptableObjects; an ID stays readable once one is destroyed). An entry goes when its lost ship's copies are
        /// removed, or, once its ship is gone, when the next ship's copies are set up (the next mission's at the latest).
        /// </summary>
        private static readonly Dictionary<int, PlayerControlledShip> EventShip = new Dictionary<int, PlayerControlledShip>();

        private static readonly List<PlayerControlledShip> StartingShips = new List<PlayerControlledShip>();
        private static readonly List<int> SetupCounts = new List<int>();

        /// <summary>Each ship's copies of the status events and the controller that holds them.</summary>
        private sealed class EventCopies
        {
            public ShipStatusNotificationController Controller;
            public readonly List<AbstractShipStatusEvent> Events = new List<AbstractShipStatusEvent>();
        }

        /// <summary>By the ship's instance ID.</summary>
        private static readonly Dictionary<int, EventCopies> CopiesByShip = new Dictionary<int, EventCopies>();

        private static readonly MethodInfo DestroyEvent = GameMember.Method(typeof(ShipStatusNotificationController), "Destroy");

        private static readonly FieldInfo ActiveTwistField = GameMember.Field(typeof(CalamityAmbienceController), "_activeSectorTwist");
        private static readonly FieldInfo TimeChangedField = GameMember.Field(typeof(CalamityAmbienceController), "_timeChanged");
        private static readonly AccessTools.FieldRef<SectorTwist> ActiveTwist = StaticRef<SectorTwist>(ActiveTwistField);
        private static readonly AccessTools.FieldRef<float> TimeChanged = StaticRef<float>(TimeChangedField);

        private static int _viewFrame = -1;
        private static PlayerControlledShip _viewShip;

        /// <summary>The thing ShipOf was last asked about this frame, and its answer (see ShipOfThisFrame).</summary>
        private static int _thingFrame = -1;
        private static object _thing;
        private static PlayerControlledShip _thingShip;

        /// <summary>The item pick-up or drop CarrierShip was last asked about this frame, and its answer.</summary>
        private static int _carryFrame = -1;
        private static ICarrier _carrier;
        private static CarryableObject _carried;
        private static PlayerControlledShip _carryShip;

        /// <summary>What a ship's storm ambience last set its sound groups to (CalamityPrefix), by the controller's instance ID.</summary>
        private sealed class StormSounds
        {
            public PlayerControlledShip Own;
            public bool Set;
            public SectorTwist Twist;
            public bool Mine;
        }

        private static readonly Dictionary<int, StormSounds> Storms = new Dictionary<int, StormSounds>();

        private static PlayerControlledShip StartingShip
        {
            get { return StartingShips.Count > 0 ? StartingShips[StartingShips.Count - 1] : null; }
        }

        /// <summary>FleetShips.ViewShip, worked out once a frame (the storm sounds ask every frame for every ship).</summary>
        private static PlayerControlledShip ViewShipThisFrame
        {
            get
            {
                int frame = Time.frameCount;
                if (frame != _viewFrame)
                {
                    _viewFrame = frame;
                    _viewShip = FleetShips.ViewShip;
                }
                return _viewShip;
            }
        }

        /// <summary>A reader for a game's static field (no reflection call per read); null when the field is missing.</summary>
        private static AccessTools.FieldRef<T> StaticRef<T>(FieldInfo field)
        {
            if (field == null) return null;
            try
            {
                return AccessTools.StaticFieldRefAccess<T>(field);
            }
            catch (Exception e)
            {
                Log.Warn("Game field " + field.DeclaringType.Name + "." + field.Name + " can't be read directly (" + e.Message + "); the game may have changed.");
                return null;
            }
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

        /// <summary>
        /// ShipOf, remembered for the thing last asked about this frame: every ship's copy of a status event asks about the
        /// same thing in turn (a death runs every ship's lifeform events).
        /// </summary>
        private static PlayerControlledShip ShipOfThisFrame(object thing)
        {
            int frame = Time.frameCount;
            if (frame == _thingFrame && ReferenceEquals(thing, _thing)) return _thingShip;
            _thingShip = ShipOf(thing);
            _thing = thing;
            _thingFrame = frame;
            return _thingShip;
        }

        // ---------------------------------------------------------------- one set of voice-line events per ship
        // Each prefix in this class that pushes (a starting ship, a set-up, a "player ship", a ship's event) tells its
        // finalizer through __state that it did, so a finalizer never pops what another call pushed.

        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "Start", PatchKind.Prefix)]
        private static void ShipStartPrefix(PlayerControlledShip __instance, out bool __state)
        {
            StartingShips.Add(__instance);
            __state = true;
        }

        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "Start", PatchKind.Finalizer)]
        private static void ShipStartFinalizer(bool __state)
        {
            if (__state && StartingShips.Count > 0) StartingShips.RemoveAt(StartingShips.Count - 1);
        }

        /// <summary>The status events set up while a ship starts read that ship (they read "the player ship").</summary>
        [GamePatch(Feature, "CG.Ship.Status.ShipStatusNotificationController", "InitializeShipStatusEvents", PatchKind.Prefix)]
        private static void InitEventsPrefix(List<AbstractShipStatusEvent> ___activeEvents, out bool __state)
        {
            SetupCounts.Add(___activeEvents != null ? ___activeEvents.Count : 0);
            ShipRedirect.BeginOverride(StartingShip);
            __state = true;
        }

        /// <summary>
        /// The copies just set up belong to the starting ship. Without its prefix this does nothing: it wouldn't know which
        /// copies are new, and the copies left untied speak as the game has them.
        /// </summary>
        [GamePatch(Feature, "CG.Ship.Status.ShipStatusNotificationController", "InitializeShipStatusEvents", PatchKind.Finalizer)]
        private static void InitEventsFinalizer(ShipStatusNotificationController __instance, List<AbstractShipStatusEvent> ___activeEvents, bool __state)
        {
            if (!__state) return;
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
                ForgetGoneCopies();
                EventCopies copies;
                if (!CopiesByShip.TryGetValue(ship.GetInstanceID(), out copies))
                {
                    copies = new EventCopies();
                    CopiesByShip[ship.GetInstanceID()] = copies;
                }
                copies.Controller = __instance;
                for (int i = start; i < ___activeEvents.Count; i++)
                {
                    AbstractShipStatusEvent statusEvent = ___activeEvents[i];
                    if (statusEvent == null) continue;
                    EventShip[statusEvent.GetInstanceID()] = ship;
                    copies.Events.Add(statusEvent);
                }
            }
            catch (Exception e)
            {
                Log.Failure("Tying the ship's voice lines to their ship", e);
            }
        }

        /// <summary>
        /// The copies of a mission that is over (the controller went with it, and the events too), and the copies tied to
        /// ships that are gone.
        /// </summary>
        private static void ForgetGoneCopies()
        {
            List<int> gone = null;
            foreach (KeyValuePair<int, EventCopies> entry in CopiesByShip)
            {
                if (entry.Value.Controller != null) continue;
                if (gone == null) gone = new List<int>();
                gone.Add(entry.Key);
            }
            if (gone != null)
            {
                foreach (int ship in gone)
                {
                    ForgetEvents(CopiesByShip[ship]);
                    CopiesByShip.Remove(ship);
                }
                gone.Clear();
            }
            foreach (KeyValuePair<int, PlayerControlledShip> entry in EventShip)
            {
                if (entry.Value != null) continue;
                if (gone == null) gone = new List<int>();
                gone.Add(entry.Key);
            }
            if (gone == null) return;
            foreach (int statusEvent in gone) EventShip.Remove(statusEvent);
        }

        private static void ForgetEvents(EventCopies copies)
        {
            foreach (AbstractShipStatusEvent statusEvent in copies.Events)
            {
                if ((object)statusEvent != null) EventShip.Remove(statusEvent.GetInstanceID());
            }
        }

        /// <summary>
        /// A lost ship goes while other ships fly on: its copies of the status events go with it, through the game's own
        /// way of removing one (they stop listening to the game's events, and are destroyed); they used to stay subscribed
        /// for the rest of the mission. This waits for the ship itself to go, not its death to start: some copies listen
        /// to the ship's own parts, which still report while it dies. At the end of a mission the game removes them all, so
        /// nothing is done while the mission closes (its ships go one by one then).
        /// </summary>
        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "OnDestroy", PatchKind.Prefix, Optional = true)]
        private static void ShipDestroyPrefix(PlayerControlledShip __instance)
        {
            EventCopies copies;
            if (__instance == null || !CopiesByShip.TryGetValue(__instance.GetInstanceID(), out copies)) return;
            CopiesByShip.Remove(__instance.GetInstanceID());
            // Otherwise the copies stay tied to the lost ship, which keeps them silent until the game removes them.
            if (copies.Controller == null || DestroyEvent == null || !FleetShips.AnotherLivingShip(__instance) || FleetShips.TearingDown) return;
            int removed = 0;
            try
            {
                foreach (AbstractShipStatusEvent statusEvent in copies.Events)
                {
                    // A one-time event that already spoke is gone already.
                    if (statusEvent == null) continue;
                    DestroyEvent.Invoke(copies.Controller, new object[] { statusEvent });
                    removed++;
                }
            }
            catch (Exception e)
            {
                Log.Failure("Removing a lost ship's voice-line events", e);
            }
            ForgetEvents(copies);
            Log.Verbose(removed + " voice-line events of " + FleetShips.Describe(__instance) + " went with it.");
        }

        /// <summary>
        /// Whether a status event may speak: its own ship's events only, and only to that ship's players. The copies of
        /// another ship than the event's own return at once.
        /// </summary>
        private static bool StatusEventAllowed(AbstractShipStatusEvent statusEvent, PlayerControlledShip source)
        {
            PlayerControlledShip own;
            if ((object)statusEvent == null || !EventShip.TryGetValue(statusEvent.GetInstanceID(), out own)) return !ShipEvents.ForeignToLocal(source);
            if (source != null && !ReferenceEquals(source, own)) return false;
            // A lost ship's copies stay silent.
            if (own == null) return false;
            return !ShipEvents.ForeignToLocal(own);
        }

        /// <summary>The live ship a copy of a status event belongs to, or null.</summary>
        private static PlayerControlledShip EventShipOf(AbstractShipStatusEvent statusEvent)
        {
            PlayerControlledShip own;
            return (object)statusEvent != null && EventShip.TryGetValue(statusEvent.GetInstanceID(), out own) && own != null ? own : null;
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
        private static void JumpStatePrefix(VoidJumpSystem __instance, out bool __state)
        {
            ShipEvents.Begin(FleetShips.Active ? __instance.PlayerShip : null);
            __state = true;
        }

        [GamePatch(Feature, "CG.Ship.Modules.GravityScoopModule", "AttractorSocketsFull", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Modules.GravityScoopModule", "AttractorItemCaptured", PatchKind.Prefix)]
        [GamePatch(Feature, "Gameplay.AstralMap.AstralMapPUN", "DestinationSet", PatchKind.Prefix)]
        [GamePatch(Feature, "SpaceObjectDangerDetector", "SetDangerFactor", PatchKind.Prefix)]
        [GamePatch(Feature, "VoidDriveEnginePart", "Update", PatchKind.Prefix)]
        [GamePatch(Feature, "Gameplay.Power.ProtectedPowerSystem", "NotifyForcedShutDown", PatchKind.Prefix)]
        [GamePatch(Feature, "SilentMode", "LocalOnlyStateSwitch", PatchKind.Prefix)]
        private static void PartEventPrefix(Component __instance, out bool __state)
        {
            ShipEvents.BeginFor(__instance);
            __state = true;
        }

        [GamePatch(Feature, "ControllingHelm", "ActiveThrusters", PatchKind.Prefix)]
        private static void BoostKeyPrefix(CG.Ship.Modules.Helm ____helm, out bool __state)
        {
            ShipEvents.BeginFor(____helm);
            __state = true;
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
        private static void PartEventFinalizer(bool __state)
        {
            if (__state) ShipEvents.End();
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
            try
            {
                if (!FleetShips.Active) return true;
                // Every ship's copy of the event asks in turn: worked out once.
                int frame = Time.frameCount;
                if (frame != _carryFrame || !ReferenceEquals(carrier, _carrier) || !ReferenceEquals(carryable, _carried))
                {
                    _carryShip = CarrierShip(carrier, carryable);
                    _carrier = carrier;
                    _carried = carryable;
                    _carryFrame = frame;
                }
                return StatusEventAllowed(__instance, _carryShip);
            }
            catch (Exception e)
            {
                Log.Failure("Choosing which ship a voice line belongs to", e);
                return true;
            }
        }

        /// <summary>
        /// The ship an item is picked up or dropped on: the one its carrier is aboard (a visitor's pick-up speaks on the ship
        /// they visit), else the item's own ship, else, for a player in open space, their crew's ship.
        /// </summary>
        private static PlayerControlledShip CarrierShip(ICarrier carrier, CarryableObject carryable)
        {
            PlayerControlledShip ship = null;
            if (carrier != null)
            {
                SpacePlatform platform = carrier.ZonedPlatform != null ? carrier.ZonedPlatform : carrier.Platform;
                ship = platform != null ? platform.GetComponent<PlayerControlledShip>() : null;
            }
            if (ship == null && carryable != null) ship = FleetShips.OwnerOf(carryable);
            if (ship == null)
            {
                var player = carrier as Player;
                if (player != null) ship = ShipOf(player);
            }
            return ship;
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
                return StatusEventAllowed(statusEvent, ShipOfThisFrame(thing));
            }
            catch (Exception e)
            {
                Log.Failure("Choosing which ship a voice line belongs to", e);
                return true;
            }
        }

        // ---------------------------------------------------------------- status events that read "the player ship" when they fire

        /// <summary>
        /// A ship's hull warnings react to that ship's hit points only. Every hit point change of anything reaches every
        /// ship's copies of these events: for anything but a player ship they all return at once (the game's handler does
        /// nothing for it either, with any number of ships; this cheapest test comes first), and so do the other ships'
        /// copies; the ship's own copy compares against its own ship.
        /// </summary>
        [GamePatch(Feature, "CG.Ship.Status.ShipHullEvent", "OnHitpointsChanged", PatchKind.Prefix)]
        private static bool HullPrefix(AbstractShipStatusEvent __instance, IDamageable affectedObject, out bool __state)
        {
            __state = false;
            var ship = affectedObject as PlayerControlledShip;
            if (ship == null) return false;
            try
            {
                if (!FleetShips.Active) return true;
                PlayerControlledShip own;
                if ((object)__instance == null || !EventShip.TryGetValue(__instance.GetInstanceID(), out own))
                {
                    // A copy tied to no ship: the game's own comparison, with "the player ship" this screen's.
                    ShipRedirect.BeginOverride(FleetShips.ViewShip);
                    __state = true;
                    return true;
                }
                if (!ReferenceEquals(own, ship) || own == null) return false;
                ShipRedirect.BeginOverride(own);
                __state = true;
                return true;
            }
            catch (Exception e)
            {
                Log.Failure("Checking whose hull a voice line is about", e);
                return true;
            }
        }

        /// <summary>A power breaker event reads its own ship's power (a copy tied to no live ship: this screen's ship's).</summary>
        [GamePatch(Feature, "CG.Ship.Status.PowerBreakerEvent", "BreakerFlipDown", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Status.PowerBreakerEvent", "OnPowerDrainChanged", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Status.PowerBreakerEvent", "SystemForcedShutdown", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Status.PowerBreakerEvent", "OnDisable", PatchKind.Prefix)]
        private static void OwnShipPrefix(AbstractShipStatusEvent __instance, out bool __state)
        {
            __state = false;
            if (!FleetShips.Active) return;
            PlayerControlledShip ship = null;
            try
            {
                ship = EventShipOf(__instance) ?? FleetShips.ViewShip;
            }
            catch (Exception e)
            {
                Log.Failure("Checking whose power a voice line is about", e);
            }
            ShipRedirect.BeginOverride(ship);
            __state = true;
        }

        /// <summary>Ends the "player ship" a prefix above set, only when it set one.</summary>
        [GamePatch(Feature, "CG.Ship.Status.ShipHullEvent", "OnHitpointsChanged", PatchKind.Finalizer)]
        [GamePatch(Feature, "CG.Ship.Status.PowerBreakerEvent", "BreakerFlipDown", PatchKind.Finalizer)]
        [GamePatch(Feature, "CG.Ship.Status.PowerBreakerEvent", "OnPowerDrainChanged", PatchKind.Finalizer)]
        [GamePatch(Feature, "CG.Ship.Status.PowerBreakerEvent", "SystemForcedShutdown", PatchKind.Finalizer)]
        [GamePatch(Feature, "CG.Ship.Status.PowerBreakerEvent", "OnDisable", PatchKind.Finalizer)]
        private static void OwnShipFinalizer(bool __state)
        {
            if (__state) ShipRedirect.EndOverride();
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

        /// <summary>The void tunnel's state effects of a ship follow that ship's own jump (co-op: every ship jumps).</summary>
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
        /// them off when the storm ends (the game keeps one setting for all ships, so the other ships' stayed on).
        /// </summary>
        [GamePatch(Feature, "CG.Client.Audio.CalamityAmbienceController", "CheckConfig", PatchKind.Prefix)]
        private static bool CalamityPrefix(CalamityAmbienceController __instance, CalamityAudioConfig[] ___ConfigsPerSectorTwist)
        {
            try
            {
                // Every frame for every ship: the game's fields are read through direct readers, the local player's ship
                // once a frame, and the sound groups are only set again when the storm or the player's ship changed.
                if (!FleetShips.Active || ___ConfigsPerSectorTwist == null || ActiveTwist == null || TimeChanged == null) return true;
                StormSounds sounds;
                int id = __instance.GetInstanceID();
                if (!Storms.TryGetValue(id, out sounds))
                {
                    PlayerControlledShip owner = FleetShips.OwnerOf(__instance);
                    if (owner == null) return true;
                    // Those of missions that are over.
                    if (Storms.Count >= 32) Storms.Clear();
                    sounds = new StormSounds { Own = owner };
                    Storms[id] = sounds;
                }
                PlayerControlledShip own = sounds.Own;
                SectorTwist twist = ActiveTwist();
                PlayerControlledShip view = ViewShipThisFrame;
                bool mine = twist != null && (view == null || ReferenceEquals(view, own));
                if (sounds.Set && ReferenceEquals(twist, sounds.Twist) && mine == sounds.Mine) return false;
                if (twist == null && Time.time - TimeChanged() < 2f) return false;
                foreach (CalamityAudioConfig config in ___ConfigsPerSectorTwist)
                {
                    if (config == null) continue;
                    bool on = mine && config.SectorTwist != null && config.SectorTwist.AssetGuid == twist.ContainerGuid;
                    SetActive(config.TransmitterGroups, on);
                    SetActive(config.ReceiverGroups, on);
                }
                sounds.Set = true;
                sounds.Twist = twist;
                sounds.Mine = mine;
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
