using System;
using System.Collections.Generic;
using System.Reflection;
using Client.Player.Interactions;
using Gameplay.Hub;
using Gameplay.Mutators;
using Gameplay.Quests;
using ResourceAssets;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// Two more missions on the hub's galaxy map, next to the game's own:
    /// - FLEET PILGRIMAGE: the standard Pilgrimage for two ships, with more enemies and more objectives per sector;
    /// - FLEET BATTLE: PvP, one ship per team.
    /// Both are the game's standard Pilgrimage underneath (so unlocks, rewards screens and the astral map work as usual);
    /// the room's fleet choice (HubFleet) says how it is played. The game builds this list in code from a fixed set of
    /// entries, so these two are added the same way, through its own private AddGamemodeEntry.
    /// </summary>
    internal static class GalaxyMapModes
    {
        public const string Feature = "Fleet missions on the galaxy map";

        private const int FleetPilgrimageType = 100;
        private const int FleetBattleType = 101;

        private static readonly MethodInfo AddEntry = GameMember.Method(typeof(GalaxyMapUIController), "AddGamemodeEntry");
        private static readonly MethodInfo UpdateStyles = GameMember.Method(typeof(GalaxyMapUIController), "UpdateStyles");
        private static readonly MethodInfo UpdateIncludedMutators = GameMember.Method(typeof(GalaxyMapUIController), "UpdateIncludedMutators");
        private static readonly MethodInfo CheckSelected = GameMember.Method(typeof(GalaxyMapUIController), "CheckSelectedGamemode");
        private static readonly FieldInfo ShownField = GameMember.Field(typeof(GalaxyMapUIController), "_shownGameModeEntry");
        private static readonly FieldInfo ActivatedField = GameMember.Field(typeof(GalaxyMapUIController), "_activatedGameModeEntry");
        private static readonly FieldInfo BannerField = GameMember.Field(typeof(GalaxyMapUIController), "_banner");
        private static readonly FieldInfo MutatorMenuField = GameMember.Field(typeof(GalaxyMapUIController), "_mutatorsSelectionMenu");

        private static readonly FieldInfo EntryOnShown = GameMember.Field("GalaxyMapUIController+GameModeEntry", "OnShown");
        private static readonly FieldInfo EntryOnActivate = GameMember.Field("GalaxyMapUIController+GameModeEntry", "OnActivate");
        private static readonly FieldInfo EntryKind = GameMember.Field("GalaxyMapUIController+GameModeEntry", "GamemodeType");

        private static GalaxyMapUIController _controller;
        private static object _fleetEntry;
        private static object _battleEntry;
        private static ContextInfo _fleetInfo;
        private static ContextInfo _battleInfo;
        private static bool _subscribed;

        [GamePatch(Feature, "GalaxyMapUIController", "UpdateAvailableGamemodes", PatchKind.Postfix)]
        private static void AvailableModesPostfix(GalaxyMapUIController __instance)
        {
            try
            {
                if (AddEntry == null || EntryOnShown == null || EntryOnActivate == null) return;
                _controller = __instance;
                MakeInfos();
                _fleetEntry = AddEntry.Invoke(__instance, new object[] { (GalaxyMapUIController.GamemodeType)FleetPilgrimageType, _fleetInfo });
                _battleEntry = AddEntry.Invoke(__instance, new object[] { (GalaxyMapUIController.GamemodeType)FleetBattleType, _battleInfo });
                Wire(__instance, _fleetEntry, HubVariant.FleetPilgrimage);
                Wire(__instance, _battleEntry, HubVariant.FleetBattle);
                if (!_subscribed)
                {
                    HubFleet.Changed += Recheck;
                    _subscribed = true;
                }
            }
            catch (Exception e)
            {
                Log.Failure("Adding the fleet missions to the galaxy map", e);
            }
        }

        private static void MakeInfos()
        {
            if (_fleetInfo != null && _battleInfo != null) return;
            Sprite icon = null;
            try
            {
                QuestAssetDef endless = ResourceAssetContainer<QuestAssetContainer, QuestAsset, QuestAssetDef>.Instance.GetAssetDefById(DataTable<QuestTable>.Instance.EndlessQuest.AssetGuid);
                if (endless != null && endless.ContextInfo != null) icon = endless.ContextInfo.Icon;
            }
            catch (Exception e)
            {
                Log.Failure("Reading the Pilgrimage's picture", e);
            }
            _fleetInfo = ContextInfo.Create(icon, "FLEET PILGRIMAGE",
                "The Pilgrimage for <b>two or three ships</b>. Enemies come in greater numbers and every sector holds more objectives, so the crews can split " +
                "up and work on different objectives at the same time. Up to 24 players; each picks the ship they fly.\n" +
                "<i>Fleet missions give no XP or rewards (Void Crew's modding rules).</i>");
            _battleInfo = ContextInfo.Create(icon, "FLEET BATTLE",
                "<b>PvP</b>: two teams of one to three ships each, starting on opposite sides of an asteroid field full of objectives that pay out alloys for " +
                "upgrades and ammo. Pick your ship before the match; the host sets the ships, balances and confirms the teams. Board through open airlocks, " +
                "or hack one with a hacking terminal from the fabricator to open the enemy's doors for five minutes. Sabotage enemy systems at their " +
                "enhancement panels, and destroy every enemy ship to win: a ship's crew watches once it is lost.\n" +
                "<i>No XP or rewards (Void Crew's modding rules).</i>");
            _fleetInfo.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _battleInfo.hideFlags = HideFlags.DontUnloadUnusedAsset;
        }

        private static void Wire(GalaxyMapUIController controller, object entry, HubVariant variant)
        {
            EntryOnShown.SetValue(entry, (Action)(() => Shown(controller, entry)));
            EntryOnActivate.SetValue(entry, (Action)(() => Activate(variant)));
        }

        /// <summary>The game's "show the Pilgrimage" for a fleet entry: same banner and mutators, its own text.</summary>
        private static void Shown(GalaxyMapUIController controller, object entry)
        {
            try
            {
                QuestAsset endless = DataTable<QuestTable>.Instance.EndlessQuest.Asset;
                ShownField.SetValue(controller, entry);
                UpdateStyles.Invoke(controller, null);
                UpdateIncludedMutators.Invoke(controller, new object[] { endless.ForcedMutators });
                var banner = BannerField != null ? BannerField.GetValue(controller) as VisualElement : null;
                if (banner != null) banner.style.backgroundImage = new StyleBackground(DataTable<QuestTable>.Instance.EndlessMoodImage);
                var menu = MutatorMenuField != null ? MutatorMenuField.GetValue(controller) as MutatorsSelectionMenu : null;
                if (menu != null) menu.OnShownQuestChangedInGalaxyMap(endless);
            }
            catch (Exception e)
            {
                Log.Failure("Showing a fleet mission on the galaxy map", e);
            }
        }

        private static void Activate(HubVariant variant)
        {
            if (!HubFleet.HostHasFleet)
            {
                Notice.Show("The host of this room doesn't run this version of Void Crew Fleet, so fleet missions aren't available here.");
                // The game marked the entry as activated before asking; show the room's real choice again.
                Recheck();
                return;
            }
            HubFleet.RequestVariant(variant);
        }

        /// <summary>When the room's mission is a fleet mission, its entry (not the plain Pilgrimage) shows as the chosen one.</summary>
        [GamePatch(Feature, "GalaxyMapUIController", "CheckSelectedGamemode", PatchKind.Postfix)]
        private static void CheckSelectedPostfix(GalaxyMapUIController __instance, bool switchToActivated)
        {
            try
            {
                HubQuestManager hub = HubQuestManager.Instance;
                if (hub == null || hub.SelectedQuest == null || hub.SelectedQuest.AssetGuid != DataTable<QuestTable>.Instance.EndlessQuest.AssetGuid) return;
                object target = HubFleet.Variant == HubVariant.FleetPilgrimage ? _fleetEntry : (HubFleet.Variant == HubVariant.FleetBattle ? _battleEntry : null);
                if (target == null || ActivatedField == null) return;
                ActivatedField.SetValue(__instance, target);
                if (switchToActivated) ShownField.SetValue(__instance, target);
                UpdateStyles.Invoke(__instance, null);
            }
            catch (Exception e)
            {
                Log.Failure("Marking the chosen fleet mission on the galaxy map", e);
            }
        }

        private static void Recheck()
        {
            if (_controller == null || CheckSelected == null) return;
            try
            {
                CheckSelected.Invoke(_controller, new object[] { false });
            }
            catch (Exception e)
            {
                Log.Failure("Refreshing the galaxy map", e);
            }
        }

        [GamePatch(Feature, "GalaxyMapUIController+GameModeEntry", "GetQuestType", PatchKind.Postfix)]
        private static void QuestTypePostfix(object __instance, ref string __result)
        {
            if (EntryKind == null || __instance == null) return;
            int kind = Convert.ToInt32(EntryKind.GetValue(__instance));
            if (kind == FleetPilgrimageType) __result = "PILGRIMAGE · FLEET";
            else if (kind == FleetBattleType) __result = "PVP · TEAMS";
        }
    }
}
