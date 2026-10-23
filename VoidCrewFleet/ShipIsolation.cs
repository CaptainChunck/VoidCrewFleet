using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CG.Client.Ship.Hull;
using CG.Ship;
using CG.Ship.Tracking;
using CG.Space;
using Gameplay.Power;
using HarmonyLib;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Keeps what happens on one ship on that ship. The game raises several ship events without saying which ship they
    /// come from; here the code raising them says which ship it is (ShipEvents), and each ship's own listeners ignore the
    /// other ship's events:
    /// - a power breaker tripping, and enemies starting to track the ship, flash only that ship's lights;
    /// - a ship's "danger" state (enemies around it) is worked out for that ship (for every ship in one pass a frame), and
    ///   only its own lights follow it.
    /// </summary>
    internal static class ShipIsolation
    {
        public const string Feature = "Ships kept apart";

        // ---------------------------------------------------------------- who raises the event

        [GamePatch(Feature, "Gameplay.Power.ProtectedPowerSystem", "BreakNextBreaker", PatchKind.Prefix)]
        [GamePatch(Feature, "Gameplay.Power.ProtectedPowerSystem", "OnPhotonSerializeView", PatchKind.Prefix)]
        private static void PowerSystemPrefix(ProtectedPowerSystem __instance)
        {
            ShipEvents.BeginFor(__instance);
        }

        [GamePatch(Feature, "CG.Ship.Tracking.TrackingComponent", "HandleReceivingTrackersList", PatchKind.Prefix)]
        private static void TrackingPrefix(TrackingComponent __instance)
        {
            ShipEvents.BeginFor(__instance);
        }

        [GamePatch(Feature, "Gameplay.Power.ProtectedPowerSystem", "BreakNextBreaker", PatchKind.Finalizer)]
        [GamePatch(Feature, "Gameplay.Power.ProtectedPowerSystem", "OnPhotonSerializeView", PatchKind.Finalizer)]
        [GamePatch(Feature, "CG.Ship.Tracking.TrackingComponent", "HandleReceivingTrackersList", PatchKind.Finalizer)]
        private static void EventFinalizer()
        {
            ShipEvents.End();
        }

        /// <summary>
        /// Each ship works out its own danger state ("enemies are after this ship"): while it does, "the player ship" is
        /// that ship, and the state it announces belongs to it.
        /// </summary>
        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "UpdateDangerState", PatchKind.Prefix)]
        private static void DangerStatePrefix(PlayerControlledShip __instance)
        {
            bool two = FleetShips.Active;
            ShipRedirect.BeginOverride(two ? __instance : null);
            ShipEvents.Begin(two ? __instance : null);
        }

        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "UpdateDangerState", PatchKind.Finalizer)]
        private static void DangerStateFinalizer()
        {
            ShipEvents.End();
            ShipRedirect.EndOverride();
        }

        private static readonly MethodInfo CombatFeedbackMethod = GameMember.Method(typeof(PlayerShipUtils), "AnyShipShouldTriggerCombatFeedback");
        private static readonly MethodInfo InDangerMethod = new Func<PlayerControlledShip, bool>(InDanger).Method;

        /// <summary>This frame's danger of every ship (InDanger): the ships, and whether a hostile ship is after each.</summary>
        private static readonly List<PlayerControlledShip> DangerShips = new List<PlayerControlledShip>();

        private static readonly List<bool> DangerFlags = new List<bool>();
        private static int _dangerFrame = -1;
        private static bool _dangerKnown;

        /// <summary>
        /// The danger check goes over every object in the sector, for each ship every frame (with six ships, six passes that
        /// each read "the player ship" twice per object). One pass a frame answers it for every ship instead (InDanger).
        /// </summary>
        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "UpdateDangerState", PatchKind.Transpiler, Optional = true)]
        private static IEnumerable<CodeInstruction> DangerStateTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (CombatFeedbackMethod != null && instruction.opcode == OpCodes.Call && Equals(instruction.operand, CombatFeedbackMethod))
                {
                    // Stack: [] -> [this] -> InDanger -> [bool].
                    var self = new CodeInstruction(OpCodes.Ldarg_0);
                    self.labels.AddRange(instruction.labels);
                    self.blocks.AddRange(instruction.blocks);
                    yield return self;
                    yield return new CodeInstruction(OpCodes.Call, InDangerMethod);
                    replaced++;
                    continue;
                }
                yield return instruction;
            }
            if (replaced == 0) Log.Warn("Ships kept apart: " + original.DeclaringType.FullName + "." + original.Name + " no longer checks for hostile ships; the game may have changed.");
        }

        /// <summary>
        /// Put in by the transpiler: whether a hostile ship that warns its target (the game's combat feedback) is after this
        /// ship, as the game's own check answers it, worked out for every ship in one pass over the sector's objects, once a
        /// frame. With one ship (or a ship that came after this frame's pass) it is the game's own check.
        /// </summary>
        public static bool InDanger(PlayerControlledShip ship)
        {
            if (FleetShips.Active)
            {
                int frame = Time.frameCount;
                if (frame != _dangerFrame)
                {
                    _dangerFrame = frame;
                    _dangerKnown = FindDanger();
                }
                if (_dangerKnown)
                {
                    for (int i = 0; i < DangerShips.Count; i++)
                    {
                        if (ReferenceEquals(DangerShips[i], ship)) return DangerFlags[i];
                    }
                }
            }
            return PlayerShipUtils.AnyShipShouldTriggerCombatFeedback();
        }

        /// <summary>
        /// The game's check for every ship at once: an object in the sector that isn't the ship itself, is a spacecraft with
        /// warning feedback and is hostile to the ship. False when it failed (each ship then asks the game itself).
        /// </summary>
        private static bool FindDanger()
        {
            DangerShips.Clear();
            DangerFlags.Clear();
            try
            {
                List<PlayerControlledShip> ships = FleetShips.All;
                for (int i = 0; i < ships.Count; i++)
                {
                    DangerShips.Add(ships[i]);
                    DangerFlags.Add(false);
                }
                List<OrbitObject> objects = GameSessionManager.ActiveOrbitObjects;
                for (int o = 0; o < objects.Count; o++)
                {
                    OrbitObject thing = objects[o];
                    var craft = thing as SpaceCraft;
                    if (ReferenceEquals(craft, null) || (craft.AggroFeedback & SpaceCraft.AggroFeedbackType.Warning) == 0) continue;
                    for (int i = 0; i < DangerShips.Count; i++)
                    {
                        if (!DangerFlags[i] && thing != DangerShips[i] && craft.IsHostile(DangerShips[i])) DangerFlags[i] = true;
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                Log.Failure("Checking which ships hostile ships are after", e);
                return false;
            }
        }

        // ---------------------------------------------------------------- each ship's own homunculus

        private static int _dispensingSlot = -1;

        /// <summary>A ship's homunculus dispenser spawns that ship's homunculus (the game takes ship 1's loadout's for every ship).</summary>
        [GamePatch(Feature, "CG.Ship.Hull.HomunculusAndBiomassSocket", "DispenseHomunculusNow", PatchKind.Prefix)]
        private static void DispensePrefix(UnityEngine.Component __instance)
        {
            try
            {
                _dispensingSlot = FleetShips.Active ? FleetShips.Slot(FleetShips.OwnerOf(__instance)) : -1;
            }
            catch (Exception e)
            {
                _dispensingSlot = -1;
                Log.Failure("Reading which ship dispenses a homunculus", e);
            }
        }

        [GamePatch(Feature, "CG.Ship.Hull.HomunculusAndBiomassSocket", "DispenseHomunculusNow", PatchKind.Finalizer)]
        private static void DispenseFinalizer()
        {
            _dispensingSlot = -1;
        }

        [GamePatch(Feature, "GameSession", "get_Homunculus", PatchKind.Postfix)]
        private static void HomunculusPostfix(GameSession __instance, ref ResourceAssets.CarryableRef __result)
        {
            if (_dispensingSlot < 1) return;
            try
            {
                FleetPlan plan = FleetSession.Current;
                ShipPlan ship = plan != null ? plan.Ship(_dispensingSlot) : null;
                if (ship == null) return;
                // Ships 2-6: their own loadout's homunculus (a copy of ship 1 has ship 1's), else the mission's own override, else
                // the default one. The session copies ship 1's loadout homunculus into its override, which isn't the mission's:
                // another ship doesn't take that one.
                if (!ship.Homunculus.IsNull) __result = new ResourceAssets.CarryableRef(ship.Homunculus);
                else if (!__instance.HomunculusOverride.IsNull && !IsShipAHomunculus(__instance)) __result = new ResourceAssets.CarryableRef(__instance.HomunculusOverride);
                else __result = DataTable<DefaultAssetTable>.Instance.DefaultHomunculus;
            }
            catch (Exception e)
            {
                Log.Failure("Choosing a ship's homunculus", e);
            }
        }

        private static bool IsShipAHomunculus(GameSession session)
        {
            try
            {
                var loadout = session.ToLoadShipData;
                return loadout != null && loadout.HomunculusOverride != null && !loadout.HomunculusOverride.IsNull &&
                       loadout.HomunculusOverride.AssetGuid == session.HomunculusOverride;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- each ship's lights

        [GamePatch(Feature, "CG.Client.Ship.Hull.LightSourceController", "OnPowerBreakerFlipped", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Client.Ship.Hull.LightSourceController", "OnPlayerShipStartedBeingTracked", PatchKind.Prefix)]
        [GamePatch(Feature, "CG.Client.Ship.Hull.LightSourceController", "OnPlayerShipDangerState", PatchKind.Prefix)]
        private static bool LightsPrefix(LightSourceController __instance)
        {
            try
            {
                return !ShipEvents.ForeignTo(__instance);
            }
            catch (Exception e)
            {
                Log.Failure("Checking which ship a light event belongs to", e);
                return true;
            }
        }
    }
}
