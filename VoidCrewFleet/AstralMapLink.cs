using System;
using System.Collections.Generic;
using System.Reflection;
using CG.Space;
using Gameplay.Ship.VoidJump;
using UI.AstralMap;

namespace VoidCrewFleet
{
    /// <summary>
    /// Every ship has its own astral map, but a co-op fleet makes one choice: the escape vector is the sector's (VoidJumpLink
    /// sets it on every ship) and the destination is the session's (the game keeps a single one, as a room value, for
    /// everyone). Each ship's map shows that choice:
    /// - a map works with its own ship. Its action panel took "the player ship" when the map was set up, which was the ship
    ///   of the local player's crew: on another ship's map it showed that ship's jump (and sent its picks through that
    ///   ship), even once that ship was lost;
    /// - co-op: a destination picked on any ship's map, by anyone, is the pick on every map. A map this player is using
    ///   right now turns to it, as the game turns a map to the destination when it is opened in the void, and no map keeps
    ///   asking for a pick once there is one. Nothing is sent for it: the game's own room value carries the pick, so the
    ///   last pick made wins and two players picking at once can't set each other off.
    /// Fleet Battle ships are enemies: their maps keep to themselves (and nobody jumps there).
    /// </summary>
    internal static class AstralMapLink
    {
        public const string Feature = VoidJumpLink.Feature;

        private static readonly FieldInfo DisplayField = GameMember.Field(typeof(AstralMapController), "_sectorDisplay");
        private static readonly FieldInfo DisplayedField = GameMember.Field(typeof(ObjectiveDisplay), "_displayedSector");
        private static readonly FieldInfo DisplaySystemField = GameMember.Field(typeof(ObjectiveDisplay), "_voidJumpSystem");
        private static readonly MethodInfo DestinationObjective = GameMember.Method(typeof(AstralMapController), "GetDestinationPreviewObjective");
        private static readonly MethodInfo ActionClicked = GameMember.Method(typeof(ObjectiveDisplay), "ActionClicked");

        // ---------------------------------------------------------------- a map works with its own ship

        /// <summary>
        /// While a map sets itself up (its action panel included), "the player ship" is the map's own ship. The finalizer
        /// ends it only when its prefix set it (__state).
        /// </summary>
        [GamePatch(Feature, "UI.AstralMap.AstralMapController", "Start", PatchKind.Prefix, Optional = true)]
        private static void StartPrefix(AstralMapController __instance, out bool __state)
        {
            __state = false;
            PlayerControlledShip ship = null;
            try
            {
                if (FleetShips.Active) ship = FleetShips.OwnerOf(__instance);
            }
            catch (Exception e)
            {
                Log.Failure("Finding the ship of an astral map", e);
            }
            ShipRedirect.BeginOverride(ship);
            __state = true;
        }

        [GamePatch(Feature, "UI.AstralMap.AstralMapController", "Start", PatchKind.Finalizer, Optional = true)]
        private static void StartFinalizer(bool __state)
        {
            if (__state) ShipRedirect.EndOverride();
        }

        // ---------------------------------------------------------------- one destination on every map

        /// <summary>
        /// The session's destination changed (each map hears it, on every player's game). In co-op every map shows it as the
        /// fleet's pick: it stops asking for one, and a map this player has open turns to it unless it shows it already (not
        /// to an interdiction, which pulls the ships out of the void without anyone picking it).
        /// </summary>
        [GamePatch(Feature, "UI.AstralMap.AstralMapController", "SectorSet", PatchKind.Postfix, Optional = true)]
        private static void DestinationPostfix(AstralMapController __instance, GameSessionSector obj, ObjectiveDisplay ____sectorDisplay, bool ____toggled,
            bool ____mapReady)
        {
            // In a sector the destination is only ever cleared (on arrival): the choice there is the escape vector.
            if (____sectorDisplay == null || !VoidJumpLink.Linked || GameSessionManager.ActiveSector != null) return;
            try
            {
                ____sectorDisplay.VectorOrDestinationSelected = obj != null;
                if (obj == null) return;
                ____sectorDisplay.SetGlowAnimation(false);
                GameSessionSector interdiction = GameSessionManager.ActiveSession != null ? GameSessionManager.ActiveSession.InterdictionSector : null;
                if (!____toggled || !____mapReady || DestinationObjective == null || (interdiction != null && obj.Id == interdiction.Id)) return;
                var target = DestinationObjective.Invoke(__instance, null) as UIObjective;
                if (target == null || ReferenceEquals(GameMember.Read<UIObjective>(DisplayedField, ____sectorDisplay, null), target)) return;
                __instance.Focus(target, true);
            }
            catch (Exception e)
            {
                Log.Failure("Showing the fleet's destination on a ship's astral map", e);
            }
        }

        // ---------------------------------------------------------------- self-test

        /// <summary>Self-test: a ship's astral map, or null.</summary>
        public static AstralMapController MapOf(PlayerControlledShip ship)
        {
            if (ship == null) return null;
            foreach (AstralMapController map in UnityEngine.Object.FindObjectsOfType<AstralMapController>())
            {
                if (map != null && FleetShips.OwnerOf(map) == ship) return map;
            }
            return null;
        }

        private static ObjectiveDisplay DisplayOf(AstralMapController map)
        {
            return map != null ? GameMember.Read<ObjectiveDisplay>(DisplayField, map, null) : null;
        }

        /// <summary>
        /// Self-test: each ship's map, the ship whose jump its action panel works with (its own, expected), whether it counts
        /// a vector or destination as picked, and what it shows.
        /// </summary>
        public static string DescribeForTest()
        {
            var parts = new List<string>();
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
                AstralMapController map = MapOf(ship);
                ObjectiveDisplay display = DisplayOf(map);
                if (display == null)
                {
                    parts.Add(FleetShips.Describe(ship) + ": no astral map");
                    continue;
                }
                var system = GameMember.Read<VoidJumpSystem>(DisplaySystemField, display, null);
                var shown = GameMember.Read<UIObjective>(DisplayedField, display, null);
                parts.Add(FleetShips.Describe(ship) + "'s map: works with " + (system != null ? FleetShips.Describe(system.PlayerShip) : "no ship") +
                          (system != null && system.PlayerShip == ship ? " (its own)" : " (NOT its own)") + ", picked " + display.VectorOrDestinationSelected +
                          ", showing " + (shown != null && shown.Sector != null ? shown.Sector.Name + " (" + shown.Sector.Id + ")" : "nothing"));
            }
            GameSessionSector destination = GameSessionSectorManager.Instance != null ? GameSessionSectorManager.Instance.DestinationSector : null;
            return string.Join("; ", parts.ToArray()) + "; destination " + (destination != null ? destination.Name + " (" + destination.Id + ")" : "none");
        }

        /// <summary>Self-test: a mission sector a ship's map offers as a destination right now (in the void), or null.</summary>
        public static GameSessionSector PickableForTest(PlayerControlledShip ship)
        {
            AstralMapController map = MapOf(ship);
            if (map == null) return null;
            foreach (UIObjective objective in map.UIObjectives)
            {
                if (objective == null || objective.Sector == null || objective.isExiting || objective.isStarting || objective.IsInterdiction) continue;
                if (objective.SectorState.Value == UIObjective.MapObjectState.Active) return objective.Sector;
            }
            return null;
        }

        /// <summary>
        /// Self-test: a player at a ship's map presses its action button (in a sector: set or clear the escape vector), or
        /// first picks a sector on the map and then presses it (in the void: select that destination). False when the map
        /// or the sector isn't there.
        /// </summary>
        public static bool PressForTest(PlayerControlledShip ship, GameSessionSector pick)
        {
            AstralMapController map = MapOf(ship);
            ObjectiveDisplay display = DisplayOf(map);
            if (display == null || ActionClicked == null) return false;
            try
            {
                if (pick != null)
                {
                    UIObjective target = null;
                    foreach (UIObjective objective in map.UIObjectives)
                    {
                        if (objective != null && objective.Sector == pick && !objective.isExiting) target = objective;
                    }
                    if (target == null) return false;
                    map.Focus(target, true);
                }
                ActionClicked.Invoke(display, null);
                return true;
            }
            catch (Exception e)
            {
                Log.Failure("[autotest] Pressing a button on " + FleetShips.Describe(ship) + "'s astral map", e);
                return false;
            }
        }
    }
}
