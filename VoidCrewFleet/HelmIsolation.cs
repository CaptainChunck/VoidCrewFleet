using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CG.Client.Player.Interactions;
using CG.Game.Player;
using CG.Ship.Modules;
using CG.Ship.Scanning;
using CG.Ship.TargetLocking;
using CG.Space;
using Gameplay.Helm;
using Gameplay.Ship;
using Gameplay.TacticalTargeting;
using UI.ObjectMarker;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Flying with two ships. Each ship's helm, outside camera and pilot HUD work on their own, but the game tells them
    /// "the pilot switched to the outside view", "the player is now flying" and "boost!" with events that don't say which
    /// ship, so both ships' cameras and HUDs answered:
    /// - only the outside camera of the helm or periscope the local player is using turns on (both turned on, and the
    ///   one switched on last was shown, often the other ship's, which doesn't move with your controls);
    /// - only the flown helm's pilot HUD shows (the other ship's HUD showed on top, with its own "HELM UNPOWERED");
    /// - a boost boosts its own ship's engine, and shakes only the players aboard it;
    /// - seat bonuses go to the seat's ship, and crew status shows who flies which ship;
    /// - target locks and scan markers are those of the ship you're on (they stayed on the first ship you saw);
    /// - another ship's pilot doesn't hide your scan reticle.
    /// </summary>
    internal static class HelmIsolation
    {
        public const string Feature = "Helm and outside camera per ship";

        private static readonly FieldInfo ViewerScanField = GameMember.Field(typeof(ObjectMarkerViewer), "shipScanComponent");
        private static readonly FieldInfo ViewerHelmField = GameMember.Field(typeof(ObjectMarkerViewer), "helmModule");
        private static readonly FieldInfo ViewerLocksField = GameMember.Field(typeof(ObjectMarkerViewer), "targetLockComponent");
        private static readonly FieldInfo ViewerLockedField = GameMember.Field(typeof(ObjectMarkerViewer), "_lockedOnOrbitMarkers");
        private static readonly MethodInfo ViewerUpdateLock = GameMember.Method(typeof(ObjectMarkerViewer), "UpdateLock");
        private static readonly MethodInfo ViewerLockDamageChanged = GameMember.Method(typeof(ObjectMarkerViewer), "OnTargetLockDamageChanged");

        private static bool _remoteScanUpdate;

        // ---------------------------------------------------------------- what the local player is using

        /// <summary>The helm the local player flies right now, or null.</summary>
        public static Helm FlownHelm()
        {
            LocalPlayer player = LocalPlayer.Instance;
            if (player == null || player.Locomotion == null) return null;
            var ability = player.Locomotion.GetAbility<ControllingHelm>();
            if (ability == null || !ability.IsActive) return null;
            return ability.AbilityActivator as Helm;
        }

        /// <summary>The outside camera the local player looks through (the flown helm's, or a periscope's), or null.</summary>
        public static ShipExternalCamera UsedCamera()
        {
            Helm helm = FlownHelm();
            if (helm != null) return helm.ExternalCamera;
            LocalPlayer player = LocalPlayer.Instance;
            if (player == null || player.Locomotion == null) return null;
            var look = player.Locomotion.GetAbility<LookOutsideShip>();
            if (look == null || !look.IsActive) return null;
            var periscope = look.AbilityActivator as Periscope;
            return periscope != null ? periscope.ExternalCamera : null;
        }

        /// <summary>The ship of the helm or periscope in use, else the ship the local player is aboard.</summary>
        public static PlayerControlledShip SeatShip()
        {
            ShipExternalCamera camera = UsedCamera();
            if (camera != null) return FleetShips.OwnerOf(camera);
            return FleetShips.AboardShip(LocalPlayer.Instance);
        }

        // ---------------------------------------------------------------- outside camera

        [GamePatch(Feature, "Gameplay.Helm.ShipExternalCamera", "OnShipExternalViewToggle", PatchKind.Prefix)]
        private static void ExternalViewPrefix(ShipExternalCamera __instance, ref ShipExternalCamera.CameraType cameraType)
        {
            if (cameraType != ShipExternalCamera.CameraType.ThirdPersonCamera || !FleetShips.Active) return;
            try
            {
                ShipExternalCamera used = UsedCamera();
                bool mine = used != null ? used == __instance : FleetShips.OwnerOf(__instance) == SeatShip();
                if (!mine) cameraType = ShipExternalCamera.CameraType.FirstPersonCamera;
            }
            catch (Exception e)
            {
                Log.Failure("Choosing which ship's outside camera to show", e);
            }
        }

        // ---------------------------------------------------------------- pilot HUD

        [GamePatch(Feature, "UI.PilotHUD.PilotHUD", "InteractionModeChanged", PatchKind.Prefix)]
        private static bool PilotHudPrefix(UI.PilotHUD.PilotHUD __instance, InteractionMode newMode, Helm ___helm)
        {
            if (!FleetShips.Active || (newMode & InteractionMode.ControllingShip) == 0) return true;
            try
            {
                Helm flown = FlownHelm();
                if (flown == null || flown == ___helm) return true;
                __instance.SetDisplay(false);
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Choosing which pilot HUD to show", e);
                return true;
            }
        }

        // ---------------------------------------------------------------- boosters

        /// <summary>The boost boosts this controller's own ship's engine (the game boosts "the player ship").</summary>
        [GamePatch(Feature, "CG.Ship.Modules.ThrusterBoosterController", "Booster", PatchKind.Prefix)]
        private static bool BoosterPrefix(ThrusterBoosterController __instance, float duration, ref IEnumerator __result)
        {
            if (!FleetShips.Active) return true;
            PlayerControlledShip ship = FleetShips.OwnerOf(__instance);
            ShipEngine engine = ship != null ? ship.GetModule<ShipEngine>() : null;
            if (engine == null) return true;
            __result = BoostEngine(engine, duration);
            return false;
        }

        private static IEnumerator BoostEngine(ShipEngine engine, float duration)
        {
            if (engine == null) yield break;
            engine.SetBooster();
            yield return new WaitForSeconds(duration);
            if (engine != null) engine.ClearBooster();
        }

        /// <summary>A boost shakes only the players aboard the boosting ship.</summary>
        [GamePatch(Feature, "CG.Ship.Modules.ThrusterBoosterController", "PlayThrusterEffects", PatchKind.Prefix)]
        private static bool BoostEffectsPrefix(ThrusterBoosterController __instance, float duration)
        {
            if (!FleetShips.Active) return true;
            try
            {
                PlayerControlledShip own = FleetShips.OwnerOf(__instance);
                if (own == null || FleetShips.AboardShip(LocalPlayer.Instance) == own) return true;
                Action fired = __instance.OnBoosterFired;
                if (fired != null) fired();
                ShipEngine engine = own.GetModule<ShipEngine>();
                if (engine != null) __instance.StartCoroutine(BoostEngine(engine, duration));
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Playing a boost", e);
                return true;
            }
        }

        // ---------------------------------------------------------------- seats

        /// <summary>Sitting down, getting up: the seat's bonuses go to the seat's own ship.</summary>
        [GamePatch(Feature, "SittingInChair", "AbilityStarted", PatchKind.Prefix)]
        [GamePatch(Feature, "SittingInChair", "AbilityStopped", PatchKind.Prefix)]
        [GamePatch(Feature, "SittingInChair", "OnDestroy", PatchKind.Prefix)]
        private static void SeatPrefix(Component ___chair)
        {
            ShipRedirect.BeginOverride(FleetShips.Active && ___chair != null ? FleetShips.OwnerOf(___chair) : null);
        }

        [GamePatch(Feature, "CoopBuddyStatsSync", "ChairTaken", PatchKind.Prefix)]
        [GamePatch(Feature, "CoopBuddyStatsSync", "ChairFreed", PatchKind.Prefix)]
        private static void BuddyChairPrefix(Component chair)
        {
            ShipRedirect.BeginOverride(FleetShips.Active && chair != null ? FleetShips.OwnerOf(chair) : null);
        }

        [GamePatch(Feature, "SittingInChair", "AbilityStarted", PatchKind.Finalizer)]
        [GamePatch(Feature, "SittingInChair", "AbilityStopped", PatchKind.Finalizer)]
        [GamePatch(Feature, "SittingInChair", "OnDestroy", PatchKind.Finalizer)]
        [GamePatch(Feature, "CoopBuddyStatsSync", "ChairTaken", PatchKind.Finalizer)]
        [GamePatch(Feature, "CoopBuddyStatsSync", "ChairFreed", PatchKind.Finalizer)]
        private static void SeatFinalizer()
        {
            ShipRedirect.EndOverride();
        }

        // ---------------------------------------------------------------- target locks and scan markers

        /// <summary>Sitting at a helm or turret: the markers show that ship's target locks and scanner.</summary>
        [GamePatch(Feature, "UI.ObjectMarker.ObjectMarkerViewer", "ModeChanged", PatchKind.Prefix)]
        private static void MarkerModePrefix(ObjectMarkerViewer __instance)
        {
            KeepMarkersOnViewShip(__instance);
        }

        /// <summary>Every second or so: the markers follow the ship the local player is on (co-op lets you change ships).</summary>
        public static void Tick()
        {
            if (FleetSession.ShipCount > 1 && Patcher.IsReady(Feature)) KeepMarkersOnViewShip(ObjectMarkerViewer.Instance);
        }

        private static void KeepMarkersOnViewShip(ObjectMarkerViewer viewer)
        {
            if (viewer == null || ViewerLocksField == null) return;
            try
            {
                // Not set up yet (no component at all): the game binds it to the view ship itself once a ship is there. A
                // destroyed component (the lost ship's) does need moving to the surviving ship.
                var bound = ViewerLocksField.GetValue(viewer) as TargetLockComponent;
                if (ReferenceEquals(bound, null)) return;
                CG.Game.ClientGame game = CG.Game.ClientGame.Current;
                PlayerControlledShip ship = FleetShips.Active ? FleetShips.ViewShip : (game != null ? game.PlayerShip : null);
                if (ship == null || ship.StartedDeath || (bound != null && FleetShips.OwnerOf(bound) == ship)) return;
                Rebind(viewer, bound, ship);
            }
            catch (Exception e)
            {
                Log.Failure("Moving the target-lock markers to the ship you're on", e);
            }
        }

        private static void Rebind(ObjectMarkerViewer viewer, TargetLockComponent oldLocks, PlayerControlledShip ship)
        {
            var updateLock = (Action<TargetLock>)Delegate.CreateDelegate(typeof(Action<TargetLock>), viewer, ViewerUpdateLock);
            var lockDamageChanged = (Action)Delegate.CreateDelegate(typeof(Action), viewer, ViewerLockDamageChanged);

            oldLocks.TargetLockChanged -= updateLock;
            var oldHelm = ViewerHelmField.GetValue(viewer) as Helm;
            if (oldHelm != null) oldHelm.TargetLockDamage.OnValueChange = (Action)Delegate.Remove(oldHelm.TargetLockDamage.OnValueChange, lockDamageChanged);

            // Take the old ship's locks off the markers, as the game does when a lock moves to another target.
            var locked = ViewerLockedField.GetValue(viewer) as List<OrbitObjectMarker>;
            if (locked != null)
            {
                foreach (OrbitObjectMarker marker in locked)
                {
                    marker.TargetLock = null;
                    marker.ShowLockOnStyles = false;
                    MarkerVE ve = marker.Ve;
                    if (ve == null) continue;
                    ve.SetLockOn(null);
                    ve.Data.MarkerModifierStyles.UpdateStyle(ve, ObjectMarkerData.EMarkerModifiers.None);
                }
                locked.Clear();
            }

            ViewerScanField.SetValue(viewer, ship.Transform.GetComponent<ScanComponent>());
            Helm helm = ship.GetModule<Helm>();
            ViewerHelmField.SetValue(viewer, helm);
            if (helm != null) helm.TargetLockDamage.OnValueChange = (Action)Delegate.Combine(helm.TargetLockDamage.OnValueChange, lockDamageChanged);
            var locks = ship.GetComponent<TargetLockComponent>();
            ViewerLocksField.SetValue(viewer, locks);
            if (locks != null)
            {
                locks.TargetLockChanged += updateLock;
                foreach (TargetLock targetLock in locks.TargetLocks) updateLock(targetLock);
            }
            Log.Verbose("Target locks and scan markers now show " + FleetShips.Describe(ship) + "'s.");
        }

        // ---------------------------------------------------------------- scan reticle

        /// <summary>
        /// Every pilot's scan ability updates on every client, and a pilot who isn't you turns your scan reticle off each
        /// frame (with one ship there is only one pilot). Only your own scan ability shows or hides it.
        /// </summary>
        [GamePatch(Feature, "Gameplay.TacticalTargeting.TacticalScan", "Update", PatchKind.Prefix)]
        private static void ScanUpdatePrefix(LocalPlayer ___LocalPlayer)
        {
            _remoteScanUpdate = FleetShips.Active && ___LocalPlayer != null && !___LocalPlayer.IsMine;
        }

        [GamePatch(Feature, "Gameplay.TacticalTargeting.TacticalScan", "Update", PatchKind.Finalizer)]
        private static void ScanUpdateFinalizer()
        {
            _remoteScanUpdate = false;
        }

        [GamePatch(Feature, "UI.ObjectMarker.ObjectMarkerViewer", "ToggleScanVisibility", PatchKind.Prefix)]
        private static bool ToggleScanPrefix()
        {
            return !_remoteScanUpdate;
        }
    }
}
