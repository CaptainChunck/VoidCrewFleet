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
using CG.Utils;
using Gameplay.Carryables;
using Gameplay.Enhancements;
using Gameplay.Power;
using Gameplay.SpacePlatforms;
using Photon.Pun;
using RSG;
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
    /// - Whoever owns a ship part (the host, or a pilot) refuses the network requests of players outside the ship's team
    ///   to switch its power (the whole ship's, a system's or a power line's), to change a panel's state, or any other of
    ///   its synced switches. The exception is what a boarder may use (a system's power switch, a panel's own switch),
    ///   asked by a boarder who is alive, aboard that ship and next to it.
    /// - Whoever owns a seat (the helm, a turret, any other) or a socket of the ship refuses enemy players who ask for it or
    ///   its item, and every client ignores an enemy taking over the ship's parts or the items in its sockets.
    /// - The host refuses to build, upgrade or take apart a module of the ship for a player outside its team, and every
    ///   client ignores a module removal that the host didn't send.
    /// </summary>
    internal static class PvpBoarding
    {
        public const string Feature = "PvP boarding rules";

        private const float WarningSeconds = 20f;
        private const float WarningGapSeconds = 3f;

        /// <summary>How close to a power switch a boarder must be, as the part's owner sees them, for their request to count.</summary>
        private const float SwitchReach = 8f;

        /// <summary>A switch found under no module yet (one still being put together) is looked at again after this long.</summary>
        private const float NoModuleSeconds = 2f;

        // The game's requests to a carrier's owner (Gameplay.Carryables.CarrierCarryableHandler): put an item in, let it out,
        // throw it out.
        private const byte InsertRequestEvent = 3;
        private const byte ReleaseRequestEvent = 4;
        private const byte EjectRequestEvent = 5;

        // Photon's own events (PUN's PunEvent): a player asks to own a network view, or one is given to a player.
        private const byte OwnershipRequestEvent = 209;
        private const byte OwnershipTransferEvent = 210;

        /// <summary>The carrier request whose event Photon is handing out right now, or null (see CarrierRequestPrefix).</summary>
        private static ExitGames.Client.Photon.EventData _carrierRequest;

        // What each thing is, worked out once per thing and forgotten when a match starts or the hub loads.
        private static readonly Dictionary<int, bool> EnhancementParts = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> PowerSwitches = new Dictionary<int, bool>();
        private static readonly Dictionary<int, float> NoModuleUntil = new Dictionary<int, float>();
        private static readonly HashSet<int> MainPowerSwitches = new HashSet<int>();
        private static readonly Dictionary<int, bool> Stations = new Dictionary<int, bool>();

        // Parts the game's own panels, doors and airlocks register as they start; those of ships that are gone are dropped
        // when a match starts or the hub loads.
        private static readonly Dictionary<int, Component> PanelParts = new Dictionary<int, Component>();
        private static readonly Dictionary<int, AbstractDoor> DoorControls = new Dictionary<int, AbstractDoor>();
        private static readonly Dictionary<int, Component> PressurizeLevers = new Dictionary<int, Component>();
        private static readonly Dictionary<int, Component> SafetySwitches = new Dictionary<int, Component>();
        private static readonly List<int> GoneIds = new List<int>();

        private static readonly Dictionary<string, float> WarningShown = new Dictionary<string, float>();
        private static float _lastWarning = -100f;
        private static GrapplingHookProjectile _hookToRestore;

        /// <summary>The local player's team (0 BLUE, 1 RED), or -1 when not on a team.</summary>
        private static int LocalTeam
        {
            get { return FleetShips.TeamOfActor(FleetShips.LocalActor); }
        }

        /// <summary>True for a ship of the other team, seen from the local player (a team's own ships are friendly).</summary>
        public static bool IsEnemyShip(PlayerControlledShip ship)
        {
            if (ship == null || !PvpCombat.Active) return false;
            int mine = LocalTeam;
            if (mine < 0) return false;
            int slot = FleetShips.Slot(ship);
            return slot >= 0 && FleetShips.TeamOfSlot(slot) != mine;
        }

        /// <summary>True when a player of this actor is on the other team from this ship.</summary>
        public static bool IsEnemyOf(int actor, PlayerControlledShip ship)
        {
            if (ship == null || !PvpCombat.Active) return false;
            int team = FleetShips.TeamOfActor(actor);
            int slot = FleetShips.Slot(ship);
            return team >= 0 && slot >= 0 && team != FleetShips.TeamOfSlot(slot);
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
            if (PanelParts.ContainsKey(id)) return true;
            bool part;
            if (EnhancementParts.TryGetValue(id, out part)) return part;
            part = component.GetComponentInParent<Enhancement>() != null;
            EnhancementParts[id] = part;
            return part;
        }

        /// <summary>
        /// A module's own power switch (any system's on/off switch). Not the central computer's: that one switches the
        /// whole ship's power, so a single click by a boarder would shut everything down. A switch under no module is
        /// looked at again a little later (a module can be seen while it is still being put together).
        /// </summary>
        public static bool IsModulePowerSwitch(Component component)
        {
            var clicker = component as SwitchClicker;
            if (clicker == null) return false;
            int id = clicker.GetInstanceID();
            bool power;
            if (PowerSwitches.TryGetValue(id, out power)) return power;
            float until;
            if (NoModuleUntil.TryGetValue(id, out until) && Time.unscaledTime < until) return false;
            IShipModuleMediator[] mediators = clicker.GetComponentsInParent<IShipModuleMediator>(true);
            if (mediators.Length == 0)
            {
                if (NoModuleUntil.Count > 4096) NoModuleUntil.Clear();
                NoModuleUntil[id] = Time.unscaledTime + NoModuleSeconds;
                return false;
            }
            power = false;
            foreach (IShipModuleMediator mediator in mediators)
            {
                if (!ReferenceEquals(PowerSwitchOf(mediator), clicker)) continue;
                if (mediator is CentralShipComputerMediator || mediator.Module is CentralShipComputerModule) MainPowerSwitches.Add(id);
                else power = true;
                break;
            }
            NoModuleUntil.Remove(id);
            PowerSwitches[id] = power;
            return power;
        }

        /// <summary>A module mediator's power switch (each module type's mediator has one, possibly unset), or null.</summary>
        private static Component PowerSwitchOf(IShipModuleMediator mediator)
        {
            var property = mediator != null ? mediator.GetType().GetProperty("PowerSwitch") : null;
            return property != null ? property.GetValue(mediator, null) as Component : null;
        }

        /// <summary>A door's own clicker, an airlock panel's door button, or an airlock's pressurise lever (not into a cockpit).</summary>
        private static bool IsDoorControl(Component component)
        {
            int id = component.GetInstanceID();
            AbstractDoor door;
            if (DoorControls.TryGetValue(id, out door)) return door != null && !PvpHack.IsCockpitDoor(door);
            return PressurizeLevers.ContainsKey(id);
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
            if (___pressurizationSwitch != null) PressurizeLevers[___pressurizationSwitch.GetInstanceID()] = ___pressurizationSwitch;
            if (___safetySwitch != null) SafetySwitches[___safetySwitch.GetInstanceID()] = ___safetySwitch;
        }

        /// <summary>
        /// A match starts or the hub loads (every client): what each thing was is worked out again, and the parts registered
        /// by ships that no longer exist are dropped. (This session's ships may already have registered theirs.)
        /// </summary>
        [GamePatch(Feature, "CG.GameLoopStateMachine.GameStates.GSIngame", "OnEnter", PatchKind.Postfix, Optional = true)]
        private static void SessionStartPostfix()
        {
            EnhancementParts.Clear();
            PowerSwitches.Clear();
            NoModuleUntil.Clear();
            MainPowerSwitches.Clear();
            Stations.Clear();
            WarningShown.Clear();
            DropGone(PanelParts);
            DropGone(DoorControls);
            DropGone(PressurizeLevers);
            DropGone(SafetySwitches);
        }

        private static void DropGone<T>(Dictionary<int, T> parts) where T : UnityEngine.Object
        {
            GoneIds.Clear();
            foreach (KeyValuePair<int, T> entry in parts)
            {
                if ((UnityEngine.Object)entry.Value == null) GoneIds.Add(entry.Key);
            }
            foreach (int id in GoneIds) parts.Remove(id);
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
            if (SafetySwitches.ContainsKey(id)) return "ENEMY SHIP: the airlock safety override is for its own crew only.";
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
            if (___cryptic != null) PanelParts[___cryptic.GetInstanceID()] = ___cryptic;
            if (___crypticClicker != null) PanelParts[___crypticClicker.GetInstanceID()] = ___crypticClicker;
        }

        [GamePatch(Feature, "Gameplay.Enhancements.EnhancementSwitchActivator", "Awake", PatchKind.Postfix)]
        private static void SwitchActivatorPostfix(SwitchClicker ___switchClicker)
        {
            if (___switchClicker != null) PanelParts[___switchClicker.GetInstanceID()] = ___switchClicker;
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
                if (door != null && !PvpHack.IsCockpitDoor(door) && PvpHack.HasAccess(FleetShips.TeamOfActor(info.Sender.ActorNumber), ship)) return true;
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
                if (accessAllows && PvpHack.HasAccess(FleetShips.TeamOfActor(info.Sender.ActorNumber), ship)) return true;
                Log.Info("PvP: refused an airlock control of " + FleetShips.Describe(FleetShips.OwnerOf(__instance)) + " to enemy player " + info.Sender.ActorNumber + ".");
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking an airlock request", e);
                return true;
            }
        }

        // ---------------------------------------------------------------- the owner refuses outsiders' power and panel requests

        // These RPCs go to whoever owns the part (the host, or a pilot) and carry no sender: RpcSender says who sent them.
        // Every player's game also asks the owner for changes it works out itself (a system switching off when the power
        // goes): refusing those from outsiders costs nothing, as the owner works out the same.

        /// <summary>The central computer's power switch, the whole ship's power: never for anyone outside the ship's team.</summary>
        [GamePatch(Feature, "Gameplay.Power.PowerSystem", "RequestPowerChange", PatchKind.Prefix, Optional = true)]
        private static bool ShipPowerRequestPrefix(PowerSystem __instance)
        {
            return AllowRequest(__instance, __instance.photonView, "the ship's power", null);
        }

        /// <summary>A system's power: from outside the ship's team, only a boarder at that system's power switch.</summary>
        [GamePatch(Feature, "Gameplay.Power.PowerDrain", "RequestSetPowerState", PatchKind.Prefix, Optional = true)]
        private static bool SystemPowerRequestPrefix(PowerDrain __instance)
        {
            return AllowRequest(__instance, __instance.photonView, "a system's power", __instance);
        }

        /// <summary>A power line's switch: never for anyone outside the ship's team.</summary>
        [GamePatch(Feature, "Gameplay.Power.PowerPropagator", "RequestSetPowerState", PatchKind.Prefix, Optional = true)]
        private static bool PowerLineRequestPrefix(PowerPropagator __instance)
        {
            return AllowRequest(__instance, __instance.photonView, "a power line", null);
        }

        /// <summary>A System Enhancement panel's state: never from outside the ship's team (an enemy's completed panel is a sabotage request, PvpSabotage).</summary>
        [GamePatch(Feature, "Gameplay.Enhancements.Enhancement", "StateChangePun", PatchKind.Prefix, Optional = true)]
        private static bool PanelStateRequestPrefix(Enhancement __instance)
        {
            return AllowRequest(__instance, __instance.photonView, "a panel", null);
        }

        /// <summary>
        /// Any other synced switch or setting of the ship (breakers, helm and weapon settings, and so on): from outside the
        /// ship's team, only a boarder at a system's power switch or at a panel. An item's settings go with whoever has it.
        /// </summary>
        [GamePatch(Feature, "ChangeResponsivePun", "RequestBool", PatchKind.Prefix, Optional = true)]
        [GamePatch(Feature, "ChangeResponsivePun", "RequestByte", PatchKind.Prefix, Optional = true)]
        [GamePatch(Feature, "ChangeResponsivePun", "RequestInt", PatchKind.Prefix, Optional = true)]
        private static bool SyncedValueRequestPrefix(ChangeResponsivePun __instance, int index, ChangeResponsivePun.ChangeResponsiveWrapper[] ___ChangeResponsives)
        {
            ChangeResponsivePun.ChangeResponsiveWrapper wrapper = ___ChangeResponsives != null && index >= 0 && index < ___ChangeResponsives.Length ? ___ChangeResponsives[index] : null;
            MonoBehaviour holder = wrapper != null && wrapper.changeResponsive != null ? wrapper.changeResponsive.Component : null;
            return AllowRequest(__instance, __instance.photonView, "a switch", holder);
        }

        /// <summary>
        /// Whether to carry out an RPC that asks for a change to a ship part. Players outside the ship's team (an enemy, or a
        /// player without a team) are refused, unless the change is to something a boarder may use (holder: the power drain
        /// or the synced switch's component, else null), and they are alive, aboard that ship and next to it, as their own
        /// game requires before it asks. The room's host is never refused: its own game logic asks a pilot who owns the
        /// part for changes. An item or a character goes with whoever has it, wherever it is.
        /// </summary>
        private static bool AllowRequest(Component part, PhotonView view, string what, Component holder)
        {
            int actor;
            if (!PvpCombat.Active || part == null || !RpcSender.Of(view, out actor)) return true;
            try
            {
                if (actor == FleetShips.LocalActor || Net.FromHost(actor)) return true;
                PlayerControlledShip ship = FleetShips.OwnerOf(part);
                int slot = FleetShips.Slot(ship);
                if (slot < 0 || FleetShips.TeamOfActor(actor) == FleetShips.TeamOfSlot(slot)) return true;
                if (Carried(part) || (holder != null && Carried(holder))) return true;
                Component usable = UsableBy(holder);
                if (usable != null && AtPart(actor, ship, usable)) return true;
                Log.InfoOnce("pvp refused " + what + " " + actor, "PvP: refused a change to " + what + " of " + FleetShips.Describe(ship) + " asked by player " + actor +
                                                                 ", who is not on its team (further ones are only logged in verbose mode).");
                Log.Verbose("PvP: refused a change to " + what + " of " + FleetShips.Describe(ship) + " asked by player " + actor + ".");
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking a request from another team", e);
                return true;
            }
        }

        private static bool Carried(Component component)
        {
            return component.GetComponentInParent<CarryableObject>() != null || component.GetComponentInParent<Player>() != null;
        }

        /// <summary>Where a boarder uses this (a system's power switch, a panel), or null when a boarder may not use it.</summary>
        private static Component UsableBy(Component holder)
        {
            if (holder == null) return null;
            var drain = holder as PowerDrain;
            if (drain != null) return ModulePowerSwitchOf(drain);
            if (!IsEnhancementPart(holder)) return null;
            Enhancement panel = holder.GetComponentInParent<Enhancement>();
            return panel != null ? panel.InteractableTransform : holder;
        }

        /// <summary>A player's character is alive, aboard this ship and next to this part, as the local game sees it.</summary>
        private static bool AtPart(int actor, PlayerControlledShip ship, Component part)
        {
            Player character = FleetShips.CharacterOf(actor);
            if (character == null || PlayerLife.IsDead(character) || FleetShips.AboardShip(character) != ship) return false;
            return (character.transform.position - part.transform.position).sqrMagnitude <= SwitchReach * SwitchReach;
        }

        /// <summary>The power switch of the system this power drain powers (not the central computer's); null for any other drain.</summary>
        private static Component ModulePowerSwitchOf(PowerDrain drain)
        {
            foreach (IShipModuleMediator mediator in drain.GetComponents<IShipModuleMediator>())
            {
                if (mediator is CentralShipComputerMediator || mediator.Module is CentralShipComputerModule) continue;
                Component powerSwitch = PowerSwitchOf(mediator);
                if (powerSwitch != null) return powerSwitch;
            }
            return null;
        }

        // ---------------------------------------------------------------- the ship's seats, sockets and items stay with its team

        /// <summary>
        /// The helm, a turret or any other seat of the ship: whoever owns the seat (the host while it is free) gives it to any
        /// player who asks, and the helm's or turret's own network views go with it. Not to a player of the other team (door
        /// access from a hack doesn't count, and the helm is kept whether or not its cockpit has doors): the request is
        /// dropped, as the game itself drops one while the seat's module is being upgraded or taken apart. (The game's own
        /// answer, TakeoverRejected, isn't marked as a network call, so it can't be sent.)
        /// </summary>
        [GamePatch(Feature, "TakeoverChair", "RequestSitInChair", PatchKind.Prefix, Optional = true)]
        private static bool SeatRequestPrefix(TakeoverChair __instance, PhotonMessageInfo info)
        {
            if (!PvpCombat.Active || info.Sender == null) return true;
            try
            {
                PlayerControlledShip ship = FleetShips.OwnerOf(__instance);
                if (ship == null) ship = FleetShips.OwnerOf(__instance.ConnectedModule);
                int actor = info.Sender.ActorNumber;
                if (!IsEnemyOf(actor, ship)) return true;
                LogRefusal("the seat " + __instance.name, ship, actor);
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking a seat request", e);
                return true;
            }
        }

        /// <summary>
        /// An item in one of the ship's sockets whose network view asks before it changes hands: the socket's owner (the
        /// game's Carrier) gives it to whoever asks, but not to a player of the other team. The game's own items change hands
        /// without asking anyone (see OwnershipEventPrefix); this covers any that ask.
        /// </summary>
        [GamePatch(Feature, "Gameplay.Carryables.Carrier", "OnOwnershipRequest", PatchKind.Prefix, Optional = true)]
        private static bool SocketItemRequestPrefix(Carrier __instance, PhotonView targetView, Photon.Realtime.Player requestingPlayer)
        {
            if (!PvpCombat.Active || requestingPlayer == null || targetView == null) return true;
            try
            {
                // Every carrier hears every request: only the one holding that item, on its owner's game, gives it away.
                CarryableObject item = __instance.Payload;
                if (!targetView.IsMine || item == null || item.ViewID != targetView.ViewID) return true;
                PlayerControlledShip ship = SocketShip(__instance.Owner as CarryablesSocket);
                int actor = requestingPlayer.ActorNumber;
                if (Net.FromHost(actor) || !IsEnemyOf(actor, ship)) return true;
                LogRefusal("the item " + item.name + " in a socket", ship, actor);
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking a request for an item in a socket", e);
                return true;
            }
        }

        /// <summary>
        /// Every client: Photon hands each event to PUN's own handler (patched here) first, then to every carrier, and the
        /// carrier a request is for (put an item in, let it out, throw it out) answers it there and then: the request is
        /// noted here for that answer (see SocketRequest). Any other event clears it. (One check per event, where a check in
        /// the carriers' own handler would run for every carrier of every ship on every event.)
        /// </summary>
        [GamePatch(Feature, "Photon.Pun.PhotonNetwork", "OnEvent", PatchKind.Prefix, Optional = true, AfterGameLoad = true)]
        private static void CarrierRequestPrefix(ExitGames.Client.Photon.EventData photonEvent)
        {
            byte code = photonEvent != null ? photonEvent.Code : (byte)0;
            _carrierRequest = code >= InsertRequestEvent && code <= EjectRequestEvent ? photonEvent : null;
        }

        [GamePatch(Feature, "Gameplay.Carryables.CarrierCarryableHandler", "TryInsertCarryable", PatchKind.Prefix, Optional = true)]
        private static bool InsertRequestPrefix(PhotonView ___photonView, ICarrierComponent ___carrier, ref IPromise __result)
        {
            return SocketRequest(InsertRequestEvent, ___photonView, ___carrier, ref __result);
        }

        [GamePatch(Feature, "Gameplay.Carryables.CarrierCarryableHandler", "TryReleaseCarryable", PatchKind.Prefix, Optional = true)]
        private static bool ReleaseRequestPrefix(PhotonView ___photonView, ICarrierComponent ___carrier, ref IPromise __result)
        {
            return SocketRequest(ReleaseRequestEvent, ___photonView, ___carrier, ref __result);
        }

        [GamePatch(Feature, "Gameplay.Carryables.CarrierCarryableHandler", "TryEjectCarryable", PatchKind.Prefix, Optional = true)]
        private static bool EjectRequestPrefix(PhotonView ___photonView, ICarrierComponent ___carrier, ref IPromise __result)
        {
            return SocketRequest(EjectRequestEvent, ___photonView, ___carrier, ref __result);
        }

        /// <summary>
        /// A player's game asks the owner of one of the ship's sockets to put an item into it, to let its item out (how a
        /// player swaps the item in a socket for the one in their hands) or to throw it out: refused to a player of the other
        /// team the way the game refuses a request itself (it fails, and the owner's game tells the asker so). The host's own
        /// game logic may still ask. A carrier's own game asking itself goes through as before.
        /// </summary>
        private static bool SocketRequest(byte code, PhotonView view, ICarrierComponent carrierComponent, ref IPromise result)
        {
            ExitGames.Client.Photon.EventData request = _carrierRequest;
            if (request == null || request.Code != code || view == null || !PvpCombat.Active) return true;
            try
            {
                var data = request.CustomData as object[];
                if (data == null || data.Length == 0 || !(data[0] is int) || (int)data[0] != view.ViewID) return true;
                // This carrier answers it now.
                _carrierRequest = null;
                var carrier = carrierComponent as Carrier;
                var socket = carrier != null ? carrier.Owner as CarryablesSocket : null;
                PlayerControlledShip ship = SocketShip(socket);
                int actor = request.Sender;
                if (Net.FromHost(actor) || !IsEnemyOf(actor, ship)) return true;
                var refused = new Promise();
                refused.Reject(new Exception("refused to a player of the other team"));
                result = refused;
                LogRefusal("the socket " + socket.name, ship, actor);
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking a socket request", e);
                return true;
            }
        }

        /// <summary>
        /// Every client: the helm's seat, the network views it hands its pilot, and every item are network views that change
        /// owner on each client by itself when a player asks for one or is handed one. No owner is asked, so the checks above
        /// never see it. Every client ignores such a change that would give a part of a ship, or an item in one of its
        /// sockets, to a player of the other team (the game of a player who tried may follow it, but no one else's does).
        /// Never refused: the host (seats go back to it, and its own game logic takes items), and items a player carries or
        /// that lie loose (the interaction rules above keep those).
        /// </summary>
        [GamePatch(Feature, "Photon.Pun.PhotonNetwork", "OnEvent", PatchKind.Prefix, Optional = true, AfterGameLoad = true)]
        private static bool OwnershipEventPrefix(ExitGames.Client.Photon.EventData photonEvent)
        {
            byte code = photonEvent != null ? photonEvent.Code : (byte)0;
            if ((code != OwnershipRequestEvent && code != OwnershipTransferEvent) || !PvpCombat.Active) return true;
            try
            {
                var data = photonEvent.CustomData as int[];
                if (data == null || data.Length < 2) return true;
                // A request makes its sender the owner; a transfer names the new owner.
                int actor = code == OwnershipRequestEvent ? photonEvent.Sender : data[1];
                if (actor <= 0 || Net.FromHost(actor)) return true;
                PhotonView view = PhotonView.Find(data[0]);
                if (view == null || view.OwnershipTransfer != OwnershipOption.Takeover) return true;
                PlayerControlledShip ship = ShipOfView(view);
                if (!IsEnemyOf(actor, ship)) return true;
                LogRefusal(view.name, ship, actor);
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking a change of owner", e);
                return true;
            }
        }

        /// <summary>
        /// The ship a network view is part of, or for an item, the ship whose socket holds it; null for an item a player
        /// carries, one lying loose or in another item's socket, and for anything that isn't part of a ship.
        /// </summary>
        private static PlayerControlledShip ShipOfView(PhotonView view)
        {
            var item = view.GetComponentInParent<CarryableObject>();
            return item != null ? SocketShip(item.Carrier as CarryablesSocket) : FleetShips.OwnerOf(view);
        }

        /// <summary>The ship a socket is part of; null for none, or for a socket of an item (a magazine's), which moves with it.</summary>
        private static PlayerControlledShip SocketShip(CarryablesSocket socket)
        {
            if (socket == null || socket.GetComponentInParent<CarryableObject>() != null) return null;
            return FleetShips.OwnerOf(socket);
        }

        // ---------------------------------------------------------------- building and taking apart the ship's modules

        /// <summary>
        /// Taking a module apart: a player's game asks the host (the build controller's network call), and the host starts it
        /// for any player, for any module, wherever they are. A player who isn't on the module's ship's team is refused (a
        /// modified game; an unmodified one can't reach the other team's modules, whose interactions are blocked above).
        /// </summary>
        [GamePatch(Feature, "CG.Client.Player.Interactions.Build.BuildProcessController", "DeconstructModuleInternal", PatchKind.Prefix, Optional = true)]
        private static bool DeconstructRequestPrefix(int moduleViewId, PhotonMessageInfo info)
        {
            return ModuleRequest(moduleViewId, info, "taking apart a module");
        }

        /// <summary>Upgrading a module: the same as taking it apart (it is taken off its socket and a better one built).</summary>
        [GamePatch(Feature, "CG.Client.Player.Interactions.Build.BuildProcessController", "UpgradeModuleInternal", PatchKind.Prefix, Optional = true)]
        private static bool UpgradeRequestPrefix(int moduleViewId, PhotonMessageInfo info)
        {
            return ModuleRequest(moduleViewId, info, "upgrading a module");
        }

        private static bool ModuleRequest(int moduleViewId, PhotonMessageInfo info, string what)
        {
            if (!PvpCombat.Active) return true;
            try
            {
                CellModule module = ObjectRegistry.FindByPhotonView<CellModule>(moduleViewId);
                return module == null || MayBuildOn(info.Sender, FleetShips.OwnerOf(module), what, module.name);
            }
            catch (Exception e)
            {
                Log.Failure("Checking a request to work on a module", e);
                return true;
            }
        }

        /// <summary>
        /// Building a module from a box in one of the ship's build sockets: the host's own game asks for it whenever a box lands
        /// in a socket (for whoever put it there; the socket rules above keep the other team from putting one in), so only a
        /// request another player sent is checked: refused to a player who isn't on the socket's ship's team.
        /// </summary>
        [GamePatch(Feature, "CG.Client.Player.Interactions.Build.BuildProcessController", "ConstructModuleInternal", PatchKind.Prefix, Optional = true)]
        private static bool ConstructRequestPrefix(int buildBoxViewID, PhotonMessageInfo info)
        {
            if (!PvpCombat.Active) return true;
            try
            {
                BuildBox box = ObjectRegistry.FindByPhotonView<BuildBox>(buildBoxViewID);
                return box == null || MayBuildOn(info.Sender, SocketShip(box.Carrier as CarryablesSocket), "building a module", box.name);
            }
            catch (Exception e)
            {
                Log.Failure("Checking a request to build a module", e);
                return true;
            }
        }

        /// <summary>
        /// Whether the host carries out a request to build, upgrade or take apart a module of this ship: always from the host
        /// itself (its own game asks for every module built from a box), and from a player of the ship's own team. Anyone else
        /// (the other team, a player without a team, or a request replayed from the room after its sender left: the game
        /// never keeps these in the room) is refused, the first time of each kind logged. A module on no player ship goes
        /// through.
        /// </summary>
        private static bool MayBuildOn(Photon.Realtime.Player sender, PlayerControlledShip ship, string what, string thing)
        {
            int actor = sender != null ? sender.ActorNumber : -1;
            if (sender != null && (actor == FleetShips.LocalActor || Net.FromHost(actor))) return true;
            int slot = FleetShips.Slot(ship);
            if (slot < 0 || (sender != null && FleetShips.TeamOfActor(actor) == FleetShips.TeamOfSlot(slot))) return true;
            string who = sender != null ? "player " + actor + ", who is not on its team" : "a player who has left the room";
            Log.InfoOnce("pvp refused " + what + " " + actor, "PvP: refused " + what + " (" + thing + ") of " + FleetShips.Describe(ship) + " asked by " + who +
                                                             " (further ones are only logged in verbose mode).");
            Log.Verbose("PvP: refused " + what + " (" + thing + ") of " + FleetShips.Describe(ship) + " asked by " + who + ".");
            return false;
        }

        /// <summary>
        /// A module taken off its ship (PlayerControlledShip.NotifyRemoveModule): only the host's game sends it, to everyone,
        /// when its work on a socket takes a module apart or upgrades it (that work runs on the host alone), and every game then
        /// takes the module off the ship, the host's destroying it. In PvP one any other player sent (a modified game), or one
        /// replayed from the room after its sender left, is ignored on every player's game, whatever their team: it would
        /// remove any module from anywhere, even one the ship started with, which can't be taken apart. When RpcSender isn't
        /// in, it counts as the game would count it.
        /// </summary>
        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "NotifyRemoveModule", PatchKind.Prefix, Optional = true)]
        private static bool RemoveModulePrefix(PlayerControlledShip __instance)
        {
            if (!PvpCombat.Active || __instance == null) return true;
            PhotonView view = __instance.photonView;
            int actor;
            bool known = RpcSender.Of(view, out actor);
            if (known ? actor == FleetShips.LocalActor || Net.FromHost(actor) : !RpcSender.FromAbsentPlayer(view)) return true;
            string who = known ? "player " + actor : "a player who has left the room";
            Log.InfoOnce("pvp refused module removal " + actor, "PvP: ignored a module removal on " + FleetShips.Describe(__instance) + " sent by " + who +
                                                               ": only the host's game sends one (further ones are only logged in verbose mode).");
            Log.Verbose("PvP: ignored a module removal on " + FleetShips.Describe(__instance) + " sent by " + who + ".");
            return false;
        }

        /// <summary>The first refusal of each kind to each player is logged; the rest only in verbose mode.</summary>
        private static void LogRefusal(string what, PlayerControlledShip ship, int actor)
        {
            Log.InfoOnce("pvp refused " + what + " " + actor, "PvP: refused " + what + " of " + FleetShips.Describe(ship) + " to player " + actor +
                                                             " of the other team (further ones are only logged in verbose mode).");
            Log.Verbose("PvP: refused " + what + " of " + FleetShips.Describe(ship) + " to player " + actor + ".");
        }
    }
}
