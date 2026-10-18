using System;
using System.Collections.Generic;
using CG.Client;
using CG.Client.Player.Interactions;
using CG.Client.Ship.Interactions;
using CG.Game.Player;
using CG.Game.SpaceObjects.Controllers;
using CG.Objects;
using CG.Ship.Hull;
using CG.Ship.Modules;
using CG.Ship.Object;
using CG.Space;
using Gameplay.Enhancements;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Boarding rules in PvP. A player may go aboard the enemy ship only through an airlock its crew left open, and once
    /// aboard may use nothing of it except its System Enhancement panels: no doors or airlock controls, seats, helm,
    /// turrets, terminals, switches, sockets, items, repairs or clone tubes. Passing through an open door needs no
    /// interaction, so it still works.
    /// - Every client hides the enemy ship's interactables from its own player (the game's one interaction raycast), and
    ///   refuses to start a seat, terminal or minigame of the enemy ship (the game's one "activate abilities" event).
    ///   This also covers a host on the enemy team, whose actions never go through the network.
    /// - The host also refuses door and airlock requests from enemy players.
    /// </summary>
    internal static class PvpBoarding
    {
        public const string Feature = "PvP boarding rules";

        private const float WarningSeconds = 20f;

        private static readonly Dictionary<int, bool> EnhancementParts = new Dictionary<int, bool>();
        private static float _lastWarning = -100f;

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

        private static bool Blocked(Component interactable)
        {
            if (interactable == null || !PvpCombat.Active) return false;
            return IsEnemyShip(ShipOf(interactable)) && !IsEnhancementPart(interactable);
        }

        private static void Warn()
        {
            if (Time.unscaledTime - _lastWarning < WarningSeconds || BreachCharge.LocalHeld != null) return;
            _lastWarning = Time.unscaledTime;
            try
            {
                if (ViewEventBus.Instance != null)
                    ViewEventBus.Instance.OnShowQuickWarning.Publish("ENEMY SHIP: you can only use its System Enhancement panels.");
            }
            catch
            {
                // Only a hint.
            }
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
                if (!Blocked(interactable)) return;
                __result = false;
                interactable = null;
                if (distance < 2.3f) Warn();
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
                Warn();
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking an action on the enemy ship", e);
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
                if (!IsEnemyOf(info.Sender.ActorNumber, FleetShips.OwnerOf(__instance))) return true;
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

        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.AirlockController", "NetworkAirlockOpenRequest", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.AirlockController", "NetworkSafetyRequest", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Game.SpaceObjects.Controllers.AirlockController", "NetworkStartPressurization", PatchKind.Prefix)]
        private static bool AirlockRequestPrefix(AirlockController __instance, PhotonMessageInfo info)
        {
            if (!PvpCombat.Active || info.Sender == null) return true;
            try
            {
                if (!IsEnemyOf(info.Sender.ActorNumber, FleetShips.OwnerOf(__instance))) return true;
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
