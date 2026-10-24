using System;
using System.Collections.Generic;
using CG.Game;
using CG.Game.Player;
using CG.Input;
using UI.MainHUD;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// Every player in the room, or one Fleet Battle team's, when the HUD's crew list can't show them all (CrewList): opened and
    /// closed with the key its prompt shows, or Escape. A window like Fleet's lobby screen and the game's hub terminals (their
    /// background, meander bars, header and lines) with the game's own crew rows in a scrolling list, made live as the HUD's
    /// list makes them (CoopBuddyController and CoopBuddyView), with the HUD's style sheets; a team's list is grouped by ship.
    /// Their menus mute and kick as the HUD's rows do. The cursor shows and the game's controls are in menu mode while it is open.
    /// </summary>
    internal static class PlayerListPanel
    {
        internal enum Kind
        {
            Everyone,
            Blue,
            Red
        }

        /// <summary>The window's requests to the game: show the cursor, and switch its controls to menu mode.</summary>
        private sealed class CursorRequest : IShowCursorSource, IInputActionMapRequest
        {
        }

        private sealed class Entry
        {
            public CoopBuddyController Controller;
            public CoopBuddyView View;
        }

        private const float WindowWidth = 560f;
        private const float ListHeight = 620f;

        private static readonly CursorRequest CursorOwner = new CursorRequest();
        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly List<Player> Shown = new List<Player>();

        private static UIDocument _document;
        private static VisualElement _screen;
        private static Label _title;
        private static Label _count;
        private static ScrollView _list;
        private static bool _open;
        private static Kind _kind;
        private static string _shownKey;
        private static float _nextCheck;
        private static bool _cursorShown;
        private static bool _inputBlocked;
        private static float _inputAsserted;

        public static bool IsOpen
        {
            get { return _open; }
        }

        /// <summary>The key: opens this list, or closes it when it is the one open.</summary>
        public static void Toggle(Kind kind)
        {
            if (_open && _kind == kind) Close();
            else Open(kind);
        }

        public static void Open(Kind kind)
        {
            try
            {
                if (!Ensure()) return;
                _kind = kind;
                _open = true;
                _shownKey = null;
                Fill();
                _screen.style.display = DisplayStyle.Flex;
                SetCursor(true);
                SetInput(true);
                Log.Verbose("The player list (" + kind + ") is open.");
            }
            catch (Exception e)
            {
                Log.Failure("Opening the player list", e);
                Close();
            }
        }

        public static void Close()
        {
            bool wasOpen = _open;
            _open = false;
            Clear();
            if (_screen != null) _screen.style.display = DisplayStyle.None;
            SetCursor(false);
            SetInput(false);
            if (wasOpen) Log.Verbose("The player list is closed.");
        }

        /// <summary>
        /// Every frame (CrewList): Escape closes the list, as does a game menu opening over it; it follows players who join or
        /// leave, and keeps the game's controls in menu mode.
        /// </summary>
        public static void Frame()
        {
            if (!_open) return;
            try
            {
                Keyboard keyboard = Keyboard.current;
                if ((keyboard != null && keyboard.escapeKey.wasPressedThisFrame) || MenuScreenController.IsOpen() || _document == null)
                {
                    Close();
                    return;
                }
                SetInput(true);
                if (Time.unscaledTime < _nextCheck) return;
                _nextCheck = Time.unscaledTime + 0.5f;
                if (Key() != _shownKey) Fill();
            }
            catch (Exception e)
            {
                Log.Failure("Updating the player list", e);
                Close();
            }
        }

        private static bool Ensure()
        {
            if (_screen != null && _screen.panel != null) return true;
            if (_document == null) _document = Ui.CreateOverlay("Void Crew Fleet player list", 4500);
            if (_document == null || _document.rootVisualElement == null) return false;
            VisualElement root = _document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            Build(root);
            return true;
        }

        private static void Build(VisualElement root)
        {
            Ui.UseGameStyles(root);
            // The HUD's own sheets, so the crew rows look as they do in its list.
            MainHUDUI hud = MainHUDUI.Instance;
            UIDocument hudDocument = hud != null ? hud.GetComponent<UIDocument>() : null;
            VisualElement hudRoot = hudDocument != null ? hudDocument.rootVisualElement : null;
            if (hudRoot != null)
            {
                for (int i = 0; i < hudRoot.styleSheets.count; i++)
                {
                    StyleSheet sheet = hudRoot.styleSheets[i];
                    if (sheet != null && !root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
                }
            }
            root.AddToClassList("font__regular");

            _screen = new VisualElement { name = "FleetPlayerList" };
            Ui.Fill(_screen);
            _screen.style.backgroundColor = Ui.GameBackdrop;
            _screen.style.alignItems = Align.Center;
            _screen.style.justifyContent = Justify.Center;
            _screen.pickingMode = PickingMode.Position;

            var window = new VisualElement { name = "Terminal" };
            window.style.width = WindowWidth;
            window.style.backgroundColor = Ui.GameItemBackground;
            Sprite background = Ui.GameSprite("UIBackground01IL");
            if (background != null) window.style.backgroundImage = new StyleBackground(background);
            Ui.Border(window, 1, Ui.GameLightGrey);
            VisualElement topBar = Ui.GameDecoBar();
            Ui.Margin(topBar, 15, 0, 10, 0);
            window.Add(topBar);

            var body = new VisualElement();
            Ui.Padding(body, 0, 30);
            VisualElement header = Ui.HorizontalRow();
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.alignItems = Align.FlexEnd;
            _title = Ui.GameLabel("PLAYERS", "ui-text-size__large", "ui-text-color__white", "loadout-panel-header", "ui-text-dropshadow");
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.Add(_title);
            _count = Ui.GameLabel("", "ui-text-size__medium", "ui-text-dropshadow");
            _count.style.color = Ui.GameBrightGrey;
            _count.style.marginBottom = 4;
            header.Add(_count);
            body.Add(header);
            body.Add(Ui.GameDivider());

            _list = new ScrollView(ScrollViewMode.Vertical);
            _list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _list.style.height = ListHeight;
            _list.style.marginTop = 8;
            _list.style.marginBottom = 8;
            body.Add(_list);
            body.Add(Ui.GameDivider());

            VisualElement footer = Ui.HorizontalRow();
            footer.style.justifyContent = Justify.FlexEnd;
            footer.style.marginTop = 12;
            footer.Add(Ui.GameButton("CLOSE", Close, "metem-button-size__small"));
            body.Add(footer);
            window.Add(body);

            VisualElement bottomBar = Ui.GameDecoBar();
            Ui.Margin(bottomBar, 14, 0, 15, 0);
            window.Add(bottomBar);
            _screen.Add(window);
            _screen.style.display = DisplayStyle.None;
            root.Add(_screen);
        }

        /// <summary>The players this list shows, in join order: everyone, or the team's (on its ships, from its crew list).</summary>
        private static List<Player> Players(Dictionary<int, CrewEntry> crews)
        {
            var players = new List<Player>();
            ClientGame game = ClientGame.Current;
            if (game == null || game.Players == null) return players;
            foreach (Player player in game.Players)
            {
                if (player == null || player.PhotonPlayer == null) continue;
                if (_kind != Kind.Everyone)
                {
                    CrewEntry entry;
                    if (!crews.TryGetValue(player.PhotonPlayer.ActorNumber, out entry) || entry.Team != (_kind == Kind.Blue ? 0 : 1)) continue;
                }
                players.Add(player);
            }
            players.Sort((a, b) => a.PhotonPlayer.ActorNumber.CompareTo(b.PhotonPlayer.ActorNumber));
            return players;
        }

        /// <summary>Who is listed where, as one string: the list is made again when it changes.</summary>
        private static string Key()
        {
            Dictionary<int, CrewEntry> crews = FleetShips.Crews();
            var key = new System.Text.StringBuilder();
            foreach (Player player in Players(crews))
            {
                CrewEntry entry;
                crews.TryGetValue(player.PhotonPlayer.ActorNumber, out entry);
                key.Append(player.PhotonPlayer.ActorNumber).Append(':').Append(entry.Slot).Append(',');
            }
            return key.ToString();
        }

        private static void Fill()
        {
            Clear();
            Dictionary<int, CrewEntry> crews = FleetShips.Crews();
            List<Player> players = Players(crews);
            _shownKey = Key();
            int team = _kind == Kind.Blue ? 0 : (_kind == Kind.Red ? 1 : -1);
            _title.text = team < 0 ? "PLAYERS" : PvpTeams.TeamName(team) + " TEAM";
            _title.style.color = team < 0 ? Color.white : FleetApi.TeamColor(team);
            _count.text = players.Count + (players.Count == 1 ? " PLAYER" : " PLAYERS");

            CoopBuddiesPanel panel = MainHUDUI.Instance != null ? MainHUDUI.Instance.GetComponent<CoopBuddiesPanel>() : null;
            VisualTreeAsset template = panel != null ? panel.CoopBuddyPrefab : null;
            if (team < 0)
            {
                foreach (Player player in players) AddRow(player, template);
                return;
            }
            // A team's list: each of its ships with its crew, then anyone of the team still waiting for a place.
            int ships = PvpTeams.ShipCount;
            var placed = new HashSet<Player>();
            foreach (int slot in FleetSlots.SlotsOfTeam(team, ships))
            {
                AddShipHeader(slot, team);
                foreach (Player player in players)
                {
                    CrewEntry entry;
                    if (!crews.TryGetValue(player.PhotonPlayer.ActorNumber, out entry) || entry.Slot != slot) continue;
                    AddRow(player, template);
                    placed.Add(player);
                }
            }
            if (placed.Count == players.Count) return;
            AddHeader("WAITING FOR A PLACE", team);
            foreach (Player player in players)
            {
                if (!placed.Contains(player)) AddRow(player, template);
            }
        }

        /// <summary>A ship's header in a team's list: "BLUE 2  FRIGATE", in the team's colour.</summary>
        private static void AddShipHeader(int slot, int team)
        {
            string type = FleetSlots.TypeNameOf(slot, GameSessionManager.InHub);
            AddHeader(FleetSlots.Name(slot, true) + (string.IsNullOrEmpty(type) ? "" : "  " + type), team);
        }

        private static void AddHeader(string text, int team)
        {
            Label header = Ui.GameLabel(text, "ui-text-size__medium", "ui-text-dropshadow");
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.color = FleetApi.TeamColor(team);
            header.style.marginTop = _list.contentContainer.childCount > 0 ? 10 : 2;
            header.style.marginBottom = 2;
            _list.Add(header);
        }

        /// <summary>A player's row as the HUD's crew list makes it: the game's template, controller and view.</summary>
        private static void AddRow(Player player, VisualTreeAsset template)
        {
            try
            {
                if (template == null) throw new InvalidOperationException("the HUD's crew row template isn't loaded");
                TemplateContainer row = template.Instantiate();
                var controller = new CoopBuddyController(player);
                var view = new CoopBuddyView(row, controller.Data);
                bool local = player == LocalPlayer.Instance;
                if (local) controller.Data.CanBeKicked.Value = false;
                controller.Data.OnKickRequest += Kick;
                controller.Data.OnMuteVoiceRequest += Mute;
                controller.Data.OnUnmuteVoiceRequest += Unmute;
                _list.Add(row);
                Entries.Add(new Entry { Controller = controller, View = view });
                Shown.Add(player);
            }
            catch (Exception e)
            {
                // A player whose character isn't fully there yet: their name alone.
                Log.Verbose("Player list: a plain row for " + (player.PhotonPlayer != null ? player.PhotonPlayer.NickName : "a player") + " (" + e.Message + ").");
                Label name = Ui.GameLabel(player.PhotonPlayer != null ? player.PhotonPlayer.NickName : "?", "ui-text-size__medium", "ui-text-color__white", "ui-text-dropshadow");
                name.style.marginLeft = 30;
                name.style.height = 40;
                name.style.unityTextAlign = TextAnchor.MiddleLeft;
                _list.Add(name);
            }
        }

        private static void Kick(Player player)
        {
            if (ClientGame.Current != null && player != null) ClientGame.Current.KickPlayer(player);
        }

        private static void Mute(Player player)
        {
            if (VoipService.Instance != null && player != null) VoipService.Instance.MuteVoice(VoipService.PlayerToCloudID(player), true);
        }

        private static void Unmute(Player player)
        {
            if (VoipService.Instance != null && player != null) VoipService.Instance.MuteVoice(VoipService.PlayerToCloudID(player), false);
        }

        private static void Clear()
        {
            foreach (Entry entry in Entries)
            {
                try
                {
                    entry.Controller.Data.OnKickRequest -= Kick;
                    entry.Controller.Data.OnMuteVoiceRequest -= Mute;
                    entry.Controller.Data.OnUnmuteVoiceRequest -= Unmute;
                    entry.View.Dispose();
                    entry.Controller.Dispose();
                }
                catch (Exception e)
                {
                    Log.Verbose("Player list: a row didn't come apart cleanly (" + e.Message + ").");
                }
            }
            Entries.Clear();
            Shown.Clear();
            if (_list != null) _list.Clear();
        }

        /// <summary>Self-test: the list as it looks with a big room, from stand-in rows made from the game's own row template.</summary>
        internal static void OpenPreview(Kind kind, int players)
        {
            Open(kind);
            if (!_open) return;
            CoopBuddiesPanel panel = MainHUDUI.Instance != null ? MainHUDUI.Instance.GetComponent<CoopBuddiesPanel>() : null;
            VisualTreeAsset template = panel != null ? panel.CoopBuddyPrefab : null;
            if (template == null) return;
            int team = kind == Kind.Blue ? 0 : (kind == Kind.Red ? 1 : -1);
            // Stand-ins only: the real list (this one test player) makes way for them.
            Clear();
            _count.text = players + " PLAYERS";
            for (int i = 0; i < players; i++)
            {
                if (team >= 0 && i % 4 == 0) AddShipHeader(team + 2 * (i / 4), team);
                TemplateContainer row = template.Instantiate();
                Label name = row.Q<Label>("PlayerName");
                if (name != null) name.text = "Crewmate " + (i + 2);
                Label rank = row.Q<Label>("RankLabel");
                if (rank != null) rank.text = (10 + i * 7).ToString();
                foreach (string part in new[] { "ActionPanel", "ChatMuteState", "VoiceMuteState", "MicrophoneMutedState" })
                {
                    VisualElement hidden = row.Q(part);
                    if (hidden != null) hidden.style.display = DisplayStyle.None;
                }
                StateVE state = row.Q<StateVE>();
                if (state != null) state.SetClassState("player-hud-health", "high");
                _list.Add(row);
            }
            // The stand-ins are no players: the next check must not replace them.
            _shownKey = Key();
        }

        private static void SetInput(bool block)
        {
            try
            {
                if (block)
                {
                    if (_inputBlocked && Time.unscaledTime - _inputAsserted < 1f) return;
                    InputActionMapRequests.AddOrChangeRequest(CursorOwner, InputStateRequestType.UI);
                    _inputBlocked = true;
                    _inputAsserted = Time.unscaledTime;
                }
                else if (_inputBlocked)
                {
                    InputActionMapRequests.RemoveRequest(CursorOwner);
                    _inputBlocked = false;
                }
            }
            catch (Exception e)
            {
                Log.Failure("Switching the game's controls for the player list", e);
                _inputBlocked = block;
            }
        }

        private static void SetCursor(bool show)
        {
            if (_cursorShown == show) return;
            try
            {
                CursorUtility.ShowCursor(CursorOwner, show);
                _cursorShown = show;
            }
            catch (Exception e)
            {
                Log.Failure("Showing the mouse cursor for the player list", e);
                _cursorShown = show;
            }
        }
    }
}
