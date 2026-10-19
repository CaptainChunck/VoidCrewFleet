using System;
using System.Reflection;
using Gameplay.Hub;
using HarmonyLib;
using Photon.Pun;
using ResourceAssets;
using UI.ShipLoadoutTerminal;
using UnityEngine;
using UnityEngine.UIElements;
using LoadoutTerminal = UI.ShipLoadoutTerminal.ShipLoadoutTerminal;

namespace VoidCrewFleet
{
    /// <summary>
    /// The hub's ship loadout terminal, made for two ships. A bar along the top of the screen switches two ships on or off
    /// and chooses which ship the terminal is setting up: SHIP 1 is the game's own selection (so the Ship Customizer's
    /// CUSTOM loadout works there as before), SHIP 2 is this plugin's. While SHIP 2 is chosen, SELECT picks ship 2's loadout
    /// and the list and SELECTED marker show ship 2's choice. Anyone in the room can use it; the host keeps the choice.
    /// </summary>
    internal static class HubTerminalUi
    {
        public const string Feature = "Two-ship loadout terminal";

        private static readonly FieldInfo LoadoutListField = GameMember.Field(typeof(LoadoutTerminal), "loadoutList");
        private static readonly FieldInfo DisplayField = GameMember.Field(typeof(LoadoutTerminal), "display");
        private static readonly MethodInfo DisplayMethod = GameMember.Method(typeof(LoadoutTerminal), "Display");

        private static bool _editingShipB;
        private static LoadoutTerminal _terminal;
        private static VisualElement _root;
        private static VisualElement _bar;
        private static Button _twoButton;
        private static ShipRow _shipARow;
        private static ShipRow _shipBRow;
        private static Label _hint;
        private static VisualElement _tint;
        private static bool _subscribed;

        private static bool Available
        {
            get { return HubFleet.HostHasFleet && Patcher.IsReady(Feature); }
        }

        // ---------------------------------------------------------------- hooks

        [GamePatch(Feature, "UI.ShipLoadoutTerminal.ShipLoadoutTerminal", "Display", PatchKind.Postfix)]
        private static void DisplayPostfix(LoadoutTerminal __instance, VisualElement ___root)
        {
            try
            {
                _terminal = __instance;
                if (___root != null) _root = ___root;
                if (!_subscribed)
                {
                    HubFleet.Changed += Refresh;
                    FleetApi.TeamsChanged += Refresh;
                    _subscribed = true;
                }
                EnsureBar();
                Refresh();
            }
            catch (Exception e)
            {
                Log.Failure("Adding the two-ship bar to the loadout terminal", e);
            }
        }

        /// <summary>SELECT while setting up ship 2 chooses ship 2's loadout instead of the game's (ship 1).</summary>
        [GamePatch(Feature, "UI.ShipLoadoutTerminal.ShipLoadoutTerminal", "OnShipChosen", PatchKind.Prefix)]
        private static bool ShipChosenPrefix(ShipLoadoutDataDef shipDef)
        {
            if (Available && shipDef != null && FleetApi.IsTeamMatch && PhotonNetwork.LocalPlayer != null)
            {
                int ship = _editingShipB ? 2 : 1;
                if (!FleetApi.CanEditShip(PhotonNetwork.LocalPlayer.ActorNumber, ship))
                {
                    string team = PvpTeams.TeamName(ship - 1);
                    Notice.Show(FleetApi.TeamOf(PhotonNetwork.LocalPlayer.ActorNumber) < 0
                        ? "Pick a team first: only a ship's own team can change its loadout."
                        : "Only " + team + " players can change the " + team + " ship.");
                    if (_root != null) _root.schedule.Execute(MarkChoice).ExecuteLater(300);
                    return false;
                }
            }
            if (!_editingShipB || !Available || shipDef == null) return true;
            if (HubFleet.LoadoutDef(shipDef.AssetGuid) == null)
            {
                Notice.Show(CustomizerLink.Installed
                    ? "Ship 2 can't have a CUSTOM loadout of its own with this version of Ship Customizer: update it, or leave ship 2 as a copy of ship 1."
                    : "Ship 2 can't use that loadout.");
            }
            else
            {
                HubFleet.RequestShipB(shipDef.AssetGuid);
            }
            // SELECT marks the shown loadout as chosen by itself; show ship 2's real choice again once that is done.
            if (_root != null) _root.schedule.Execute(MarkChoice).ExecuteLater(300);
            return false;
        }

        [GamePatch(Feature, "UI.ShipLoadoutTerminal.ShipLoadoutTerminal", "ShowSelection", PatchKind.Postfix)]
        private static void ShowSelectionPostfix()
        {
            if (_editingShipB && Available) MarkShipB();
        }

        [HarmonyBefore(CustomizerLink.PluginGuid)]
        [GamePatch(Feature, "UI.ShipLoadoutTerminal.ModuleLayout", "Show", PatchKind.Postfix)]
        private static void LayoutShowPostfix(ModuleLayout __instance, ShipLoadoutDataDef data)
        {
            try
            {
                if (_editingShipB && Available && data != null) __instance.Selected(!HubFleet.ChosenShipB.IsNull && data.AssetGuid == HubFleet.ChosenShipB);
            }
            catch (Exception e)
            {
                Log.Failure("Marking ship 2's loadout", e);
            }
        }

        [GamePatch(Feature, "UI.ShipLoadoutTerminal.ShipLoadoutTerminal", "OnDestroy", PatchKind.Prefix)]
        private static void TerminalDestroyPrefix(LoadoutTerminal __instance)
        {
            if (__instance != _terminal) return;
            if (_editingShipB) CustomizerLink.SetTerminalShip(1);
            _terminal = null;
            _root = null;
            _bar = null;
            _tint = null;
            _editingShipB = false;
        }

        // ---------------------------------------------------------------- the hub's mission panel

        private static Label _flowQuestLabel;
        private static Label _flowShipLabel;
        private static string _gameQuestText;
        private static string _gameShipText;
        private static bool _flowSubscribed;

        /// <summary>The hub's mission panel ("STANDARD / NO SHIP SELECTED") names both ships when there are two.</summary>
        [GamePatch(Feature, "HUBFlowDisplay", "ShipSelected", PatchKind.Postfix)]
        private static void FlowShipPostfix(Label ___selectedShipLabel)
        {
            _flowShipLabel = ___selectedShipLabel;
            _gameShipText = ___selectedShipLabel != null ? ___selectedShipLabel.text : null;
            WatchFleet();
            RefreshFlow();
        }

        /// <summary>... and the fleet mission (FLEET PILGRIMAGE, FLEET BATTLE), which the game shows as the mission it runs on.</summary>
        [GamePatch(Feature, "HUBFlowDisplay", "QuestSelected", PatchKind.Postfix)]
        private static void FlowQuestPostfix(Label ___selectedQuestLabel)
        {
            _flowQuestLabel = ___selectedQuestLabel;
            _gameQuestText = ___selectedQuestLabel != null ? ___selectedQuestLabel.text : null;
            WatchFleet();
            RefreshFlow();
        }

        private static void WatchFleet()
        {
            if (_flowSubscribed) return;
            HubFleet.Changed += RefreshFlow;
            _flowSubscribed = true;
        }

        private static void RefreshFlow()
        {
            try
            {
                bool two = Available && HubFleet.PlannedMode != FleetMode.OneShip;
                bool pvp = HubFleet.Variant == HubVariant.FleetBattle;
                if (_flowShipLabel != null && _flowShipLabel.panel != null && _gameShipText != null)
                {
                    _flowShipLabel.text = !two
                        ? _gameShipText
                        : Slot(pvp ? "<color=#5AA9FF>BLUE</color>" : "SHIP 1", _gameShipText) + "\n" +
                          Slot(pvp ? "<color=#FF5A4F>RED</color>" : "SHIP 2", HubFleet.ChosenShipB.IsNull ? "SAME AS SHIP 1" : FlowName(HubFleet.ShipBDef, HubFleet.ShipBName));
                }
                if (_flowQuestLabel != null && _flowQuestLabel.panel != null && _gameQuestText != null)
                {
                    string variant = !Available ? null
                        : HubFleet.Variant == HubVariant.FleetPilgrimage ? "FLEET PILGRIMAGE"
                        : HubFleet.Variant == HubVariant.FleetBattle ? "FLEET BATTLE (PVP)" : null;
                    _flowQuestLabel.text = variant != null ? "<b>" + variant + "</b>" : _gameQuestText;
                }
            }
            catch (Exception e)
            {
                Log.Failure("Showing both ships on the hub's mission panel", e);
            }
        }

        private static string Slot(string name, string ship)
        {
            return "<color=#C9B27A>" + name + "</color>  " + ship;
        }

        /// <summary>A loadout the way the panel names ship 1's: "<b>SHIP</b>: LOADOUT".</summary>
        private static string FlowName(ShipLoadoutDataDef def, string fallback)
        {
            if (def != null && def.ShipContextInfo != null && def.ContextInfo != null)
                return ("<b>" + def.ShipContextInfo.HeaderText + "</b>: " + def.ContextInfo.HeaderText).ToUpperInvariant();
            string text = def != null ? HubFleet.DescribeLoadout(def) : fallback;
            return string.IsNullOrEmpty(text) ? "-" : text.ToUpperInvariant();
        }

        // ---------------------------------------------------------------- the FLEET section

        /// <summary>Narrow enough to stay clear of the ship picture's hardpoints (the Ship Customizer makes them clickable).</summary>
        private const float BarWidth = 196f;

        /// <summary>A ship's row in the FLEET section: one of the terminal's own LOADOUTS rows, so it looks and highlights like them.</summary>
        private sealed class ShipRow
        {
            public VisualElement Root;
            public VisualElement Marker;
            public Label Title;
        }

        private static void EnsureBar()
        {
            if (_root == null || (_bar != null && _bar.parent == _root)) return;
            // The terminal's top-right corner, beside RESOURCES and above the ship picture, is free: a FLEET section in the
            // terminal's own style goes there, with its header and decorative line, its LOADOUTS rows and its small button.
            // The Ship Customizer's toolbar sits lower down, just above SELECT.
            _bar = new VisualElement { name = "FleetShipBar", pickingMode = PickingMode.Ignore };
            _bar.style.position = Position.Absolute;
            _bar.style.top = 50;
            _bar.style.right = 14;
            _bar.style.width = BarWidth;
            _bar.style.flexDirection = FlexDirection.Column;
            _bar.style.alignItems = Align.Stretch;

            _bar.Add(Ui.GameLabel("FLEET", "ui-text-size__large", "ui-text-color__white", "loadout-panel-header", "ui-text-dropshadow"));
            _bar.Add(Ui.GameDivider());

            _twoButton = Ui.GameButton("", ToggleTwoShips, "metem-button-size__small");
            Ui.Margin(_twoButton, 8, 0, 0, 0);
            _twoButton.style.paddingLeft = 8;
            _twoButton.style.paddingRight = 8;
            _twoButton.style.fontSize = 18;
            _bar.Add(_twoButton);

            // A faint backing keeps the rows readable over any ship picture; hover and selection paint over it.
            var rows = new VisualElement { pickingMode = PickingMode.Ignore };
            rows.style.marginTop = 6;
            rows.style.backgroundColor = new Color(0f, 0f, 0f, 0.45f);
            _shipARow = MakeShipRow(() => EditShip(false));
            _shipBRow = MakeShipRow(() => EditShip(true));
            rows.Add(_shipARow.Root);
            rows.Add(_shipBRow.Root);
            _bar.Add(rows);

            _hint = Ui.GameLabel("", "ui-text-color__white");
            _hint.style.fontSize = 16;
            _hint.style.whiteSpace = WhiteSpace.Normal;
            _hint.style.marginTop = 6;
            _hint.style.backgroundColor = new Color(Ui.GameDarkGrey.r, Ui.GameDarkGrey.g, Ui.GameDarkGrey.b, 0.9f);
            Ui.Padding(_hint, 5, 8);
            _bar.Add(_hint);
            VisualElement bottom = Ui.GameDivider();
            bottom.style.marginTop = 6;
            _bar.Add(bottom);

            _root.Add(_bar);
            _bar.BringToFront();
            Ui.Announce(_bar);
        }

        /// <summary>A row made from the terminal's own LOADOUTS row template; by hand, with the same classes, if it is missing.</summary>
        private static ShipRow MakeShipRow(Action onClick)
        {
            var row = new ShipRow();
            try
            {
                VisualTreeAsset template = _terminal != null ? _terminal.ListEntryPrefab : null;
                if (template != null) row.Root = template.Instantiate();
            }
            catch (Exception e)
            {
                Log.Failure("Building a FLEET row from the terminal's own list row", e);
                row.Root = null;
            }
            if (row.Root != null)
            {
                row.Title = row.Root.Q<Label>("Title");
                row.Marker = row.Root.Q("RarityMarker");
                // Its padlock means "rank too low", which never applies here.
                VisualElement padlock = row.Root.Q("LockIcon");
                if (padlock != null) padlock.style.display = DisplayStyle.None;
            }
            if (row.Root == null || row.Title == null)
            {
                row.Root = new VisualElement();
                var entry = new VisualElement { pickingMode = PickingMode.Ignore };
                entry.style.flexDirection = FlexDirection.Row;
                entry.style.alignItems = Align.Center;
                entry.style.paddingTop = 6;
                entry.style.paddingBottom = 6;
                row.Marker = new VisualElement { pickingMode = PickingMode.Ignore };
                row.Marker.style.width = 10;
                row.Marker.style.alignSelf = Align.Stretch;
                row.Marker.style.marginRight = 10;
                row.Title = Ui.GameLabel("", "ui-text-size__mediumPlus");
                entry.Add(row.Marker);
                entry.Add(row.Title);
                row.Root.Add(entry);
            }
            row.Root.AddToClassList("metem-list-entry");
            row.Root.AddToClassList("audio-hover__list");
            row.Root.AddToClassList("audio-click__list");
            row.Title.style.fontSize = 18;
            row.Title.style.whiteSpace = WhiteSpace.Normal;
            row.Title.style.flexShrink = 1;
            row.Title.enableRichText = true;
            Ui.Click(row.Root, onClick);
            return row;
        }

        /// <summary>A row's two lines ("SHIP 1: DESTROYER" over "ROOKIE"), whether it is the ship being set up, and its team colour.</summary>
        private static void SetRow(ShipRow row, string slot, string ship, string loadout, bool selected, Color? team, bool enabled)
        {
            row.Title.text = "<size=80%>" + slot + (string.IsNullOrEmpty(ship) ? "" : ": " + ship) + "</size>\n" + loadout;
            row.Root.SetEnabled(enabled);
            row.Root.EnableInClassList("unity-collection-view__item--selected", selected && enabled);
            if (row.Marker != null) row.Marker.style.backgroundColor = team.HasValue ? new StyleColor(team.Value) : new StyleColor(StyleKeyword.Null);
        }

        private static void Refresh()
        {
            if (_bar == null) return;
            try
            {
                bool available = Available;
                _bar.style.display = available ? DisplayStyle.Flex : DisplayStyle.None;
                if (!available)
                {
                    ShowTint(Color.clear);
                    if (_editingShipB) CustomizerLink.SetTerminalShip(1);
                    _editingShipB = false;
                    return;
                }
                bool two = HubFleet.PlannedMode != FleetMode.OneShip;
                bool locked = HubFleet.Variant != HubVariant.None;
                bool teams = FleetApi.IsTeamMatch;
                if (!two && _editingShipB) EditShip(false);

                _twoButton.text = teams ? "TEAMS & RULES" : (two ? "TWO SHIPS: ON" : "TWO SHIPS: OFF");
                _twoButton.SetEnabled(!locked || teams);

                string ship, loadout;
                SplitName(HubFleet.ShipADef, null, out ship, out loadout);
                SetRow(_shipARow, teams ? "BLUE" : "SHIP 1", ship, loadout, !_editingShipB, teams ? FleetApi.ShipColor(1) : (Color?)null, true);
                if (!two)
                {
                    ship = null;
                    loadout = "OFF";
                }
                else if (HubFleet.ChosenShipB.IsNull)
                {
                    ship = null;
                    loadout = "AS SHIP 1";
                }
                else
                {
                    SplitName(HubFleet.ShipBDef, HubFleet.ShipBName, out ship, out loadout);
                }
                SetRow(_shipBRow, teams ? "RED" : "SHIP 2", ship, loadout, _editingShipB, teams ? FleetApi.ShipColor(2) : (Color?)null, two);

                if (teams)
                {
                    int shipNumber = _editingShipB ? 2 : 1;
                    string team = PvpTeams.TeamName(shipNumber - 1);
                    int actor = PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : -1;
                    int mine = FleetApi.TeamOf(actor);
                    _hint.text = FleetApi.CanEditShip(actor, shipNumber)
                        ? "The " + team + " ship: pick its ship and loadout, then SELECT."
                        : (mine < 0 ? "You're not on a team: the host can open TEAMS & RULES again." : "The " + team + " ship: only " + team + " players can change it.");
                }
                else
                {
                    _hint.text = _editingShipB
                        ? "Setting up SHIP 2: pick a ship and loadout, then SELECT."
                        : (two ? "Setting up SHIP 1. Press SHIP 2 to set up the second ship." : "One ship, up to 8 players. Turn TWO SHIPS on for a second ship.");
                }
                ShowTint(teams ? FleetApi.ShipColor(_editingShipB ? 2 : 1) : Color.clear);
                if (_editingShipB) MarkShipB();
            }
            catch (Exception e)
            {
                Log.Failure("Updating the FLEET section", e);
            }
        }

        /// <summary>A loadout as the FLEET rows show it: the ship ("DESTROYER") and the loadout ("ROOKIE").</summary>
        private static void SplitName(ShipLoadoutDataDef def, string fallback, out string ship, out string loadout)
        {
            string text = def != null ? HubFleet.DescribeLoadout(def) : fallback;
            if (string.IsNullOrEmpty(text))
            {
                ship = null;
                loadout = "-";
                return;
            }
            text = text.Replace("METEM ", "").ToUpperInvariant();
            int dash = text.IndexOf(" - ", System.StringComparison.Ordinal);
            ship = dash > 0 ? text.Substring(0, dash) : null;
            loadout = dash > 0 ? text.Substring(dash + 3) : text;
        }

        /// <summary>Fleet Battle: the terminal's background takes the team colour of the ship being set up.</summary>
        private static void ShowTint(Color colour)
        {
            if (_root == null) return;
            if (colour.a <= 0f)
            {
                if (_tint != null) _tint.style.display = DisplayStyle.None;
                return;
            }
            // Over the terminal's background picture (its #Terminal element draws it) and under everything on it.
            VisualElement host = _root.Q("Terminal") ?? _root;
            if (_tint == null || _tint.parent != host)
            {
                _tint = new VisualElement { name = "FleetTeamTint", pickingMode = PickingMode.Ignore };
                _tint.style.position = Position.Absolute;
                _tint.style.left = 0;
                _tint.style.right = 0;
                _tint.style.top = 0;
                _tint.style.bottom = 0;
                host.Insert(0, _tint);
            }
            _tint.style.backgroundColor = new Color(colour.r, colour.g, colour.b, 0.14f);
            _tint.style.display = DisplayStyle.Flex;
        }

        private static void ToggleTwoShips()
        {
            if (FleetApi.IsTeamMatch)
            {
                // TEAMS & RULES: while the teams are picked it brings this player's lobby screen back (after HIDE). Once they
                // are confirmed, the host opens team picking again for everyone (the teams are kept until changed), and the
                // other players look at the teams and the rules.
                if (PvpTeams.PickingTeams || !PhotonNetwork.IsMasterClient) PvpTeamScreen.ShowLocally();
                else PvpTeams.Reset();
                return;
            }
            if (HubFleet.Variant != HubVariant.None) return;
            HubFleet.RequestTwoShips(HubFleet.PlannedMode == FleetMode.OneShip);
        }

        /// <summary>Switches which ship the terminal sets up, and shows that ship's current loadout.</summary>
        private static void EditShip(bool shipB)
        {
            if (shipB && HubFleet.PlannedMode == FleetMode.OneShip) return;
            _editingShipB = shipB;
            // Before showing it: the Ship Customizer's CUSTOM then shows and edits this ship's own layout.
            CustomizerLink.SetTerminalShip(shipB ? 2 : 1);
            ShipLoadoutDataDef def = shipB ? HubFleet.ShipBDef : HubFleet.ShipADef;
            ShowLoadout(def);
            Refresh();
        }

        private static void ShowLoadout(ShipLoadoutDataDef def)
        {
            if (_terminal == null || def == null) return;
            try
            {
                var list = LoadoutListField != null ? LoadoutListField.GetValue(_terminal) as ShipLoadoutList : null;
                if (list != null)
                {
                    // List that loadout's ship first (CUSTOM is listed only with its own ship).
                    if (!list.ContainsLoadout(def)) list.OnShipEntryClicked(def.ShipGuid);
                    if (list.ContainsLoadout(def))
                    {
                        list.ShipListSelected(def.ShipGuid);
                        list.LoadoutPicked(def);
                        list.Select(def);
                    }
                }
                if (DisplayMethod != null) DisplayMethod.Invoke(_terminal, new object[] { def });
            }
            catch (Exception e)
            {
                Log.Failure("Showing a ship's loadout on the terminal", e);
            }
        }

        /// <summary>The list and SELECTED marker show ship 2's choice while ship 2 is being set up.</summary>
        /// <summary>After a refused or redirected SELECT: the SELECTED mark shows the real choice of the ship being set up.</summary>
        private static void MarkChoice()
        {
            if (_terminal == null) return;
            try
            {
                var display = DisplayField != null ? DisplayField.GetValue(_terminal) as ModuleLayout : null;
                if (display == null || display.CurrentLoadout == null) return;
                if (_editingShipB)
                {
                    if (HubFleet.ChosenShipB.IsNull) display.Selected(false);
                    else MarkShipB();
                    return;
                }
                ShipLoadoutDataDef chosen = HubShipManager.Instance != null ? HubShipManager.Instance.CurrentShipSelected : null;
                display.Selected(chosen != null && chosen.AssetGuid == display.CurrentLoadout.AssetGuid);
            }
            catch (Exception e)
            {
                Log.Failure("Marking the chosen loadout", e);
            }
        }

        private static void MarkShipB()
        {
            if (_terminal == null || !_editingShipB || HubFleet.ChosenShipB.IsNull) return;
            try
            {
                ShipLoadoutDataDef shipB = HubFleet.ShipBDef;
                var list = LoadoutListField != null ? LoadoutListField.GetValue(_terminal) as ShipLoadoutList : null;
                var display = DisplayField != null ? DisplayField.GetValue(_terminal) as ModuleLayout : null;
                if (list != null && shipB != null) list.LoadoutPicked(shipB);
                if (display != null) display.Selected(shipB != null && display.CurrentLoadout != null && display.CurrentLoadout.AssetGuid == shipB.AssetGuid);
            }
            catch (Exception e)
            {
                Log.Failure("Marking ship 2's loadout", e);
            }
        }
    }
}
