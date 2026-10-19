using System;
using System.Collections.Generic;
using System.Reflection;
using CG.Game.Player;
using CG.Objects;
using CG.Ship.Repair;
using CG.Space;
using Gameplay.Carryables;
using Gameplay.Defects;
using HarmonyLib;
using Photon.Pun;
using UI.MainHUD;
using UI.ObjectMarker;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Pings and the HUD's ship markers with two ships. In co-op everyone sees every ping and both ships' damage markers
    /// (defects and hull breaches) and power-recovery hints. In PvP each team sees only its own team's pings and only its
    /// own ship's damage markers and hints. In both modes:
    /// - the HUD's hull bar shows its own ship's hull, not whichever ship was hit last;
    /// - "homunculus removed / connected" warnings show only to the players of that ship;
    /// - the blinking power-recovery hint follows one ship's power, breakers and homunculus, and points at that ship's
    ///   parts (the game keeps one hint and lets the ship that registered last take it over). In co-op each ship gets
    ///   its own hint.
    /// </summary>
    internal static class ShipMarkers
    {
        public const string Feature = "Pings and ship markers per ship";

        private static readonly MethodInfo RemoveDefectMarker = AccessTools.Method(typeof(DefectMarkerProvider), "RemoveMarker");
        private static readonly MethodInfo RemoveBreachMarker = AccessTools.Method(typeof(HullMarkerProvider), "RemoveMarker");
        private static readonly MethodInfo UrgencyInit = GameMember.Method(typeof(UrgencyMarkerProvider), "Init");

        private static readonly Dictionary<PlayerControlledShip, UrgencyMarkerProvider> UrgencyByShip = new Dictionary<PlayerControlledShip, UrgencyMarkerProvider>();
        private static readonly Dictionary<int, PlayerControlledShip> TargetHome = new Dictionary<int, PlayerControlledShip>();
        private static UrgencyMarkerProvider _urgencyTemplate;

        /// <summary>True when an actor is on the other PvP team (a player not on a team yet is nobody's enemy).</summary>
        private static bool IsOtherTeam(int actor)
        {
            if (!FleetSession.IsPvP) return false;
            int mine = FleetShips.CrewSlot(FleetShips.LocalActor);
            int theirs = FleetShips.CrewSlot(actor);
            return mine >= 0 && theirs >= 0 && mine != theirs;
        }

        /// <summary>A part of the other team's ship (PvP only).</summary>
        private static bool IsEnemyPart(Component part)
        {
            return part != null && PvpBoarding.IsEnemyShip(FleetShips.OwnerOf(part));
        }

        // ---------------------------------------------------------------- pings

        /// <summary>Another team's ping: no marker, no sound, no chat line (co-op: everyone sees every ping).</summary>
        [GamePatch(Feature, "UI.Ping.PingComponent", "SharedPing", PatchKind.Prefix)]
        private static bool SharedPingPrefix(PhotonMessageInfo info)
        {
            try
            {
                return info.Sender == null || !IsOtherTeam(info.Sender.ActorNumber);
            }
            catch (Exception e)
            {
                Log.Failure("Checking whose ping this is", e);
                return true;
            }
        }

        // ---------------------------------------------------------------- damage markers

        [GamePatch(Feature, "UI.ObjectMarker.DefectMarkerProvider", "DefectChanged", PatchKind.Prefix)]
        private static bool DefectChangedPrefix(DefectMarkerProvider __instance, Defect defect)
        {
            try
            {
                if (defect == null || !IsEnemyPart(defect)) return true;
                if (__instance.Markers.ContainsKey(defect) && RemoveDefectMarker != null) RemoveDefectMarker.Invoke(__instance, new object[] { defect });
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking a defect marker", e);
                return true;
            }
        }

        [GamePatch(Feature, "UI.ObjectMarker.HullMarkerProvider", "BreachUpdated", PatchKind.Prefix)]
        private static bool BreachUpdatedPrefix(HullMarkerProvider __instance, HullBreach obj)
        {
            try
            {
                if (obj == null || !PvpBoarding.IsEnemyShip(ShipOfBreach(obj))) return true;
                if (__instance.Markers.ContainsKey(obj) && RemoveBreachMarker != null) RemoveBreachMarker.Invoke(__instance, new object[] { obj });
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking a hull breach marker", e);
                return true;
            }
        }

        private static PlayerControlledShip ShipOfBreach(HullBreach breach)
        {
            PlayerControlledShip ship = FleetShips.OwnerOf(breach);
            if (ship != null) return ship;
            foreach (PlayerControlledShip candidate in FleetShips.All)
            {
                HullDamageController controller = candidate != null ? candidate.GetComponentInChildren<HullDamageController>() : null;
                if (controller != null && controller.Breaches != null && controller.Breaches.Contains(breach)) return candidate;
            }
            return null;
        }

        /// <summary>A safety net: no damage marker or power hint of the enemy ship is drawn, whoever made it.</summary>
        [GamePatch(Feature, "UI.ObjectMarker.ObjectMarkerViewer", "IsVisible", PatchKind.Postfix)]
        private static void MarkerVisiblePostfix(ObjectMarkerData marker, ref bool __result)
        {
            const ObjectMarkerData.EMarkerType shipState = ObjectMarkerData.EMarkerType.Defect | ObjectMarkerData.EMarkerType.HullBreach | ObjectMarkerData.EMarkerType.Urgency;
            if (!__result || marker == null || (marker.MarkerType & shipState) == 0 || marker.FollowTarget == null || !FleetSession.IsPvP) return;
            if (IsEnemyPart(marker.FollowTarget)) __result = false;
        }

        // ---------------------------------------------------------------- the HUD's ship info follows your ship

        private static readonly FieldInfo HudShipField = GameMember.Field(typeof(UI.MainHUD.ShipInfoHUD), "playerShip");
        private static readonly FieldInfo HudHullField = GameMember.Field(typeof(UI.MainHUD.ShipInfoHUD), "hullDamageController");
        private static readonly FieldInfo HudBreakersField = GameMember.Field(typeof(UI.MainHUD.ShipInfoHUD), "breakerInstances");
        private static readonly MethodInfo HudEnable = GameMember.Method(typeof(UI.MainHUD.ShipInfoHUD), "OnEnable");
        private static readonly MethodInfo HudDisable = GameMember.Method(typeof(UI.MainHUD.ShipInfoHUD), "OnDisable");
        private static readonly MethodInfo HudAddBreaker = GameMember.Method(typeof(UI.MainHUD.ShipInfoHUD), "CreateBreakerEntry");
        private static readonly MethodInfo HudPowerChanged = GameMember.Method(typeof(UI.MainHUD.ShipInfoHUD), "PowerChanged");
        private static UI.MainHUD.ShipInfoHUD _hud;

        /// <summary>
        /// The HUD's hull, power and breakers bind once, at spawn, to the ship of that moment. When the local player moves
        /// to the other ship, or is re-cloned there after losing their own, they move along with the player.
        /// </summary>
        private static void KeepHudOnViewShip()
        {
            if (FleetSession.ShipCount < 2 || GameSessionManager.InHub || HudShipField == null || HudEnable == null || HudDisable == null) return;
            CG.Game.ClientGame game = CG.Game.ClientGame.Current;
            PlayerControlledShip view = game != null ? game.PlayerShip : null;
            if (view == null || view.StartedDeath) return;
            if (_hud == null) _hud = UnityEngine.Object.FindObjectOfType<UI.MainHUD.ShipInfoHUD>();
            if (_hud == null || !_hud.isActiveAndEnabled) return;
            var bound = HudShipField.GetValue(null) as PlayerControlledShip;
            if (bound == view) return;
            try
            {
                HudDisable.Invoke(_hud, null);
                if (HudHullField != null) HudHullField.SetValue(_hud, view.Transform.GetComponentInChildren<HullDamageController>());
                HudEnable.Invoke(_hud, null);
                var breakers = HudBreakersField != null ? HudBreakersField.GetValue(_hud) as System.Collections.IList : null;
                if (breakers != null && HudAddBreaker != null)
                {
                    foreach (object entry in breakers)
                    {
                        var disposable = entry as IDisposable;
                        if (disposable != null) disposable.Dispose();
                    }
                    breakers.Clear();
                    var power = view.ShipsPowerSystem as Gameplay.Power.ProtectedPowerSystem;
                    if (power != null)
                    {
                        foreach (Gameplay.Power.PowerBreaker breaker in power.Breakers) HudAddBreaker.Invoke(_hud, new object[] { breaker });
                    }
                }
                if (HudPowerChanged != null) HudPowerChanged.Invoke(_hud, null);
                Log.Verbose("The HUD's ship info now shows " + FleetShips.Describe(view) + ".");
            }
            catch (Exception e)
            {
                Log.Failure("Moving the HUD's ship info to the ship you're on", e);
            }
        }

        // ---------------------------------------------------------------- the HUD's hull bar

        [GamePatch(Feature, "UI.MainHUD.ShipInfoHUD", "OnHullStateChanged", PatchKind.Prefix)]
        private static bool HullStatePrefix(HullState hullState, HullDamageController ___hullDamageController)
        {
            return !FleetShips.Active || hullState == null || ___hullDamageController == null || ReferenceEquals(hullState, ___hullDamageController.State);
        }

        // ---------------------------------------------------------------- homunculus warnings

        [GamePatch(Feature, "CG.Ship.Modules.CentralShipComputerModule", "OnRemoveCarryable", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Ship.Modules.CentralShipComputerModule", "OnAcquireCarryable", PatchKind.Prefix)]
        private static void CorePrefix(Component __instance)
        {
            ShipEvents.BeginFor(__instance);
        }

        [GamePatch(Feature, "CG.Ship.Modules.CentralShipComputerModule", "OnRemoveCarryable", PatchKind.Finalizer)]
        [GamePatch(Feature, "CG.Ship.Modules.CentralShipComputerModule", "OnAcquireCarryable", PatchKind.Finalizer)]
        private static void CoreFinalizer()
        {
            ShipEvents.End();
        }

        /// <summary>A quick warning raised by another ship than the local player's (e.g. its homunculus) is not shown.</summary>
        [GamePatch(Feature, "HUD_QuickWarning", "OnShowQuickWarning", PatchKind.Prefix)]
        private static bool QuickWarningPrefix()
        {
            return !ShipEvents.ForeignToLocal();
        }

        // ---------------------------------------------------------------- the power-recovery hint

        /// <summary>
        /// The hint is tied to one ship: the local player's crew ship (or the first one assigned), with only that ship's
        /// parts as its targets.
        /// </summary>
        [GamePatch(Feature, "UI.ObjectMarker.UrgencyMarkerProvider", "Init", PatchKind.Prefix)]
        private static void UrgencyInitPrefix(UrgencyMarkerProvider __instance, Dictionary<EUrgencyMarkerTargetSource, Transform> ____markerTargets)
        {
            try
            {
                if (!FleetShips.Active) return;
                PruneUrgency();
                PlayerControlledShip ship = FleetShips.AssignedOwner(__instance);
                if (ship == null)
                {
                    ship = FleetShips.LocalShip;
                    if (ship == null) ship = FleetShips.ViewShip;
                    if (ship == null) return;
                    FleetShips.AssignOwner(__instance, ship);
                    _urgencyTemplate = __instance;
                }
                UrgencyByShip[ship] = __instance;
                if (____markerTargets == null) return;
                ____markerTargets.Clear();
                foreach (UrgencyMarkerTarget target in UnityEngine.Object.FindObjectsOfType<UrgencyMarkerTarget>())
                {
                    if (target != null && HomeShip(target.transform) == ship) ____markerTargets[target.TargetSourceType] = target.transform;
                }
                Log.Verbose("The power-recovery hint follows " + FleetShips.Describe(ship) + " (" + ____markerTargets.Count + " targets).");
            }
            catch (Exception e)
            {
                Log.Failure("Tying the power-recovery hint to its ship", e);
            }
        }

        /// <summary>Co-op: once the hint exists, each other ship gets a hint of its own.</summary>
        [GamePatch(Feature, "UI.ObjectMarker.UrgencyMarkerProvider", "Init", PatchKind.Postfix)]
        private static void UrgencyInitPostfix()
        {
            EnsureUrgencyPerShip();
        }

        [GamePatch(Feature, "UI.ObjectMarker.UrgencyMarkerProvider", "OnHintMarkerTargetRegistered", PatchKind.Prefix)]
        private static bool UrgencyTargetPrefix(UrgencyMarkerProvider __instance, Transform target)
        {
            if (!FleetShips.Active || target == null) return true;
            PlayerControlledShip home = HomeShip(target);
            PlayerControlledShip mine = FleetShips.AssignedOwner(__instance);
            return home == null || mine == null || home == mine;
        }

        [GamePatch(Feature, "UI.ObjectMarker.UrgencyMarkerProvider", "OnCarryableInserted", PatchKind.Prefix)]
        [GamePatch(Feature, "UI.ObjectMarker.UrgencyMarkerProvider", "OnCarryableEjected", PatchKind.Prefix)]
        private static bool UrgencySocketPrefix(UrgencyMarkerProvider __instance, ICarryablesSocket socket)
        {
            if (!FleetShips.Active) return true;
            PlayerControlledShip ship = FleetShips.OwnerOf(socket as Component);
            PlayerControlledShip mine = FleetShips.AssignedOwner(__instance);
            return ship == null || mine == null || ship == mine;
        }

        /// <summary>Carrying another ship's homunculus doesn't light this ship's "bring it to the core" hint.</summary>
        [GamePatch(Feature, "UI.ObjectMarker.UrgencyMarkerProvider", "OnLocalPayloadChanged", PatchKind.Prefix)]
        private static void UrgencyPayloadPrefix(UrgencyMarkerProvider __instance, ref CarryableObject carryable)
        {
            try
            {
                if (carryable == null || !FleetShips.Active || !__instance.HomunculusRef.IsInstanceOf(carryable)) return;
                UrgencyMarkerTarget target = carryable.GetComponentInChildren<UrgencyMarkerTarget>(true);
                PlayerControlledShip home = target != null ? HomeShip(target.transform) : FleetShips.OwnerOf(carryable);
                PlayerControlledShip mine = FleetShips.AssignedOwner(__instance);
                if (home != null && mine != null && home != mine) carryable = null;
            }
            catch (Exception e)
            {
                Log.Failure("Checking a carried homunculus", e);
            }
        }

        /// <summary>The ship a hint target belongs to, remembered the first time it is seen (a homunculus can be carried away).</summary>
        private static PlayerControlledShip HomeShip(Transform target)
        {
            if (target == null) return null;
            PlayerControlledShip ship;
            int id = target.GetInstanceID();
            if (TargetHome.TryGetValue(id, out ship) && ship != null) return ship;
            ship = FleetShips.OwnerOf(target);
            if (ship == null)
            {
                CarryableObject carryable = target.GetComponentInParent<CarryableObject>();
                if (carryable != null) ship = FleetShips.OwnerOf(carryable);
            }
            if (ship != null) TargetHome[id] = ship;
            return ship;
        }

        private static void PruneUrgency()
        {
            foreach (PlayerControlledShip ship in new List<PlayerControlledShip>(UrgencyByShip.Keys))
            {
                UrgencyMarkerProvider provider = UrgencyByShip[ship];
                if (ship != null && provider != null) continue;
                UrgencyByShip.Remove(ship);
                if (provider != null && provider != _urgencyTemplate) UnityEngine.Object.Destroy(provider);
            }
        }

        /// <summary>Co-op: every ship without a hint gets one (a copy of the HUD's own), tied to that ship.</summary>
        private static void EnsureUrgencyPerShip()
        {
            UrgencyMarkerProvider template = _urgencyTemplate;
            if (template == null || !FleetShips.Active || FleetSession.IsPvP || UrgencyInit == null) return;
            try
            {
                PruneUrgency();
                foreach (PlayerControlledShip ship in FleetShips.All)
                {
                    UrgencyMarkerProvider existing;
                    if (ship == null || (UrgencyByShip.TryGetValue(ship, out existing) && existing != null)) continue;
                    UrgencyMarkerProvider copy = template.gameObject.AddComponent<UrgencyMarkerProvider>();
                    copy.MarkerOnShipCoreInfo = template.MarkerOnShipCoreInfo;
                    copy.MarkerOnHomunculusInfo = template.MarkerOnHomunculusInfo;
                    copy.MarkerOnPowerButtonInfo = template.MarkerOnPowerButtonInfo;
                    copy.MarkerOnBreakersInfo = template.MarkerOnBreakersInfo;
                    copy.HomunculusRef = template.HomunculusRef;
                    copy.ShipCoreModuleRef = template.ShipCoreModuleRef;
                    copy.DisableBreakerMarkerDelayPreShutDown = template.DisableBreakerMarkerDelayPreShutDown;
                    copy.DisableBreakerMarkerDelayPostShutdown = template.DisableBreakerMarkerDelayPostShutdown;
                    copy.ForceActivateSystemTime = template.ForceActivateSystemTime;
                    FleetShips.AssignOwner(copy, ship);
                    UrgencyByShip[ship] = copy;
                    UrgencyInit.Invoke(copy, null);
                    Log.Info("Co-op: " + FleetShips.Describe(ship) + " has its own power-recovery hint.");
                }
            }
            catch (Exception e)
            {
                Log.Failure("Giving each ship its power-recovery hint", e);
            }
        }

        /// <summary>A few times a second: a ship that arrived late gets its hint; a lost ship's hint goes.</summary>
        public static void Tick()
        {
            KeepHudOnViewShip();
            if (_urgencyTemplate == null) return;
            if (!FleetShips.Active)
            {
                PruneUrgency();
                return;
            }
            if (!FleetSession.IsPvP && UrgencyByShip.Count < FleetShips.All.Count) EnsureUrgencyPerShip();
        }
    }
}
