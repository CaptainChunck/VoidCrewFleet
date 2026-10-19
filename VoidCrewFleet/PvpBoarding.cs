using System;
using System.Collections.Generic;
using CG.Client;
using CG.Client.Ship;
using CG.Client.Ship.Modules.Mediators;
using CG.Client.Player.Interactions;
using CG.Client.Ship.Interactions;
using CG.Game;
using CG.Game.Player;
using CG.Game.SpaceObjects.Controllers;
using CG.Objects;
using CG.Ship.Hull;
using CG.Ship.Modules;
using CG.Ship.Object;
using CG.Space;
using Gameplay.Enhancements;
using Gameplay.SpacePlatforms;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Boarding rules in PvP. A player may go aboard the enemy ship through an airlock its crew left open, or after their
    /// team hacked one of its airlocks (<see cref="PvpHack"/>). Aboard the enemy ship a player may use:
    /// - its System Enhancement panels (sabotage), and the power switches of its modules (turn a turret, shield or any
    ///   other system off), always; not the central computer's power switch, which is the whole ship's power;
    /// - its doors, airlock door buttons and pressurise levers, while their team has door access from a hack, except the
    ///   doors into the cockpit (never), and never an airlock's safety override.
    /// Nothing else: no seats, helm, turrets, terminals, other switches, sockets (the homunculus stays in its cradle),
    /// items (the superior grappling hook doesn't catch them either), repairs or clone tubes. Passing through an open
    /// door needs no interaction, so it always works.
    /// - Every client hides the enemy ship's interactables from its own player (the game's one interaction raycast), and
    ///   refuses to start a seat, terminal or minigame of the enemy ship (the game's one "activate abilities" event).
    ///   This also covers a host on the enemy team, whose actions never go through the network.
    /// - The host also refuses door and airlock requests from enemy players without access.
    /// </summary>
    internal static class PvpBoarding
    {
        public const string Feature = "PvP boarding rules";

        private const float WarningSeconds = 20f;
        private const float WarningGapSeconds = 3f;

        private static readonly Dictionary<int, bool> EnhancementParts = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> PowerSwitches = new Dictionary<int, bool>();
        private static readonly HashSet<int> MainPowerSwitches = new HashSet<int>();
        private static readonly Dictionary<int, AbstractDoor> DoorControls = new Dictionary<int, AbstractDoor>();
        private static readonly HashSet<int> PressurizeLevers = new HashSet<int>();
        private static readonly HashSet<int> SafetySwitches = new HashSet<int>();
        private static readonly Dictionary<int, bool> Stations = new Dictionary<int, bool>();
        private static readonly Dictionary<string, float> WarningShown = new Dictionary<string, float>();
        private static float _lastWarning = -100f;
        private static GrapplingHookProjectile _hookToRestore;

        /// <summary>The local player's team (0 BLUE, 1 RED), or -1 when not on a team.</summary>
        private static int LocalTeam
        {
            get { return FleetShips.CrewSlot(FleetShips.LocalActor); }
        }

        /// <summary>True for the other team's ship, seen from the local player.</summary>
        public static bool IsEnemyShip(PlayerControlledShip ship)
        {
            if (ship == null || !PvpCombat.Active) return false;
            int mine = LocalTeam;
            if (mine < 0) return false;
            int slot = FleetShips.Slot(ship);
            return slot >= 0 && slot != mine;
        }

        /// <summary>True when a player of this actor is on the other team from this ship.</summary>
        public static bool IsEnemyOf(int actor, PlayerControlledShip ship)
        {
            if (ship == null || !PvpCombat.Active) return false;
            int team = FleetShips.CrewSlot(actor);
            int slot = FleetShips.Slot(ship);
            return team >= 0 && slot >= 0 && team != slot;
        }

        /// <summary>The ship a thing on board belongs to; a carried or loose item goes by the socket or ship holding it.</summary>
        public static PlayerControlledShip ShipOf(Component component)
        {
            if (component == null) return null;
            var grabbable = component as Grabbable;
            if (grabbable != null)
            {
                CarryableObject carryable = grabbable.Carryable;
                if (carryable == null) return null;
                var socket = carryable.Carrier as CarryablesSocket;
                if (socket != null) return FleetShips.OwnerOf(socket);
                if (carryable.Carrier is Player) return null;
                return FleetShips.OwnerOf(carryable);
            }
            return FleetShips.OwnerOf(component);
        }

        /// <summary>Part of a System Enhancement panel: the panel, its minigame or its switch.</summary>
        public static bool IsEnhancementPart(Component component)
        {
            if (component == null) return false;
            int id = component.GetInstanceID();
            bool part;
            if (EnhancementParts.TryGetValue(id, out part)) return part;
            part = component.GetComponentInParent<Enhancement>() != null;
            EnhancementParts[id] = part;
            return part;
        }

        /// <summary>
        /// A module's own power switch (any system's on/off switch). Not the central computer's: that one switches the
        /// whole ship's power, so a single click by a boarder would shut everything down.
        /// </summary>
        public static bool IsModulePowerSwitch(Component component)
        {
            var clicker = component as SwitchClicker;
            if (clicker == null) return false;
            int id = clicker.GetInstanceID();
            bool power;
            if (PowerSwitches.TryGetValue(id, out power)) return power;
            power = false;
            foreach (IShipModuleMediator mediator in clicker.GetComponentsInParent<IShipModuleMediator>(true))
            {
                var property = mediator.GetType().GetProperty("PowerSwitch");
                if (property == null || !ReferenceEquals(property.GetValue(mediator, null), clicker)) continue;
                if (mediator is CentralShipComputerMediator || mediator.Module is CentralShipComputerModule) MainPowerSwitches.Add(id);
                else power = true;
                break;
            }
            PowerSwitches[id] = power;
            return power;
        }

        /// <summary>A door's own clicker, an airlock panel's door button, or an airlock's pressurise lever (not into a cockpit).</summary>
        private static bool IsDoorControl(Component component)
        {
            int id = component.GetInstanceID();
            AbstractDoor door;
            if (DoorControls.TryGetValue(id, out door)) return door != null && !PvpHack.IsCockpitDoor(door);
            return PressurizeLevers.Contains(id);
        }

        private static bool Blocked(Component interactable)
        {
            if (interactable == null || !PvpCombat.Active) return false;
            PlayerControlledShip ship = ShipOf(interactable);
            if (!IsEnemyShip(ship)) return false;
            if (IsEnhancementPart(interactable) || IsModulePowerSwitch(interactable)) return false;
            return !(IsDoorControl(interactable) && PvpHack.LocalHasAccess(ship));
        }

        /// <summary>For the self-test: how many of a ship's door controls the local player may use now.</summary>
        public static string DescribeAccess(PlayerControlledShip ship)
        {
            int doors = 0;
            int usable = 0;
            foreach (KeyValuePair<int, AbstractDoor> entry in DoorControls)
            {
                if (entry.Value == null || FleetShips.OwnerOf(entry.Value) != ship) continue;
                doors++;
                if (!PvpHack.IsCockpitDoor(entry.Value) && PvpHack.LocalHasAccess(ship)) usable++;
            }
            return usable + " of " + doors + " door controls usable by the local team";
        }

        // ---------------------------------------------------------------- doors and airlock levers register their controls

        [GamePatch(Feature, "CG.Client.Ship.Interactions.DoorActor", "Start", PatchKind.Postfix)]
        private static void DoorActorPostfix(DoorActor __instance, List<ClickerInteractable> ___clickers)
        {
            if (___clickers == null || __instance.Door == null) return;
            foreach (ClickerInteractable clicker in ___clickers)
            {
                if (clicker != null) DoorControls[clicker.GetInstanceID()] = __instance.Door;
            }
        }

        [GamePatch(Feature, "CG.Client.Ship.AirlockButtonPanel", "Initialize", PatchKind.Postfix)]
        private static void AirlockPanelPostfix(ClickerInteractable ___pressurizationSwitch, SwitchClicker ___safetySwitch)
        {
            if (___pressurizationSwitch != null) PressurizeLevers.Add(___pressurizationSwitch.GetInstanceID());
            if (___safetySwitch != null) SafetySwitches.Add(___safetySwitch.GetInstanceID());
        }

        /// <summary>
        /// Tells the local player why they can't use a thing of the enemy ship: each text at most every 20 seconds, and
        /// none sooner than 3 seconds after the last one.
        /// </summary>
        private static void Warn(Component blocked, bool station)
        {
            if (blocked == null || Time.unscaledTime - _lastWarning < WarningGapSeconds || HackTerminal.LocalHeld != null) return;
            string text = WarningText(blocked, station);
            float shown;
            if (WarningShown.TryGetValue(text, out shown) && Time.unscaledTime - shown < WarningSeconds) return;
            _lastWarning = Time.unscaledTime;
            WarningShown[text] = _lastWarning;
            try
            {
                if (ViewEventBus.Instance != null) ViewEventBus.Instance.OnShowQuickWarning.Publish(text);
            }
            catch
            {
                // Only a hint.
            }
        }

        /// <summary>Why the local player can't use this thing of the enemy ship (no hacking advice while they have door access).</summary>
        private static string WarningText(Component blocked, bool station)
        {
            int id = blocked.GetInstanceID();
            AbstractDoor door;
            if (DoorControls.TryGetValue(id, out door) && door != null && PvpHack.IsCockpitDoor(door)) return "ENEMY COCKPIT: its doors never open for enemies.";
            if (SafetySwitches.Contains(id)) return "ENEMY SHIP: the airlock safety override is for its own crew only.";
            if (MainPowerSwitches.Contains(id)) return "ENEMY SHIP: the central computer's power switch is for its own crew only.";
            bool access = PvpHack.LocalHasAccess(ShipOf(blocked));
            if (!access && IsDoorControl(blocked)) return "ENEMY SHIP: hack one of its airlocks to use its doors.";
            if (blocked is Grabbable || blocked is SocketInteractable || blocked is CarryableObject) return "ENEMY SHIP: you can't take its items.";
            if (station || IsStation(blocked)) return "ENEMY SHIP: its seats, helm, turrets, terminals and clone tubes are for its own crew only.";
            return access
                ? "ENEMY SHIP: only its doors, System Enhancement panels and power switches."
                : "ENEMY SHIP: only its System Enhancement panels and power switches (hack an airlock for its doors).";
        }

        /// <summary>Part of something that seats or takes over a player: a seat, the helm, a turret, a terminal, a periscope, a clone tube.</summary>
        private static bool IsStation(Component component)
        {
            int id = component.GetInstanceID();
            bool station;
            if (Stations.TryGetValue(id, out station)) return station;
            station = (component.GetComponentInParent<IAbilityActivator>() as Component) != null;
            Stations[id] = station;
            return station;
        }

        // ---------------------------------------------------------------- the game's panels register their parts

        [GamePatch(Feature, "Gameplay.Enhancements.EnhancementCrypticActivator", "Awake", PatchKind.Postfix)]
        private static void CrypticActivatorPostfix(CrypticCore ___cryptic, ClickerInteractable ___crypticClicker)
        {
            if (___cryptic != null) EnhancementParts[___cryptic.GetInstanceID()] = true;
            if (___crypticClicker != null) EnhancementParts[___crypticClicker.GetInstanceID()] = true;
        }

        [GamePatch(Feature, "Gameplay.Enhancements.EnhancementSwitchActivator", "Awake", PatchKind.Postfix)]
        private static void SwitchActivatorPostfix(SwitchClicker ___switchClicker)
        {
            if (___switchClicker != null) EnhancementParts[___switchClicker.GetInstanceID()] = true;
        }

        // ---------------------------------------------------------------- the local player's interactions

        /// <summary>The enemy ship's things are not interactable for the local player (no highlight, prompt or use).</summary>
        [GamePatch(Feature, "CG.Client.Player.Interactions.RaycastHandler", "IsValidInteractableHit", PatchKind.Postfix)]
        private static void ValidHitPostfix(ref bool __result, ref AbstractInteractable interactable, float distance)
        {
            if (!__result || interactable == null) return;
            try
            {
                AbstractInteractable blocked = interactable;
                if (!Blocked(blocked)) return;
                __result = false;
                interactable = null;
                if (distance < 2.3f) Warn(blocked, false);
            }
            catch (Exception e)
            {
                Log.Failure("Checking an interaction on the enemy ship", e);
            }
        }

        /// <summary>The same for what the raycast settles on last (an item in a socket becomes the socket).</summary>
        [GamePatch(Feature, "CG.Client.Player.Interactions.RaycastHandler", "FindInteractable", PatchKind.Postfix)]
        private static void FindInteractablePostfix(ref AbstractInteractable interactable)
        {
            if (interactable == null) return;
            try
            {
                if (Blocked(interactable)) interactable = null;
            }
            catch (Exception e)
            {
                Log.Failure("Checking an interaction on the enemy ship", e);
            }
        }

        /// <summary>Seats, helm, turrets, terminals, periscope, clone tubes and minigames of the enemy ship don't start.</summary>
        [GamePatch(Feature, "CustomCharacterLocomotion", "OnActivateAbilities", PatchKind.Prefix)]
        private static bool ActivateAbilitiesPrefix(CustomCharacterLocomotion __instance, IAbilityActivator activator, bool activate)
        {
            if (!activate || !PvpCombat.Active) return true;
            try
            {
                if (LocalPlayer.Instance == null || __instance == null || __instance.gameObject != LocalPlayer.Instance.gameObject) return true;
                var component = activator as Component;
                if (component == null || !Blocked(component)) return true;
                Log.Verbose("PvP: blocked " + component.GetType().Name + " on the enemy ship.");
                Warn(component, true);
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking an action on the enemy ship", e);
                return true;
            }
        }

        /// <summary>
        /// The superior grappling hook catches a loose item it hits, pulls it in and picks it up without the interaction
        /// raycast: not an item of the enemy ship (even a dropped homunculus). On such a hit the hook acts like the standard
        /// one, which doesn't catch items. Every client simulates every player's hook, so this goes by the team of the
        /// player who threw it, and all of them agree.
        /// </summary>
        [GamePatch(Feature, "GrapplingHookProjectile", "TryHit", PatchKind.Prefix, Optional = true)]
        private static void HookHitPrefix(GrapplingHookProjectile __instance, Collider hitCollider)
        {
            _hookToRestore = null;
            if (__instance == null || !__instance.CanAttractCarryables || hitCollider == null || !PvpCombat.Active) return;
            try
            {
                CarryableObject carryable = HookTarget(__instance, hitCollider);
                if (carryable == null) return;
                Player thrower = HookThrower(__instance);
                int actor = thrower != null && thrower.photonView != null ? thrower.photonView.OwnerActorNr : FleetShips.LocalActor;
                Component item = carryable.GetComponent<Grabbable>();
                if (item == null) item = carryable;
                if (!IsEnemyOf(actor, ShipOf(item))) return;
                __instance.CanAttractCarryables = false;
                _hookToRestore = __instance;
                if (thrower != null && thrower == LocalPlayer.Instance) Warn(item, false);
            }
            catch (Exception e)
            {
                Log.Failure("Checking a grappling hook hit on the enemy ship", e);
            }
        }

        [GamePatch(Feature, "GrapplingHookProjectile", "TryHit", PatchKind.Finalizer, Optional = true)]
        private static void HookHitFinalizer()
        {
            GrapplingHookProjectile hook = _hookToRestore;
            _hookToRestore = null;
            if (hook != null) hook.CanAttractCarryables = true;
        }

        /// <summary>The item a grappling hook hit would catch: the game's own lookup of what was hit, when that is an item.</summary>
        private static CarryableObject HookTarget(GrapplingHookProjectile hook, Collider hit)
        {
            IDamageReceiver receiver;
            ConnectedSimulationObject simulated = hook.IsBeingSimulated ? hit.GetComponentInParent<ConnectedSimulationObject>() : null;
            if (simulated != null) receiver = simulated.ConnectedObject != null ? simulated.ConnectedObject.GetComponent<IDamageReceiver>() : null;
            else receiver = hit.GetComponentInParent<IDamageReceiver>();
            return receiver as CarryableObject;
        }

        /// <summary>The player whose grappling hook ability threw this hook, or null.</summary>
        private static Player HookThrower(GrapplingHookProjectile hook)
        {
            ClientGame game = ClientGame.Current;
            if (game == null || game.Players == null) return null;
            foreach (Player player in game.Players)
            {
                if (player == null || player.Locomotion == null) continue;
                var ability = player.Locomotion.GetAbility<ChannelGrapplingHook>();
                if (ability != null && ability.ActiveHeadProjectileInstance == hook) return player;
            }
            return null;
        }

        /// <summary>
        /// Backstop: the local player picks up nothing of the enemy ship that the interaction raycast doesn't offer (the
        /// grappling hook picks up what it pulled in through here).
        /// </summary>
        [GamePatch(Feature, "CarryableInteract", "StartInteraction", PatchKind.Prefix, Optional = true,
            Arguments = new[] { "CG.Client.Player.Interactions.AbstractInteractable", "System.Boolean" })]
        private static bool StartInteractionPrefix(AbstractInteractable interactable, LocalPlayer ___player)
        {
            if (interactable == null || !PvpCombat.Active) return true;
            try
            {
                if (___player == null || ___player != LocalPlayer.Instance || !Blocked(interactable)) return true;
                Log.Verbose("PvP: blocked picking up " + interactable.GetType().Name + " of the enemy ship.");
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking a pick-up on the enemy ship", e);
                return true;
            }
        }

        /// <summary>The module upgrade tool aims by itself; it can't upgrade the enemy's modules.</summary>
        [GamePatch(Feature, "CG.Ship.Object.InteractiveCarryableModuleUpgrader", "IsInvalidUpgradeTarget", PatchKind.Postfix)]
        private static void UpgradeTargetPostfix(CellModule module, ref bool __result)
        {
            if (__result || module == null || !PvpCombat.Active) return;
            if (IsEnemyShip(FleetShips.OwnerOf(module))) __result = true;
        }

        // ---------------------------------------------------------------- the host refuses enemy door and airlock requests

        [GamePatch(Feature, "DoorController", "RequestOpenOnMaster", PatchKind.Prefix)]
        private static bool DoorRequestPrefix(DoorController __instance, int index, PhotonMessageInfo info, List<AbstractDoor> ____shipDoors)
        {
            if (!PvpCombat.Active || info.Sender == null) return true;
            try
            {
                PlayerControlledShip ship = FleetShips.OwnerOf(__instance);
                if (!IsEnemyOf(info.Sender.ActorNumber, ship)) return true;
                AbstractDoor door = ____shipDoors != null && index >= 0 && index < ____shipDoors.Count ? ____shipDoors[index] : null;
                if (door != null && !PvpHack.IsCockpitDoor(door) && PvpHack.HasAccess(FleetShips.CrewSlot(info.Sender.ActorNumber), ship)) return true;
                // Tell the requester the door's real state, which undoes its own early open.
                if (____shipDoors != null && index >= 0 && index < ____shipDoors.Count)
                    __instance.GetComponent<PhotonView>().RPC("NotifyClientOfOutcome", info.Sender, ____shipDoors[index].IsOpen, index);
                Log.Info("PvP: refused a door of " + FleetShips.Describe(FleetShips.OwnerOf(__instance)) + " to enemy player " + info.Sender.ActorNumber + ".");
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking a door request", e);
                return true;
            }
        }

        /// <summary>Opening an airlock door and cycling the airlock: allowed with door access from a hack.</summary>
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.AirlockController", "NetworkAirlockOpenRequest", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.AirlockController", "NetworkStartPressurization", PatchKind.Prefix)]
        private static bool AirlockRequestPrefix(AirlockController __instance, PhotonMessageInfo info)
        {
            return AirlockRequest(__instance, info, true);
        }

        /// <summary>The safety override (venting the ship): never for enemies.</summary>
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.AirlockController", "NetworkSafetyRequest", PatchKind.Prefix)]
        private static bool AirlockSafetyPrefix(AirlockController __instance, PhotonMessageInfo info)
        {
            return AirlockRequest(__instance, info, false);
        }

        private static bool AirlockRequest(AirlockController __instance, PhotonMessageInfo info, bool accessAllows)
        {
            if (!PvpCombat.Active || info.Sender == null) return true;
            try
            {
                PlayerControlledShip ship = FleetShips.OwnerOf(__instance);
                if (!IsEnemyOf(info.Sender.ActorNumber, ship)) return true;
                if (accessAllows && PvpHack.HasAccess(FleetShips.CrewSlot(info.Sender.ActorNumber), ship)) return true;
                Log.Info("PvP: refused an airlock control of " + FleetShips.Describe(FleetShips.OwnerOf(__instance)) + " to enemy player " + info.Sender.ActorNumber + ".");
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking an airlock request", e);
                return true;
            }
        }
    }
}
