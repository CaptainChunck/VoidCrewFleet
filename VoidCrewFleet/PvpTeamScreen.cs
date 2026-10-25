using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CG.GameLoopStateMachine;
using CG.GameLoopStateMachine.GameStates;
using CG.Input;
using Photon.Pun;
using Photon.Realtime;
using ResourceAssets;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// The lobby screen, where players pick the ship they fly.
    /// - Fleet Battle: it opens for everyone when the host chooses FLEET BATTLE. Two team columns with each team's ships (one
    ///   to three), each with its crew and a JOIN button, and the players without a ship between them. The host also sets
    ///   the ships per team and each ship's type, moves players, auto-balances and confirms; a SHIP RULES tab sits beside
    ///   the teams. (A match whose teams weren't confirmed in the hub shows it at the start, holding every spawn.)
    /// - Co-op with several ships: it opens for everyone when the host plans two or three ships (CREWS on the FLEET panel
    ///   brings it back). One column per ship; players pick a ship, the host sets the ship count and each ship's type (or
    ///   leaves it free), moves players, auto-balances and confirms the crews.
    /// LEAVE goes back to the main menu the way the game's own menu does. The screen is a UI Toolkit panel, so VR UI Fix
    /// shows it in the headset like the game's menus.
    /// </summary>
    internal static class PvpTeamScreen
    {
        /// <summary>This screen's requests to the game: show the cursor, and switch its controls to menu mode.</summary>
        private sealed class CursorRequest : IShowCursorSource, IInputActionMapRequest
        {
        }

        /// <summary>One ship's part of the screen: header, type button, crew and JOIN button.</summary>
        private sealed class ShipBlock
        {
            public int Slot;
            public Label Header;
            public Label Loadout;
            public Button TypeButton;
            public VisualElement Crew;
            public Button Join;
        }

        private static readonly CursorRequest CursorOwner = new CursorRequest();
        private static readonly List<ShipBlock> Blocks = new List<ShipBlock>();
        private static readonly Label[] TeamCounts = new Label[2];

        private static UIDocument _document;
        private static VisualElement _screen;
        private static Label _title;
        private static Label _subtitle;
        private static VisualElement _waitingList;
        private static Label _unpicked;
        private static VisualElement _hostStrip;
        private static Label _countLabel;
        private static readonly Button[] CountButtons = new Button[3];
        private static Button _mirrorButton;
        private static VisualElement _hostButtons;
        private static Button _balanceButton;
        private static Button _confirmButton;
        private static Label _status;
        private static Button _leaveButton;
        private static Label _flash;
        private static float _flashUntil;
        private static VisualElement _tabs;
        private static Button _teamsTab;
        private static Button _rulesTab;
        private static Label _rulesSummary;
        private static VisualElement _teamsView;
        private static VisualElement _rulesView;
        private static bool _showRules;
        private static string _signature;
        private static string _layout;
        private static bool _visible;
        private static bool _cursorShown;
        private static bool _inputBlocked;
        private static float _inputAsserted;
        private static bool _dismissed;
        private static bool _dismissedTeams;
        private static bool _dismissedPlaced;
        private static bool _viewing;
        private static float _leaveArmedUntil;
        private static Button _hideButton;
        private static Button _changeButton;

        /// <summary>The room HIDE and CLOSE were for: in another room the screen opens as usual.</summary>
        private static string _room;

        /// <summary>The ship rules the summary shows, as their JSON (the same string until they change).</summary>
        private static string _rulesJson;

        /// <summary>The room's players in join order, as of the last refresh.</summary>
        private static readonly List<Player> Players = new List<Player>();

        public static bool Visible
        {
            get { return _visible; }
        }

        /// <summary>A few times a second, from the runtime.</summary>
        public static void Tick()
        {
            ForgetOtherRoom();
            if (!ShouldShow())
            {
                Hide();
                return;
            }
            if (!EnsureScreen()) return;
            if (!_visible)
            {
                _visible = true;
                _signature = null;
                _screen.style.display = DisplayStyle.Flex;
                Log.Info("Showing the lobby screen.");
            }
            SetCursor(true);
            SetInput(true);
            Refresh();
        }

        /// <summary>
        /// Brings the lobby screen up for the local player: back after HIDE while the teams are picked, to pick a co-op ship
        /// (CREWS), or, once the teams are confirmed, to look at the teams and the ship rules (read-only).
        /// </summary>
        public static void ShowLocally()
        {
            ForgetOtherRoom();
            _dismissed = false;
            _viewing = true;
            Tick();
        }

        /// <summary>HIDE and CLOSE last for one room: when this player is in another room (or none), they are forgotten.</summary>
        private static void ForgetOtherRoom()
        {
            string room = PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.Name : null;
            if (room == _room) return;
            _room = room;
            _dismissed = false;
            _viewing = false;
        }

        /// <summary>A notice for the local player (a full ship, a refused confirm) while the screen hides the chat.</summary>
        public static void Flash(string message)
        {
            if (!_visible || _flash == null || string.IsNullOrEmpty(message)) return;
            _flash.text = message;
            _flashUntil = Time.unscaledTime + 6f;
        }

        private static bool ShouldShow()
        {
            if (!FleetRuntime.GameReady || !PhotonNetwork.InRoom) return false;
            try
            {
                GameStateMachine machine = GameStateMachine.Instance;
                if (machine == null || !(machine.CurrentState is GSSpawn || machine.CurrentState is GSIngame)) return false;
                if (PvpTeams.PickingTeams)
                {
                    // The game's own menus come first (this screen would cover the escape menu); HIDE puts it away until reopened.
                    if (_dismissed) KeepHidden();
                    return !_dismissed && !MenuScreenController.IsOpen();
                }
                _dismissed = false;
                if (!_viewing || !PvpTeams.HubFleetPlanned)
                {
                    _viewing = false;
                    return false;
                }
                return !MenuScreenController.IsOpen();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// HIDE lasts while the same picking goes on. The screen comes back when the host switches between co-op crews and
        /// Fleet Battle teams, or when this player loses the ship they had (crews cleared, their ship taken out of the fleet).
        /// </summary>
        private static void KeepHidden()
        {
            bool placed = FleetShips.CrewSlot(FleetShips.LocalActor) >= 0;
            if (FleetApi.IsTeamMatch != _dismissedTeams || (_dismissedPlaced && !placed))
            {
                _dismissed = false;
                return;
            }
            if (placed) _dismissedPlaced = true;
        }

        private static void Hide()
        {
            if (_visible)
            {
                _visible = false;
                if (_screen != null) _screen.style.display = DisplayStyle.None;
                Log.Info("The lobby screen is closed.");
                if (PvpTeams.Locked && FleetSession.IsPvP && !GameSessionManager.InHub)
                    Notice.Show("Enemy players aboard your ship? Hold the right mouse button to raise your fists, then left click to punch (five punches knock one out).");
            }
            SetCursor(false);
            SetInput(false);
        }

        /// <summary>
        /// While the screen is up the game's controls are in menu mode (as under its own menus), so the character behind it
        /// doesn't walk or turn. Re-asserted every second: another screen may have put its own request on top since.
        /// </summary>
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
                Log.Failure("Switching the game's controls for the lobby screen", e);
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
                Log.Failure("Showing the mouse cursor for the lobby screen", e);
                _cursorShown = show;
            }
        }

        // ---------------------------------------------------------------- building

        private static bool EnsureScreen()
        {
            if (_document != null && _screen != null && _screen.panel != null) return true;
            if (_document == null)
            {
                _document = Ui.CreateOverlay("Void Crew Fleet lobby screen", 5000);
                if (_document == null) return false;
            }
            VisualElement root = _document.rootVisualElement;
            if (root == null) return false;
            root.pickingMode = PickingMode.Ignore;
            Build(root);
            // A rebuilt screen starts hidden: let Tick show it again.
            _visible = false;
            _layout = null;
            return true;
        }

        /// <summary>
        /// The lobby screen fills the screen but for this margin (in the game's 1920 x 1080 reference pixels), its sections sharing
        /// its width with SectionGap between them, so it grows with wide screens and gives long names room.
        /// </summary>
        private const float ScreenMargin = 24f;
        public const float SectionGap = 24f;

        /// <summary>The sections of a row of them, SectionGap apart.</summary>
        internal static void SpaceSections(VisualElement row)
        {
            for (int i = 0; i < row.childCount; i++) row[i].style.marginLeft = i > 0 ? SectionGap : 0f;
        }

        private static void Build(VisualElement root)
        {
            Ui.UseGameStyles(root);
            _screen = new VisualElement { name = "FleetTeamScreen" };
            Ui.Fill(_screen);
            _screen.style.backgroundColor = Ui.GameBackdrop;
            _screen.style.alignItems = Align.Stretch;
            _screen.style.justifyContent = Justify.Center;
            _screen.style.paddingTop = ScreenMargin;
            _screen.style.paddingBottom = ScreenMargin;
            _screen.style.paddingLeft = ScreenMargin;
            _screen.style.paddingRight = ScreenMargin;
            _screen.pickingMode = PickingMode.Position;

            // A screen in the style of the game's hub terminals (the loadout terminal's): its background picture, the
            // meander bars along the top and bottom, sections with big white headers over its decorative lines, rows like
            // its RESOURCES list, and its big hex button for the one thing to do at the end.
            var panel = new VisualElement { name = "Terminal" };
            panel.style.flexGrow = 1;
            panel.style.backgroundColor = Ui.GameItemBackground;
            Sprite background = Ui.GameSprite("UIBackground01IL");
            if (background != null) panel.style.backgroundImage = new StyleBackground(background);
            Ui.Border(panel, 1, Ui.GameLightGrey);

            VisualElement topBar = Ui.GameDecoBar();
            Ui.Margin(topBar, 15, 0, 10, 0);
            panel.Add(topBar);

            var body = new VisualElement();
            Ui.Padding(body, 0, 30);
            body.style.flexGrow = 1;

            VisualElement headerRow = Ui.HorizontalRow();
            headerRow.style.justifyContent = Justify.SpaceBetween;
            _title = Ui.GameLabel("FLEET BATTLE", "ui-text-size__large", "ui-text-color__white", "loadout-panel-header", "ui-text-dropshadow");
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            headerRow.Add(_title);
            // With Ship Customizer: a SHIP RULES tab beside the teams.
            _tabs = Ui.HorizontalRow();
            _teamsTab = Ui.GameButton("TEAMS", () => ShowRules(false), "metem-button-size__small");
            Ui.Margin(_teamsTab, 0, 0, 0, 10);
            _tabs.Add(_teamsTab);
            _rulesTab = Ui.GameButton("SHIP RULES", () => ShowRules(true), "metem-button-size__small");
            Ui.Margin(_rulesTab, 0, 0, 0, 10);
            _tabs.Add(_rulesTab);
            headerRow.Add(_tabs);
            body.Add(headerRow);
            body.Add(Ui.GameDivider());

            VisualElement infoRow = Ui.HorizontalRow();
            infoRow.style.justifyContent = Justify.SpaceBetween;
            infoRow.style.marginTop = 8;
            _subtitle = Ui.GameLabel("", "ui-text-size__medium", "ui-text-color__white", "ui-text-dropshadow");
            _subtitle.style.flexShrink = 1;
            infoRow.Add(_subtitle);
            _rulesSummary = Ui.GameLabel("", "ui-text-size__medium", "ui-text-dropshadow");
            _rulesSummary.style.unityFontStyleAndWeight = FontStyle.Bold;
            _rulesSummary.style.unityTextAlign = TextAnchor.MiddleRight;
            _rulesSummary.style.flexShrink = 1;
            _rulesSummary.style.marginLeft = 20;
            infoRow.Add(_rulesSummary);
            body.Add(infoRow);

            // The host's choices: how many ships (per team), and whether both teams get the same types.
            _hostStrip = Ui.HorizontalRow();
            _hostStrip.style.alignItems = Align.Center;
            _hostStrip.style.marginTop = 10;
            _countLabel = Ui.GameLabel("SHIPS PER TEAM", "ui-text-size__medium", "ui-text-dropshadow");
            _countLabel.style.color = Ui.GameBrightGrey;
            _countLabel.style.marginRight = 10;
            _hostStrip.Add(_countLabel);
            for (int i = 0; i < CountButtons.Length; i++)
            {
                int count = i + 1;
                CountButtons[i] = Ui.GameButton(count.ToString(), () => SetCount(count), "metem-button-size__small");
                CountButtons[i].style.minWidth = 56;
                Ui.Margin(CountButtons[i], 0, 8, 0, 0);
                _hostStrip.Add(CountButtons[i]);
            }
            _mirrorButton = Ui.GameButton("", ToggleMirror, "metem-button-size__small");
            Ui.Margin(_mirrorButton, 0, 0, 0, 24);
            _hostStrip.Add(_mirrorButton);
            body.Add(_hostStrip);

            _teamsView = Ui.HorizontalRow();
            _teamsView.style.alignItems = Align.Stretch;
            _teamsView.style.justifyContent = Justify.SpaceBetween;
            _teamsView.style.flexGrow = 1;
            _teamsView.style.minHeight = 0;
            _teamsView.style.marginTop = 10;
            body.Add(_teamsView);
            _rulesView = ShipRulesView.Build();
            _rulesView.style.marginTop = 10;
            _rulesView.style.display = DisplayStyle.None;
            body.Add(_rulesView);
            _rulesJson = null;

            // The status on the left; on the right LEAVE, and for the host AUTO-BALANCE and CONFIRM TEAMS, the big hex
            // button where the loadout terminal has SELECT.
            VisualElement bottom = Ui.HorizontalRow();
            bottom.style.justifyContent = Justify.SpaceBetween;
            bottom.style.marginTop = 10;
            VisualElement texts = Ui.Column();
            texts.style.flexShrink = 1;
            texts.style.flexGrow = 1;
            texts.style.marginRight = 20;
            _status = Ui.GameLabel("", "ui-text-size__medium", "ui-text-color__white", "ui-text-dropshadow");
            _status.style.whiteSpace = WhiteSpace.Normal;
            texts.Add(_status);
            _flash = Ui.GameLabel("", "ui-text-size__medium", "ui-text-dropshadow");
            _flash.style.color = Ui.GameOrange;
            _flash.style.unityFontStyleAndWeight = FontStyle.Bold;
            _flash.style.marginTop = 4;
            _flash.style.minHeight = 26;
            texts.Add(_flash);
            bottom.Add(texts);

            VisualElement buttons = Ui.HorizontalRow();
            buttons.style.flexShrink = 0;
            _leaveButton = Ui.GameButton("LEAVE LOBBY", Leave, "metem-button-size__small", "metem-button__cancel");
            Ui.Margin(_leaveButton, 0, 12, 0, 0);
            buttons.Add(_leaveButton);
            _hideButton = Ui.GameButton("HIDE", HideLocally, "metem-button-size__small");
            Ui.Margin(_hideButton, 0, 12, 0, 0);
            buttons.Add(_hideButton);
            _changeButton = Ui.GameButton("CHANGE MISSION", ChangeMission, "metem-button-size__small");
            Ui.Margin(_changeButton, 0, 12, 0, 0);
            buttons.Add(_changeButton);
            _hostButtons = Ui.HorizontalRow();
            _balanceButton = Ui.GameButton("AUTO-BALANCE", PvpTeams.AutoBalance, "metem-button-size__small");
            Ui.Margin(_balanceButton, 0, 12, 0, 0);
            _hostButtons.Add(_balanceButton);
            _confirmButton = Ui.GameButton("CONFIRM TEAMS", PvpTeams.Confirm, "metem-button-size__large");
            _confirmButton.style.width = 360;
            Ui.Margin(_confirmButton, 0, 0, 0, 0);
            _hostButtons.Add(_confirmButton);
            buttons.Add(_hostButtons);
            bottom.Add(buttons);
            body.Add(bottom);
            panel.Add(body);

            VisualElement bottomBar = Ui.GameDecoBar();
            Ui.Margin(bottomBar, 10, 0, 15, 0);
            panel.Add(bottomBar);

            _screen.Add(panel);
            _screen.style.display = DisplayStyle.None;
            root.Add(_screen);
            Ui.Announce(_screen);
        }

        /// <summary>The columns for this many ships: two team columns around the players without a ship, or one per co-op ship.</summary>
        private static void BuildColumns(bool teams, int ships)
        {
            _teamsView.Clear();
            Blocks.Clear();
            _waitingList = null;
            if (_unpicked != null) _unpicked.RemoveFromHierarchy();
            _unpicked = null;
            if (teams)
            {
                _teamsView.Add(TeamColumn(0, ships));
                _teamsView.Add(WaitingColumn("NO TEAM YET", "Players here haven't picked a ship."));
                _teamsView.Add(TeamColumn(1, ships));
                SpaceSections(_teamsView);
                return;
            }
            for (int slot = 0; slot < ships; slot++)
            {
                Label header;
                VisualElement content;
                VisualElement section = Ui.GameSection("SHIP " + (slot + 1), Ui.SharedWidth, out header, out content);
                ShipBlock block = MakeBlock(slot, content, false);
                block.Header = header;
                _teamsView.Add(section);
            }
            if (ships < 3)
            {
                _teamsView.Add(WaitingColumn("NO SHIP YET", "Players here haven't picked a ship."));
            }
            else
            {
                // No room for a fourth column: the players without a ship are named under the ships.
                _unpicked = Ui.GameLabel("", "ui-text-size__medium", "ui-text-dropshadow");
                _unpicked.style.color = Ui.GameBrightGrey;
            }
            SpaceSections(_teamsView);
        }

        private static VisualElement TeamColumn(int team, int ships)
        {
            Label header;
            VisualElement content;
            VisualElement section = Ui.GameSection(PvpTeams.TeamName(team) + " TEAM", Ui.SharedWidth, out header, out content);
            header.style.color = FleetApi.TeamColor(team);
            TeamCounts[team] = Ui.GameLabel("", "ui-text-size__medium", "ui-text-dropshadow");
            TeamCounts[team].style.color = Ui.GameBrightGrey;
            TeamCounts[team].style.marginBottom = 6;
            content.Add(TeamCounts[team]);
            // Up to three ships and twelve players: the column scrolls.
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            content.Add(scroll);
            foreach (int slot in FleetSlots.SlotsOfTeam(team, ships))
            {
                var holder = new VisualElement();
                holder.style.marginBottom = 10;
                ShipBlock block = MakeBlock(slot, holder, true);
                scroll.Add(holder);
                block.Header.style.color = FleetApi.TeamColor(team);
            }
            return section;
        }

        /// <summary>A ship's block: its name (and the host's TYPE button), its loadout, its crew and its JOIN button.</summary>
        private static ShipBlock MakeBlock(int slot, VisualElement parent, bool withHeader)
        {
            var block = new ShipBlock { Slot = slot };
            VisualElement top = Ui.HorizontalRow();
            top.style.justifyContent = Justify.SpaceBetween;
            top.style.alignItems = Align.Center;
            top.style.marginBottom = 4;
            if (withHeader)
            {
                block.Header = Ui.GameLabel("", "ui-text-size__mediumPlus", "ui-text-dropshadow");
                block.Header.style.unityFontStyleAndWeight = FontStyle.Bold;
                top.Add(block.Header);
            }
            block.Loadout = Ui.GameLabel("", "ui-text-size__medium", "ui-text-color__white", "ui-text-dropshadow");
            block.Loadout.style.flexShrink = 1;
            block.Loadout.style.whiteSpace = WhiteSpace.Normal;
            if (!withHeader) top.Add(block.Loadout);
            block.TypeButton = Ui.GameButton("", () => CycleType(slot), "metem-button-size__small");
            block.TypeButton.style.height = 32;
            block.TypeButton.style.fontSize = 16;
            block.TypeButton.style.paddingTop = 0;
            block.TypeButton.style.paddingBottom = 0;
            block.TypeButton.style.flexShrink = 0;
            top.Add(block.TypeButton);
            parent.Add(top);
            if (withHeader) parent.Add(block.Loadout);
            block.Crew = Ui.Column();
            block.Crew.style.marginTop = 4;
            parent.Add(block.Crew);
            block.Join = Ui.GameButton("", () =>
            {
                if (FleetShips.CrewSlot(FleetShips.LocalActor) != slot) PvpTeams.Join(slot);
            }, "metem-button-size__small");
            Ui.Margin(block.Join, 4, 0, 0, 0);
            parent.Add(block.Join);
            Blocks.Add(block);
            return block;
        }

        private static VisualElement WaitingColumn(string title, string hintText)
        {
            Label header;
            VisualElement content;
            VisualElement section = Ui.GameSection(title, Ui.SharedWidth, out header, out content);
            Label hint = Ui.GameLabel(hintText, "ui-text-size__medium", "ui-text-dropshadow");
            hint.style.color = Ui.GameBrightGrey;
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.marginBottom = 8;
            content.Add(hint);
            // Everyone starts here when FLEET BATTLE is chosen: up to 24 players, so it scrolls.
            _waitingList = new ScrollView(ScrollViewMode.Vertical);
            _waitingList.style.flexGrow = 1;
            content.Add(_waitingList);
            return section;
        }

        // ---------------------------------------------------------------- updating

        internal static void ShowRules(bool on)
        {
            _showRules = on && ShipRulesView.Available && FleetApi.IsTeamMatch;
            _teamsView.style.display = _showRules ? DisplayStyle.None : DisplayStyle.Flex;
            _rulesView.style.display = _showRules ? DisplayStyle.Flex : DisplayStyle.None;
            _teamsTab.EnableInClassList("metem-button-selected", !_showRules);
            _rulesTab.EnableInClassList("metem-button-selected", _showRules);
            if (_showRules) ShipRulesView.Refresh();
        }

        private static void RefreshRules(bool teams)
        {
            bool available = teams && ShipRulesView.Available;
            _tabs.style.display = available ? DisplayStyle.Flex : DisplayStyle.None;
            _rulesSummary.style.display = available ? DisplayStyle.Flex : DisplayStyle.None;
            if (!available)
            {
                if (_showRules) ShowRules(false);
                return;
            }
            // The summary is written again only when the rules changed (their JSON is a new string then).
            string json = ShipRules.ShownJson;
            if (!ReferenceEquals(json, _rulesJson))
            {
                _rulesJson = json;
                ShipRules.Rules rules = ShipRules.Shown;
                _rulesSummary.text = "SHIP RULES: " + ShipRules.Describe(rules).ToUpperInvariant();
                _rulesSummary.style.color = rules.Restricts ? Ui.GameOrange : Ui.GameBrightGrey;
            }
            _teamsTab.EnableInClassList("metem-button-selected", !_showRules);
            _rulesTab.EnableInClassList("metem-button-selected", _showRules);
            if (_showRules) ShipRulesView.Refresh();
        }

        private static string ShipTitle(int slot, bool teams)
        {
            return FleetSlots.Name(slot, teams);
        }

        private static void Refresh()
        {
            if (_flash != null && _flash.text.Length > 0 && Time.unscaledTime > _flashUntil) _flash.text = "";
            bool teams = FleetApi.IsTeamMatch;
            try
            {
                RefreshRules(teams);
            }
            catch (Exception e)
            {
                Log.Failure("Updating the ship rules on the lobby screen", e);
            }

            int ships = PvpTeams.ShipCount;
            string layout = (teams ? "T" : "C") + ships;
            if (layout != _layout)
            {
                _layout = layout;
                _signature = null;
                BuildColumns(teams, ships);
            }

            Dictionary<int, CrewEntry> crews = FleetShips.Crews();
            List<Player> players = Players;
            PvpTeams.RoomPlayers(players);
            bool host = PhotonNetwork.IsMasterClient;
            bool picking = PvpTeams.PickingTeams;
            // The ships' names, types and the mission come from the room's fleet choices: their revision stands for them.
            string signature = Signature(crews, players, host) + (picking ? "|picking" : "|locked") + (Time.unscaledTime < _leaveArmedUntil ? "|leave" : "") +
                               "|" + HubFleet.Revision;
            if (signature == _signature) return;
            _signature = signature;

            try
            {
                bool inHub = GameSessionManager.InHub;
                List<int> actors = players.Select(p => p.ActorNumber).ToList();
                int me = FleetShips.LocalActor;
                CrewEntry mine;
                bool placed = crews.TryGetValue(me, out mine) && mine.State == CrewState.Playing && mine.Slot >= 0;

                _title.text = teams ? "FLEET BATTLE" : "FLEET CREWS";
                _subtitle.text = teams
                    ? "Pick a ship: it puts you on its team. Destroy every enemy ship to win. Teams and crews stay even."
                    : "Pick the ship you fly for the whole mission. Crews stay even. If your ship is destroyed, you aren't re-cloned again.";

                // The host's choices.
                bool hostControls = host && inHub && picking;
                _hostStrip.style.display = hostControls ? DisplayStyle.Flex : DisplayStyle.None;
                int count = teams ? HubFleet.ShipsPerTeam : ships;
                _countLabel.text = teams ? "SHIPS PER TEAM" : "SHIPS";
                for (int i = 0; i < CountButtons.Length; i++)
                {
                    CountButtons[i].EnableInClassList("metem-button-selected", i + 1 == count);
                    // Co-op: the Fleet Pilgrimage has at least two ships.
                    CountButtons[i].SetEnabled(teams || i > 0 || HubFleet.Variant != HubVariant.FleetPilgrimage);
                }
                _mirrorButton.style.display = teams ? DisplayStyle.Flex : DisplayStyle.None;
                _mirrorButton.text = HubFleet.MirrorTypes ? "BOTH TEAMS: SAME SHIPS" : "BOTH TEAMS: OWN SHIPS";
                _mirrorButton.EnableInClassList("metem-button-selected", HubFleet.MirrorTypes);

                foreach (ShipBlock block in Blocks)
                {
                    int slot = block.Slot;
                    GUIDUnion type = HubFleet.ForcedType(slot);
                    if (block.Header != null) block.Header.text = ShipTitle(slot, teams);
                    block.Loadout.text = (type.IsNull ? "" : FleetSlots.TypeName(type) + ": ") + PvpTeams.ShipName(slot).ToUpperInvariant();
                    block.TypeButton.text = type.IsNull ? "ANY TYPE" : FleetSlots.TypeName(type);
                    block.TypeButton.style.display = hostControls ? DisplayStyle.Flex : DisplayStyle.None;
                    block.Crew.Clear();
                    foreach (Player player in players)
                    {
                        CrewEntry entry;
                        if (crews.TryGetValue(player.ActorNumber, out entry) && entry.State == CrewState.Playing && entry.Slot == slot)
                            block.Crew.Add(PlayerRow(player, slot, teams, ships, host && picking));
                    }
                    int crew = CrewRules.CrewOf(crews, actors, slot);
                    string refusal = CrewRules.CanJoin(crews, actors, me, slot, ships, teams, s => ShipTitle(s, teams), PvpTeams.TeamName);
                    bool joined = placed && mine.Slot == slot;
                    bool full = !joined && refusal != null;
                    Button join = block.Join;
                    join.text = joined ? "YOU FLY " + ShipTitle(slot, teams) : (full ? ShipTitle(slot, teams) + " IS FULL" : "JOIN " + ShipTitle(slot, teams));
                    join.style.display = picking || joined ? DisplayStyle.Flex : DisplayStyle.None;
                    join.SetEnabled(!full && picking && !joined);
                    join.EnableInClassList("metem-button-selected", joined);
                    join.style.unityBackgroundImageTintColor = teams && !full ? new StyleColor(FleetApi.TeamColor(FleetSlots.TeamOf(slot, true))) : new StyleColor(StyleKeyword.Null);
                    if (block.Header != null && !teams) block.Header.text = ShipTitle(slot, false) + "  (" + crew + ")";
                }

                if (teams)
                {
                    int max = CrewRules.MaxPerTeam(actors.Count);
                    for (int team = 0; team < 2; team++)
                    {
                        if (TeamCounts[team] == null) continue;
                        int size = CrewRules.TeamSize(crews, actors, team, false);
                        TeamCounts[team].text = (size + " of " + max + (max == 1 ? " player" : " players")).ToUpperInvariant();
                    }
                }

                var unplaced = players.Where(p =>
                {
                    CrewEntry entry;
                    return !crews.TryGetValue(p.ActorNumber, out entry) || entry.State != CrewState.Playing || entry.Slot < 0 || entry.Slot >= ships;
                }).ToList();
                if (_waitingList != null)
                {
                    _waitingList.Clear();
                    foreach (Player player in unplaced) _waitingList.Add(PlayerRow(player, -1, teams, ships, host && picking));
                }
                if (_unpicked != null)
                {
                    _unpicked.text = unplaced.Count == 0 ? "" : "NOT PICKED YET: " + string.Join(", ", unplaced.Select(p => Name(p)).ToArray());
                    if (_unpicked.parent == null) _teamsView.parent.Insert(_teamsView.parent.IndexOf(_teamsView) + 1, _unpicked);
                }

                string why = PvpTeams.CheckPicks();
                // More players than the crews take: moving players can't help, more ships can (CheckPicks says what).
                bool overfull = actors.Count > ships * CrewRules.MaxCrew;
                _hostButtons.style.display = host && picking ? DisplayStyle.Flex : DisplayStyle.None;
                _confirmButton.text = teams ? "CONFIRM TEAMS" : "CONFIRM CREWS";
                _confirmButton.SetEnabled(why == null);
                if (_leaveButton != null) _leaveButton.text = Time.unscaledTime < _leaveArmedUntil ? "CLICK AGAIN TO LEAVE" : (inHub ? "LEAVE LOBBY" : "LEAVE MATCH");
                _hideButton.text = PvpTeams.PickingTeams ? "HIDE" : "CLOSE";
                _hideButton.style.display = inHub ? DisplayStyle.Flex : DisplayStyle.None;
                _changeButton.style.display = teams && host && picking && inHub ? DisplayStyle.Flex : DisplayStyle.None;
                if (!teams)
                {
                    List<int> empty = CrewRules.EmptyShips(crews, actors, ships);
                    string emptyNote = empty.Count == 0 || why != null
                        ? ""
                        : " " + string.Join(" and ", empty.Select(s => ShipTitle(s, false)).ToArray()) + (empty.Count == 1 ? " has" : " have") +
                          " no crew and will fly empty.";
                    if (!picking)
                        _status.text = "The crews are confirmed. " + (host ? "To change them, press CREWS on the FLEET panel." : "Only the host can change the crews.");
                    else if (host)
                        _status.text = why == null
                            ? "Everyone has a ship. Press CONFIRM CREWS, then set up the ships and sit in the ready room to start." + emptyNote
                            : why + (overfull ? "" : " Move players with the ship buttons on their row, or press AUTO-BALANCE.");
                    else
                        _status.text = (placed ? "You fly " + ShipTitle(mine.Slot, false) + ". " : "Pick a ship. ") +
                                       "The host confirms the crews; then set up your ship at the loadout terminal.";
                }
                else if (!picking)
                {
                    _status.text = "The teams are confirmed. " + (host
                        ? "To change them or the ship rules, press TEAMS & RULES on the FLEET panel."
                        : "Only the host can change the teams and the ship rules.");
                }
                else if (host)
                {
                    _status.text = why == null
                        ? (inHub
                            ? "The teams are even. Press CONFIRM TEAMS, then set up the ships and sit in the ready room to start the match."
                            : "The teams are even. Press CONFIRM TEAMS to start the match.")
                        : why + (overfull ? "" : " Move players with the ship buttons on their row, or press AUTO-BALANCE.");
                }
                else
                {
                    _status.text = (placed ? "" : "Pick a ship. ") + (inHub
                        ? "The host confirms the teams; then set up your team's ships at the loadout terminal."
                        : "The host confirms the teams, then everyone spawns aboard their ship.");
                }
            }
            catch (Exception e)
            {
                Log.Failure("Updating the lobby screen", e);
            }
        }

        private static string Signature(Dictionary<int, CrewEntry> crews, List<Player> players, bool host)
        {
            var text = new StringBuilder();
            text.Append(host ? 'H' : 'C').Append(PhotonNetwork.MasterClient != null ? PhotonNetwork.MasterClient.ActorNumber : 0);
            foreach (Player player in players)
            {
                CrewEntry entry;
                text.Append('|').Append(player.ActorNumber).Append(':').Append(crews.TryGetValue(player.ActorNumber, out entry) ? entry.ToString() : "-").Append(':')
                    .Append(player.NickName);
            }
            return text.ToString();
        }

        private static string Name(Player player)
        {
            return string.IsNullOrEmpty(player.NickName) ? "Player " + player.ActorNumber : player.NickName;
        }

        private static VisualElement PlayerRow(Player player, int slot, bool teams, int ships, bool host)
        {
            bool me = player.IsLocal;
            // A row like the terminal's RESOURCES list, with the game's "active" orange edge on your own.
            VisualElement row = Ui.GameListRow();
            if (me)
            {
                row.style.borderLeftWidth = 10;
                row.style.borderLeftColor = Ui.GameOrange;
            }

            VisualElement left = Ui.HorizontalRow();
            left.style.flexShrink = 1;
            left.style.flexGrow = 1;
            left.style.minWidth = 0;
            Label label = Ui.GameLabel(Name(player), "ui-text-size__medium");
            label.style.fontSize = 20;
            label.style.color = me ? Ui.GameOrange : Color.white;
            label.style.unityFontStyleAndWeight = me ? FontStyle.Bold : FontStyle.Normal;
            label.style.flexShrink = 1;
            label.style.minWidth = 0;
            label.style.overflow = Overflow.Hidden;
            // One line, cut short with "…" only when even a wide column hasn't room (squeezed, it wrapped a letter at a time).
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.textOverflow = TextOverflow.Ellipsis;
            left.Add(label);
            if (player.IsMasterClient) left.Add(Tag("HOST"));
            if (me) left.Add(Tag("YOU"));
            row.Add(left);

            if (host)
            {
                // The host moves anyone: one small button per other ship ("B2" / "R1" in Fleet Battle, "2" in co-op).
                int actor = player.ActorNumber;
                VisualElement moves = Ui.HorizontalRow();
                moves.style.flexShrink = 0;
                for (int target = 0; target < ships; target++)
                {
                    if (target == slot) continue;
                    int to = target;
                    string text = teams ? (to % 2 == 0 ? "B" : "R") + (to / 2 + 1) : (to + 1).ToString();
                    Color colour = teams ? FleetApi.TeamColor(to % 2) : Ui.GameLightGrey;
                    moves.Add(MoveButton(text, colour, () => PvpTeams.Move(actor, to)));
                }
                row.Add(moves);
            }
            return row;
        }

        private static Label Tag(string text)
        {
            Label tag = Ui.GameLabel(text, "ui-text-size__small");
            tag.style.fontSize = 15;
            tag.style.unityFontStyleAndWeight = FontStyle.Bold;
            tag.style.color = Ui.GameBrightGrey;
            tag.style.marginLeft = 10;
            tag.style.flexShrink = 0;
            return tag;
        }

        /// <summary>The game's small button, shrunk to fit a row, with its outline in the ship's colour.</summary>
        private static Button MoveButton(string text, Color color, Action onClick)
        {
            Button button = Ui.GameButton(text, onClick, "metem-button-size__small");
            button.style.height = 32;
            button.style.minWidth = 40;
            button.style.fontSize = 16;
            button.style.paddingLeft = 6;
            button.style.paddingRight = 6;
            button.style.paddingTop = 0;
            button.style.paddingBottom = 0;
            Ui.Margin(button, 0, 0, 0, 4);
            button.style.unityBackgroundImageTintColor = color;
            return button;
        }

        // ---------------------------------------------------------------- the host's choices

        /// <summary>Host: how many ships (co-op) or ships per team (Fleet Battle).</summary>
        private static void SetCount(int count)
        {
            if (!PhotonNetwork.IsMasterClient) return;
            if (FleetApi.IsTeamMatch) HubFleet.RequestShipsPerTeam(count);
            else HubFleet.RequestShipCount(count);
            _signature = null;
        }

        /// <summary>Host: a ship's type, to the next in the terminal's order (co-op: and then ANY TYPE).</summary>
        private static void CycleType(int slot)
        {
            if (!PhotonNetwork.IsMasterClient) return;
            bool teams = FleetApi.IsTeamMatch;
            HubFleet.RequestType(slot, FleetSlots.NextType(HubFleet.ForcedType(slot), !teams));
            _signature = null;
        }

        private static void ToggleMirror()
        {
            if (!PhotonNetwork.IsMasterClient) return;
            HubFleet.RequestMirror(!HubFleet.MirrorTypes);
            _signature = null;
        }

        /// <summary>LEAVE asks once more: one slipped click (easy with a VR pointer) shouldn't drop a player to the main menu.</summary>
        private static void Leave()
        {
            if (Time.unscaledTime > _leaveArmedUntil)
            {
                _leaveArmedUntil = Time.unscaledTime + 4f;
                _leaveButton.text = "CLICK AGAIN TO LEAVE";
                _signature = null;
                return;
            }
            Log.Info("Leaving the room from the lobby screen.");
            Hide();
            GameStateMachine.Instance.ChangeState<GSQuitFromMenu>();
        }

        /// <summary>Puts the screen away for this player; TEAMS & RULES (or CREWS) on the FLEET panel brings it back.</summary>
        private static void HideLocally()
        {
            if (PvpTeams.PickingTeams)
            {
                _dismissed = true;
                _dismissedTeams = FleetApi.IsTeamMatch;
                _dismissedPlaced = FleetShips.CrewSlot(FleetShips.LocalActor) >= 0;
                Notice.Show(FleetApi.IsTeamMatch
                    ? "The team screen is hidden. TEAMS & RULES on the loadout terminal's FLEET panel brings it back."
                    : "The crew screen is hidden. CREWS on the loadout terminal's FLEET panel brings it back.");
            }
            _viewing = false;
            Hide();
        }

        /// <summary>Host: back out of FLEET BATTLE (the galaxy map is behind this screen) to choose another mission.</summary>
        private static void ChangeMission()
        {
            if (!PhotonNetwork.IsMasterClient) return;
            HubFleet.RequestVariant(HubVariant.None);
            Notice.ShowAll("The host is choosing another mission: FLEET BATTLE is off.");
        }
    }
}
