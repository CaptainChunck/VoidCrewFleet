using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CG.Client.UserData;
using CG.Game;
using CG.Game.Missions;
using CG.Objects;
using CG.Ship.Modules;
using CG.Space;
using Gameplay.CompositeWeapons;
using Gameplay.Factory;
using Gameplay.Utilities;
using HarmonyLib;
using Photon.Pun;
using ResourceAssets;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace VoidCrewFleet
{
    /// <summary>
    /// Each player ship has its own alloys and biomass (the game keeps one count for the room, used by every fabricator,
    /// bioport and clone tube). With several ships:
    /// - the host keeps one count per ship and sends each change through the game's own NotifyAlloyChange /
    ///   NotifyBiomassChange messages, with the ship appended to the item GUID array (the game reads only its first four
    ///   numbers), so every client knows every ship's count, late joiners included;
    /// - fabricators, recyclers, fabricator upgrades, bioports and clone tubes check and spend their own ship's count, and
    ///   learn blueprints for their own ship;
    /// - the game's own count (HUD, ship voice lines, ship status) is the count of the ship the local player belongs to
    ///   right now (FleetShips.ViewShip), switched silently when that changes; fabricator terminals always show their own
    ///   ship's count, and only refresh when it changes, and the clone tube screen the count of the ship whose tube the
    ///   local player is in (CloneTubeScreen);
    /// - items' "per N alloys" and "more / fewer than N alloys (biomass)" rules count their own ship's resources
    ///   (ShipStatRules says which ship a rule is for), and re-check when that ship's count changes;
    /// - the game's starting bonus (biomass for every player in the room, alloys by every player's rank) is shared out: each
    ///   ship starts with its own crew's (players not on a ship yet are shared out evenly), except that a ship 2-6 with a
    ///   CUSTOM loadout of its own starts with that loadout's alloys; the end-of-mission tally counts the player's own ship.
    /// With one ship nothing is tagged or redirected.
    /// </summary>
    internal static class ShipResources
    {
        public const string Feature = "Per-ship resources";

        private const int Tag = 0x56434652; // "VCFR"
        private const string KeyStart = "VCF_ResStart";

        private const int Slots = FleetSlots.MaxSlots;

        private static readonly int[] Alloy = new int[Slots];
        private static readonly int[] Biomass = new int[Slots];
        private static readonly bool[] HasAlloy = new bool[Slots];
        private static readonly bool[] HasBiomass = new bool[Slots];
        private static readonly List<int> Attributed = new List<int>();
        private static int[] _hostStart;
        private static int _shown = -1;
        private static int _pinned = -1;
        private static int _survivor = -1;
        private static bool _wasActive;

        /// <summary>The ship whose results the end-of-mission tally is working out right now, or -1.</summary>
        private static int _tallySlot = -1;

        private static readonly FieldInfo AlloyField = GameMember.Field(typeof(GameSessionSuppliesManager), "_alloyAmount");
        private static readonly FieldInfo BiomassField = GameMember.Field(typeof(GameSessionSuppliesManager), "_biomassAmount");

        // Read for every shop item a terminal lists and every occupied clone tube each frame: direct readers, not reflection.
        private static readonly AccessTools.FieldRef<FabricatorActionTab, FabricatorData> TabData =
            InstanceRef<FabricatorActionTab, FabricatorData>(GameMember.Field(typeof(FabricatorActionTab), "data"));
        private static readonly AccessTools.FieldRef<FabricatorData, CarryableFactoryLogic> DataLogic =
            InstanceRef<FabricatorData, CarryableFactoryLogic>(GameMember.Field(typeof(FabricatorData), "logic"));
        private static readonly AccessTools.FieldRef<CloneTubeState, CloneTube> StateTube =
            InstanceRef<CloneTubeState, CloneTube>(GameMember.Field(typeof(CloneTubeState), "CloneTube"));

        private static readonly MethodInfo GetAlloy = Getter(GameMember.Property(typeof(GameSessionSuppliesManager), "AlloyAmount"));
        private static readonly MethodInfo GetBiomass = Getter(GameMember.Property(typeof(GameSessionSuppliesManager), "BiomassAmount"));
        private static readonly MethodInfo ModAlloy = GameMember.Method(typeof(GameSessionSuppliesManager), "ModifyAlloyCount");
        private static readonly MethodInfo ModBiomass = GameMember.Method(typeof(GameSessionSuppliesManager), "ModifyBiomassCount");
        private static readonly MethodInfo PlayerBiomass = GameMember.Method(typeof(GameSessionSuppliesManager), "PlayerBiomassBonus");
        private static readonly MethodInfo AlloyForMethod = AccessTools.Method(typeof(ShipResources), nameof(AlloyFor));
        private static readonly MethodInfo BiomassForMethod = AccessTools.Method(typeof(ShipResources), nameof(BiomassFor));
        private static readonly MethodInfo ModifyAlloyForMethod = AccessTools.Method(typeof(ShipResources), nameof(ModifyAlloyFor));
        private static readonly MethodInfo ModifyBiomassForMethod = AccessTools.Method(typeof(ShipResources), nameof(ModifyBiomassFor));
        private static readonly Action<ModDynamicCondition> RuleCheck =
            Bind<Action<ModDynamicCondition>>(GameMember.Method(typeof(ModDynamicCondition), "CheckIfActive"));
        private static readonly Action<ModValueResourceCount, int> RuleAmountChanged =
            Bind<Action<ModValueResourceCount, int>>(GameMember.Method(typeof(ModValueResourceCount), "OnResourceChanged"));

        /// <summary>A direct reader of a game's instance field; null when the field is missing or has changed.</summary>
        private static AccessTools.FieldRef<T, F> InstanceRef<T, F>(FieldInfo field)
        {
            if (field == null) return null;
            try
            {
                return AccessTools.FieldRefAccess<T, F>(field);
            }
            catch (Exception e)
            {
                Log.Warn("Game field " + field.DeclaringType.Name + "." + field.Name + " can't be read directly (" + e.Message + "); the game may have changed.");
                return null;
            }
        }

        /// <summary>A fast call to a game method (the instance is the first argument); null when it is missing or has changed.</summary>
        private static T Bind<T>(MethodInfo method) where T : class
        {
            if (method == null) return null;
            try
            {
                return Delegate.CreateDelegate(typeof(T), method, false) as T;
            }
            catch
            {
                return null;
            }
        }

        private static MethodInfo Getter(PropertyInfo property)
        {
            return property != null ? property.GetGetMethod(true) : null;
        }

        /// <summary>An item rule that counts a ship's alloys or biomass.</summary>
        private sealed class ResourceRule
        {
            public WeakReference Rule;
            public PlayerControlledShip Ship;
            public bool Biomass;
        }

        private static readonly List<ResourceRule> Rules = new List<ResourceRule>();

        public static bool Active
        {
            get { return FleetShips.Active && Patcher.IsReady(Feature); }
        }

        /// <summary>For reports: "ship 1 120 alloys / 50 biomass, ship 2 ...".</summary>
        public static string Describe()
        {
            var text = new System.Text.StringBuilder();
            for (int slot = 0; slot < ShipSlots; slot++)
                text.Append("ship ").Append(slot + 1).Append(' ').Append(Alloy[slot]).Append(" alloys / ").Append(Biomass[slot]).Append(" biomass, ");
            return text.Append("shown ").Append(_shown + 1).ToString();
        }

        /// <summary>How many ships this mission has counts for.</summary>
        private static int ShipSlots
        {
            get { return Mathf.Clamp(Mathf.Max(FleetShips.PublishedCount, FleetSession.ShipCount), 1, Slots); }
        }

        private static bool Valid(int slot)
        {
            return slot >= 0 && slot < Slots;
        }

        // ---------------------------------------------------------------- which ship a piece of code is for

        private static int SlotOf(object context)
        {
            var tab = context as FabricatorActionTab;
            if (tab != null) context = TabData != null ? TabData(tab) : null;
            var data = context as FabricatorData;
            if (data != null) context = DataLogic != null ? DataLogic(data) : null;
            var state = context as CloneTubeState;
            if (state != null) context = StateTube != null ? StateTube(state) : null;
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
            return Valid(slot) ? slot : 0;
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
            return Valid(slot) && HasAlloy[slot] ? Alloy[slot] : manager.AlloyAmount;
        }

        public static int BiomassFor(GameSessionSuppliesManager manager, object context)
        {
            if (!Active) return manager.BiomassAmount;
            int slot = SlotOf(context);
            return Valid(slot) && HasBiomass[slot] ? Biomass[slot] : manager.BiomassAmount;
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
            if (a == null || a.Length != 6 || a[4] != Tag || !Valid(a[5])) return false;
            slot = a[5];
            guid = new[] { a[0], a[1], a[2], a[3] };
            return true;
        }

        /// <summary>Host: a ship's new count, through the game's own message (it also runs here at once and is stored).</summary>
        private static void Send(GameSessionSuppliesManager manager, int slot, bool alloy, int value, bool voice, byte cause, GUIDUnion guid)
        {
            if (!Valid(slot)) return;
            if (!alloy) value = Mathf.Max(0, value);
            manager.photonView.RPC(alloy ? "NotifyAlloyChange" : "NotifyBiomassChange", RpcTarget.All, value, voice, cause, Tagged(guid, slot));
        }

        private static int AttributedSlot()
        {
            int slot = Attributed.Count > 0 ? Attributed[Attributed.Count - 1] : -1;
            if (Valid(slot)) return slot;
            slot = FleetShips.Slot(ClientGame.Current != null ? ClientGame.Current.PlayerShip : null);
            if (!Valid(slot)) slot = 0;
            Log.WarnOnce("resources unattributed", "A resource change did not say which ship it is for; it goes to ship " + (slot + 1) + ".");
            return slot;
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

        /// <summary>Mission start and the quest "set resources" action: every ship. Debug tools: the ship shown here.</summary>
        [GamePatch(Feature, "GameSessionSuppliesManager", "SetAlloyAmountNoNotification", PatchKind.Prefix)]
        private static bool SetAlloyPrefix(GameSessionSuppliesManager __instance, int alloyAmount, ResourceChangeAlloy changeCause)
        {
            if (!Active) return true;
            if (!__instance.photonView.IsMine) return false;
            int[] shares = null;
            for (int slot = 0; slot < ShipSlots; slot++)
            {
                if (changeCause == ResourceChangeAlloy.DEBUG && slot != Shown()) continue;
                int amount = StartAlloys(__instance, slot, alloyAmount, changeCause, ref shares);
                Send(__instance, slot, true, amount, false, (byte)changeCause, GUIDUnion.Empty());
            }
            return false;
        }

        /// <summary>
        /// A ship's amount when the run starts. The game's own starting amount (every player's rank bonus, for a new run) is
        /// shared out by crew. A ship 2-6 with a loadout of its own starts with its CUSTOM starting alloys (Ship Customizer),
        /// or else its crew's bonus (not ship 1's CUSTOM amount); a copy of ship 1 starts as ship 1 does.
        /// </summary>
        private static int StartAlloys(GameSessionSuppliesManager manager, int slot, int sessionAmount, ResourceChangeAlloy changeCause, ref int[] shares)
        {
            bool bonus = changeCause == ResourceChangeAlloy.GAME_STATE_INGAME_ENTER;
            if (!bonus && changeCause != ResourceChangeAlloy.GAME_SESSION_PROGRESS_TRACKER_START) return sessionAmount;
            FleetPlan plan = FleetSession.Current;
            ShipPlan ship = slot > 0 && plan != null ? plan.Ship(slot) : null;
            int own = ship != null ? ship.StartAlloys : -2;
            if (own >= 0)
            {
                Log.Info("Ship " + (slot + 1) + " starts with " + own + " alloys (its CUSTOM loadout's).");
                return own;
            }
            // Ship 1, or a copy of it, in a run with an amount of its own (ship 1's CUSTOM loadout's).
            if (own == -2 && !bonus) return sessionAmount;
            if (shares == null)
            {
                int roomBonus = bonus ? sessionAmount : manager.GetTotalPlayerAlloyBonus();
                shares = BonusShares(manager, true, roomBonus);
                LogShares(true, roomBonus, shares);
            }
            return slot < shares.Length ? shares[slot] : 0;
        }

        [GamePatch(Feature, "GameSessionSuppliesManager", "SetBiomassAmountNoNotification", PatchKind.Prefix)]
        private static bool SetBiomassPrefix(GameSessionSuppliesManager __instance, int biomassAmount, ResourceChangeBiomass changeCause)
        {
            if (!Active) return true;
            if (!__instance.photonView.IsMine) return false;
            // A new run's starting biomass is the game's bonus for every player in the room: each ship gets its crew's.
            int[] shares = null;
            if (changeCause == ResourceChangeBiomass.GAMESESSION_SETUP && NewRunBiomass())
            {
                shares = BonusShares(__instance, false, biomassAmount);
                LogShares(false, biomassAmount, shares);
            }
            for (int slot = 0; slot < ShipSlots; slot++)
            {
                if (changeCause == ResourceChangeBiomass.DEBUG && slot != Shown()) continue;
                int amount = shares != null && slot < shares.Length ? shares[slot] : biomassAmount;
                Send(__instance, slot, false, amount, false, (byte)changeCause, GUIDUnion.Empty());
            }
            return false;
        }

        /// <summary>Whether the run starts with the game's bonus biomass (a new run), not with an amount of its own (a saved run's).</summary>
        private static bool NewRunBiomass()
        {
            GameSession session = GameSessionManager.ActiveSession;
            return session != null && session.InitialBiomass == -1;
        }

        // ---------------------------------------------------------------- the starting bonus is shared out by crew

        /// <summary>
        /// Each ship's share of a starting bonus the game gives once for the whole room (biomass for every player, alloys by
        /// every player's rank) and would give every ship in full: its own crew's bonus, from the crews the host has placed
        /// (as the mission loads, ShipSpawn). The bonus of players not on a ship yet (Fleet Battle teams still to be picked)
        /// is shared out evenly, and so is all of it when the players' bonuses don't add up to the game's (a game update
        /// counting it differently).
        /// </summary>
        private static int[] BonusShares(GameSessionSuppliesManager manager, bool alloy, int roomBonus)
        {
            int ships = ShipSlots;
            var shares = new int[ships];
            int placed = 0;
            try
            {
                Dictionary<int, CrewEntry> crews = FleetShips.Crews();
                int total = 0;
                foreach (Photon.Realtime.Player player in PhotonNetwork.PlayerList)
                {
                    int bonus = alloy ? AlloyBonusOf(player) : BiomassBonusOf(manager, player);
                    total += bonus;
                    CrewEntry entry;
                    if (!crews.TryGetValue(player.ActorNumber, out entry) || entry.State != CrewState.Playing || entry.Slot < 0 || entry.Slot >= ships) continue;
                    shares[entry.Slot] += bonus;
                    placed += bonus;
                }
                if (total != roomBonus)
                {
                    Log.WarnOnce("starting bonus " + alloy, "The game's starting " + (alloy ? "alloys" : "biomass") + " (" + roomBonus + ") is not the sum of the " +
                                                          "players' (" + total + "); every ship gets an equal part. The game may have changed.");
                    Array.Clear(shares, 0, ships);
                    placed = 0;
                }
            }
            catch (Exception e)
            {
                Log.Failure("Sharing out the starting " + (alloy ? "alloys" : "biomass") + " by crew", e);
                Array.Clear(shares, 0, ships);
                placed = 0;
            }
            int rest = Mathf.Max(0, roomBonus - placed);
            for (int slot = 0; slot < ships; slot++) shares[slot] += rest / ships + (slot < rest % ships ? 1 : 0);
            return shares;
        }

        /// <summary>For the log: "Starting biomass by crew (100 for the room): ship 1 50, ship 2 50."</summary>
        private static void LogShares(bool alloy, int roomBonus, int[] shares)
        {
            var text = new System.Text.StringBuilder();
            for (int slot = 0; slot < shares.Length; slot++) text.Append(slot > 0 ? ", " : "").Append("ship ").Append(slot + 1).Append(' ').Append(shares[slot]);
            Log.Info("Starting " + (alloy ? "alloys" : "biomass") + " by crew (" + roomBonus + " for the room): " + text + ".");
        }

        /// <summary>A player's starting alloys: the alloy rewards of their rank, as the game counts them for the room.</summary>
        private static int AlloyBonusOf(Photon.Realtime.Player player)
        {
            object rank;
            if (player == null || player.CustomProperties == null || !player.CustomProperties.TryGetValue("RP_PR", out rank)) return 0;
            int bonus = 0;
            foreach (UnlockItemDef item in ResourceAssetContainer<UnlockContainer, UnityEngine.Object, UnlockItemDef>.Instance.GetUnlocksInCategory(
                         ResourceAssetContainer<AlloyRewardContainer, AlloyReward, AlloyRewardDef>.Instance, 0, (int)rank, UnlockCriteriaType.PlayerRank))
            {
                bonus += ((AlloyReward)item.Asset).Alloys;
            }
            return bonus;
        }

        /// <summary>A player's starting biomass, by the game's own rule for one player; 0 when that can't be read.</summary>
        private static int BiomassBonusOf(GameSessionSuppliesManager manager, Photon.Realtime.Player player)
        {
            if (PlayerBiomass == null) return 0;
            object bonus = PlayerBiomass.Invoke(manager, new object[] { player });
            return bonus is int ? (int)bonus : 0;
        }

        /// <summary>Host: a player joining gets both ships' counts (the game has every client send its one count).</summary>
        [GamePatch(Feature, "GameSessionSuppliesManager", "OnPlayerEnteredRoom", PatchKind.Prefix)]
        private static bool PlayerEnteredPrefix(GameSessionSuppliesManager __instance, Photon.Realtime.Player newPlayer)
        {
            if (!Active) return true;
            if (!__instance.photonView.IsMine) return false;
            for (int slot = 0; slot < Slots; slot++)
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
            if (!Untag(ref itemGuidArray, out slot))
            {
                KeepSurvivorCount(true, newAlloyAmount);
                return true;
            }
            Alloy[slot] = newAlloyAmount;
            HasAlloy[slot] = true;
            RefreshRules(slot, false);
            if (slot == Shown()) return true;
            // Another ship's count: every fabricator terminal hears of it, and only that ship's own refresh (TabChanged).
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
            if (!Untag(ref itemGuidArray, out slot))
            {
                KeepSurvivorCount(false, newBiomassAmount);
                return true;
            }
            Biomass[slot] = Mathf.Max(0, newBiomassAmount);
            HasBiomass[slot] = true;
            RefreshRules(slot, true);
            return slot == Shown();
        }

        /// <summary>
        /// A count without a ship while this client still counts per ship: the host lost the other ship first and counts
        /// with one ship already, so it is the remaining ship's count. It is kept for that ship too, so switching back to
        /// the game's own count can't bring an older one back.
        /// </summary>
        private static void KeepSurvivorCount(bool alloy, int value)
        {
            if (!_wasActive) return;
            int slot = _survivor;
            if (slot < 0)
            {
                List<int> alive = FleetShips.AliveSlots();
                slot = alive.Count == 1 ? alive[0] : -1;
            }
            if (!Valid(slot)) return;
            if (alloy)
            {
                Alloy[slot] = value;
                HasAlloy[slot] = true;
            }
            else
            {
                Biomass[slot] = Mathf.Max(0, value);
                HasBiomass[slot] = true;
            }
            RefreshRules(slot, !alloy);
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
            if (_survivor < 0)
            {
                // The ship that is left: its count is everyone's from now on.
                IList<PlayerControlledShip> left = FleetShips.All;
                int slot = left.Count == 1 ? FleetShips.Slot(left[0]) : -1;
                if (Valid(slot)) _survivor = slot;
            }
            if (_survivor >= 0)
            {
                // Already shown (OnShipLost pinned it): the game's count is that ship's latest, the host's counts without
                // a ship included, so it stays as it is.
                if (_shown != _survivor) Show(manager, _survivor);
                if (manager.photonView.IsMine)
                {
                    // Now with one ship these go through the game's own code, untagged.
                    manager.SetAlloyAmountNoNotification(Alloy[_survivor], ResourceChangeAlloy.GAME_SESSION_PROGRESS_TRACKER_START);
                    manager.SetBiomassAmountNoNotification(Biomass[_survivor], ResourceChangeBiomass.GAMESESSION_SETUP);
                }
            }
            Reset();
        }

        /// <summary>
        /// A ship is lost: its resources go with it. When one ship is left, its count is everyone's from now on; with more
        /// left, each player's count follows the ship they are on.
        /// </summary>
        public static void OnShipLost(PlayerControlledShip ship)
        {
            if (!Active) return;
            int dead = FleetShips.Slot(ship);
            if (!Valid(dead)) return;
            List<int> alive = FleetShips.AliveSlots();
            alive.Remove(dead);
            if (alive.Count != 1) return;
            _survivor = alive[0];
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
            for (int i = 0; i < Slots; i++)
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

        /// <summary>What a fabricator terminal's tab shows now (its own ship's count; an upgrade tab, with the upgrade's cost).</summary>
        private sealed class TabShown
        {
            public long State;
        }

        private static readonly ConditionalWeakTable<FabricatorActionTab, TabShown> TabsShown = new ConditionalWeakTable<FabricatorActionTab, TabShown>();

        /// <summary>
        /// Whether a tab has something new to show. The game's "alloys changed" reaches every terminal of every ship, any
        /// ship's change (and a fabrication tab rebuilds its whole shop list for it); a tab whose own ship's count is what it
        /// already shows skips it.
        /// </summary>
        private static bool TabChanged(FabricatorActionTab tab, long state)
        {
            TabShown shown;
            if (!TabsShown.TryGetValue(tab, out shown))
            {
                TabsShown.Add(tab, new TabShown { State = state });
                return true;
            }
            if (shown.State == state) return false;
            shown.State = state;
            return true;
        }

        [GamePatch(Feature, "FabricationTab", "UpdateAlloyLabel", PatchKind.Prefix)]
        private static bool FabricationLabelPrefix(FabricatorActionTab __instance, ref int obj)
        {
            try
            {
                if (!Active) return true;
                obj = AlloyFor(GameSessionSuppliesManager.Instance, __instance);
                return TabChanged(__instance, obj);
            }
            catch (Exception e)
            {
                Log.Failure("Showing a fabricator terminal its own ship's alloys", e);
                return true;
            }
        }

        [GamePatch(Feature, "UpgradeTab", "OnAlloysUpdated", PatchKind.Prefix)]
        private static bool UpgradeLabelPrefix(FabricatorActionTab __instance, FabricatorModule ____module, ref int alloys)
        {
            try
            {
                if (!Active) return true;
                alloys = AlloyFor(GameSessionSuppliesManager.Instance, __instance);
                // Its button also depends on the next upgrade's cost, which an upgrade changes.
                int cost = ____module != null ? ____module.CurrentUpgradeCost : 0;
                return TabChanged(__instance, ((long)cost << 32) | (uint)alloys);
            }
            catch (Exception e)
            {
                Log.Failure("Showing a fabricator terminal its own ship's alloys", e);
                return true;
            }
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
        // The tally only changes the numbers on the results screen: a mission with several ships gives no rewards or XP
        // (ProgressionGuard blocks them).

        /// <summary>Host: each ship's starting value (what its modules and items would recycle for), for the tally.</summary>
        [GamePatch(Feature, "GameSessionSuppliesManager", "Start", PatchKind.Postfix)]
        private static void ManagerStartPostfix(GameSessionSuppliesManager __instance)
        {
            try
            {
                if (GameSessionManager.InHub || !PhotonNetwork.IsMasterClient || !Active || PhotonNetwork.CurrentRoom == null) return;
                var start = new int[ShipSlots];
                for (int slot = 0; slot < start.Length; slot++)
                {
                    PlayerControlledShip ship = FleetShips.BySlot(slot);
                    if (ship == null) continue;
                    ShipRedirect.BeginOverride(ship);
                    try
                    {
                        start[slot] = RecycledValue(__instance);
                    }
                    finally
                    {
                        ShipRedirect.EndOverride();
                    }
                }
                _hostStart = start;
                PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyStart, start } });
                Log.Info("Each ship's starting value in alloys: " + string.Join(", ", start.Select((v, i) => "ship " + (i + 1) + " " + v).ToArray()) + ".");
            }
            catch (Exception e)
            {
                Log.Failure("Working out each ship's starting value", e);
            }
        }

        /// <summary>
        /// What "the player ship" would recycle for: the game's own count, unless an item on board has no crafting data at
        /// all (another mod's part that was never made craftable, such as Ectype Skirmisher's micromissile payload). The
        /// game's count then throws, and the same count is made here with such items as 0.
        /// </summary>
        private static int RecycledValue(GameSessionSuppliesManager manager)
        {
            try
            {
                return manager.GetPlayerShipRecycledAlloyValue();
            }
            catch (NullReferenceException)
            {
                return RecycledValueSkippingUnknown();
            }
        }

        /// <summary>GameSessionSuppliesManager.GetPlayerShipRecycledAlloyValue, with items that have no crafting data counted as 0.</summary>
        private static int RecycledValueSkippingUnknown()
        {
            var crafting = ResourceAssetContainer<CraftingDataContainer, UnityEngine.Object, CraftableItemDef>.Instance;
            int total = 0;
            ClientGame game = ClientGame.Current;
            PlayerControlledShip ship = game != null ? game.PlayerShip : null;
            if (ship != null)
            {
                foreach (CellModule module in ship.GetAllModules())
                {
                    if (module == null || !module.BuildingConstraints.AllowDeconstruction || module.BuildBoxRef == null) continue;
                    CraftableItemDef def;
                    if (module is CompositeWeaponModule weapon && crafting.TryGetByGuid(weapon.CompositeDataRef.AssetGuid, out def))
                        total += def.CraftingRules.Recycle.Amount;
                    else if (crafting.TryGetByGuid(module.BuildBoxRef.AssetGuid, out def) && def.Asset != null)
                        total += def.CraftingRules.Recycle.Amount;
                }
            }
            foreach (CarryableObject item in MissionHelper.GetAllCarryablesOnShip())
            {
                CraftableItemDef def;
                if (item is CompositeWeaponBuildBox box && crafting.TryGetByGuid(box.WeaponDataRef.AssetGuid, out def))
                    total += def.CraftingRules.Recycle.Amount;
                else if (item == null)
                    total += 2; // the game's own figure for an item it can't read
                else if ((def = crafting.GetAssetDefById(item.assetGuid, false)) != null)
                    total += def.CraftingRules.Recycle.Amount;
                else
                    Log.InfoOnce("no recycle value " + item.assetGuid.AsHex(), "The end-of-mission tally counts " + item.name + " (" + item.assetGuid.AsHex() +
                        ") as 0 alloys: it has no crafting data, so it isn't one of the game's items.");
            }
            return total;
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
                    if (Valid(slot))
                    {
                        _pinned = slot;
                        _tallySlot = slot;
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
            _tallySlot = -1;
            ShipRedirect.EndOverride();
        }

        /// <summary>
        /// The tally takes off the starting bonus the player's own ship had: its crew's share, not the whole room's (worked
        /// out from the crews now, so players who left count no more).
        /// </summary>
        [GamePatch(Feature, "GameSessionSuppliesManager", "GetTotalPlayerAlloyBonus", PatchKind.Postfix, Optional = true)]
        private static void AlloyBonusPostfix(GameSessionSuppliesManager __instance, ref int __result)
        {
            if (_tallySlot >= 0) __result = TallyShare(__instance, true, __result);
        }

        [GamePatch(Feature, "GameSessionSuppliesManager", "GetTotalPlayerBiomassBonus", PatchKind.Postfix, Optional = true)]
        private static void BiomassBonusPostfix(GameSessionSuppliesManager __instance, ref int __result)
        {
            if (_tallySlot >= 0) __result = TallyShare(__instance, false, __result);
        }

        private static int TallyShare(GameSessionSuppliesManager manager, bool alloy, int roomBonus)
        {
            try
            {
                int[] shares = BonusShares(manager, alloy, roomBonus);
                return _tallySlot < shares.Length ? shares[_tallySlot] : roomBonus;
            }
            catch (Exception e)
            {
                Log.Failure("Tallying the player's own ship's starting bonus", e);
                return roomBonus;
            }
        }

        // ---------------------------------------------------------------- items' rules that count a ship's resources

        /// <summary>The count of a resource of a rule's own ship; false when there is none (one ship, or not known yet).</summary>
        private static bool OwnAmount(object rule, CG.Ship.Object.ResourceType type, out int amount)
        {
            amount = 0;
            bool biomass = type == CG.Ship.Object.ResourceType.Biomass;
            if (!biomass && type != CG.Ship.Object.ResourceType.Alloys) return false;
            if (!Active) return false;
            int slot = FleetShips.Slot(ShipStatRules.RuleShip(rule));
            if (!Valid(slot) || !(biomass ? HasBiomass[slot] : HasAlloy[slot])) return false;
            amount = biomass ? Biomass[slot] : Alloy[slot];
            return true;
        }

        /// <summary>A rule just set up counts its ship's resource (a rule set up again for another ship, that ship's only).</summary>
        private static void Register(object rule, CG.Ship.Object.ResourceType type)
        {
            bool biomass = type == CG.Ship.Object.ResourceType.Biomass;
            if (!biomass && type != CG.Ship.Object.ResourceType.Alloys) return;
            Unregister(rule);
            PlayerControlledShip ship = ShipStatRules.RuleShip(rule);
            if (ship != null) Rules.Add(new ResourceRule { Rule = new WeakReference(rule), Ship = ship, Biomass = biomass });
        }

        private static void Unregister(object rule)
        {
            for (int i = Rules.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(Rules[i].Rule.Target, rule)) Rules.RemoveAt(i);
            }
        }

        /// <summary>A rule let go of its ship (its item is gone, or it is set up again for another ship): it counts nothing more.</summary>
        [GamePatch(Feature, "Gameplay.Utilities.ResourceCountModRule", "OnDestroy", PatchKind.Postfix, Optional = true)]
        [GamePatch(Feature, "Gameplay.Utilities.ModValueResourceCount", "OnDestroy", PatchKind.Postfix, Optional = true)]
        private static void RuleDestroyPostfix(object __instance)
        {
            if (Rules.Count > 0) Unregister(__instance);
        }

        /// <summary>
        /// A ship's count changed: its items' rules for that resource check again. The game's own event only reports the
        /// count of the ship shown here.
        /// </summary>
        private static void RefreshRules(int slot, bool biomass)
        {
            if (Rules.Count == 0) return;
            int amount = biomass ? Biomass[slot] : Alloy[slot];
            for (int i = Rules.Count - 1; i >= 0; i--)
            {
                ResourceRule entry = Rules[i];
                object rule = entry.Rule.Target;
                if (rule == null || entry.Ship == null)
                {
                    // The item is gone, or its ship.
                    Rules.RemoveAt(i);
                    continue;
                }
                if (entry.Biomass != biomass || FleetShips.Slot(entry.Ship) != slot) continue;
                try
                {
                    var condition = rule as ModDynamicCondition;
                    if (condition != null)
                    {
                        if (RuleCheck != null) RuleCheck(condition);
                    }
                    else
                    {
                        var perAmount = rule as ModValueResourceCount;
                        if (perAmount != null && RuleAmountChanged != null) RuleAmountChanged(perAmount, amount);
                    }
                }
                catch (Exception e)
                {
                    Log.Failure("Updating an item rule that counts " + (biomass ? "biomass" : "alloys"), e);
                }
            }
        }

        /// <summary>"More / fewer than N alloys (biomass)": its own ship's count.</summary>
        [GamePatch(Feature, "Gameplay.Utilities.ResourceCountModRule", "ShouldApply", PatchKind.Prefix, Optional = true)]
        private static bool CountRulePrefix(ResourceCountModRule __instance, bool ___isLess, ref bool __result)
        {
            int amount;
            if (!OwnAmount(__instance, __instance.ResourceType, out amount)) return true;
            __result = ___isLess ? amount < __instance.Value : amount > __instance.Value;
            return false;
        }

        [GamePatch(Feature, "Gameplay.Utilities.ResourceCountModRule", "OnInitialize", PatchKind.Postfix, Optional = true)]
        private static void CountRuleInitPostfix(ResourceCountModRule __instance)
        {
            Register(__instance, __instance.ResourceType);
        }

        /// <summary>"Per N alloys (biomass)": starts from, and follows, its own ship's count.</summary>
        [GamePatch(Feature, "Gameplay.Utilities.ModValueResourceCount", "OnInitialize", PatchKind.Postfix, Optional = true)]
        private static void PerRuleInitPostfix(ModValueResourceCount __instance, ref int ____currentAmount)
        {
            Register(__instance, __instance.ResourceType);
            int amount;
            if (OwnAmount(__instance, __instance.ResourceType, out amount)) ____currentAmount = amount;
        }

        [GamePatch(Feature, "Gameplay.Utilities.ModValueResourceCount", "OnResourceChanged", PatchKind.Prefix, Optional = true)]
        private static void PerRuleChangedPrefix(ModValueResourceCount __instance, ref int newAmount)
        {
            int amount;
            if (OwnAmount(__instance, __instance.ResourceType, out amount)) newAmount = amount;
        }
    }
}
