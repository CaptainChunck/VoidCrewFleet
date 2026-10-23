using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CG.Game.Player;
using CG.Network;
using CG.Space;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// The screen inside the clone tubes (healing, biomass, efficiency). Every tube has one and all of them show the same
    /// picture: the game lets the first tube's screen draw it and switches the others off for good. With several ships:
    /// - when the ship whose tube draws the screen is lost, a switched-off screen of a ship that is left takes over (the
    ///   tubes of every ship went blank before): as the ship starts dying (ShipDeath.ShipLost), or at the latest when that
    ///   screen goes with the ship;
    /// - a lost ship's tubes going away no longer cut the screen off from its events (the game resets them whenever a tube
    ///   is destroyed, which with one ship only happens when the mission ends);
    /// - it shows the biomass of the ship whose tube the local player is in (in Fleet Battle that can be a teammate's ship).
    /// </summary>
    internal static class CloneTubeScreen
    {
        public const string Feature = FleetShips.Feature;

        private static readonly FieldInfo ActiveScreenField = GameMember.Field(typeof(ClonetubeUIController), "_activeController");
        private static readonly FieldInfo TextTableField = GameMember.Field(typeof(UILocalizationProvider), "localizationData");
        private static readonly MethodInfo GetBiomass = Getter(GameMember.Property(typeof(GameSessionSuppliesManager), "BiomassAmount"));
        private static readonly MethodInfo ScreenBiomassMethod = new Func<GameSessionSuppliesManager, int>(ScreenBiomass).Method;

        /// <summary>The tubes of lost ships (instance IDs), until they are destroyed.</summary>
        private static readonly HashSet<int> LostTubes = new HashSet<int>();

        /// <summary>Screens (instance IDs) that have handed over already this mission.</summary>
        private static readonly HashSet<int> HandedOver = new HashSet<int>();

        /// <summary>The mission (game session) LostTubes and HandedOver are about; they start empty in the next (ThisMission).</summary>
        private static object _mission;

        /// <summary>The text table of the screen that draws, for a screen that takes over (the game removed the others').</summary>
        private static LocalizedStringTable _textTable;

        private static LocalPlayer _tubePlayer;
        private static InClonetube _tubeAbility;

        private static ClonetubeUIController ActiveScreen
        {
            get { return GameMember.Read<ClonetubeUIController>(ActiveScreenField, null, null); }
        }

        /// <summary>The notes of an earlier mission are dropped as the first of this mission's comes (screens and tubes go with it).</summary>
        private static void ThisMission()
        {
            object mission = GameSessionManager.ActiveSession;
            if (ReferenceEquals(mission, _mission)) return;
            _mission = mission;
            LostTubes.Clear();
            HandedOver.Clear();
        }

        /// <summary>A property's getter, or null when the property is missing.</summary>
        private static MethodInfo Getter(PropertyInfo property)
        {
            return property != null ? property.GetGetMethod(true) : null;
        }

        /// <summary>A ship is lost: its tubes are noted, and if one of them draws the screen, a tube of another ship takes over.</summary>
        public static void OnShipLost(PlayerControlledShip ship)
        {
            if (ship == null) return;
            try
            {
                ThisMission();
                foreach (CloneTube tube in ActiveTubes())
                {
                    if (tube != null && FleetShips.OwnerOf(tube) == ship) LostTubes.Add(tube.GetInstanceID());
                }
                ClonetubeUIController screen = ActiveScreen;
                if (screen != null && FleetShips.OwnerOf(screen) == ship) HandOver(screen, ship, false);
            }
            catch (Exception e)
            {
                Log.Failure("Keeping the clone tube screen when a ship is lost", e);
            }
        }

        /// <summary>The screen that starts drawing: its text table is kept for a screen that may take over later.</summary>
        [GamePatch(Feature, "ClonetubeUIController", "Setup", PatchKind.Postfix, Optional = true)]
        private static void ScreenSetupPostfix(ClonetubeUIController __instance)
        {
            try
            {
                UILocalizationProvider texts = __instance.Doc != null ? __instance.Doc.GetComponent<UILocalizationProvider>() : null;
                LocalizedStringTable table = texts != null ? GameMember.Read<LocalizedStringTable>(TextTableField, texts, null) : null;
                if (table != null) _textTable = table;
            }
            catch (Exception e)
            {
                Log.Failure("Reading the clone tube screen's texts", e);
            }
        }

        /// <summary>The drawing screen goes with a lost ship (and ShipLost didn't hand over): a tube of another ship takes over.</summary>
        [GamePatch(Feature, "ClonetubeUIController", "OnDestroy", PatchKind.Prefix, Optional = true)]
        private static void ScreenDestroyPrefix(ClonetubeUIController __instance)
        {
            try
            {
                ThisMission();
                if (!FleetShips.Active || HandedOver.Contains(__instance.GetInstanceID()) || !ReferenceEquals(ActiveScreen, __instance)) return;
                PlayerControlledShip own = FleetShips.OwnerOf(__instance);
                if (own == null || !own.StartedDeath || !AnotherShipLeft(own)) return;
                HandOver(__instance, own, true);
            }
            catch (Exception e)
            {
                Log.Failure("Keeping the clone tube screen when a ship is lost", e);
            }
        }

        /// <summary>
        /// A switched-off screen of another ship draws the picture from now on, with the game's own controller and text
        /// translation, as the first screen of a mission does. The lost ship's controller goes at the end of this frame
        /// (or is going already), before the new one looks (two frames later) whether another screen draws.
        /// </summary>
        private static void HandOver(ClonetubeUIController screen, PlayerControlledShip lost, bool goingAlready)
        {
            if (!HandedOver.Add(screen.GetInstanceID())) return;
            UIDocument spare = SpareScreen(screen.Doc, lost);
            if (spare == null)
            {
                Log.Warn("No clone tube screen of another ship was found to take over from " + FleetShips.Describe(lost) + "'s.");
                return;
            }
            UILocalizationProvider texts = screen.Doc != null ? screen.Doc.GetComponent<UILocalizationProvider>() : null;
            LocalizedStringTable table = texts != null ? GameMember.Read<LocalizedStringTable>(TextTableField, texts, null) : null;
            if (table == null) table = _textTable;
            if (!goingAlready)
            {
                if (screen.Doc != null) screen.Doc.enabled = false;
                UnityEngine.Object.Destroy(screen);
            }
            spare.enabled = true;
            if (table != null && spare.GetComponent<UILocalizationProvider>() == null) UILocalizationProvider.AddUILocalizationProviderAtRuntime(spare.gameObject, table);
            ClonetubeUIController controller = spare.gameObject.AddComponent<ClonetubeUIController>();
            controller.Doc = spare;
            Log.Info("The clone tube screen is drawn on " + FleetShips.Describe(FleetShips.OwnerOf(spare)) + " now (" + FleetShips.Describe(lost) + " is lost).");
        }

        /// <summary>A switched-off screen with the same picture in a tube of a ship that is left, lowest ship first.</summary>
        private static UIDocument SpareScreen(UIDocument current, PlayerControlledShip lost)
        {
            VisualTreeAsset picture = current != null ? current.visualTreeAsset : null;
            UIDocument best = null;
            int bestRank = int.MaxValue;
            foreach (CloneTube tube in ActiveTubes())
            {
                PlayerControlledShip ship = tube != null ? FleetShips.OwnerOf(tube) : null;
                if (ship == null || ship == lost || ship.StartedDeath) continue;
                int slot = FleetShips.Slot(ship);
                foreach (UIDocument doc in tube.GetComponentsInChildren<UIDocument>(true))
                {
                    if (doc == null || doc == current || doc.GetComponent<ClonetubeUIController>() != null) continue;
                    if (picture != null ? doc.visualTreeAsset != picture : doc.visualTreeAsset == null || doc.visualTreeAsset.name != "Clonetube") continue;
                    // A screen whose object is switched on first.
                    int rank = (slot >= 0 ? slot : 99) * 2 + (doc.gameObject.activeInHierarchy ? 0 : 1);
                    if (rank >= bestRank) continue;
                    best = doc;
                    bestRank = rank;
                }
            }
            return best;
        }

        /// <summary>
        /// The clone tubes whose objects are switched on: those the ships registered (CloneTubes), which saves a search of the
        /// whole scene (15-20 ms in a fleet mission, on every client as a ship is lost), else that search.
        /// </summary>
        private static List<CloneTube> ActiveTubes()
        {
            List<CloneTube> tubes = CloneTubes.RegisteredTubes();
            if (tubes == null) return new List<CloneTube>(UnityEngine.Object.FindObjectsOfType<CloneTube>());
            tubes.RemoveAll(tube => !tube.gameObject.activeInHierarchy);
            return tubes;
        }

        /// <summary>
        /// A lost ship's tube going away keeps the screen's events (the game would reset them for every screen) while
        /// another ship is left.
        /// </summary>
        [GamePatch(Feature, "CloneTube", "OnDestroy", PatchKind.Prefix, Optional = true)]
        private static bool TubeDestroyPrefix(CloneTube __instance, AutoUnsubscribeCollection ____listeners)
        {
            try
            {
                ThisMission();
                bool lost = LostTubes.Remove(__instance.GetInstanceID());
                PlayerControlledShip own = FleetShips.OwnerOf(__instance);
                if (!lost) lost = own != null && own.StartedDeath;
                if (!lost || !AnotherShipLeft(own)) return true;
                if (____listeners != null) ____listeners.UnsubscribeAll();
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Removing a lost ship's clone tube", e);
                return true;
            }
        }

        /// <summary>A ship other than this one (which may be unknown) that is not dying.</summary>
        private static bool AnotherShipLeft(PlayerControlledShip ship)
        {
            foreach (PlayerControlledShip other in FleetShips.All)
            {
                if (other != null && other != ship && !other.StartedDeath) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- the biomass on the screen

        /// <summary>The screen's biomass is that of the ship whose tube the local player is in.</summary>
        [GamePatch(ShipResources.Feature, "ClonetubeUIController", "Update", PatchKind.Transpiler, Optional = true)]
        private static IEnumerable<CodeInstruction> ScreenBiomassTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && GetBiomass != null && Equals(instruction.operand, GetBiomass))
                {
                    var call = new CodeInstruction(OpCodes.Call, ScreenBiomassMethod);
                    call.labels.AddRange(instruction.labels);
                    call.blocks.AddRange(instruction.blocks);
                    yield return call;
                    replaced++;
                    continue;
                }
                yield return instruction;
            }
            if (replaced == 0) Log.Warn("Clone tube screen: " + original.DeclaringType.FullName + "." + original.Name + " no longer reads the biomass; the game may have changed.");
        }

        /// <summary>Put in by the transpiler: the biomass of the local player's tube's ship, else the game's count.</summary>
        public static int ScreenBiomass(GameSessionSuppliesManager manager)
        {
            try
            {
                CloneTube tube = LocalTube();
                if (tube != null) return ShipResources.BiomassFor(manager, tube);
            }
            catch (Exception e)
            {
                Log.Failure("Showing the biomass on the clone tube screen", e);
            }
            return manager.BiomassAmount;
        }

        /// <summary>The clone tube the local player is in, or null.</summary>
        private static CloneTube LocalTube()
        {
            LocalPlayer me = LocalPlayer.Instance;
            if (me == null) return null;
            if (!ReferenceEquals(me, _tubePlayer))
            {
                _tubePlayer = me;
                _tubeAbility = me.Locomotion != null ? me.Locomotion.GetAbility<InClonetube>() : null;
            }
            return _tubeAbility != null && _tubeAbility.IsActive ? _tubeAbility.ActiveClonetube : null;
        }
    }
}
