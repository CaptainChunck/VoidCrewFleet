using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CG.GameLoopStateMachine;
using CG.GameLoopStateMachine.GameStates;
using CG.Input;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// The PvP team screen. After a PvP match has loaded, everyone waits in the game's own spawn queue (so nobody times
    /// out) and this screen covers the loading fade: two team columns, the players not on a team yet, and a JOIN button
    /// per team. The host also gets BLUE / RED buttons on every player, AUTO-BALANCE and CONFIRM TEAMS; confirming locks
    /// the teams and everyone is cloned aboard their team's ship. LEAVE MATCH goes back to the main menu the way the
    /// game's own menu does. The screen is a UI Toolkit panel, so VR UI Fix shows it in the headset like the game's menus.
    /// </summary>
    internal static class PvpTeamScreen
    {
        /// <summary>This screen's requests to the game: show the cursor, and switch its controls to menu mode.</summary>
        private sealed class CursorRequest : IShowCursorSource, IInputActionMapRequest
        {
        }

        private const int RowsPerTeam = HubFleet.MaxCrewPerShip;

        private static readonly CursorRequest CursorOwner = new CursorRequest();
        private static readonly VisualElement[] TeamLists = new VisualElement[2];
        private static readonly Label[] TeamCounts = new Label[2];
        private static readonly Label[] TeamShips = new Label[2];
        private static readonly Button[] JoinButtons = new Button[2];

        private static UIDocument _document;
        private static VisualElement _screen;
        private static VisualElement _waitingList;
        private static VisualElement _hostButtons;
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
        private static bool _visible;
        private static bool _cursorShown;
        private static bool _inputBlocked;
        private static float _inputAsserted;
        private static bool _dismissed;
        private static bool _viewing;
        private static float _leaveArmedUntil;
        private static Button _hideButton;
        private static Button _changeButton;

        public static bool Visible
        {
            get { return _visible; }
        }

        /// <summary>A few times a second, from the runtime.</summary>
        public static void Tick()
        {
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
                Log.Info("PvP: showing the team screen.");
            }
            SetCursor(true);
            SetInput(true);
            Refresh();
        }

        /// <summary>
        /// Brings the lobby screen up for the local player: back after HIDE while the teams are picked, or, once they are
        /// confirmed, to look at the teams and the ship rules (read-only).
        /// </summary>
        public static void ShowLocally()
        {
            _dismissed = false;
            _viewing = true;
            Tick();
        }

        /// <summary>A notice for the local player (a full team, a refused confirm) while the screen hides the chat.</summary>
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
                    return !_dismissed && !MenuScreenController.IsOpen();
                }
                _dismissed = false;
                if (!_viewing || !FleetApi.IsTeamMatch || !GameSessionManager.InHub)
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

        private static void Hide()
        {
            if (_visible)
            {
                _visible = false;
                if (_screen != null) _screen.style.display = DisplayStyle.None;
                Log.Info("PvP: the team screen is closed.");
                if (PvpTeams.Locked && Settings.PunchKey != null && !GameSessionManager.InHub)
                    Notice.Show("Enemy players aboard your ship? Punch them with " + Settings.PunchKey.Value + " (five punches knock one out).");
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
                Log.Failure("Switching the game's controls for the team screen", e);
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
                Log.Failure("Showing the mouse cursor for the team screen", e);
                _cursorShown = show;
            }
        }

        // ---------------------------------------------------------------- building

        private static bool EnsureScreen()
        {
            if (_document != null && _screen != null && _screen.panel != null) return true;
            if (_document == null)
            {
                _document = Ui.CreateOverlay("Void Crew Fleet team screen", 5000);
                if (_document == null) return false;
            }
            VisualElement root = _document.rootVisualElement;
            if (root == null) return false;
            root.pickingMode = PickingMode.Ignore;
            Build(root);
            // A rebuilt screen starts hidden: let Tick show it again.
            _visible = false;
            return true;
        }

        /// <summary>The lobby screen's width (in the game's 1920 x 1080 reference pixels) and its three sections'.</summary>
        private const float ScreenWidth = 1640f;
        public const float SectionWidth = 500f;

        private static void Build(VisualElement root)
        {
            Ui.UseGameStyles(root);
            _screen = new VisualElement { name = "FleetTeamScreen" };
            Ui.Fill(_screen);
            _screen.style.backgroundColor = Ui.GameBackdrop;
            _screen.style.alignItems = Align.Center;
            _screen.style.justifyContent = Justify.Center;
            _screen.pickingMode = PickingMode.Position;

            // A screen in the style of the game's hub terminals (the loadout terminal's): its background picture, the
            // meander bars along the top and bottom, sections with big white headers over its decorative lines, rows like
            // its RESOURCES list, and its big hex button for the one thing to do at the end.
            var panel = new VisualElement { name = "Terminal" };
            panel.style.width = ScreenWidth;
            panel.style.backgroundColor = Ui.GameItemBackground;
            Sprite background = Ui.GameSprite("UIBackground01IL");
            if (background != null) panel.style.backgroundImage = new StyleBackground(background);
            Ui.Border(panel, 1, Ui.GameLightGrey);

            VisualElement topBar = Ui.GameDecoBar();
            Ui.Margin(topBar, 15, 0, 10, 0);
            panel.Add(topBar);

            var body = new VisualElement();
            Ui.Padding(body, 0, 30);

            VisualElement headerRow = Ui.HorizontalRow();
            headerRow.style.justifyContent = Justify.SpaceBetween;
            Label title = Ui.GameLabel("FLEET BATTLE", "ui-text-size__large", "ui-text-color__white", "loadout-panel-header", "ui-text-dropshadow");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            headerRow.Add(title);
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
            Label subtitle = Ui.GameLabel("Pick a team. Each team crews one ship; destroy the other team's ship to win. Teams stay even.",
                "ui-text-size__medium", "ui-text-color__white", "ui-text-dropshadow");
            subtitle.style.flexShrink = 1;
            infoRow.Add(subtitle);
            _rulesSummary = Ui.GameLabel("", "ui-text-size__medium", "ui-text-dropshadow");
            _rulesSummary.style.unityFontStyleAndWeight = FontStyle.Bold;
            _rulesSummary.style.unityTextAlign = TextAnchor.MiddleRight;
            _rulesSummary.style.flexShrink = 1;
            _rulesSummary.style.marginLeft = 20;
            infoRow.Add(_rulesSummary);
            body.Add(infoRow);

            _teamsView = Ui.HorizontalRow();
            _teamsView.style.alignItems = Align.Stretch;
            _teamsView.style.justifyContent = Justify.SpaceBetween;
            _teamsView.style.height = ShipRulesView.Height;
            _teamsView.style.marginTop = 14;
            _teamsView.Add(TeamColumn(0));
            _teamsView.Add(WaitingColumn());
            _teamsView.Add(TeamColumn(1));
            body.Add(_teamsView);
            _rulesView = ShipRulesView.Build();
            _rulesView.style.marginTop = 14;
            _rulesView.style.display = DisplayStyle.None;
            body.Add(_rulesView);

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
            Button balance = Ui.GameButton("AUTO-BALANCE", PvpTeams.AutoBalance, "metem-button-size__small");
            Ui.Margin(balance, 0, 12, 0, 0);
            _hostButtons.Add(balance);
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

        private static VisualElement TeamColumn(int team)
        {
            Label header;
            VisualElement content;
            VisualElement section = Ui.GameSection(PvpTeams.TeamName(team) + " TEAM", SectionWidth, out header, out content);
            header.style.color = FleetApi.TeamColor(team);

            TeamShips[team] = Ui.GameLabel("", "ui-text-size__medium", "ui-text-color__white", "ui-text-dropshadow");
            content.Add(TeamShips[team]);
            TeamCounts[team] = Ui.GameLabel("", "ui-text-size__medium", "ui-text-dropshadow");
            TeamCounts[team].style.color = Ui.GameBrightGrey;
            TeamCounts[team].style.marginBottom = 8;
            content.Add(TeamCounts[team]);

            // Scrolls when a team is long (8 players) on a small screen.
            TeamLists[team] = new ScrollView(ScrollViewMode.Vertical);
            TeamLists[team].style.flexGrow = 1;
            content.Add(TeamLists[team]);

            JoinButtons[team] = Ui.GameButton("JOIN " + PvpTeams.TeamName(team), () =>
            {
                if (FleetShips.CrewSlot(FleetShips.LocalActor) != team) PvpTeams.Join(team);
            }, "metem-button-size__small");
            Ui.Margin(JoinButtons[team], 8, 0, 0, 0);
            content.Add(JoinButtons[team]);
            return section;
        }

        private static VisualElement WaitingColumn()
        {
            Label header;
            VisualElement content;
            VisualElement section = Ui.GameSection("NO TEAM YET", SectionWidth, out header, out content);
            Label hint = Ui.GameLabel("Players here have not picked a side.", "ui-text-size__medium", "ui-text-dropshadow");
            hint.style.color = Ui.GameBrightGrey;
            hint.style.marginBottom = 8;
            content.Add(hint);
            // Everyone starts here when FLEET BATTLE is chosen: up to 16 players, so it scrolls.
            _waitingList = new ScrollView(ScrollViewMode.Vertical);
            _waitingList.style.flexGrow = 1;
            content.Add(_waitingList);
            return section;
        }

        // ---------------------------------------------------------------- updating

        internal static void ShowRules(bool on)
        {
            _showRules = on && ShipRulesView.Available;
            _teamsView.style.display = _showRules ? DisplayStyle.None : DisplayStyle.Flex;
            _rulesView.style.display = _showRules ? DisplayStyle.Flex : DisplayStyle.None;
            _teamsTab.EnableInClassList("metem-button-selected", !_showRules);
            _rulesTab.EnableInClassList("metem-button-selected", _showRules);
            if (_showRules) ShipRulesView.Refresh();
        }

        private static void RefreshRules()
        {
            bool available = ShipRulesView.Available;
            _tabs.style.display = available ? DisplayStyle.Flex : DisplayStyle.None;
            _rulesSummary.style.display = available ? DisplayStyle.Flex : DisplayStyle.None;
            if (!available)
            {
                if (_showRules) ShowRules(false);
                return;
            }
            ShipRules.Rules rules = ShipRules.Shown;
            _rulesSummary.text = "SHIP RULES: " + ShipRules.Describe(rules).ToUpperInvariant();
            _rulesSummary.style.color = rules.Restricts ? Ui.GameOrange : Ui.GameBrightGrey;
            _teamsTab.EnableInClassList("metem-button-selected", !_showRules);
            _rulesTab.EnableInClassList("metem-button-selected", _showRules);
            if (_showRules) ShipRulesView.Refresh();
        }

        private static void Refresh()
        {
            if (_flash != null && _flash.text.Length > 0 && Time.unscaledTime > _flashUntil) _flash.text = "";
            try
            {
                RefreshRules();
            }
            catch (Exception e)
            {
                Log.Failure("Updating the ship rules on the team screen", e);
            }

            Dictionary<int, int> teams = FleetShips.CrewMap();
            Player[] players = PhotonNetwork.PlayerList.OrderBy(p => p.ActorNumber).ToArray();
            bool host = PhotonNetwork.IsMasterClient;
            bool picking = PvpTeams.PickingTeams;
            string signature = Signature(teams, players, host) + (picking ? "|picking" : "|locked") + (Time.unscaledTime < _leaveArmedUntil ? "|leave" : "");
            if (signature == _signature) return;
            _signature = signature;

            try
            {
                for (int team = 0; team < 2; team++)
                {
                    TeamShips[team].text = ("Ship " + (team + 1) + ": " + PvpTeams.ShipName(team)).ToUpperInvariant();
                    TeamLists[team].Clear();
                }
                _waitingList.Clear();
                foreach (Player player in players)
                {
                    int team;
                    if (!teams.TryGetValue(player.ActorNumber, out team) || (team != 0 && team != 1)) team = -1;
                    (team >= 0 ? TeamLists[team] : _waitingList).Add(PlayerRow(player, team, host && picking));
                }

                int max = PvpTeams.MaxPerTeam;
                int mine;
                if (!teams.TryGetValue(FleetShips.LocalActor, out mine)) mine = -1;
                for (int team = 0; team < 2; team++)
                {
                    int count = PvpTeams.Count(teams, team);
                    TeamCounts[team].text = (count + " of " + max + (max == 1 ? " player" : " players")).ToUpperInvariant();
                    // Your own team's button is filled in its colour (the game's selected button); a full team's is disabled.
                    Button join = JoinButtons[team];
                    bool joined = mine == team;
                    bool full = !joined && count >= max;
                    join.text = joined ? "YOU ARE " + PvpTeams.TeamName(team) : (full ? PvpTeams.TeamName(team) + " IS FULL" : "JOIN " + PvpTeams.TeamName(team));
                    join.style.display = picking || joined ? DisplayStyle.Flex : DisplayStyle.None;
                    join.SetEnabled(!full && picking);
                    join.EnableInClassList("metem-button-selected", joined);
                    join.style.unityBackgroundImageTintColor = full ? new StyleColor(StyleKeyword.Null) : new StyleColor(FleetApi.TeamColor(team));
                }

                string why;
                bool balanced = PvpTeams.Balanced(teams, out why);
                _hostButtons.style.display = host && picking ? DisplayStyle.Flex : DisplayStyle.None;
                _confirmButton.SetEnabled(balanced);
                bool inHub = GameSessionManager.InHub;
                if (_leaveButton != null) _leaveButton.text = Time.unscaledTime < _leaveArmedUntil ? "CLICK AGAIN TO LEAVE" : (inHub ? "LEAVE LOBBY" : "LEAVE MATCH");
                _hideButton.text = picking ? "HIDE" : "CLOSE";
                _hideButton.style.display = inHub ? DisplayStyle.Flex : DisplayStyle.None;
                _changeButton.style.display = host && picking && inHub ? DisplayStyle.Flex : DisplayStyle.None;
                if (!picking)
                {
                    _status.text = "The teams are confirmed. " + (host
                        ? "To change them or the ship rules, press TEAMS & RULES on the FLEET panel."
                        : "Only the host can change the teams and the ship rules.");
                }
                else if (host)
                {
                    _status.text = balanced
                        ? (inHub
                            ? "The teams are even. Press CONFIRM TEAMS, then set up the ships and sit in the ready room to start the match."
                            : "The teams are even. Press CONFIRM TEAMS to start the match.")
                        : why + " Move players with their BLUE / RED buttons, or press AUTO-BALANCE.";
                }
                else
                {
                    _status.text = (mine < 0 ? "Pick a team. " : "") + (inHub
                        ? "The host confirms the teams; then set up your team's ship at the loadout terminal."
                        : "The host confirms the teams, then everyone spawns aboard their team's ship.");
                }
            }
            catch (Exception e)
            {
                Log.Failure("Updating the team screen", e);
            }
        }

        private static string Signature(Dictionary<int, int> teams, Player[] players, bool host)
        {
            var text = new StringBuilder();
            text.Append(host ? 'H' : 'C').Append(PhotonNetwork.MasterClient != null ? PhotonNetwork.MasterClient.ActorNumber : 0);
            foreach (Player player in players)
            {
                int team;
                if (!teams.TryGetValue(player.ActorNumber, out team)) team = -1;
                text.Append('|').Append(player.ActorNumber).Append(':').Append(team).Append(':').Append(player.NickName);
            }
            return text.ToString();
        }

        private static VisualElement PlayerRow(Player player, int team, bool host)
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
            string name = string.IsNullOrEmpty(player.NickName) ? "Player " + player.ActorNumber : player.NickName;
            Label label = Ui.GameLabel(name, "ui-text-size__medium");
            label.style.fontSize = 22;
            label.style.color = me ? Ui.GameOrange : Color.white;
            label.style.unityFontStyleAndWeight = me ? FontStyle.Bold : FontStyle.Normal;
            label.style.flexShrink = 1;
            label.style.overflow = Overflow.Hidden;
            left.Add(label);
            if (player.IsMasterClient) left.Add(Tag("HOST"));
            if (me) left.Add(Tag("YOU"));
            row.Add(left);

            if (host)
            {
                int actor = player.ActorNumber;
                VisualElement moves = Ui.HorizontalRow();
                moves.style.flexShrink = 0;
                if (team != 0) moves.Add(MoveButton("BLUE", FleetApi.TeamColor(0), () => PvpTeams.Move(actor, 0)));
                if (team != 1) moves.Add(MoveButton("RED", FleetApi.TeamColor(1), () => PvpTeams.Move(actor, 1)));
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

        /// <summary>The game's small button, shrunk to fit a row, with its outline in the team's colour.</summary>
        private static Button MoveButton(string text, Color color, Action onClick)
        {
            Button button = Ui.GameButton(text, onClick, "metem-button-size__small");
            button.style.height = 32;
            button.style.fontSize = 16;
            button.style.paddingLeft = 12;
            button.style.paddingRight = 12;
            button.style.paddingTop = 0;
            button.style.paddingBottom = 0;
            Ui.Margin(button, 0, 0, 0, 6);
            button.style.unityBackgroundImageTintColor = color;
            return button;
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
            Log.Info("PvP: leaving the match from the team screen.");
            Hide();
            GameStateMachine.Instance.ChangeState<GSQuitFromMenu>();
        }

        /// <summary>Puts the screen away for this player; TEAMS & RULES on the FLEET panel brings it back.</summary>
        private static void HideLocally()
        {
            if (PvpTeams.PickingTeams)
            {
                _dismissed = true;
                Notice.Show("The team screen is hidden. TEAMS & RULES on the loadout terminal's FLEET panel brings it back.");
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
