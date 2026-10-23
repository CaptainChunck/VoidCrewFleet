using System;
using System.Collections.Generic;
using System.Reflection;
using CG.Game.Player;
using CG.Objects;
using CG.Ship.Repair;
using CG.Space;
using Gameplay.Carryables;
using Gameplay.Defects;
using Photon.Pun;
using UI.MainHUD;
using UI.ObjectMarker;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Pings and the HUD's ship markers with two ships. In co-op everyone sees every ping and both ships' damage markers
    /// (defects and hull breaches) and power-recovery hints. In PvP each team sees only its own team's pings and only its
    /// own ship's damage markers and hints, and a player waiting for a place on no team yet sees none of either team's. In
    /// both modes:
    /// - the HUD's hull bar shows its own ship's hull, not whichever ship was hit last;
    /// - "homunculus removed / connected" warnings show only to the players of that ship;
    /// - the blinking power-recovery hint follows one ship's power, breakers and homunculus, and points at that ship's
    ///   parts (the game keeps one hint and lets the ship that registered last take it over). In co-op each ship gets
    ///   its own hint. A lost ship's hint goes with it.
    /// </summary>
    internal static class ShipMarkers
    {
        public const string Feature = "Pings and ship markers per ship";

        private static readonly MethodInfo RemoveDefectMarker = GameMember.Method(typeof(DefectMarkerProvider), "RemoveMarker");
        private static readonly MethodInfo RemoveBreachMarker = GameMember.Method(typeof(HullMarkerProvider), "RemoveMarker");
        private static readonly MethodInfo UrgencyInit = GameMember.Method(typeof(UrgencyMarkerProvider), "Init");
        private static readonly FieldInfo UrgencyMarkerField = GameMember.Field(typeof(UrgencyMarkerProvider), "_markerData");

        private static readonly Dictionary<PlayerControlledShip, UrgencyMarkerProvider> UrgencyByShip = new Dictionary<PlayerControlledShip, UrgencyMarkerProvider>();

        /// <summary>The ship each hint target belongs to (by the target's instance ID); emptied when the ships are gone.</summary>
        private static readonly Dictionary<int, PlayerControlledShip> TargetHome = new Dictionary<int, PlayerControlledShip>();

        /// <summary>
        /// The mission's hint targets and what each marks, by the target's instance ID: searched for once, when the mission's
        /// first hint starts, then kept up as each target registers (the game announces every target as it starts).
        /// </summary>
        private static readonly Dictionary<int, KeyValuePair<EUrgencyMarkerTargetSource, Transform>> Targets =
            new Dictionary<int, KeyValuePair<EUrgencyMarkerTargetSource, Transform>>();

        private static object _targetsSession;
        private static int _searchFrame = -1;
        private static readonly List<int> Gone = new List<int>();
        private static UrgencyMarkerProvider _urgencyTemplate;

        /// <summary>A power-recovery hint and the ship it is tied to.</summary>
        private sealed class Hint
        {
            public UrgencyMarkerProvider Provider;
            public PlayerControlledShip Ship;
        }

        /// <summary>
        /// Every tied hint by instance ID. Unlike FleetShips' assignment it still names the ship once that ship is destroyed,
        /// so a lost ship's hint is told apart from a hint tied to no ship.
        /// </summary>
        private static readonly Dictionary<int, Hint> Hints = new Dictionary<int, Hint>();

        /// <summary>
        /// True when an actor is on the other PvP team. A player on no team (waiting for a place) is on neither side: no team's
        /// pings reach them, and theirs reach no team (two players on no team share theirs).
        /// </summary>
        private static bool IsOtherTeam(int actor)
        {
            if (!FleetSession.IsPvP) return false;
            int mine = FleetShips.TeamOfActor(FleetShips.LocalActor);
            int theirs = FleetShips.TeamOfActor(actor);
            if (mine < 0 && theirs < 0) return false;
            return mine != theirs;
        }

        /// <summary>A part of the other team's ship (PvP only).</summary>
        private static bool IsEnemyPart(Component part)
        {
            return part != null && PvpBoarding.IsEnemyShip(FleetShips.OwnerOf(part));
        }

        private static int _teamFrame = -1;
        private static bool _noTeam;

        /// <summary>
        /// Whether the local player is a Fleet Battle player on no team (waiting for a place), worked out once a frame: every
        /// team's ships are hidden from them.
        /// </summary>
        private static bool OnNoTeam()
        {
            int frame = Time.frameCount;
            if (frame != _teamFrame)
            {
                _teamFrame = frame;
                _noTeam = PvpCombat.Active && FleetShips.TeamOfActor(FleetShips.LocalActor) < 0;
            }
            return _noTeam;
        }

        // ---------------------------------------------------------------- pings

        /// <summary>
        /// Another team's ping: no marker, no sound, no chat line (co-op: everyone sees every ping). A player on no team
        /// gets no team's.
        /// </summary>
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
                if (obj == null || !PvpCombat.Active || !PvpBoarding.IsEnemyShip(ShipOfBreach(obj))) return true;
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

        /// <summary>
        /// A safety net: no damage marker or power hint of the enemy ship is drawn, whoever made it. A player on no team
        /// (waiting for a place) sees none of any ship's: theirs are kept, hidden, so their own ship's show once they get a
        /// place.
        /// </summary>
        [GamePatch(Feature, "UI.ObjectMarker.ObjectMarkerViewer", "IsVisible", PatchKind.Postfix)]
        private static void MarkerVisiblePostfix(ObjectMarkerData marker, ref bool __result)
        {
            const ObjectMarkerData.EMarkerType shipState = ObjectMarkerData.EMarkerType.Defect | ObjectMarkerData.EMarkerType.HullBreach | ObjectMarkerData.EMarkerType.Urgency;
            try
            {
                if (!__result || marker == null || (marker.MarkerType & shipState) == 0 || !FleetSession.IsPvP) return;
                if (OnNoTeam() || (marker.FollowTarget != null && IsEnemyPart(marker.FollowTarget))) __result = false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking a ship marker's visibility", e);
            }
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

        /// <summary>True once the HUD has been caught as it was enabled (HudEnabledPostfix), so it is never searched for.</summary>
        private static bool _hudCaught;

        /// <summary>The HUD's ship info, caught as the game enables it (every mission's HUD).</summary>
        [GamePatch(Feature, "UI.MainHUD.ShipInfoHUD", "OnEnable", PatchKind.Postfix, Optional = true)]
        private static void HudEnabledPostfix(UI.MainHUD.ShipInfoHUD __instance)
        {
            _hud = __instance;
            _hudCaught = true;
        }

        /// <summary>
        /// The HUD's hull, power and breakers bind once, at spawn, to the ship of that moment. When the local player moves
        /// to the other ship, or is re-cloned there after losing their own, they move along with the player. A spectator
        /// without a character has no HUD to move.
        /// </summary>
        private static void KeepHudOnViewShip()
        {
            if (FleetSession.ShipCount < 2 || GameSessionManager.InHub || HudShipField == null || HudEnable == null || HudDisable == null) return;
            if (LocalPlayer.Instance == null) return;
            CG.Game.ClientGame game = CG.Game.ClientGame.Current;
            PlayerControlledShip view = game != null ? game.PlayerShip : null;
            if (view == null || view.StartedDeath) return;
            // Only searched for when the patch that catches it is missing.
            if (_hud == null && !_hudCaught) _hud = UnityEngine.Object.FindObjectOfType<UI.MainHUD.ShipInfoHUD>();
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
                    Tie(__instance, ship);
                    _urgencyTemplate = __instance;
                }
                if (IsLost(ship)) return;
                UrgencyByShip[ship] = __instance;
                if (____markerTargets == null) return;
                TakeTargets(____markerTargets, ship, false);
                // None known for this ship (its parts started while no hint was listening): the scene is searched again.
                if (____markerTargets.Count == 0) TakeTargets(____markerTargets, ship, true);
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

        // A hint tied to a lost ship reacts to nothing (FleetShips.AssignedOwner no longer names a destroyed ship, which
        // made such a hint take every ship's targets and events).

        [GamePatch(Feature, "UI.ObjectMarker.UrgencyMarkerProvider", "OnHintMarkerTargetRegistered", PatchKind.Prefix)]
        private static bool UrgencyTargetPrefix(UrgencyMarkerProvider __instance, EUrgencyMarkerTargetSource targetSource, Transform target)
        {
            if (target != null && _targetsSession != null) Targets[target.GetInstanceID()] = new KeyValuePair<EUrgencyMarkerTargetSource, Transform>(targetSource, target);
            if (TiedToLostShip(__instance)) return false;
            if (!FleetShips.Active || target == null) return true;
            PlayerControlledShip home = HomeShip(target);
            PlayerControlledShip mine = FleetShips.AssignedOwner(__instance);
            return home == null || mine == null || home == mine;
        }

        /// <summary>A hint's targets: those of the mission's hint targets (MissionTargets) that belong to its ship.</summary>
        private static void TakeTargets(Dictionary<EUrgencyMarkerTargetSource, Transform> markerTargets, PlayerControlledShip ship, bool search)
        {
            markerTargets.Clear();
            foreach (KeyValuePair<EUrgencyMarkerTargetSource, Transform> target in MissionTargets(search))
            {
                if (target.Value != null && HomeShip(target.Value) == ship) markerTargets[target.Key] = target.Value;
            }
        }

        /// <summary>
        /// The mission's hint targets: searched for once per mission (a search of the whole scene; one per hint cost 18 ms at
        /// the start of a mission), or again when asked (at most once a frame), and kept up from the game's own announcement
        /// of each target that starts later (UrgencyTargetPrefix).
        /// </summary>
        private static Dictionary<int, KeyValuePair<EUrgencyMarkerTargetSource, Transform>>.ValueCollection MissionTargets(bool search)
        {
            object session = GameSessionManager.ActiveSession;
            if (!ReferenceEquals(session, _targetsSession) || _targetsSession == null)
            {
                _targetsSession = session;
                Targets.Clear();
                TargetHome.Clear();
                search = true;
            }
            if (search && Time.frameCount != _searchFrame)
            {
                _searchFrame = Time.frameCount;
                foreach (UrgencyMarkerTarget target in UnityEngine.Object.FindObjectsOfType<UrgencyMarkerTarget>())
                {
                    if (target != null) Targets[target.transform.GetInstanceID()] = new KeyValuePair<EUrgencyMarkerTargetSource, Transform>(target.TargetSourceType, target.transform);
                }
            }
            return Targets.Values;
        }

        [GamePatch(Feature, "UI.ObjectMarker.UrgencyMarkerProvider", "OnCarryableInserted", PatchKind.Prefix)]
        [GamePatch(Feature, "UI.ObjectMarker.UrgencyMarkerProvider", "OnCarryableEjected", PatchKind.Prefix)]
        private static bool UrgencySocketPrefix(UrgencyMarkerProvider __instance, ICarryablesSocket socket)
        {
            if (TiedToLostShip(__instance)) return false;
            if (!FleetShips.Active) return true;
            PlayerControlledShip ship = FleetShips.OwnerOf(socket as Component);
            PlayerControlledShip mine = FleetShips.AssignedOwner(__instance);
            return ship == null || mine == null || ship == mine;
        }

        /// <summary>Carrying another ship's homunculus doesn't light this ship's "bring it to the core" hint.</summary>
        [GamePatch(Feature, "UI.ObjectMarker.UrgencyMarkerProvider", "OnLocalPayloadChanged", PatchKind.Prefix)]
        private static bool UrgencyPayloadPrefix(UrgencyMarkerProvider __instance, ref CarryableObject carryable)
        {
            try
            {
                if (TiedToLostShip(__instance)) return false;
                if (carryable == null || !FleetShips.Active || !__instance.HomunculusRef.IsInstanceOf(carryable)) return true;
                UrgencyMarkerTarget target = carryable.GetComponentInChildren<UrgencyMarkerTarget>(true);
                PlayerControlledShip home = target != null ? HomeShip(target.transform) : FleetShips.OwnerOf(carryable);
                PlayerControlledShip mine = FleetShips.AssignedOwner(__instance);
                if (home != null && mine != null && home != mine) carryable = null;
            }
            catch (Exception e)
            {
                Log.Failure("Checking a carried homunculus", e);
            }
            return true;
        }

        /// <summary>
        /// A hint tied to a lost ship shows nothing: every reason the game checks (its ship's power and breakers, which can
        /// still change while the ship dies, the homunculus, a socket) ends here.
        /// </summary>
        [GamePatch(Feature, "UI.ObjectMarker.UrgencyMarkerProvider", "CheckConditions", PatchKind.Prefix, Optional = true)]
        private static bool UrgencyConditionsPrefix(UrgencyMarkerProvider __instance)
        {
            if (!TiedToLostShip(__instance)) return true;
            HideMarker(__instance);
            return false;
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

        /// <summary>Ties a hint to a ship: FleetShips' assignment (the game's code in it reads that ship) and Hints.</summary>
        private static void Tie(UrgencyMarkerProvider provider, PlayerControlledShip ship)
        {
            FleetShips.AssignOwner(provider, ship);
            Hints[provider.GetInstanceID()] = new Hint { Provider = provider, Ship = ship };
        }

        /// <summary>A ship that is dying or gone.</summary>
        private static bool IsLost(PlayerControlledShip ship)
        {
            return ship == null || ship.StartedDeath;
        }

        /// <summary>Whether a hint is tied to a lost ship (a hint tied to no ship, in a one-ship mission, is not).</summary>
        private static bool TiedToLostShip(UrgencyMarkerProvider provider)
        {
            Hint hint;
            return (object)provider != null && Hints.TryGetValue(provider.GetInstanceID(), out hint) && IsLost(hint.Ship);
        }

        private static void HideMarker(UrgencyMarkerProvider provider)
        {
            var marker = GameMember.Read<ObjectMarkerData>(UrgencyMarkerField, provider, null);
            if (marker != null && marker.Enabled != null) marker.Enabled.SetValue(false);
        }

        /// <summary>
        /// A hint goes: its marker is hidden, switching it off lets go of its ship's power and breakers (the game's own
        /// OnDisable, which still reads the ship it is tied to), then it is untied and destroyed.
        /// </summary>
        private static void Retire(UrgencyMarkerProvider provider)
        {
            HideMarker(provider);
            if (provider.enabled) provider.enabled = false;
            FleetShips.AssignOwner(provider, null);
            UnityEngine.Object.Destroy(provider);
        }

        /// <summary>A ship is lost: its power-recovery hint goes with it (every ship's in co-op, the player's own in PvP).</summary>
        public static void OnShipLost(PlayerControlledShip ship)
        {
            if (ship == null) return;
            try
            {
                bool templateLost = false;
                foreach (Hint hint in new List<Hint>(Hints.Values))
                {
                    if (hint.Ship != ship || hint.Provider == null) continue;
                    if (hint.Provider == _urgencyTemplate) templateLost = true;
                    Retire(hint.Provider);
                    Log.Verbose("The power-recovery hint of " + FleetShips.Describe(ship) + " goes with it.");
                }
                UrgencyByShip.Remove(ship);
                // Copies of a hint are made from a live one.
                if (templateLost) _urgencyTemplate = AnyLiveHint();
            }
            catch (Exception e)
            {
                Log.Failure("Removing a lost ship's power-recovery hint", e);
            }
        }

        private static UrgencyMarkerProvider AnyLiveHint()
        {
            foreach (KeyValuePair<PlayerControlledShip, UrgencyMarkerProvider> entry in UrgencyByShip)
            {
                if (entry.Value != null && !IsLost(entry.Key)) return entry.Value;
            }
            return null;
        }

        /// <summary>The hints of lost ships go (all but the one copies are made from, which stays but shows nothing).</summary>
        private static void PruneUrgency()
        {
            List<KeyValuePair<PlayerControlledShip, UrgencyMarkerProvider>> gone = null;
            foreach (KeyValuePair<PlayerControlledShip, UrgencyMarkerProvider> entry in UrgencyByShip)
            {
                if (entry.Value != null && !IsLost(entry.Key)) continue;
                if (gone == null) gone = new List<KeyValuePair<PlayerControlledShip, UrgencyMarkerProvider>>();
                gone.Add(entry);
            }
            if (gone == null) return;
            foreach (KeyValuePair<PlayerControlledShip, UrgencyMarkerProvider> entry in gone)
            {
                UrgencyByShip.Remove(entry.Key);
                if (entry.Value != null && entry.Value != _urgencyTemplate) Retire(entry.Value);
            }
        }

        /// <summary>Forgets the ties of hints that are destroyed.</summary>
        private static void PruneHints()
        {
            List<int> gone = null;
            foreach (KeyValuePair<int, Hint> entry in Hints)
            {
                if (entry.Value.Provider != null) continue;
                if (gone == null) gone = new List<int>();
                gone.Add(entry.Key);
            }
            if (gone == null) return;
            foreach (int id in gone) Hints.Remove(id);
        }

        private static int LivingShipCount()
        {
            int count = 0;
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
                if (!IsLost(ship)) count++;
            }
            return count;
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
                    if (IsLost(ship) || (UrgencyByShip.TryGetValue(ship, out existing) && existing != null)) continue;
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
                    Tie(copy, ship);
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

        /// <summary>
        /// The hint targets' ships that are gone are forgotten, and every target once no ship is left (between missions), so
        /// a mission's ships aren't kept in memory after it.
        /// </summary>
        private static void PruneTargets()
        {
            if (TargetHome.Count == 0 && Targets.Count == 0) return;
            if (FleetShips.All.Count == 0)
            {
                TargetHome.Clear();
                Targets.Clear();
                return;
            }
            foreach (KeyValuePair<int, PlayerControlledShip> entry in TargetHome)
            {
                if (entry.Value == null) Gone.Add(entry.Key);
            }
            if (Gone.Count == 0) return;
            foreach (int target in Gone) TargetHome.Remove(target);
            Gone.Clear();
        }

        /// <summary>A few times a second: a ship that arrived late gets its hint; a lost ship's hint goes.</summary>
        public static void Tick()
        {
            KeepHudOnViewShip();
            PruneHints();
            PruneUrgency();
            PruneTargets();
            if (_urgencyTemplate == null || !FleetShips.Active || FleetSession.IsPvP) return;
            if (UrgencyByShip.Count < LivingShipCount()) EnsureUrgencyPerShip();
        }
    }
}
