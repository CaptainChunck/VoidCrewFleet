using System;
using System.Collections.Generic;
using System.Reflection;
using CG.Game.Player;
using Gameplay.Chat;
using Photon.Pun;
using UI.Core;
using UI.MainHUD;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// The HUD's crew list (the game's CoopBuddiesPanel: an 80 px row for each other player, made for Void Crew's 4 players;
    /// 9 fit above the player's own panel), kept to what fits in a big room:
    /// - before teams are picked (and in the co-op hub, and in one-ship games): every player, in the game's list on the left;
    /// - in the hub once Fleet Battle's teams are confirmed: BLUE in the game's list on the left, RED in a list of the same rows
    ///   on the right;
    /// - in a fleet mission: the players on the local player's own ship (7 at most, CrewRules.MaxCrew), then the other ships'
    ///   cards (ShipCards).
    /// A list with more players than fit shows one row fewer and, in its place, the game's own key prompt for the whole list
    /// (PlayerListPanel): [ for the left one (everyone, or BLUE), ] for the right one (RED), as set in the config. With teams,
    /// players on neither (who joined once the teams were confirmed and wait for a place) are in BLUE's whole list, and the
    /// left prompt shows whenever there are any, with their count. The game's rows are only hidden or moved, so their voice and
    /// health updates carry on.
    /// </summary>
    internal static class CrewList
    {
        public const string Feature = "Crew list per ship";

        private enum Mode
        {
            Everyone,
            Teams,
            OwnShip
        }

        /// <summary>The most player rows in a list (9 fit): with more players, 7 and the key prompt.</summary>
        private const int ListRows = 8;

        /// <summary>The most player rows in a mission: a full crew's others.</summary>
        private const int CrewRows = CrewRules.MaxCrew - 1;

        /// <summary>The game's crew row: 289 px wide, 20 px into its place (CoopBuddyStyles' .coop-buddy).</summary>
        private const float RowWidth = 309f;

        private static readonly FieldInfo ViewsField = GameMember.Field(typeof(CoopBuddiesPanel), "view");
        private static readonly FieldInfo RowField = GameMember.Field(typeof(CoopBuddyView), "container");
        private static readonly PropertyInfo PromptKey = GameMember.Property(typeof(KeyBindVE), "Key");

        private static readonly StyleEnum<DisplayStyle> Shown = new StyleEnum<DisplayStyle>(StyleKeyword.Null);
        private static readonly StyleEnum<DisplayStyle> Hidden = new StyleEnum<DisplayStyle>(DisplayStyle.None);

        private static readonly Dictionary<VisualElement, int> ActorOfRow = new Dictionary<VisualElement, int>();
        private static readonly Dictionary<int, VisualElement> RowOfActor = new Dictionary<int, VisualElement>();
        /// <summary>The players with a row, in the order their rows first appeared (the game's own order: as they joined).</summary>
        private static readonly List<int> Order = new List<int>();
        private static readonly List<VisualElement> StandIns = new List<VisualElement>();

        // The game's HUD and its crew list panel.
        private static MainHUDUI _hudUi;
        private static UIDocument _hudDocument;
        private static CoopBuddiesPanel _panel;

        // The HUD the lists were set up in: set up again for a new document or tree.
        private static UIDocument _hud;
        private static VisualElement _hudRoot;
        private static VisualElement _list;
        private static VisualElement _right;
        private static KeyBindVE _leftPrompt;
        private static KeyBindVE _rightPrompt;
        private static float _indent = float.NaN;
        private static float _rightTop = float.NaN;

        // What the lists were last set for: nothing is worked out again while all of these stay the same.
        private static Mode _mode;
        private static object _crewStamp;
        private static int _rowCount = -1;
        private static int _players = -1;
        private static bool _shownOnce;

        private static InputAction _leftKey;
        private static InputAction _rightKey;
        private static bool _preview;

        /// <summary>Whether the whole list's window was made ahead of its first use (once, when a prompt first showed).</summary>
        private static bool _windowPrepared;

        /// <summary>A few times a second.</summary>
        public static void Tick()
        {
            Refresh(false);
            ShipCards.Tick(_list != null && _mode == Mode.OwnShip);
            if (!_preview) FollowList();
        }

        /// <summary>
        /// The game's menu key (Escape) with the whole-list window open closes the window only: the game's menu action stays
        /// on while its controls are in menu mode, so it would also open the escape menu.
        /// </summary>
        [GamePatch(Feature, "ToggleInGameMenu", "TryToggleMenu", PatchKind.Prefix, Optional = true)]
        private static bool MenuKeyPrefix()
        {
            if (!PlayerListPanel.IsOpen) return true;
            PlayerListPanel.Close();
            return false;
        }

        /// <summary>A player's row is added: shown, hidden or moved at once, not a quarter of a second later.</summary>
        [GamePatch(Feature, "UI.MainHUD.CoopBuddiesPanel", "PlayerSpawned", PatchKind.Postfix, Optional = true)]
        private static void PlayerSpawnedPostfix()
        {
            Refresh(true);
        }

        /// <summary>Every frame: the keys that open the whole lists, and the open list itself.</summary>
        public static void Frame()
        {
            try
            {
                bool usable = _list != null && _mode != Mode.OwnShip && PhotonNetwork.InRoom;
                SetKeys(usable || PlayerListPanel.IsOpen);
                if (_leftKey != null && _leftKey.enabled && KeysFree())
                {
                    if (_leftKey.WasPressedThisFrame()) PlayerListPanel.Toggle(_mode == Mode.Teams ? PlayerListPanel.Kind.Blue : PlayerListPanel.Kind.Everyone);
                    else if (_rightKey.WasPressedThisFrame() && _mode == Mode.Teams) PlayerListPanel.Toggle(PlayerListPanel.Kind.Red);
                }
                if (PlayerListPanel.IsOpen && !usable) PlayerListPanel.Close();
                PlayerListPanel.Frame();
            }
            catch (Exception e)
            {
                Log.Failure("The player list's keys", e);
            }
        }

        /// <summary>For the self-test's reports: the rows shown in each list, and the prompts.</summary>
        internal static string Describe()
        {
            if (_list == null) return "not set up";
            return _mode + ": " + Count(_list) + " rows on the left" + (PromptShown(_leftPrompt) ? " + prompt \"" + PromptText(_leftPrompt) + "\"" : "") + ", " +
                   (_right != null && _right.resolvedStyle.display != DisplayStyle.None ? Count(_right) + " on the right" + (PromptShown(_rightPrompt) ? " + prompt \"" + PromptText(_rightPrompt) + "\"" : "") : "none on the right") +
                   "; cards: " + ShipCards.Describe();
        }

        private static int Count(VisualElement list)
        {
            int shown = 0;
            for (int i = 0; i < list.childCount; i++)
            {
                VisualElement child = list[i];
                if ((ActorOfRow.ContainsKey(child) || StandIns.Contains(child)) && child.resolvedStyle.display != DisplayStyle.None) shown++;
            }
            return shown;
        }

        private static bool PromptShown(KeyBindVE prompt)
        {
            return prompt != null && prompt.resolvedStyle.display != DisplayStyle.None;
        }

        private static string PromptText(KeyBindVE prompt)
        {
            Label key = prompt.Q<Label>("Key");
            Label action = prompt.Q<Label>("Action");
            return (key != null ? key.text : "") + "  " + (action != null ? action.text : "");
        }

        private static void Refresh(bool force)
        {
            if (_preview) return;
            try
            {
                MainHUDUI hudUi = MainHUDUI.Instance;
                if (hudUi == null) return;
                if (hudUi != _hudUi)
                {
                    _hudUi = hudUi;
                    _hudDocument = hudUi.GetComponent<UIDocument>();
                    _panel = hudUi.GetComponent<CoopBuddiesPanel>();
                }
                UIDocument hud = _hudDocument;
                VisualElement root = hud != null ? hud.rootVisualElement : null;
                var views = _panel != null ? GameMember.Read<Dictionary<Player, CoopBuddyView>>(ViewsField, _panel, null) : null;
                if (root == null || views == null) return;
                if (hud != _hud || root != _hudRoot || (_list != null && _list.panel == null && root.panel != null))
                {
                    Build(hud, root);
                    force = true;
                }
                if (_list == null) return;
                // The list's own indent (40 px in the game's style sheet): the cards stand out of it, under the ship panel's rows.
                float indent = _list.resolvedStyle.paddingLeft;
                if (!float.IsNaN(indent) && indent != _indent)
                {
                    _indent = indent;
                    ShipCards.SetIndent(indent);
                }

                bool inHub = GameSessionManager.InHub;
                Mode mode = inHub
                    ? (FleetApi.IsTeamMatch && PvpTeams.HubFleetPlanned && PvpTeams.Locked ? Mode.Teams : Mode.Everyone)
                    : (FleetSession.ShipCount >= 2 ? Mode.OwnShip : Mode.Everyone);
                object crewStamp = FleetShips.CrewStamp;
                int players = PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.PlayerCount : 0;
                if (!force && _shownOnce && mode == _mode && ReferenceEquals(crewStamp, _crewStamp) && views.Count == _rowCount && players == _players) return;
                _shownOnce = true;
                _mode = mode;
                _crewStamp = crewStamp;
                _rowCount = views.Count;
                _players = players;
                ShowPlayers(views, mode == Mode.Everyone ? new Dictionary<int, CrewEntry>() : FleetShips.Crews(), mode);
            }
            catch (Exception e)
            {
                Log.Failure("Keeping the crew list to what fits", e);
            }
        }

        /// <summary>The right list, the key prompts and the ship cards' places, in a HUD document or tree that is new.</summary>
        private static void Build(UIDocument hud, VisualElement root)
        {
            if (_right != null)
            {
                // The game's rows in it go back to the game's list (the old one goes with its document).
                if (_list != null && _list.panel != null)
                {
                    while (_right.childCount > 0)
                    {
                        VisualElement row = _right[0];
                        if (ActorOfRow.ContainsKey(row)) _list.Add(row);
                        else row.RemoveFromHierarchy();
                    }
                }
                _right.RemoveFromHierarchy();
            }
            if (_leftPrompt != null) _leftPrompt.RemoveFromHierarchy();
            ActorOfRow.Clear();
            RowOfActor.Clear();
            Order.Clear();
            _hud = hud;
            _hudRoot = root;
            _indent = float.NaN;
            _rightTop = float.NaN;
            _shownOnce = false;
            _list = root.Q("CoopBuddiesContainer");
            _right = null;
            _leftPrompt = null;
            _rightPrompt = null;
            if (_list == null)
            {
                Log.WarnOnce("crew list", "The game's HUD has no crew list (CoopBuddiesContainer), so it isn't kept to what fits.");
                return;
            }
            // The right list stands in the HUD's right column (with the objectives), mirroring the game's list on the left.
            VisualElement rightSide = root.Q("RightSide") ?? root;
            _right = new VisualElement { name = "FleetRedList", pickingMode = PickingMode.Ignore };
            _right.style.position = Position.Absolute;
            _right.style.right = 20;
            _right.style.width = RowWidth;
            _right.style.flexDirection = FlexDirection.Column;
            _right.style.display = DisplayStyle.None;
            rightSide.Add(_right);
            ShipCards.Attach(root, _list);

            SetKeys(true);
            _leftPrompt = Prompt("FleetPlayersPrompt", _list, _leftKey);
            _rightPrompt = Prompt("FleetRedPrompt", _right, _rightKey);
        }

        /// <summary>
        /// The game's own key prompt (KeyBindVE: its key cap and action text) for one of the list keys, at the end of a list. Its
        /// key is given once it is attached: attaching makes a prompt look its key up among the game's own actions, and warn when
        /// it isn't one of them.
        /// </summary>
        private static KeyBindVE Prompt(string name, VisualElement list, InputAction key)
        {
            string label = "?";
            try
            {
                string control;
                string device;
                InputControlPath.ToHumanReadableString(key.bindings[0].effectivePath, out device, out control, InputControlPath.HumanReadableStringOptions.OmitDevice);
                if (device == "Keyboard" && Keyboard.current != null && Keyboard.current[control] != null) label = Keyboard.current[control].displayName;
            }
            catch
            {
                // the prompt makes its own key cap from the action
            }
            KeyBindVE prompt = null;
            try
            {
                bool late = PromptKey != null;
                prompt = new KeyBindVE(name, late ? "" : label, false);
                prompt.pickingMode = PickingMode.Ignore;
                prompt.style.marginLeft = 20;
                prompt.style.marginTop = 4;
                prompt.style.display = DisplayStyle.None;
                list.Add(prompt);
                if (late) PromptKey.SetValue(prompt, label, null);
                prompt.Init(null, key);
                return prompt;
            }
            catch (Exception e)
            {
                // The lists still keep to what fits; the whole list is opened with the key all the same.
                if (prompt != null) prompt.RemoveFromHierarchy();
                Log.WarnOnce("crew list prompt", "The game's key prompt couldn't be made for the player lists (" + e.Message + "); they show without it.");
                Log.Verbose("The key prompt's failure: " + e);
                return null;
            }
        }

        /// <summary>Which rows show where, for the mode, and the prompts of lists that don't all fit.</summary>
        private static void ShowPlayers(Dictionary<Player, CoopBuddyView> views, Dictionary<int, CrewEntry> crews, Mode mode)
        {
            ActorOfRow.Clear();
            RowOfActor.Clear();
            foreach (KeyValuePair<Player, CoopBuddyView> view in views)
            {
                VisualElement row = GameMember.Read<VisualElement>(RowField, view.Value, null);
                Photon.Realtime.Player owner = view.Key != null ? view.Key.PhotonPlayer : null;
                if (row == null || owner == null) continue;
                ActorOfRow[row] = owner.ActorNumber;
                RowOfActor[owner.ActorNumber] = row;
            }
            Order.RemoveAll(actor => !RowOfActor.ContainsKey(actor));
            for (int i = 0; i < _list.childCount; i++)
            {
                int actor;
                if (ActorOfRow.TryGetValue(_list[i], out actor) && !Order.Contains(actor)) Order.Add(actor);
            }
            foreach (int actor in RowOfActor.Keys)
            {
                if (!Order.Contains(actor)) Order.Add(actor);
            }

            Photon.Realtime.Room room = PhotonNetwork.CurrentRoom;
            ICollection<int> inRoom = room != null ? (ICollection<int>)room.Players.Keys : new int[0];
            List<int> left;
            List<int> red = null;
            List<int> right = null;
            int leftPlayers = 0;
            int rightPlayers = 0;
            // Teams: the players on neither team (who joined once they were confirmed and wait for a place), counted in the
            // left prompt, which then always shows: BLUE's whole list has them.
            int teamless = 0;
            if (mode == Mode.OwnShip)
            {
                left = CrewRules.Listed(Order, crews, FleetShips.LocalActor, CrewRows);
            }
            else if (mode == Mode.Teams)
            {
                List<int> blue = CrewRules.OfTeam(Order, crews, 0);
                red = CrewRules.OfTeam(Order, crews, 1);
                teamless = CrewRules.WithoutTeam(new List<int>(inRoom), crews).Count;
                left = blue.GetRange(0, CrewRules.RowsShown(blue.Count, ListRows, teamless > 0));
                right = red.GetRange(0, CrewRules.RowsShown(red.Count, ListRows));
                leftPlayers = blue.Count;
                rightPlayers = red.Count;
            }
            else
            {
                left = Order.GetRange(0, CrewRules.RowsShown(Order.Count, ListRows));
                leftPlayers = Order.Count;
            }

            bool moved = false;
            foreach (int actor in Order)
            {
                VisualElement row = RowOfActor[actor];
                VisualElement place = red != null && red.Contains(actor) ? _right : _list;
                if (row.parent != place)
                {
                    place.Add(row);
                    moved = true;
                }
                row.style.display = left.Contains(actor) || (right != null && right.Contains(actor)) ? Shown : Hidden;
            }
            // Each list in the players' own order, its prompt under its rows.
            if (moved)
            {
                foreach (int actor in Order) RowOfActor[actor].BringToFront();
            }

            if (mode == Mode.Teams)
            {
                SetPrompt(_leftPrompt, leftPlayers > ListRows || teamless > 0, "BLUE TEAM  ·  " + Players(CrewRules.TeamSize(crews, inRoom, 0, true)) +
                                                                                (teamless > 0 ? "  ·  " + teamless + " WAITING" : ""));
                SetPrompt(_rightPrompt, rightPlayers > ListRows, "RED TEAM  ·  " + Players(CrewRules.TeamSize(crews, inRoom, 1, true)));
            }
            else
            {
                SetPrompt(_leftPrompt, leftPlayers > ListRows, "ALL " + Players(room != null ? room.PlayerCount : leftPlayers + 1));
                SetPrompt(_rightPrompt, false, null);
            }
            if (_leftPrompt != null) _leftPrompt.BringToFront();
            if (_rightPrompt != null) _rightPrompt.BringToFront();
        }

        private static string Players(int count)
        {
            return count + (count == 1 ? " PLAYER" : " PLAYERS");
        }

        private static void SetPrompt(KeyBindVE prompt, bool show, string text)
        {
            if (prompt == null) return;
            prompt.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show) return;
            Label action = prompt.Q<Label>("Action");
            if (action != null && action.text != text) action.text = text;
            if (_windowPrepared) return;
            // The whole list's window is made now, while its key is only offered, not at the first press (making a panel
            // takes a moment).
            _windowPrepared = true;
            PlayerListPanel.Prepare();
        }

        /// <summary>
        /// A few times a second: the right list shows only for teams, hides with the game's list (on a terminal's screen, say),
        /// and starts level with it.
        /// </summary>
        private static void FollowList()
        {
            if (_right == null || _list == null) return;
            bool show = _mode == Mode.Teams && _list.resolvedStyle.display != DisplayStyle.None && _list.panel != null;
            _right.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show || _right.parent == null) return;
            float top = _right.parent.WorldToLocal(_list.worldBound.position).y;
            if (float.IsNaN(top) || Mathf.Abs(top - _rightTop) < 0.5f) return;
            _rightTop = top;
            _right.style.top = top;
        }

        private static void SetKeys(bool on)
        {
            if (_leftKey == null)
            {
                if (!on) return;
                // The game's key prompts read an action's own interactions, which an action made with its binding leaves null:
                // made with none (an empty list, as the game's own actions have), then given the binding.
                _leftKey = new InputAction("Fleet player list", InputActionType.Button, null, "");
                _leftKey.AddBinding(Settings.PlayerListKey.Value);
                _rightKey = new InputAction("Fleet RED team list", InputActionType.Button, null, "");
                _rightKey.AddBinding(Settings.RedListKey.Value);
            }
            if (on == _leftKey.enabled) return;
            if (on)
            {
                _leftKey.Enable();
                _rightKey.Enable();
            }
            else
            {
                _leftKey.Disable();
                _rightKey.Disable();
            }
        }

        /// <summary>The keys are free: no text being typed, and no game menu up (the list itself aside).</summary>
        private static bool KeysFree()
        {
            TextChat chat = TextChat.Instance;
            if (chat != null && chat.TextingEnabled) return false;
            return PlayerListPanel.IsOpen || !MenuScreenController.IsOpen();
        }

        /// <summary>
        /// Self-test, alone in the room: how a big room's lists look, from stand-in rows made from the game's own row template
        /// ("everyone": 7 and the prompt; "teams": 7 and a prompt each side; "own": a full crew over the ship cards); null puts
        /// the real lists back.
        /// </summary>
        internal static void PreviewForTest(string kind)
        {
            try
            {
                foreach (VisualElement row in StandIns) row.RemoveFromHierarchy();
                StandIns.Clear();
                _preview = kind != null;
                if (_list == null) return;
                if (kind == null)
                {
                    Refresh(true);
                    FollowList();
                    return;
                }
                VisualTreeAsset template = _panel != null ? _panel.CoopBuddyPrefab : null;
                if (template == null) return;
                bool teams = kind == "teams";
                AddStandIns(template, _list, kind == "own" ? CrewRows : ListRows - 1, 2);
                if (teams) AddStandIns(template, _right, ListRows - 1, 9);
                // Teams: with a player waiting for a place, the longest the left prompt gets.
                SetPrompt(_leftPrompt, kind != "own", teams ? "BLUE TEAM  ·  12 PLAYERS  ·  1 WAITING" : "ALL 24 PLAYERS");
                SetPrompt(_rightPrompt, teams, "RED TEAM  ·  12 PLAYERS");
                if (_leftPrompt != null) _leftPrompt.BringToFront();
                if (_rightPrompt != null) _rightPrompt.BringToFront();
                _right.style.display = teams ? DisplayStyle.Flex : DisplayStyle.None;
                if (teams)
                {
                    _rightTop = float.NaN;
                    float top = _right.parent.WorldToLocal(_list.worldBound.position).y;
                    if (!float.IsNaN(top)) _right.style.top = top;
                }
                Log.Info("[autotest] Crew list preview (" + kind + "): " + StandIns.Count + " stand-in rows.");
            }
            catch (Exception e)
            {
                Log.Failure("[autotest] crew list preview", e);
            }
        }

        private static void AddStandIns(VisualTreeAsset template, VisualElement list, int count, int firstNumber)
        {
            // Over the ship cards, as the game's rows are.
            VisualElement cards = list.Q("FleetShipCards");
            int at = cards != null && cards.parent == list ? list.IndexOf(cards) : list.childCount;
            for (int i = 0; i < count; i++)
            {
                TemplateContainer row = template.Instantiate();
                Label name = row.Q<Label>("PlayerName");
                if (name != null) name.text = "Crewmate " + (firstNumber + i);
                Label rank = row.Q<Label>("RankLabel");
                if (rank != null) rank.text = (10 + (firstNumber + i) * 7).ToString();
                // As the game sets a row up (CoopBuddyView): its menu and mute marks hidden, a healthy player.
                foreach (string part in new[] { "ActionPanel", "ChatMuteState", "VoiceMuteState", "MicrophoneMutedState" })
                {
                    VisualElement hidden = row.Q(part);
                    if (hidden != null) hidden.style.display = DisplayStyle.None;
                }
                StateVE state = row.Q<StateVE>();
                if (state != null) state.SetClassState("player-hud-health", "high");
                list.Insert(at + i, row);
                StandIns.Add(row);
            }
        }
    }
}
