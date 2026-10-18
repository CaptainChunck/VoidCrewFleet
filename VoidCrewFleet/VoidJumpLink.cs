using System;
using System.Collections.Generic;
using System.Reflection;
using CG.Client;
using CG.Ship.Modules;
using CG.Space;
using Code.Gameplay;
using Gameplay.Quests;
using Gameplay.Ship;
using Gameplay.Ship.VoidJump;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Void jumps with two ships. The session has one active sector, so the two ships always jump together: ship 1's crew
    /// plots the exit vector, charges the drive and pulls the lever as usual (anyone may still choose the destination on
    /// either ship's astral map), and ship 2 is carried along, living through every step of the jump with its own crew.
    ///
    /// Each ship has its own VoidJumpSystem. Ship 2's follows ship 1's: on the client that drives ship 2's jump system, every
    /// state change of ship 1 is applied to ship 2 too, and the game's own sync carries ship 2's state to everyone else.
    /// Ship 2's jump system makes no moves of its own (its timers and "not charged" signals are ignored), and the parts of
    /// a jump that must only happen once (the interdiction roll and counters, leaving the sector, the autosave) are skipped
    /// for it. The two ships arrive side by side at the destination's entry point.
    /// PvP matches have no void jumps.
    /// </summary>
    internal static class VoidJumpLink
    {
        public const string Feature = "Two ships jump together";

        private const int StateInactive = 0;
        private const int StateAligning = 2;
        private const int StateTravellingUnstable = 8;
        private const int FirstEndState = 9;

        private static readonly MethodInfo ChangeStateByIndex = GameMember.Method(typeof(VoidJumpSystem), "ChangeActiveStateByIndex");
        private static readonly FieldInfo StateIndexField = GameMember.Field(typeof(VoidJumpSystem), "activeStateIndex");

        private static readonly List<bool> Counted = new List<bool>();
        private static int _followerDepth;
        private static bool _mirroring;
        private static bool _starting;
        private static VoidJumpSystem _reading;
        private static float _nextWarning;

        /// <summary>True while ship 2's jump system is changing state (the once-only parts of a jump are skipped then).</summary>
        private static bool InFollowerStep
        {
            get { return _followerDepth > 0; }
        }

        private static bool Linked
        {
            get { return FleetSession.Mode == FleetMode.TwoShips && FleetShips.Active; }
        }

        private static bool IsFollower(VoidJumpSystem system)
        {
            return Linked && system != null && system.PlayerShip != null && FleetShips.Slot(system.PlayerShip) == 1;
        }

        private static bool IsLeader(VoidJumpSystem system)
        {
            return Linked && system != null && system.PlayerShip != null && FleetShips.Slot(system.PlayerShip) == 0;
        }

        private static VoidJumpSystem SystemOf(PlayerControlledShip ship)
        {
            return ship != null ? ship.GetComponentInChildren<VoidJumpSystem>() : null;
        }

        /// <summary>True while a ship is somewhere between leaving a sector and arriving in the next one.</summary>
        public static bool IsTravelling(PlayerControlledShip ship)
        {
            VoidJumpSystem system = SystemOf(ship);
            if (system == null) return false;
            VoidJumpState state = system.ActiveState;
            return state is VoidJumpTravellingStable || state is VoidJumpTravellingUnstable || state is VoidJumpApproachingDestination ||
                   state is VoidJumpSpinningDown || state is VoidJumpInterdiction;
        }

        // ---------------------------------------------------------------- ship 2 follows ship 1

        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "ChangeActiveStateByIndex", PatchKind.Prefix)]
        private static bool ChangeStatePrefix(VoidJumpSystem __instance, int newActiveStateIndex)
        {
            if (!IsFollower(__instance))
            {
                Counted.Add(false);
                return true;
            }
            bool allowed = _mirroring || _starting || _reading == __instance;
            if (!allowed || newActiveStateIndex >= FirstEndState)
            {
                Counted.Add(false);
                return false;
            }
            _followerDepth++;
            Counted.Add(true);
            return true;
        }

        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "ChangeActiveStateByIndex", PatchKind.Finalizer)]
        private static void ChangeStateFinalizer()
        {
            if (Counted.Count == 0) return;
            bool counted = Counted[Counted.Count - 1];
            Counted.RemoveAt(Counted.Count - 1);
            if (counted) _followerDepth--;
        }

        /// <summary>Ship 1 changed state: the client driving ship 2's jump system makes ship 2 do the same.</summary>
        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "ChangeActiveStateByIndex", PatchKind.Postfix)]
        private static void ChangeStatePostfix(VoidJumpSystem __instance, int newActiveStateIndex)
        {
            if (!IsLeader(__instance) || newActiveStateIndex >= FirstEndState || ChangeStateByIndex == null) return;
            PlayerControlledShip followerShip = FleetShips.BySlot(1);
            VoidJumpSystem follower = SystemOf(followerShip);
            if (follower == null || follower.photonView == null || !follower.photonView.AmOwner) return;
            try
            {
                if (StateIndexField != null && (int)StateIndexField.GetValue(follower) == newActiveStateIndex) return;
                if (newActiveStateIndex >= StateAligning && newActiveStateIndex <= StateTravellingUnstable && follower.ActiveSystemHook == null)
                {
                    VoidDriveModule drive = followerShip.GetModule<VoidDriveModule>();
                    if (drive != null) follower.ActiveSystemHook = drive;
                }
                _mirroring = true;
                ChangeStateByIndex.Invoke(follower, new object[] { newActiveStateIndex });
            }
            catch (Exception e)
            {
                Log.Failure("Moving ship 2 through the void jump with ship 1", e);
            }
            finally
            {
                _mirroring = false;
            }
        }

        /// <summary>Other clients take ship 2's state from the game's own sync, as for any ship.</summary>
        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "OnPhotonSerializeView", PatchKind.Prefix)]
        private static void SerializePrefix(VoidJumpSystem __instance, PhotonStream stream)
        {
            if (stream != null && stream.IsReading && IsFollower(__instance)) _reading = __instance;
        }

        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "OnPhotonSerializeView", PatchKind.Finalizer)]
        private static void SerializeFinalizer()
        {
            _reading = null;
        }

        /// <summary>At the start of a mission every ship's jump system picks its first state (in the void or not).</summary>
        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "Start", PatchKind.Prefix)]
        private static void StartPrefix()
        {
            _starting = true;
        }

        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "Start", PatchKind.Finalizer)]
        private static void StartFinalizer()
        {
            _starting = false;
        }

        /// <summary>Ship 2's own signals (lever, exit vector, "not charged") don't move it; ship 1's jump does.</summary>
        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "SendSignal", PatchKind.Prefix)]
        private static bool SignalPrefix(VoidJumpSystem __instance)
        {
            return !IsFollower(__instance);
        }

        // ---------------------------------------------------------------- once per jump, not once per ship

        [GamePatch(Feature, "Gameplay.Quests.Quest", "NextJumpInterdictionCheck", PatchKind.Prefix)]
        private static bool InterdictionRollPrefix(ref ValueTuple<bool, bool> __result)
        {
            if (!InFollowerStep) return true;
            __result = new ValueTuple<bool, bool>(false, false);
            return false;
        }

        [GamePatch(Feature, "Gameplay.Quests.Quest", "MarkInterdictionEntered", PatchKind.Prefix)]
        private static bool InterdictionCountPrefix()
        {
            return !InFollowerStep;
        }

        [GamePatch(Feature, "GameSessionSectorManager", "ExitCurrentSector", PatchKind.Prefix)]
        private static bool ExitSectorPrefix()
        {
            return !InFollowerStep;
        }

        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "EnterVoid", PatchKind.Prefix)]
        private static bool EnterVoidPrefix(VoidJumpSystem __instance)
        {
            return !IsFollower(__instance);
        }

        /// <summary>Ship 2 can reach "arrived" a moment before the sector is entered on a client; it joins the sector then.</summary>
        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpInactive", "OnEnter", PatchKind.Prefix)]
        private static bool InactiveEnterPrefix(VoidJumpSystem ___voidJumpSystem)
        {
            if (!IsFollower(___voidJumpSystem) || GameSessionManager.ActiveSector != null || ___voidJumpSystem.DestinationSector != null) return true;
            if (___voidJumpSystem.ActiveSystemHook != null)
            {
                ___voidJumpSystem.ActiveSystemHook.OnEnter((VoidJumpState)___voidJumpSystem.GetState<VoidJumpInactive>());
                ___voidJumpSystem.ActiveSystemHook = null;
            }
            ___voidJumpSystem.FromSector = null;
            return false;
        }

        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "EnterSector", PatchKind.Prefix)]
        private static bool EnterSectorPrefix(VoidJumpSystem __instance)
        {
            return !IsFollower(__instance) || GameSessionManager.ActiveSector != null || __instance.DestinationSector != null;
        }

        // ---------------------------------------------------------------- who may pull the lever

        [GamePatch(Feature, "CG.Ship.Modules.VoidDriveModule", "TryStartJumpEngine", PatchKind.Prefix)]
        private static bool TryStartJumpPrefix(VoidDriveModule __instance, ref ImpulseJumpOutcome __result)
        {
            try
            {
                if (!FleetShips.Active) return true;
                PlayerControlledShip ship = FleetShips.OwnerOf(__instance);
                if (FleetSession.IsPvP)
                {
                    __result = ImpulseJumpOutcome.CantFlyNoImpulse;
                    Warn("No void jumps in a PvP match: the fight ends when a ship is destroyed.");
                    return false;
                }
                if (Linked && FleetShips.Slot(ship) == 1)
                {
                    __result = ImpulseJumpOutcome.CantFlyNoImpulse;
                    Warn("Ship 1 makes the void jump, and ship 2 jumps with it. You can still pick the destination on the astral map.");
                    return false;
                }
            }
            catch (Exception e)
            {
                Log.Failure("Checking who may start the void jump", e);
            }
            return true;
        }

        private static void Warn(string text)
        {
            if (Time.unscaledTime < _nextWarning) return;
            _nextWarning = Time.unscaledTime + 3f;
            try
            {
                ViewEventBus.Instance.OnShowQuickWarning.Publish(text);
            }
            catch
            {
                Notice.Show(text);
            }
        }

        // ---------------------------------------------------------------- arriving side by side

        private static Vector3 ArrivalOffset(VoidJumpSystem system, Sector.ImpulseEnterPoint enter)
        {
            if (!Linked || system == null || system.PlayerShip == null || enter == null) return Vector3.zero;
            Vector3 look = enter.LookDirectionPosition - enter.Position;
            look.y = 0f;
            if (look.sqrMagnitude < 0.01f) look = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, look.normalized);
            float half = Settings.ShipSpacing.Value * 0.5f;
            return right * (FleetShips.Slot(system.PlayerShip) == 0 ? -half : half);
        }

        private static Sector.ImpulseEnterPoint EnterPoint(VoidJumpSystem system)
        {
            GameSessionSector destination = system != null ? system.DestinationSector : null;
            return destination != null && destination.SectorAsset != null ? destination.SectorAsset.PlayerShipEnterPoint : null;
        }

        [GamePatch(Feature, "CG.Ship.Modules.VoidDriveModule", "OnEnter", PatchKind.Postfix)]
        private static void DriveEnterPostfix(VoidJumpState jumpState, VoidJumpSystem ___voidJumpSystem, ref Vector3 ___velocityToRelocateInVoid)
        {
            try
            {
                if (!Linked || !(jumpState is VoidJumpApproachingDestination || jumpState is VoidJumpInterdiction)) return;
                Sector.ImpulseEnterPoint enter = EnterPoint(___voidJumpSystem);
                if (enter == null) return;
                Vector3 target = enter.Position + ArrivalOffset(___voidJumpSystem, enter);
                ___velocityToRelocateInVoid = (target - ___voidJumpSystem.PlayerShipPlatform.Position) / 0.1f;
            }
            catch (Exception e)
            {
                Log.Failure("Aiming a ship at its arrival point", e);
            }
        }

        /// <summary>The game's arrival steering, aimed at each ship's own arrival point.</summary>
        [GamePatch(Feature, "CG.Ship.Modules.VoidDriveModule", "UpdateState", PatchKind.Prefix)]
        private static bool DriveUpdatePrefix(VoidDriveModule __instance, VoidJumpState jumpState, VoidJumpSystem ___voidJumpSystem, Helm ___helm,
            ShipEngine ___shipEngine, Vector3 ___velocityToRelocateInVoid)
        {
            if (!Linked || !(jumpState is VoidJumpApproachingDestination || jumpState is VoidJumpInterdiction)) return true;
            try
            {
                if (!__instance.HookPv.AmController) return false;
                Sector.ImpulseEnterPoint enter = EnterPoint(___voidJumpSystem);
                if (enter == null) return true;
                if (___helm.RotateTowardsPerFrame(enter.LookDirectionPosition - enter.Position)) ___shipEngine.SetInput(Vector3.zero, Vector3.zero);
                Vector3 target = enter.Position + ArrivalOffset(___voidJumpSystem, enter);
                Vector3 toTarget = target - ___voidJumpSystem.PlayerShipPlatform.Position;
                if (toTarget.magnitude < 5f || Vector3.Dot(toTarget.normalized, ___velocityToRelocateInVoid.normalized) < 0f)
                {
                    ___voidJumpSystem.PlayerShipPlatform.Velocity = Vector3.zero;
                }
                else
                {
                    ___voidJumpSystem.PlayerShipPlatform.SetVelocity(___velocityToRelocateInVoid, ___voidJumpSystem.PlayerShipPlatform.AngularVelocity);
                }
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Steering a ship to its arrival point", e);
                return true;
            }
        }
    }
}
