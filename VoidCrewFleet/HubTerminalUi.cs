using System;
using System.Reflection;
using Gameplay.Hub;
using HarmonyLib;
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
            _editingShipB = false;
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
                    if (_editingShipB) CustomizerLink.SetTerminalShip(1);
                    _editingShipB = false;
                    return;
                }
                bool two = HubFleet.PlannedMode != FleetMode.OneShip;
                bool locked = HubFleet.Variant != HubVariant.None;
                if (!two && _editingShipB) EditShip(false);

                Ui.SetButtonText(_twoButton, locked ? "FLEET MISSION\n2 SHIPS" : (two ? "TWO SHIPS: ON" : "TWO SHIPS: OFF"));
                Ui.SetButtonText(_shipAButton, "SHIP 1\n" + Short(HubFleet.ShipADef));
                Ui.SetButtonText(_shipBButton, !two ? "SHIP 2\nOFF"
                    : "SHIP 2\n" + (HubFleet.ChosenShipB.IsNull ? "AS SHIP 1" : Short(HubFleet.ShipBDef, HubFleet.ShipBName)));
                Highlight(_shipAButton, !_editingShipB);
                Highlight(_shipBButton, _editingShipB);
                _shipBButton.SetEnabled(two);
                _twoButton.SetEnabled(!locked);

                _hint.text = _editingShipB
                    ? "Choosing SHIP 2: pick a ship and loadout, then SELECT. Players are split evenly between the ships."
                    : (two ? "Choosing SHIP 1. Press SHIP 2 to choose the second ship." : "One ship, up to 8 players. Turn TWO SHIPS on for a second ship.");
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

        private static void Highlight(VisualElement button, bool on)
        {
            button.style.backgroundColor = on ? new Color(0.45f, 0.33f, 0.1f, 0.95f) : Ui.ButtonColor;
            Ui.Border(button, 1, on ? Ui.Gold : new Color(1f, 1f, 1f, 0.18f));
        }

        private static void ToggleTwoShips()
        {
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
