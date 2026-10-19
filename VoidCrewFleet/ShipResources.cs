using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CG.Game;
using CG.Space;
using HarmonyLib;
using Photon.Pun;
using ResourceAssets;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace VoidCrewFleet
{
    /// <summary>
    /// Each player ship has its own alloys and biomass (the game keeps one count for the room, used by every fabricator,
    /// bioport and clone tube). With two ships:
    /// - the host keeps one count per ship and sends each change through the game's own NotifyAlloyChange /
    ///   NotifyBiomassChange messages, with the ship appended to the item GUID array (the game reads only its first four
    ///   numbers), so every client knows both counts, late joiners included;
    /// - fabricators, recyclers, fabricator upgrades, bioports and clone tubes check and spend their own ship's count, and
    ///   learn blueprints for their own ship;
    /// - the game's own count (HUD, ship voice lines, ship status, clone tube screen) is the count of the ship the local
    ///   player belongs to right now (FleetShips.ViewShip), switched silently when that changes; fabricator terminals
    ///   always show their own ship's count;
    /// - both ships start with the game's own starting amounts, except that ship 2 with a loadout of its own starts with its
    ///   own alloys (its CUSTOM loadout's, or the game's default); the end-of-mission tally counts the player's own ship.
    /// With one ship nothing is tagged or redirected.
    /// </summary>
    internal static class ShipResources
    {
        public const string Feature = "Per-ship resources";

        private const int Tag = 0x56434652; // "VCFR"
        private const string KeyStart = "VCF_ResStart";

        private static readonly int[] Alloy = new int[2];
        private static readonly int[] Biomass = new int[2];
        private static readonly bool[] HasAlloy = new bool[2];
        private static readonly bool[] HasBiomass = new bool[2];
        private static readonly List<int> Attributed = new List<int>();
        private static int[] _hostStart;
        private static int _shown = -1;
        private static int _pinned = -1;
        private static int _survivor = -1;
        private static bool _wasActive;

        private static readonly FieldInfo AlloyField = GameMember.Field(typeof(GameSessionSuppliesManager), "_alloyAmount");
        private static readonly FieldInfo BiomassField = GameMember.Field(typeof(GameSessionSuppliesManager), "_biomassAmount");
        private static readonly FieldInfo TabData = GameMember.Field(typeof(FabricatorActionTab), "data");
        private static readonly FieldInfo DataLogic = GameMember.Field(typeof(FabricatorData), "logic");
        private static readonly FieldInfo StateTube = GameMember.Field(typeof(CloneTubeState), "CloneTube");

        private static readonly MethodInfo GetAlloy = AccessTools.PropertyGetter(typeof(GameSessionSuppliesManager), "AlloyAmount");
        private static readonly MethodInfo GetBiomass = AccessTools.PropertyGetter(typeof(GameSessionSuppliesManager), "BiomassAmount");
        private static readonly MethodInfo ModAlloy = AccessTools.Method(typeof(GameSessionSuppliesManager), "ModifyAlloyCount");
        private static readonly MethodInfo ModBiomass = AccessTools.Method(typeof(GameSessionSuppliesManager), "ModifyBiomassCount");
        private static readonly MethodInfo AlloyForMethod = AccessTools.Method(typeof(ShipResources), "AlloyFor");
        private static readonly MethodInfo BiomassForMethod = AccessTools.Method(typeof(ShipResources), "BiomassFor");
        private static readonly MethodInfo ModifyAlloyForMethod = AccessTools.Method(typeof(ShipResources), "ModifyAlloyFor");
        private static readonly MethodInfo ModifyBiomassForMethod = AccessTools.Method(typeof(ShipResources), "ModifyBiomassFor");

        public static bool Active
        {
            get { return FleetShips.Active && Patcher.IsReady(Feature); }
        }

        /// <summary>For reports: "ship 1 120 alloys / 50 biomass, ship 2 ...".</summary>
        public static string Describe()
        {
            return "ship 1 " + Alloy[0] + " alloys / " + Biomass[0] + " biomass, ship 2 " + Alloy[1] + " alloys / " + Biomass[1] + " biomass, shown " +
                   (_shown + 1);
        }

        // ---------------------------------------------------------------- which ship a piece of code is for

        private static int SlotOf(object context)
        {
            if (context is FabricatorActionTab) context = GameMember.Read<object>(TabData, context, null);
            if (context is FabricatorData) context = GameMember.Read<object>(DataLogic, context, null);
            if (context is CloneTubeState) context = GameMember.Read<object>(StateTube, context, null);
            if (context == null) return -1;
            Component component = context as Component;
            if (component == null) component = ShipRedirect.OuterComponent(context);
            PlayerControlledShip ship = component != null ? FleetShips.OwnerOf(component) : null;
            return ship != null ? FleetShips.Slot(ship) : -1;
        }

        /// <summary>The ship whose count the game's own count shows on this client.</summary>
        private static int Desired()
        {
            if (_pinned >= 0) return _pinned;
            int slot = FleetShips.Slot(FleetShips.ViewShip);
            return slot >= 0 && slot <= 1 ? slot : 0;
        }

        private static int Shown()
        {
            if (_shown < 0) _shown = Desired();
            return _shown;
        }

        // ---------------------------------------------------------------- call-site helpers (put in by the transpiler)

        public static int AlloyFor(GameSessionSuppliesManager manager, object context)
        {
            if (!Active) return manager.AlloyAmount;
            int slot = SlotOf(context);
            return slot >= 0 && slot <= 1 && HasAlloy[slot] ? Alloy[slot] : manager.AlloyAmount;
        }

        public static int BiomassFor(GameSessionSuppliesManager manager, object context)
        {
            if (!Active) return manager.BiomassAmount;
            int slot = SlotOf(context);
            return slot >= 0 && slot <= 1 && HasBiomass[slot] ? Biomass[slot] : manager.BiomassAmount;
        }

        public static void ModifyAlloyFor(GameSessionSuppliesManager manager, int add, ResourceChangeAlloy cause, GUIDUnion guid, object context)
        {
            Attributed.Add(Active ? SlotOf(context) : -1);
            try
            {
                manager.ModifyAlloyCount(add, cause, guid);
            }
            finally
            {
                Attributed.RemoveAt(Attributed.Count - 1);
            }
        }

        public static void ModifyBiomassFor(GameSessionSuppliesManager manager, int add, ResourceChangeBiomass cause, GUIDUnion guid, object context)
        {
            Attributed.Add(Active ? SlotOf(context) : -1);
            try
            {
                manager.ModifyBiomassCount(add, cause, guid);
            }
            finally
            {
                Attributed.RemoveAt(Attributed.Count - 1);
            }
        }

        /// <summary>The fabricator, recycler, upgrade, bioport and clone tube code reads and spends its own ship's count.</summary>
        [GamePatch(Feature, "Gameplay.Factory.CarryableFactoryLogic", "Print", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.Factory.CarryableFactoryLogic+<DoPrintProcess>d__47", "MoveNext", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.Factory.CarryableFactoryLogic+<>c__DisplayClass47_0", "<DoPrintProcess>b__1", PatchKind.Transpiler)]
        [GamePatch(Feature, "Gameplay.Factory.CarryableFactoryLogic+<DoRecycleProcess>d__50", "MoveNext", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.FabricatorModule", "Upgrade", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Modules.FabricatorModule", "UpgradeInternal", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Ship.Hull.HomunculusAndBiomassSocket", "<Start>b__12_0", PatchKind.Transpiler)]
        [GamePatch(Feature, "CloneTubeHealing", "RemainingBiomass", PatchKind.Transpiler)]
        [GamePatch(Feature, "CloneTubeHealing", "DeductBiomass", PatchKind.Transpiler)]
        [GamePatch(Feature, "FabricatorData", "CanPrint", PatchKind.Transpiler)]
        [GamePatch(Feature, "FabricatorData", "PrintNow", PatchKind.Transpiler)]
        [GamePatch(Feature, "FabricationTab", ".ctor", PatchKind.Transpiler)]
        [GamePatch(Feature, "FabricationTab", "CheckErrors", PatchKind.Transpiler)]
        [GamePatch(Feature, "FabricationTab", "CanAfford", PatchKind.Transpiler)]
        [GamePatch(Feature, "UpgradeTab", ".ctor", PatchKind.Transpiler)]
        [GamePatch(Feature, "UpgradeTab", "CheckErrors", PatchKind.Transpiler)]
        private static IEnumerable<CodeInstruction> ResourceTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                var called = instruction.operand as MethodInfo;
                MethodInfo helper = null;
                if (called != null && (instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call))
                {
                    if (called == GetAlloy) helper = AlloyForMethod;
                    else if (called == GetBiomass) helper = BiomassForMethod;
                    else if (called == ModAlloy) helper = ModifyAlloyForMethod;
                    else if (called == ModBiomass) helper = ModifyBiomassForMethod;
                }
                if (helper == null)
                {
                    yield return instruction;
                    continue;
                }
                // [manager, args...] -> [manager, args..., this] -> helper. Every target is an instance method.
                var context = new CodeInstruction(OpCodes.Ldarg_0);
                context.labels.AddRange(instruction.labels);
                context.blocks.AddRange(instruction.blocks);
                yield return context;
                yield return new CodeInstruction(OpCodes.Call, helper);
                replaced++;
            }
            if (replaced == 0)
                Log.Warn("Per-ship resources: " + original.DeclaringType.FullName + "." + original.Name + " no longer uses the resources; the game may have changed.");
        }

        // ---------------------------------------------------------------- the host changes a ship's count

        private static int[] Tagged(GUIDUnion guid, int slot)
        {
            int[] g = guid.AsIntArray();
            return new[] { g[0], g[1], g[2], g[3], Tag, slot };
        }

        private static bool Untag(ref int[] guid, out int slot)
        {
            slot = -1;
            int[] a = guid;
            if (a == null || a.Length != 6 || a[4] != Tag || a[5] < 0 || a[5] > 1) return false;
            slot = a[5];
            guid = new[] { a[0], a[1], a[2], a[3] };
            return true;
        }

        /// <summary>Host: a ship's new count, through the game's own message (it also runs here at once and is stored).</summary>
        private static void Send(GameSessionSuppliesManager manager, int slot, bool alloy, int value, bool voice, byte cause, GUIDUnion guid)
        {
            if (slot < 0 || slot > 1) return;
            if (!alloy) value = Mathf.Max(0, value);
            manager.photonView.RPC(alloy ? "NotifyAlloyChange" : "NotifyBiomassChange", RpcTarget.All, value, voice, cause, Tagged(guid, slot));
        }

        private static int AttributedSlot()
        {
            int slot = Attributed.Count > 0 ? Attributed[Attributed.Count - 1] : -1;
            if (slot >= 0 && slot <= 1) return slot;
            slot = FleetShips.Slot(ClientGame.Current != null ? ClientGame.Current.PlayerShip : null);
            Log.WarnOnce("resources unattributed", "A resource change did not say which ship it is for; it goes to " + (slot == 1 ? "ship 2" : "ship 1") + ".");
            return slot >= 0 && slot <= 1 ? slot : 0;
        }

        [GamePatch(Feature, "GameSessionSuppliesManager", "ModifyAlloyCount", PatchKind.Prefix)]
        private static bool ModifyAlloyPrefix(GameSessionSuppliesManager __instance, int alloyAddition, ResourceChangeAlloy resourceChangeAlloy, GUIDUnion itemGuid)
        {
            if (!Active) return true;
            if (!__instance.photonView.IsMine) return false;
            int slot = AttributedSlot();
            Send(__instance, slot, true, Alloy[slot] + alloyAddition, true, (byte)resourceChangeAlloy, itemGuid);
            return false;
        }

        [GamePatch(Feature, "GameSessionSuppliesManager", "ModifyBiomassCount", PatchKind.Prefix)]
        private static bool ModifyBiomassPrefix(GameSessionSuppliesManager __instance, int biomassAddition, ResourceChangeBiomass resourceChangeBiomass, GUIDUnion itemGuid)
        {
            if (!Active) return true;
            if (!__instance.photonView.IsMine) return false;
            int slot = AttributedSlot();
            Send(__instance, slot, false, Biomass[slot] + biomassAddition, true, (byte)resourceChangeBiomass, itemGuid);
            return false;
        }

        /// <summary>Mission start and the quest "set resources" action: both ships. Debug tools: the ship shown here.</summary>
        [GamePatch(Feature, "GameSessionSuppliesManager", "SetAlloyAmountNoNotification", PatchKind.Prefix)]
        private static bool SetAlloyPrefix(GameSessionSuppliesManager __instance, int alloyAmount, ResourceChangeAlloy changeCause)
        {
            if (!Active) return true;
            if (!__instance.photonView.IsMine) return false;
            for (int slot = 0; slot < 2; slot++)
            {
                if (changeCause == ResourceChangeAlloy.DEBUG && slot != Shown()) continue;
                int amount = slot == 1 ? ShipBStartAlloys(__instance, alloyAmount, changeCause) : alloyAmount;
                Send(__instance, slot, true, amount, false, (byte)changeCause, GUIDUnion.Empty());
            }
            return false;
        }

        /// <summary>
        /// Ship 2's amount when the run starts: the same as ship 1 when it is a copy of ship 1; with a loadout of its own, its
        /// CUSTOM starting alloys (Ship Customizer), or else the game's own amount (not ship 1's CUSTOM amount).
        /// </summary>
        private static int ShipBStartAlloys(GameSessionSuppliesManager manager, int sessionAmount, ResourceChangeAlloy changeCause)
        {
            if (changeCause != ResourceChangeAlloy.GAME_SESSION_PROGRESS_TRACKER_START && changeCause != ResourceChangeAlloy.GAME_STATE_INGAME_ENTER) return sessionAmount;
            FleetPlan plan = FleetSession.Current;
            if (plan == null || plan.ShipBAlloys == -2) return sessionAmount;
            int amount = plan.ShipBAlloys >= 0 ? plan.ShipBAlloys : manager.GetTotalPlayerAlloyBonus();
            Log.Info("Ship 2 starts with " + amount + " alloys (" + (plan.ShipBAlloys >= 0 ? "its CUSTOM loadout's" : "the game's own amount") + "); ship 1 with " + sessionAmount + ".");
            return amount;
        }

        [GamePatch(Feature, "GameSessionSuppliesManager", "SetBiomassAmountNoNotification", PatchKind.Prefix)]
        private static bool SetBiomassPrefix(GameSessionSuppliesManager __instance, int biomassAmount, ResourceChangeBiomass changeCause)
        {
            if (!Active) return true;
            if (!__instance.photonView.IsMine) return false;
            for (int slot = 0; slot < 2; slot++)
            {
                if (changeCause == ResourceChangeBiomass.DEBUG && slot != Shown()) continue;
                Send(__instance, slot, false, biomassAmount, false, (byte)changeCause, GUIDUnion.Empty());
            }
            return false;
        }

        /// <summary>Host: a player joining gets both ships' counts (the game has every client send its one count).</summary>
        [GamePatch(Feature, "GameSessionSuppliesManager", "OnPlayerEnteredRoom", PatchKind.Prefix)]
        private static bool PlayerEnteredPrefix(GameSessionSuppliesManager __instance, Photon.Realtime.Player newPlayer)
        {
            if (!Active) return true;
            if (!__instance.photonView.IsMine) return false;
            for (int slot = 0; slot < 2; slot++)
            {
                if (HasAlloy[slot])
                    __instance.photonView.RPC("NotifyAlloyChange", newPlayer, Alloy[slot], false, (byte)ResourceChangeAlloy.PLAYER_JOINED, Tagged(GUIDUnion.Empty(), slot));
                if (HasBiomass[slot])
                    __instance.photonView.RPC("NotifyBiomassChange", newPlayer, Biomass[slot], false, (byte)ResourceChangeBiomass.PLAYER_JOINED, Tagged(GUIDUnion.Empty(), slot));
            }
            return false;
        }

        // ---------------------------------------------------------------- every client receives a ship's count

        [GamePatch(Feature, "GameSessionSuppliesManager", "NotifyAlloyChange", PatchKind.Prefix)]
        private static bool NotifyAlloyPrefix(GameSessionSuppliesManager __instance, int newAlloyAmount, ref int[] itemGuidArray)
        {
            int slot;
            if (!Untag(ref itemGuidArray, out slot)) return true;
            Alloy[slot] = newAlloyAmount;
            HasAlloy[slot] = true;
            if (slot == Shown()) return true;
            // The other ship's count: only its own fabricator terminals re-read their count.
            try
            {
                if (ClientGame.Current != null) ClientGame.Current.ModelEventBus.OnAlloysChangedSilent.Publish(__instance.AlloyAmount);
            }
            catch (Exception e)
            {
                Log.Failure("Refreshing the other ship's fabricator", e);
            }
            return false;
        }

        [GamePatch(Feature, "GameSessionSuppliesManager", "NotifyBiomassChange", PatchKind.Prefix)]
        private static bool NotifyBiomassPrefix(int newBiomassAmount, ref int[] itemGuidArray)
        {
            int slot;
            if (!Untag(ref itemGuidArray, out slot)) return true;
            Biomass[slot] = Mathf.Max(0, newBiomassAmount);
            HasBiomass[slot] = true;
            return slot == Shown();
        }

        // ---------------------------------------------------------------- the game's count follows this screen's ship

        private static void Show(GameSessionSuppliesManager manager, int slot)
        {
            _shown = slot;
            if (manager == null) return;
            var bus = ClientGame.Current != null ? ClientGame.Current.ModelEventBus : null;
            if (HasAlloy[slot] && AlloyField != null)
            {
                AlloyField.SetValue(manager, Alloy[slot]);
                if (bus != null) bus.OnAlloysChangedSilent.Publish(Alloy[slot]);
            }
            if (HasBiomass[slot] && BiomassField != null)
            {
                BiomassField.SetValue(manager, Biomass[slot]);
                if (bus != null) bus.OnBiomassChangedSilent.Publish(Biomass[slot]);
            }
        }

        /// <summary>Every frame: the shown count follows this screen's ship; with one ship left, back to the game's count.</summary>
        public static void Frame()
        {
            GameSessionSuppliesManager manager = GameSessionSuppliesManager.Instance;
            if (manager == null) return;
            if (Active)
            {
                _wasActive = true;
                int slot = Desired();
                if (slot != _shown) Show(manager, slot);
                return;
            }
            if (!_wasActive) return;
            _wasActive = false;
            if (_survivor >= 0)
            {
                Show(manager, _survivor);
                if (manager.photonView.IsMine)
                {
                    // Now with one ship these go through the game's own code, untagged.
                    manager.SetAlloyAmountNoNotification(Alloy[_survivor], ResourceChangeAlloy.GAME_SESSION_PROGRESS_TRACKER_START);
                    manager.SetBiomassAmountNoNotification(Biomass[_survivor], ResourceChangeBiomass.GAMESESSION_SETUP);
                }
            }
            Reset();
        }

        /// <summary>A ship is lost: its resources go with it, and the survivor's count is everyone's from now on.</summary>
        public static void OnShipLost(PlayerControlledShip ship)
        {
            if (!Active) return;
            int dead = FleetShips.Slot(ship);
            if (dead < 0 || dead > 1) return;
            _survivor = 1 - dead;
            _pinned = _survivor;
        }

        [GamePatch(Feature, "GameSessionSuppliesManager", "Awake", PatchKind.Postfix)]
        private static void ManagerAwakePostfix()
        {
            Reset();
            _hostStart = null;
        }

        private static void Reset()
        {
            for (int i = 0; i < 2; i++)
            {
                Alloy[i] = 0;
                Biomass[i] = 0;
                HasAlloy[i] = false;
                HasBiomass[i] = false;
            }
            Attributed.Clear();
            _shown = -1;
            _pinned = -1;
            _survivor = -1;
            _wasActive = false;
        }

        // ---------------------------------------------------------------- fabricator terminals show their own ship's count

        [GamePatch(Feature, "FabricationTab", "UpdateAlloyLabel", PatchKind.Prefix)]
        private static void FabricationLabelPrefix(FabricatorActionTab __instance, ref int obj)
        {
            if (Active) obj = AlloyFor(GameSessionSuppliesManager.Instance, __instance);
        }

        [GamePatch(Feature, "UpgradeTab", "OnAlloysUpdated", PatchKind.Prefix)]
        private static void UpgradeLabelPrefix(FabricatorActionTab __instance, ref int alloys)
        {
            if (Active) alloys = AlloyFor(GameSessionSuppliesManager.Instance, __instance);
        }

        // ---------------------------------------------------------------- blueprints are learned per ship

        /// <summary>"Already learned?" is checked against this fabricator's own blueprints (the game asks "the" ship's).</summary>
        [GamePatch(Feature, "CG.Ship.Modules.FabricatorModule", "TryAddItemToSharedUnlockPool", PatchKind.Prefix)]
        private static void LearnPrefix(Component __instance)
        {
            ShipRedirect.BeginOverride(FleetShips.Active ? FleetShips.OwnerOf(__instance) : null);
        }

        [GamePatch(Feature, "CG.Ship.Modules.FabricatorModule", "TryAddItemToSharedUnlockPool", PatchKind.Finalizer)]
        private static void LearnFinalizer()
        {
            ShipRedirect.EndOverride();
        }

        /// <summary>Each crew's perk blueprints go to their own ship's fabricator.</summary>
        [GamePatch(Feature, "CG.Ship.Modules.FabricatorModule", "TryAddPlayerPerkUnlocksToSharedUnlocks", PatchKind.Prefix)]
        private static bool PerkUnlocksPrefix(Component __instance, CG.Game.Player.Player playerCharacter)
        {
            if (!FleetShips.Active || playerCharacter == null) return true;
            int crew = FleetShips.CrewSlot(playerCharacter.ActorId);
            return crew < 0 || crew == FleetShips.Slot(FleetShips.OwnerOf(__instance));
        }

        [GamePatch(Feature, "CG.Ship.Modules.FabricatorModule", "ItemAddedToSharedUnlockPool", PatchKind.Prefix)]
        private static void LearnedPrefix(Component __instance)
        {
            ShipEvents.BeginFor(__instance);
        }

        [GamePatch(Feature, "CG.Ship.Modules.FabricatorModule", "ItemAddedToSharedUnlockPool", PatchKind.Finalizer)]
        private static void LearnedFinalizer()
        {
            ShipEvents.End();
        }

        /// <summary>"New blueprint" only for the ship the local player belongs to.</summary>
        [GamePatch(Feature, "CG.Ship.Status.SharedUnlockPoolEvent", "ItemAddedToSharedUnlockPoolFired", PatchKind.Prefix)]
        private static bool LearnedEventPrefix()
        {
            return !ShipEvents.ForeignToLocal();
        }

        // ---------------------------------------------------------------- start values and the end-of-mission tally

        /// <summary>Host: each ship's starting value (what its modules and items would recycle for), for the tally.</summary>
        [GamePatch(Feature, "GameSessionSuppliesManager", "Start", PatchKind.Postfix)]
        private static void ManagerStartPostfix(GameSessionSuppliesManager __instance)
        {
            try
            {
                if (GameSessionManager.InHub || !PhotonNetwork.IsMasterClient || !Active || PhotonNetwork.CurrentRoom == null) return;
                var start = new int[2];
                for (int slot = 0; slot < 2; slot++)
                {
                    PlayerControlledShip ship = FleetShips.BySlot(slot);
                    if (ship == null) continue;
                    ShipRedirect.BeginOverride(ship);
                    try
                    {
                        start[slot] = __instance.GetPlayerShipRecycledAlloyValue();
                    }
                    finally
                    {
                        ShipRedirect.EndOverride();
                    }
                }
                _hostStart = start;
                PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyStart, start } });
                Log.Info("Each ship's starting value: ship 1 " + start[0] + ", ship 2 " + start[1] + " alloys.");
            }
            catch (Exception e)
            {
                Log.Failure("Working out each ship's starting value", e);
            }
        }

        private static int[] StartValues()
        {
            if (_hostStart != null && PhotonNetwork.IsMasterClient) return _hostStart;
            object value;
            if (PhotonNetwork.CurrentRoom == null || !PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(KeyStart, out value)) return null;
            return value as int[];
        }

        [GamePatch(Feature, "GameSessionSuppliesManager", "GetPlayerShipStartingAlloyValue", PatchKind.Postfix)]
        private static void StartingValuePostfix(ref int __result)
        {
            if (FleetSession.ShipCount < 2) return;
            int[] start = StartValues();
            int slot = FleetShips.Slot(ClientGame.Current != null ? ClientGame.Current.PlayerShip : null);
            if (start != null && slot >= 0 && slot < start.Length) __result = start[slot];
        }

        /// <summary>The tally counts the player's own crew's ship (in PvP their team's).</summary>
        [GamePatch(Feature, "CG.GameLoopStateMachine.GameStates.GSIngame", "OnGameEnded", PatchKind.Prefix)]
        private static void GameEndedPrefix(ClientGameState state)
        {
            PlayerControlledShip own = null;
            try
            {
                if (state == ClientGameState.Ended && Active)
                {
                    int slot = FleetShips.CrewSlot(FleetShips.LocalActor);
                    if (slot < 0) slot = FleetShips.Slot(FleetShips.ViewShip);
                    if (slot >= 0 && slot <= 1)
                    {
                        _pinned = slot;
                        Show(GameSessionSuppliesManager.Instance, slot);
                        own = FleetShips.BySlot(slot);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Failure("Tallying the player's own ship", e);
            }
            ShipRedirect.BeginOverride(own);
        }

        [GamePatch(Feature, "CG.GameLoopStateMachine.GameStates.GSIngame", "OnGameEnded", PatchKind.Finalizer)]
        private static void GameEndedFinalizer()
        {
            ShipRedirect.EndOverride();
        }

        /// <summary>Host code that gives a ship resources (e.g. a PvP objective).</summary>
        public static void Give(PlayerControlledShip ship, int alloys, int biomass)
        {
            GameSessionSuppliesManager manager = GameSessionSuppliesManager.Instance;
            if (manager == null || !manager.photonView.IsMine) return;
            if (!Active)
            {
                if (alloys != 0) manager.ModifyAlloyCount(alloys, ResourceChangeAlloy.QUEST, GUIDUnion.Empty());
                if (biomass != 0) manager.ModifyBiomassCount(biomass, ResourceChangeBiomass.QUEST, GUIDUnion.Empty());
                return;
            }
            int slot = FleetShips.Slot(ship);
            if (slot < 0 || slot > 1) return;
            if (alloys != 0) Send(manager, slot, true, Alloy[slot] + alloys, true, (byte)ResourceChangeAlloy.QUEST, GUIDUnion.Empty());
            if (biomass != 0) Send(manager, slot, false, Biomass[slot] + biomass, true, (byte)ResourceChangeBiomass.QUEST, GUIDUnion.Empty());
        }
    }
}
