using System;
using System.Collections.Generic;
using System.Linq;
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
    /// The hub's ship loadout terminal, made for a fleet. A FLEET section in the terminal's top-right corner shows how many
    /// ships the game's own missions use (the host's SHIPS button sets it; in Fleet Battle TEAMS & RULES opens the lobby
    /// screen), lists every ship, and chooses which ship the terminal is setting up: SHIP 1 is the game's own selection (so
    /// the Ship Customizer's CUSTOM loadout works there as before), ships 2-6 are this plugin's. Using the terminal starts on
    /// the player's own ship. While another ship is chosen, SELECT picks that ship's loadout and the list and SELECTED marker
    /// show its choice. Anyone in the room can set up a ship (in Fleet Battle, only a ship's own team); the host keeps the
    /// choices, and refuses a loadout of another type than the one the host forced for that ship.
    /// </summary>
    internal static class HubTerminalUi
    {
        public const string Feature = "Fleet loadout terminal";

        private static readonly FieldInfo LoadoutListField = GameMember.Field(typeof(LoadoutTerminal), "loadoutList");
        private static readonly FieldInfo DisplayField = GameMember.Field(typeof(LoadoutTerminal), "display");
        private static readonly MethodInfo DisplayMethod = GameMember.Method(typeof(LoadoutTerminal), "Display");

        private static int _editingSlot;
        private static LoadoutTerminal _terminal;
        private static VisualElement _root;
        private static VisualElement _bar;
        private static Button _countButton;
        private static Button _crewsButton;
        private static readonly List<ShipRow> Rows = new List<ShipRow>();
        private static Label _hint;
        private static VisualElement _tint;
        private static bool _subscribed;

        private static bool Available
        {
            get { return HubFleet.HostHasFleet && Patcher.IsReady(Feature); }
        }

        private static bool EditingOther
        {
            get { return _editingSlot > 0; }
        }

        // ---------------------------------------------------------------- hooks

        [GamePatch(Feature, "UI.ShipLoadoutTerminal.ShipLoadoutTerminal", "Display", PatchKind.Postfix)]
        private static void DisplayPostfix(LoadoutTerminal __instance, VisualElement ___root)
        {
            try
            {
                bool opened = __instance != _terminal;
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
                if (opened) ShowOwnShip();
            }
            catch (Exception e)
            {
                Log.Failure("Adding the FLEET section to the loadout terminal", e);
            }
        }

        /// <summary>Each time a player starts using the terminal, it shows the ship they crew.</summary>
        [GamePatch(Feature, "UI.ShipLoadoutTerminal.ShipLoadoutTerminal", "OnEnterUI", PatchKind.Postfix, Optional = true)]
        private static void EnterPostfix(LoadoutTerminal __instance)
        {
            try
            {
                if (__instance == _terminal) ShowOwnShip();
            }
            catch (Exception e)
            {
                Log.Failure("Showing your own ship at the loadout terminal", e);
            }
        }

        /// <summary>The terminal switches to the ship the player crews (their pick on the lobby screen), when it shows another.</summary>
        private static void ShowOwnShip()
        {
            if (_root == null || !Available) return;
            int own = FleetShips.CrewSlot(FleetShips.LocalActor);
            if (own < 0 || own == _editingSlot || own >= HubFleet.PlannedShips) return;
            _root.schedule.Execute(() => EditShip(own)).ExecuteLater(50);
        }

        /// <summary>
        /// SELECT: in Fleet Battle only a ship's own team may change it, and a ship whose type the host forced only takes a
        /// loadout of that type. While setting up a ship 2-6, it chooses that ship's loadout instead of the game's (ship 1).
        /// The host checks the team again when the choice reaches it (HubFleet: CheckLoadout for ships 2-6, SelectShipPrefix
        /// for ship 1).
        /// </summary>
        [GamePatch(Feature, "UI.ShipLoadoutTerminal.ShipLoadoutTerminal", "OnShipChosen", PatchKind.Prefix)]
        private static bool ShipChosenPrefix(ShipLoadoutDataDef shipDef)
        {
            if (!Available || shipDef == null) return true;
            int slot = Mathf.Clamp(_editingSlot, 0, FleetSlots.MaxSlots - 1);
            string refusal = null;
            if (FleetApi.IsTeamMatch && PhotonNetwork.LocalPlayer != null && !FleetApi.CanEditShip(PhotonNetwork.LocalPlayer.ActorNumber, slot + 1))
            {
                string team = PvpTeams.TeamName(FleetSlots.TeamOf(slot, true));
                refusal = FleetApi.TeamOf(PhotonNetwork.LocalPlayer.ActorNumber) < 0
                    ? "Pick a ship first: only a ship's own team can change its loadout."
                    : "Only " + team + " players can change " + HubFleet.ShipLabel(slot) + ".";
            }
            GUIDUnion type = HubFleet.ForcedType(slot);
            if (refusal == null && !type.IsNull && HubFleet.TypeOf(shipDef) != type)
                refusal = HubFleet.ShipLabel(slot) + " must be a " + FleetSlots.TypeName(type) + " (the host's choice).";
            if (refusal == null && slot > 0 && CustomizerLink.IsCustom(shipDef.AssetGuid) && !CustomizerLink.CanCustomize(slot + 1))
                refusal = "CUSTOM on " + HubFleet.ShipLabel(slot) + " needs Ship Customizer 0.9.0 or later.";
            if (refusal == null && slot > 0 && HubFleet.LoadoutDef(shipDef.AssetGuid) == null)
                refusal = HubFleet.ShipLabel(slot) + " can't use that loadout.";
            if (refusal != null)
            {
                Notice.Show(refusal);
                if (_root != null) _root.schedule.Execute(MarkChoice).ExecuteLater(300);
                return false;
            }
            if (slot == 0) return true;
            HubFleet.RequestLoadout(slot, shipDef.AssetGuid);
            // SELECT marks the shown loadout as chosen by itself; show the ship's real choice again once that is done.
            if (_root != null) _root.schedule.Execute(MarkChoice).ExecuteLater(300);
            return false;
        }

        [GamePatch(Feature, "UI.ShipLoadoutTerminal.ShipLoadoutTerminal", "ShowSelection", PatchKind.Postfix)]
        private static void ShowSelectionPostfix()
        {
            if (EditingOther && Available) MarkOther();
        }

        [HarmonyBefore(CustomizerLink.PluginGuid)]
        [GamePatch(Feature, "UI.ShipLoadoutTerminal.ModuleLayout", "Show", PatchKind.Postfix)]
        private static void LayoutShowPostfix(ModuleLayout __instance, ShipLoadoutDataDef data)
        {
            try
            {
                if (!EditingOther || !Available || data == null) return;
                GUIDUnion chosen = HubFleet.ChosenLoadout(_editingSlot);
                __instance.Selected(!chosen.IsNull && data.AssetGuid == chosen);
            }
            catch (Exception e)
            {
                Log.Failure("Marking a ship's loadout", e);
            }
        }

        [GamePatch(Feature, "UI.ShipLoadoutTerminal.ShipLoadoutTerminal", "OnDestroy", PatchKind.Prefix)]
        private static void TerminalDestroyPrefix(LoadoutTerminal __instance)
        {
            if (__instance != _terminal) return;
            if (EditingOther) CustomizerLink.SetTerminalShip(1);
            _terminal = null;
            _root = null;
            _bar = null;
            _tint = null;
            Rows.Clear();
            _editingSlot = 0;
        }

        // ---------------------------------------------------------------- the hub's mission panel

        private static Label _flowQuestLabel;
        private static Label _flowShipLabel;
        private static string _gameQuestText;
        private static string _gameShipText;
        private static bool _flowSubscribed;

        /// <summary>The hub's mission panel ("STANDARD / NO SHIP SELECTED") names every ship when there are several.</summary>
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
                int ships = Available ? HubFleet.PlannedShips : 1;
                bool pvp = HubFleet.Variant == HubVariant.FleetBattle;
                if (_flowShipLabel != null && _flowShipLabel.panel != null && _gameShipText != null)
                {
                    if (ships < 2)
                    {
                        _flowShipLabel.text = _gameShipText;
                    }
                    else if (pvp)
                    {
                        // One line per team: its ships' types.
                        _flowShipLabel.text = TeamLine(0, ships) + "\n" + TeamLine(1, ships);
                    }
                    else
                    {
                        var lines = new List<string> { Slot("SHIP 1", _gameShipText) };
                        for (int slot = 1; slot < ships; slot++)
                            lines.Add(Slot("SHIP " + (slot + 1), HubFleet.CopiesShip1(slot) ? "SAME AS SHIP 1" : FlowName(HubFleet.ShipDef(slot), HubFleet.ChosenName(slot))));
                        _flowShipLabel.text = string.Join("\n", lines.ToArray());
                    }
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
                Log.Failure("Showing the fleet on the hub's mission panel", e);
            }
        }

        private static string TeamLine(int team, int ships)
        {
            string colour = ColorUtility.ToHtmlStringRGB(FleetApi.TeamColor(team));
            IEnumerable<string> types = FleetSlots.SlotsOfTeam(team, ships).Select(slot => ShipType(slot));
            return "<color=#" + colour + ">" + PvpTeams.TeamName(team) + "</color>  " + string.Join(", ", types.ToArray());
        }

        /// <summary>A ship's type as the panels name it ("FRIGATE").</summary>
        private static string ShipType(int slot)
        {
            GUIDUnion type = HubFleet.ForcedType(slot);
            if (type.IsNull) type = HubFleet.TypeOf(HubFleet.ShipDef(slot));
            return FleetSlots.TypeName(type);
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
            public int Slot;
            public VisualElement Root;
            public VisualElement Marker;
            public Label Title;
        }

        private static void EnsureBar()
        {
            if (_root == null || (_bar != null && _bar.parent == _root)) return;
            // The terminal's top-right corner, beside RESOURCES and above the ship picture, is free: a FLEET section in the
            // terminal's own style goes there, with its header and decorative line, its LOADOUTS rows and its small buttons.
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

            _countButton = SmallButton(ToggleShips);
            Ui.Margin(_countButton, 8, 0, 0, 0);
            _bar.Add(_countButton);

            // A faint backing keeps the rows readable over any ship picture; hover and selection paint over it.
            var rows = new VisualElement { pickingMode = PickingMode.Ignore };
            rows.style.marginTop = 6;
            rows.style.backgroundColor = new Color(0f, 0f, 0f, 0.45f);
            Rows.Clear();
            for (int index = 0; index < FleetSlots.MaxSlots; index++)
            {
                int place = index;
                // The row at this place shows a different ship depending on the order (Fleet Battle lists by team).
                ShipRow row = MakeShipRow(() => EditShip(Rows[place].Slot));
                row.Slot = index;
                Rows.Add(row);
                rows.Add(row.Root);
            }
            _bar.Add(rows);

            _crewsButton = SmallButton(OpenLobby);
            _crewsButton.text = "CREWS";
            Ui.Margin(_crewsButton, 6, 0, 0, 0);
            _bar.Add(_crewsButton);

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

        private static Button SmallButton(Action onClick)
        {
            Button button = Ui.GameButton("", onClick, "metem-button-size__small");
            button.style.paddingLeft = 8;
            button.style.paddingRight = 8;
            button.style.fontSize = 18;
            return button;
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

        /// <summary>A row's two lines ("SHIP 2: DESTROYER" over "ROOKIE"), whether it is the ship being set up, and its team colour.</summary>
        private static void SetRow(ShipRow row, string label, string ship, string loadout, bool selected, Color? team, bool compact)
        {
            row.Title.style.fontSize = compact ? 16 : 18;
            row.Title.text = "<size=80%>" + label + (string.IsNullOrEmpty(ship) ? "" : ": " + ship) + "</size>\n" + loadout;
            row.Root.EnableInClassList("unity-collection-view__item--selected", selected);
            if (row.Marker != null) row.Marker.style.backgroundColor = team.HasValue ? new StyleColor(team.Value) : new StyleColor(StyleKeyword.Null);
        }

        /// <summary>The order the rows are listed in: ship order in co-op, by team in Fleet Battle (BLUE 1-3, then RED 1-3).</summary>
        private static List<int> RowOrder(int ships, bool teams)
        {
            if (!teams) return Enumerable.Range(0, ships).ToList();
            return FleetSlots.SlotsOfTeam(0, ships).Concat(FleetSlots.SlotsOfTeam(1, ships)).ToList();
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
                    if (EditingOther) CustomizerLink.SetTerminalShip(1);
                    _editingSlot = 0;
                    return;
                }
                int ships = HubFleet.PlannedShips;
                bool teams = FleetApi.IsTeamMatch;
                bool host = PhotonNetwork.IsMasterClient;
                bool savedRun = HubFleet.SavedRunChosen;
                if (_editingSlot >= ships) EditShip(0);

                _countButton.text = teams ? "TEAMS & RULES" : "SHIPS: " + ships;
                // Everyone may open TEAMS & RULES; only the host sets the ship count, and a saved run has one ship.
                _countButton.SetEnabled(teams || (host && !savedRun));
                _crewsButton.style.display = !teams && ships > 1 ? DisplayStyle.Flex : DisplayStyle.None;

                List<int> order = RowOrder(ships, teams);
                bool compact = ships > 3;
                for (int i = 0; i < Rows.Count; i++)
                {
                    ShipRow row = Rows[i];
                    bool shown = i < order.Count;
                    row.Root.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
                    if (!shown) continue;
                    int slot = order[i];
                    row.Slot = slot;
                    string ship, loadout;
                    if (slot > 0 && HubFleet.CopiesShip1(slot))
                    {
                        SplitName(HubFleet.ShipADef, null, out ship, out loadout);
                        loadout = "AS SHIP 1";
                    }
                    else
                    {
                        SplitName(HubFleet.ShipDef(slot), HubFleet.ChosenName(slot), out ship, out loadout);
                    }
                    if (!HubFleet.ForcedType(slot).IsNull) ship = FleetSlots.TypeName(HubFleet.ForcedType(slot)) + " ONLY";
                    SetRow(row, HubFleet.ShipLabel(slot), ship, loadout, slot == _editingSlot, teams ? FleetApi.ShipColor(slot + 1) : (Color?)null, compact);
                }

                int actor = PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : -1;
                string label = HubFleet.ShipLabel(_editingSlot);
                if (teams)
                {
                    string team = PvpTeams.TeamName(FleetSlots.TeamOf(_editingSlot, true));
                    int mine = FleetApi.TeamOf(actor);
                    // Without a team once the teams are confirmed: every crew was full when this player joined.
                    string noTeam = PvpTeams.Locked
                        ? "Every crew is full: you wait for a free place."
                        : "Pick a ship on the lobby screen (TEAMS & RULES) to set up your team's ships.";
                    _hint.text = FleetApi.CanEditShip(actor, _editingSlot + 1)
                        ? label + ": pick its loadout, then SELECT."
                        : (mine < 0 ? noTeam : label + ": only " + team + " players can change it.");
                }
                else if (savedRun)
                {
                    _hint.text = "Saved runs have one ship.";
                }
                else if (ships < 2)
                {
                    _hint.text = "One ship, up to " + CrewRules.MaxCrew + " players. " +
                                 (host ? "SHIPS adds a second or third ship." : "The host can add a second or third ship.");
                }
                else
                {
                    _hint.text = "Setting up " + label + ": pick a ship and loadout, then SELECT. CREWS: who flies which ship.";
                }
                GUIDUnion forced = HubFleet.ForcedType(_editingSlot);
                if (!forced.IsNull) _hint.text += " It must be a " + FleetSlots.TypeName(forced) + ".";
                if (_editingSlot > 1 && CustomizerLink.Installed && !CustomizerLink.CanCustomize(_editingSlot + 1))
                    _hint.text += " CUSTOM here needs Ship Customizer 0.9.0.";
                ShowTint(teams ? FleetApi.ShipColor(_editingSlot + 1) : Color.clear);
                if (EditingOther) MarkOther();
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
            int dash = text.IndexOf(" - ", StringComparison.Ordinal);
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

        /// <summary>SHIPS (host only): one more ship (back to one after three). In Fleet Battle: TEAMS & RULES.</summary>
        private static void ToggleShips()
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
            if (!PhotonNetwork.IsMasterClient || HubFleet.SavedRunChosen) return;
            int ships = HubFleet.PlannedMode == FleetMode.OneShip ? 1 : HubFleet.PlannedShips;
            int next = ships >= FleetSlots.MaxCoopShips ? 1 : ships + 1;
            if (next < 2 && HubFleet.Variant == HubVariant.FleetPilgrimage) next = 2;
            HubFleet.RequestShipCount(next);
        }

        /// <summary>
        /// CREWS: the lobby screen, where players pick the ship they fly. While the crews are picked it brings this player's
        /// screen back (after HIDE); once they are confirmed, the host opens crew picking again for everyone (the crews are
        /// kept until changed), and the other players look at the crews.
        /// </summary>
        private static void OpenLobby()
        {
            if (PvpTeams.PickingTeams || !PhotonNetwork.IsMasterClient) PvpTeamScreen.ShowLocally();
            else PvpTeams.Reset();
        }

        /// <summary>Switches which ship the terminal sets up, and shows that ship's current loadout.</summary>
        private static void EditShip(int slot)
        {
            if (slot < 0 || slot >= Math.Max(1, HubFleet.PlannedShips)) slot = 0;
            _editingSlot = slot;
            // Before showing it: the Ship Customizer's CUSTOM then shows and edits this ship's own layout.
            CustomizerLink.SetTerminalShip(slot + 1);
            ShowLoadout(HubFleet.ShipDef(slot));
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

        /// <summary>After a refused or redirected SELECT: the SELECTED mark shows the real choice of the ship being set up.</summary>
        private static void MarkChoice()
        {
            if (_terminal == null) return;
            try
            {
                var display = DisplayField != null ? DisplayField.GetValue(_terminal) as ModuleLayout : null;
                if (display == null || display.CurrentLoadout == null) return;
                if (EditingOther)
                {
                    if (HubFleet.ChosenLoadout(_editingSlot).IsNull) display.Selected(false);
                    else MarkOther();
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

        /// <summary>The list and SELECTED marker show the choice of the ship 2-6 being set up.</summary>
        private static void MarkOther()
        {
            if (_terminal == null || !EditingOther) return;
            GUIDUnion chosen = HubFleet.ChosenLoadout(_editingSlot);
            if (chosen.IsNull) return;
            try
            {
                ShipLoadoutDataDef def = HubFleet.LoadoutDef(chosen);
                var list = LoadoutListField != null ? LoadoutListField.GetValue(_terminal) as ShipLoadoutList : null;
                var display = DisplayField != null ? DisplayField.GetValue(_terminal) as ModuleLayout : null;
                if (list != null && def != null) list.LoadoutPicked(def);
                if (display != null) display.Selected(def != null && display.CurrentLoadout != null && display.CurrentLoadout.AssetGuid == def.AssetGuid);
            }
            catch (Exception e)
            {
                Log.Failure("Marking a ship's loadout", e);
            }
        }
    }
}
