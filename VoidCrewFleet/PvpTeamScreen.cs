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
        private sealed class CursorRequest : IShowCursorSource
        {
        }

        private const int RowsPerTeam = HubFleet.MaxCrewPerShip;

        private static readonly CursorRequest CursorOwner = new CursorRequest();
        private static readonly VisualElement[] TeamLists = new VisualElement[2];
        private static readonly Label[] TeamCounts = new Label[2];
        private static readonly Label[] TeamShips = new Label[2];
        private static readonly VisualElement[] JoinButtons = new VisualElement[2];

        private static UIDocument _document;
        private static VisualElement _screen;
        private static VisualElement _waitingList;
        private static VisualElement _hostButtons;
        private static VisualElement _confirmButton;
        private static Label _status;
        private static Label _flash;
        private static float _flashUntil;
        private static string _signature;
        private static bool _visible;
        private static bool _cursorShown;

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
            Refresh();
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
                if (!PvpTeams.Choosing) return false;
                GameStateMachine machine = GameStateMachine.Instance;
                return machine != null && (machine.CurrentState is GSSpawn || machine.CurrentState is GSIngame);
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
                if (PvpTeams.Locked && Settings.PunchKey != null)
                    Notice.Show("Enemy players aboard your ship? Punch them with " + Settings.PunchKey.Value + " (five punches knock one out).");
            }
            SetCursor(false);
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
            return true;
        }

        private static void Build(VisualElement root)
        {
            _screen = new VisualElement { name = "FleetTeamScreen" };
            Ui.Fill(_screen);
            _screen.style.backgroundColor = new Color(0f, 0f, 0f, 0.9f);
            _screen.style.alignItems = Align.Center;
            _screen.style.justifyContent = Justify.Center;
            _screen.pickingMode = PickingMode.Position;

            VisualElement window = Ui.WindowBox(1060);
            window.style.alignItems = Align.Stretch;

            Label title = Ui.Label("FLEET BATTLE", 36, Ui.Gold, true);
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            window.Add(title);
            Label subtitle = Ui.Label("Choose your team. Each team crews one ship: destroy the other team's ship to win. " +
                                      "Teams stay even; with an odd number of players one team has one more.", 17, Ui.Text);
            subtitle.style.unityTextAlign = TextAnchor.MiddleCenter;
            Ui.Margin(subtitle, 6, 0, 18, 0);
            window.Add(subtitle);

            VisualElement columns = Ui.HorizontalRow();
            columns.style.alignItems = Align.Stretch;
            columns.style.justifyContent = Justify.SpaceBetween;
            columns.Add(TeamColumn(0));
            columns.Add(WaitingColumn());
            columns.Add(TeamColumn(1));
            window.Add(columns);

            _status = Ui.Label("", 17, Ui.Text);
            _status.style.unityTextAlign = TextAnchor.MiddleCenter;
            _status.style.marginTop = 16;
            window.Add(_status);

            _flash = Ui.Label("", 16, Ui.Warning, true);
            _flash.style.unityTextAlign = TextAnchor.MiddleCenter;
            _flash.style.marginTop = 4;
            _flash.style.minHeight = 20;
            window.Add(_flash);

            VisualElement bottom = Ui.HorizontalRow();
            bottom.style.justifyContent = Justify.Center;
            bottom.style.marginTop = 12;
            _hostButtons = Ui.HorizontalRow();
            VisualElement balance = Ui.Button("AUTO-BALANCE", PvpTeams.AutoBalance, 18);
            Ui.Margin(balance, 0, 12, 0, 0);
            _hostButtons.Add(balance);
            _confirmButton = Ui.Button("CONFIRM TEAMS", PvpTeams.Confirm, 18);
            _confirmButton.style.backgroundColor = new Color(0.45f, 0.33f, 0.1f, 0.95f);
            Ui.Border(_confirmButton, 1, Ui.Gold);
            Ui.Margin(_confirmButton, 0, 40, 0, 0);
            _hostButtons.Add(_confirmButton);
            bottom.Add(_hostButtons);
            VisualElement leave = Ui.Button("LEAVE MATCH", Leave, 18);
            Ui.Margin(leave, 0, 0, 0, 0);
            bottom.Add(leave);
            window.Add(bottom);

            _screen.Add(window);
            _screen.style.display = DisplayStyle.None;
            root.Add(_screen);
            Ui.Announce(_screen);
        }

        private static VisualElement TeamColumn(int team)
        {
            Color color = team == 0 ? Ui.TeamA : Ui.TeamB;
            VisualElement column = Ui.Column();
            column.style.width = 340;
            column.style.backgroundColor = new Color(color.r * 0.16f, color.g * 0.16f, color.b * 0.16f, 0.95f);
            Ui.Border(column, 2, color);
            Ui.Radius(column, 5);
            Ui.Padding(column, 10, 12);

            column.Add(Ui.Label(PvpTeams.TeamName(team) + " TEAM", 26, color, true));
            TeamShips[team] = Ui.Label("", 14, Ui.TextDim);
            column.Add(TeamShips[team]);
            TeamCounts[team] = Ui.Label("", 15, Ui.Text);
            Ui.Margin(TeamCounts[team], 4, 0, 8, 0);
            column.Add(TeamCounts[team]);

            TeamLists[team] = Ui.Column();
            TeamLists[team].style.minHeight = RowsPerTeam * 36;
            TeamLists[team].style.flexGrow = 1;
            column.Add(TeamLists[team]);

            JoinButtons[team] = Ui.Button("JOIN " + PvpTeams.TeamName(team), () => PvpTeams.Join(team), 18);
            Ui.Margin(JoinButtons[team], 10, 0, 0, 0);
            column.Add(JoinButtons[team]);
            return column;
        }

        private static VisualElement WaitingColumn()
        {
            VisualElement column = Ui.Column();
            column.style.width = 300;
            column.style.backgroundColor = new Color(0.1f, 0.1f, 0.11f, 0.95f);
            Ui.Border(column, 1, new Color(1f, 1f, 1f, 0.15f));
            Ui.Radius(column, 5);
            Ui.Padding(column, 10, 12);
            column.Add(Ui.Label("NOT ON A TEAM YET", 20, Ui.TextDim, true));
            Label hint = Ui.Label("Players here have not picked a side.", 14, Ui.TextDim);
            Ui.Margin(hint, 4, 0, 8, 0);
            column.Add(hint);
            _waitingList = Ui.Column();
            _waitingList.style.flexGrow = 1;
            column.Add(_waitingList);
            return column;
        }

        // ---------------------------------------------------------------- updating

        private static void Refresh()
        {
            if (_flash != null && _flash.text.Length > 0 && Time.unscaledTime > _flashUntil) _flash.text = "";

            Dictionary<int, int> teams = FleetShips.CrewMap();
            Player[] players = PhotonNetwork.PlayerList.OrderBy(p => p.ActorNumber).ToArray();
            bool host = PhotonNetwork.IsMasterClient;
            string signature = Signature(teams, players, host);
            if (signature == _signature) return;
            _signature = signature;

            try
            {
                for (int team = 0; team < 2; team++)
                {
                    TeamShips[team].text = "Ship " + (team + 1) + ": " + PvpTeams.ShipName(team);
                    TeamLists[team].Clear();
                }
                _waitingList.Clear();
                foreach (Player player in players)
                {
                    int team;
                    if (!teams.TryGetValue(player.ActorNumber, out team) || (team != 0 && team != 1)) team = -1;
                    (team >= 0 ? TeamLists[team] : _waitingList).Add(PlayerRow(player, team, host));
                }

                int max = PvpTeams.MaxPerTeam;
                int mine;
                if (!teams.TryGetValue(FleetShips.LocalActor, out mine)) mine = -1;
                for (int team = 0; team < 2; team++)
                {
                    int count = PvpTeams.Count(teams, team);
                    TeamCounts[team].text = count + " of " + max + (max == 1 ? " player" : " players");
                    JoinButtons[team].SetEnabled(mine != team && count < max);
                    Ui.SetButtonText(JoinButtons[team], mine == team ? "YOU ARE " + PvpTeams.TeamName(team) : "JOIN " + PvpTeams.TeamName(team));
                }

                string why;
                bool balanced = PvpTeams.Balanced(teams, out why);
                _hostButtons.style.display = host ? DisplayStyle.Flex : DisplayStyle.None;
                _confirmButton.SetEnabled(balanced);
                if (host)
                {
                    _status.text = balanced
                        ? "The teams are even. Press CONFIRM TEAMS to start the match."
                        : why + " Move players with their BLUE / RED buttons, or press AUTO-BALANCE.";
                }
                else
                {
                    _status.text = (mine < 0 ? "Pick a team. " : "") + "The host confirms the teams, then everyone spawns aboard their team's ship.";
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
            VisualElement row = Ui.HorizontalRow();
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.backgroundColor = Ui.Row;
            row.style.minHeight = 32;
            row.style.marginBottom = 4;
            Ui.Radius(row, 3);
            Ui.Padding(row, 3, 8);
            if (me) Ui.Border(row, 1, Ui.Gold);

            string name = string.IsNullOrEmpty(player.NickName) ? "Player " + player.ActorNumber : player.NickName;
            Label label = Ui.Label(name + (player.IsMasterClient ? "  (host)" : "") + (me ? "  (you)" : ""), 16, me ? Ui.Gold : Ui.Text, me);
            label.style.flexShrink = 1;
            label.style.overflow = Overflow.Hidden;
            row.Add(label);

            if (host)
            {
                int actor = player.ActorNumber;
                VisualElement moves = Ui.HorizontalRow();
                moves.style.flexShrink = 0;
                if (team != 0) moves.Add(MoveButton("BLUE", Ui.TeamA, () => PvpTeams.Move(actor, 0)));
                if (team != 1) moves.Add(MoveButton("RED", Ui.TeamB, () => PvpTeams.Move(actor, 1)));
                row.Add(moves);
            }
            return row;
        }

        private static VisualElement MoveButton(string text, Color color, Action onClick)
        {
            VisualElement button = Ui.Button(text, onClick, 13);
            Ui.Padding(button, 2, 8);
            Ui.Margin(button, 0, 0, 0, 6);
            Ui.Border(button, 1, color);
            return button;
        }

        private static void Leave()
        {
            Log.Info("PvP: leaving the match from the team screen.");
            Hide();
            GameStateMachine.Instance.ChangeState<GSQuitFromMenu>();
        }
    }
}
