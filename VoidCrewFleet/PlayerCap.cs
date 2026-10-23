using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using CG.Utils;
using Extensions;
using Gameplay.MissionDifficulty;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using Steamworks;
using UI.Core;
using UI.Settings;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// Rooms of up to 24 players (8 per ship). The game caps rooms at 6 in four places and breaks above that in two:
    /// - the Photon room (6 at creation) and the three player-limit sliders (main menu host panel, ESC-menu lobby
    ///   panel, hub matchmaking terminal), one of which writes its clamped value back to the room whenever it opens;
    /// - the Steam lobby used for invites and join-by-friend (6 members);
    /// - the difficulty table, which has rows for 1-6 players and throws for any other count, failing every session load;
    /// - the tally screen, which draws one row per player into a fixed number of slots.
    /// </summary>
    internal static class PlayerCap
    {
        public const string Feature = "24-player rooms";

        /// <summary>The largest room the plugin allows (three ships of eight).</summary>
        public const int MaxRoom = 24;

        private static readonly FieldInfo ClampedMax = GameMember.Field(typeof(ClampedSetting<int>), "max");
        private static readonly PropertyInfo SliderHighValue = GameMember.Property(typeof(SliderIntSettingEntryVE), "HighValue");
        private static readonly PropertyInfo SliderDisplayMode = GameMember.Property(typeof(SliderIntSettingEntryVE), "DisplayMode");
        private static readonly MethodInfo SliderUpdateLabels = GameMember.Method(typeof(SliderIntSettingEntryVE), "UpdateLabels");
        private static readonly FieldInfo SliderSetting = GameMember.Field(typeof(SliderIntSettingEntryVE), "setting");

        /// <summary>The largest room this player hosts.</summary>
        public static int Limit
        {
            get { return Mathf.Clamp(Settings.MaxPlayers.Value, 4, MaxRoom); }
        }

        /// <summary>
        /// The largest size the room can be set to here: this player's own limit, but never less than the room's size or
        /// its players, so a player who becomes host with a lower setting keeps the room as it is. In a vanilla room this
        /// player took over as host, the game's own limit of 6 stands in for their own (its players may not have the
        /// plugin). Outside a room, this player's own limit.
        /// </summary>
        private static int Ceiling
        {
            get
            {
                Room room = PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom : null;
                if (room == null) return Limit;
                int own = ModRegistration.IsFleetRoom ? Limit : GameLimit;
                return Mathf.Max(own, Mathf.Max(room.MaxPlayers, room.PlayerCount));
            }
        }

        /// <summary>The game's own largest room.</summary>
        private const int GameLimit = 6;

        // ---------------------------------------------------------------- the room

        /// <summary>A new room starts at the full size; the host can lower it with the sliders, which now go to 24.</summary>
        [GamePatch(Feature, "PhotonService", "OnCreatedRoom", PatchKind.Postfix)]
        private static void OnCreatedRoomPostfix()
        {
            try
            {
                if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
                if (PhotonNetwork.CurrentRoom.MaxPlayers == 6) PhotonNetwork.CurrentRoom.MaxPlayers = (byte)Limit;
                Log.Info("Your room holds up to " + PhotonNetwork.CurrentRoom.MaxPlayers + " players.");
            }
            catch (Exception e)
            {
                Log.Failure("Raising the room's player limit", e);
            }
        }

        /// <summary>
        /// Every slider and menu sets the room size through this; keep it within 1 and the ceiling (which never cuts the room
        /// below its current size or players).
        /// </summary>
        [GamePatch(Feature, "PhotonService", "SetCurrentRoomPlayerLimit", PatchKind.Prefix)]
        private static void SetLimitPrefix(ref int limit)
        {
            limit = Mathf.Clamp(limit, 1, Ceiling);
        }

        [GamePatch(Feature, "MatchmakingRoomSetup", ".ctor", PatchKind.Postfix, Arguments = new[] { "UnityEngine.UIElements.VisualElement" })]
        private static void RoomSetupPostfix(IntSetting ___playerLimit, SliderIntSettingEntryVE ___playerLimitVE)
        {
            RaiseSlider(___playerLimit, ___playerLimitVE, CurrentRoomLimit(), Ceiling, "hub matchmaking terminal");
        }

        /// <summary>
        /// The hub terminal writes the room's size back through its slider whenever it reads the room (when it opens, and when
        /// the room's players change), clamped to the slider's maximum. That maximum was set when the terminal was built: raise
        /// it first when the room has grown past it since (another host made it bigger), or the write-back would shrink it.
        /// </summary>
        [GamePatch(Feature, "MatchmakingRoomSetup", "RetrieveAndUpdateInfo", PatchKind.Prefix, Optional = true)]
        private static void RoomSetupReadPrefix(IntSetting ___playerLimit, SliderIntSettingEntryVE ___playerLimitVE)
        {
            WidenSlider(___playerLimit, ___playerLimitVE, Ceiling, "hub matchmaking terminal");
        }

        [GamePatch(Feature, "LobbyPanel", ".ctor", PatchKind.Postfix,
            Arguments = new[] { "UnityEngine.UIElements.VisualElement", "UnityEngine.UIElements.VisualTreeAsset" })]
        private static void LobbyPanelPostfix(IntSetting ____playerLimit, SliderIntSettingEntryVE ____playerLimitVE)
        {
            RaiseSlider(____playerLimit, ____playerLimitVE, CurrentRoomLimit(), Ceiling, "lobby panel");
        }

        /// <summary>
        /// The escape menu's room settings read the room's size into their slider when they open, and a slider that moves
        /// writes its value to the room: the same as the hub terminal.
        /// </summary>
        [GamePatch(Feature, "LobbyPanel", "OnConfigToggled", PatchKind.Prefix, Optional = true)]
        private static void LobbyPanelOpenPrefix(IntSetting ____playerLimit, SliderIntSettingEntryVE ____playerLimitVE)
        {
            WidenSlider(____playerLimit, ____playerLimitVE, Ceiling, "lobby panel");
        }

        /// <summary>The main menu's host panel: the default becomes the full size.</summary>
        [GamePatch(Feature, "MatchMakingMenu", "Init", PatchKind.Postfix)]
        private static void HostMenuPostfix(IntSetting ____playerLimit, SliderIntSettingEntryVE ____playerLimitVE)
        {
            RaiseSlider(____playerLimit, ____playerLimitVE, Limit, Limit, "host menu");
        }

        private static int CurrentRoomLimit()
        {
            return PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.MaxPlayers : Limit;
        }

        /// <summary>
        /// Lets a 1..6 player-limit setting and its slider go to <paramref name="ceiling"/>, and shows <paramref name="value"/>
        /// without telling the room (the game's terminal would otherwise write the old clamp, 6, back to the room when it
        /// opens).
        /// </summary>
        private static void RaiseSlider(IntSetting setting, SliderIntSettingEntryVE slider, int value, int ceiling, string where)
        {
            try
            {
                if (setting == null || ClampedMax == null) return;
                ClampedMax.SetValue(setting, ceiling);
                setting.SetValue(value, true);
                FitSlider(slider, ceiling);
            }
            catch (Exception e)
            {
                Log.Failure("Raising the player-limit slider (" + where + ")", e);
            }
        }

        /// <summary>
        /// Raises a player-limit setting's maximum and its slider to <paramref name="ceiling"/> when they are lower. Never
        /// lowers them: a slider whose maximum drops below its value moves, and a moving slider writes its value to the room.
        /// </summary>
        private static void WidenSlider(IntSetting setting, SliderIntSettingEntryVE slider, int ceiling, string where)
        {
            try
            {
                if (setting == null || ClampedMax == null || ceiling <= setting.MaxValue) return;
                ClampedMax.SetValue(setting, ceiling);
                FitSlider(slider, ceiling);
            }
            catch (Exception e)
            {
                Log.Failure("Raising the player-limit slider (" + where + ")", e);
            }
        }

        /// <summary>The slider (its steps included) goes up to the ceiling, showing its setting's value.</summary>
        private static void FitSlider(SliderIntSettingEntryVE slider, int ceiling)
        {
            if (slider == null || slider.slider == null) return;
            int oldHigh = SliderHighValue != null ? (int)SliderHighValue.GetValue(slider, null) : slider.slider.highValue;
            slider.slider.highValue = ceiling;
            if (SliderHighValue != null) SliderHighValue.SetValue(slider, ceiling, null);
            AddSteps(slider, oldHigh, ceiling);
            if (SliderSetting != null && SliderSetting.GetValue(slider) != null) slider.Reload();
            if (SliderUpdateLabels != null) SliderUpdateLabels.Invoke(slider, null);
        }

        /// <summary>Sliders drawn with one clickable step per value get the steps for the new values too.</summary>
        private static void AddSteps(SliderIntSettingEntryVE slider, int oldHigh, int ceiling)
        {
            if (SliderDisplayMode == null) return;
            var mode = (SliderIntSettingEntryVE.NumberDisplayMode)SliderDisplayMode.GetValue(slider, null);
            if (mode != SliderIntSettingEntryVE.NumberDisplayMode.RangeWithSteps && mode != SliderIntSettingEntryVE.NumberDisplayMode.ValueWithSteps) return;
            VisualElement steps = slider.Q("StepContainer");
            if (steps == null) return;
            for (int value = oldHigh + 1; value <= ceiling; value++)
            {
                if (steps.Q(value.ToString()) != null) continue;
                var step = new VisualElement { name = value.ToString(), focusable = true };
                step.AddToClassList("step-selector");
                int stepValue = value;
                Ui.Click(step, () =>
                {
                    var current = SliderSetting != null ? SliderSetting.GetValue(slider) as IntSetting : null;
                    if (current != null) current.SetValue(stepValue);
                    slider.Reload();
                });
                steps.Add(step);
            }
        }

        // ---------------------------------------------------------------- the difficulty table

        /// <summary>
        /// The table only has rows for the player counts the game allows. Above that, use the largest row instead of
        /// throwing (which fails the whole session load). Two-ship sessions use the crew of one ship.
        /// </summary>
        [GamePatch(Feature, "Gameplay.MissionDifficulty.DifficultyPlayerCountTable", "GetConfig", PatchKind.Prefix)]
        private static void GetConfigPrefix(DifficultyPlayerCountTable __instance, ref int playerCount)
        {
            try
            {
                int asked = playerCount;
                int perShip = FleetSession.DifficultyPlayerCount(playerCount);
                int largest = 1;
                bool found = false;
                if (__instance.setups != null)
                {
                    foreach (DifficultyPlayerCountTable.DifficultySetup setup in __instance.setups)
                    {
                        if (setup == null) continue;
                        if (setup.playerCount == perShip) found = true;
                        if (setup.playerCount > largest) largest = setup.playerCount;
                    }
                }
                playerCount = found ? perShip : Mathf.Clamp(perShip, 1, largest);
                if (playerCount != asked) Log.InfoOnce("difficulty " + asked + ">" + playerCount, "Difficulty uses the " + playerCount + "-player settings for " + asked + " players.");
            }
            catch (Exception e)
            {
                Log.Failure("Choosing the difficulty row", e);
            }
        }

        // ---------------------------------------------------------------- the tally screen

        /// <summary>
        /// The game's TallyScreenPlayerUI.Setup, stopping at the last name slot instead of failing on the next player.
        /// The 3D characters (TallyMenu.SetupPlayers) already stop there, in the same player order.
        /// </summary>
        [GamePatch(Feature, "TallyScreenPlayerUI", "Setup", PatchKind.Prefix)]
        private static bool TallySetupPrefix(TallyScreenPlayerUI __instance, Dictionary<Player, VisualElement> ___SpeakerElements,
            Dictionary<Player, bool> ___PlayersSpeaking)
        {
            try
            {
                List<Player> players = PhotonUtil.GetPlayers();
                int slots = __instance.TallyMenu.TallyPartySlots.Length;
                if (players.Count <= slots) return true;

                VisualElement root = __instance.Doc.rootVisualElement;
                for (int j = 0; j < slots; j++)
                {
                    VisualElement row = root.Q("Player_" + j);
                    if (row == null) continue;
                    Player player = players[j];
                    int rank = 0;
                    bool isMaxRank = false;
                    object value;
                    if (player.CustomProperties.TryGetValue("RP_PR", out value) && value is int) rank = (int)value;
                    if (player.CustomProperties.TryGetValue("RP_FR", out value) && value is int && (int)value > 0)
                    {
                        rank = (int)value;
                        isMaxRank = true;
                    }
                    var rankDisplay = row.Q<RankDisplayVE>();
                    if (rankDisplay != null) rankDisplay.Show(rank, isMaxRank);
                    Label nameLabel = row.Q<Label>();
                    if (nameLabel != null)
                    {
                        nameLabel.text = "";
                        player.GetValidNickname(name => nameLabel.text = name);
                    }
                    if (!___SpeakerElements.ContainsKey(player)) ___SpeakerElements.Add(player, row.Q("Speaking"));
                    if (!___PlayersSpeaking.ContainsKey(player)) ___PlayersSpeaking.Add(player, false);
                    Player blockedPlayer = player;
                    PhotonUtil.IsPlayerBlocked(player).ContinueWith(task =>
                    {
                        try
                        {
                            __instance.SetBlockState(blockedPlayer, task.Result);
                        }
                        catch
                        {
                            // The screen may be gone by now.
                        }
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
                Log.Info("The tally screen shows " + slots + " of the " + players.Count + " players (it has " + slots + " places).");
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Filling the tally screen", e);
                return false;
            }
        }

        // ---------------------------------------------------------------- the Steam lobby

        /// <summary>The Steam lobby behind invites and join-by-friend is created for 6; the owner raises it.</summary>
        [GamePatch(Feature, "PlatformAbstractionLayer.Steam.SteamMatchmakingHandler", "OnLobbyCreatedInternal", PatchKind.Postfix, Optional = true)]
        private static void SteamLobbyCreatedPostfix(LobbyCreated_t param)
        {
            try
            {
                if (param.m_eResult != EResult.k_EResultOK) return;
                SteamMatchmaking.SetLobbyMemberLimit(new CSteamID(param.m_ulSteamIDLobby), Limit);
            }
            catch (Exception e)
            {
                Log.Failure("Raising the Steam lobby's member limit", e);
            }
        }

        /// <summary>
        /// A Steam lobby that could not be entered (for example a full one) is reported like a successful one, and the
        /// game then tries to follow its unreadable room code out of the Photon room. Ignore failed entries.
        /// </summary>
        [GamePatch(Feature, "PlatformAbstractionLayer.Steam.SteamMatchmakingHandler", "OnLobbyEnteredInternal", PatchKind.Prefix, Optional = true)]
        private static bool SteamLobbyEnteredPrefix(LobbyEnter_t param)
        {
            if (param.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) return true;
            Log.Warn("Could not enter a friend's Steam lobby (response " + param.m_EChatRoomEnterResponse + "); staying in the room. Steam invites may not work this time.");
            return false;
        }
    }
}
