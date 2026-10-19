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
        private static VisualElement _twoButton;
        private static VisualElement _shipAButton;
        private static VisualElement _shipBButton;
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
                    if (_root != null && _editingShipB) _root.schedule.Execute(MarkShipB).ExecuteLater(300);
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
            if (_root != null) _root.schedule.Execute(MarkShipB).ExecuteLater(300);
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

        // ---------------------------------------------------------------- the bar

        private static void EnsureBar()
        {
            if (_root == null || (_bar != null && _bar.parent == _root)) return;
            // The terminal's right-hand strip, beside RESOURCES and above SELECT, is empty: a column of buttons goes there
            // (the Ship Customizer's own buttons sit just above SELECT, below this column).
            _bar = new VisualElement { name = "FleetShipBar" };
            _bar.style.position = Position.Absolute;
            _bar.style.top = 70;
            _bar.style.right = 14;
            _bar.style.width = 190;
            _bar.style.flexDirection = FlexDirection.Column;
            _bar.style.alignItems = Align.Stretch;
            _bar.style.backgroundColor = Ui.Window;
            Ui.Padding(_bar, 8, 8);
            Ui.Radius(_bar, 5);
            Ui.Border(_bar, 1, new Color(1f, 1f, 1f, 0.15f));

            Label title = Ui.Label("FLEET", 16, Ui.Gold, true);
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            title.style.marginBottom = 6;
            _bar.Add(title);

            _twoButton = ColumnButton(ToggleTwoShips);
            _shipAButton = ColumnButton(() => EditShip(false));
            _shipBButton = ColumnButton(() => EditShip(true));
            _bar.Add(_twoButton);
            _bar.Add(_shipAButton);
            _bar.Add(_shipBButton);

            _hint = Ui.Label("", 12, Ui.TextDim);
            _hint.style.marginTop = 6;
            _hint.style.unityTextAlign = TextAnchor.UpperLeft;
            _bar.Add(_hint);

            _root.Add(_bar);
            _bar.BringToFront();
            Ui.Announce(_bar);
        }

        private static VisualElement ColumnButton(Action onClick)
        {
            VisualElement button = Ui.Button("", onClick, 13);
            Ui.Margin(button, 0, 0, 5, 0);
            Ui.Padding(button, 5, 6);
            button.style.justifyContent = Justify.FlexStart;
            var label = button.Q<Label>();
            if (label != null)
            {
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.unityTextAlign = TextAnchor.MiddleLeft;
            }
            return button;
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

                Ui.SetButtonText(_twoButton, teams ? "FLEET BATTLE\nTEAMS" : (locked ? "FLEET MISSION\n2 SHIPS" : (two ? "TWO SHIPS: ON" : "TWO SHIPS: OFF")));
                Ui.SetButtonText(_shipAButton, (teams ? "BLUE SHIP\n" : "SHIP 1\n") + Short(HubFleet.ShipADef));
                Ui.SetButtonText(_shipBButton, !two ? "SHIP 2\nOFF"
                    : (teams ? "RED SHIP\n" : "SHIP 2\n") + (HubFleet.ChosenShipB.IsNull ? "AS SHIP 1" : Short(HubFleet.ShipBDef, HubFleet.ShipBName)));
                Highlight(_shipAButton, !_editingShipB, teams ? FleetApi.ShipColor(1) : (Color?)null);
                Highlight(_shipBButton, _editingShipB, teams ? FleetApi.ShipColor(2) : (Color?)null);
                _shipBButton.SetEnabled(two);
                _twoButton.SetEnabled(!locked || teams);

                if (teams)
                {
                    int ship = _editingShipB ? 2 : 1;
                    string team = PvpTeams.TeamName(ship - 1);
                    int actor = PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : -1;
                    int mine = FleetApi.TeamOf(actor);
                    _hint.text = "The " + team + " team's ship. " + (FleetApi.CanEditShip(actor, ship)
                        ? "You're " + team + ": pick its ship and loadout, then SELECT."
                        : (mine < 0 ? "Pick a team first (FLEET BATTLE TEAMS)." : "Only " + team + " players can change it; you can look."));
                }
                else
                {
                    _hint.text = _editingShipB
                        ? "Choosing SHIP 2: pick a ship and loadout, then SELECT. Players are split evenly between the ships."
                        : (two ? "Choosing SHIP 1. Press SHIP 2 to choose the second ship." : "One ship, up to 8 players. Turn TWO SHIPS on for a second ship.");
                }
                ShowTint(teams ? FleetApi.ShipColor(_editingShipB ? 2 : 1) : Color.clear);
                if (_editingShipB) MarkShipB();
            }
            catch (Exception e)
            {
                Log.Failure("Updating the two-ship bar", e);
            }
        }

        private static string Short(ShipLoadoutDataDef def, string fallback = null)
        {
            string text = def != null ? HubFleet.DescribeLoadout(def) : fallback;
            if (string.IsNullOrEmpty(text)) return "-";
            return text.Replace("METEM ", "").ToUpperInvariant();
        }

        /// <summary>The ship being set up is highlighted; in Fleet Battle each ship's button wears its team's colour.</summary>
        private static void Highlight(VisualElement button, bool on, Color? team = null)
        {
            if (team.HasValue)
            {
                Color colour = team.Value;
                button.style.backgroundColor = new Color(colour.r * 0.35f, colour.g * 0.35f, colour.b * 0.35f, on ? 0.95f : 0.7f);
                Ui.Border(button, on ? 2 : 1, on ? Ui.Gold : colour);
                return;
            }
            button.style.backgroundColor = on ? new Color(0.45f, 0.33f, 0.1f, 0.95f) : Ui.ButtonColor;
            Ui.Border(button, 1, on ? Ui.Gold : new Color(1f, 1f, 1f, 0.18f));
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
            if (_tint == null || _tint.parent != _root)
            {
                _tint = new VisualElement { name = "FleetTeamTint", pickingMode = PickingMode.Ignore };
                _tint.style.position = Position.Absolute;
                _tint.style.left = 0;
                _tint.style.right = 0;
                _tint.style.top = 0;
                _tint.style.bottom = 0;
                _root.Insert(0, _tint);
            }
            _tint.style.backgroundColor = new Color(colour.r, colour.g, colour.b, 0.2f);
            _tint.style.display = DisplayStyle.Flex;
        }

        private static void ToggleTwoShips()
        {
            if (FleetApi.IsTeamMatch)
            {
                // FLEET BATTLE TEAMS: the host opens team picking again for everyone.
                if (PhotonNetwork.IsMasterClient) PvpTeams.Reset();
                else Notice.Show("Only the host can open team picking again.");
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
