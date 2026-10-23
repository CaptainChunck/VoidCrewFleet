using System;
using CG.Client.Player.Interactions;
using CG.Game.Player;
using CG.GameLoopStateMachine;
using CG.GameLoopStateMachine.GameStates;
using CG.Space;
using Photon.Pun;
using ResourceAssets;
using UI.MainHUD;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// The local player's team and ship as one more row of the game's ship panel in the top left corner, built like the
    /// panel's own rows from its own art: the diamond with the game's ship icon in the team's colour, the bar plate with
    /// "BLUE TEAM  BLUE 2" ("SHIP 2" in co-op) and the value plate with the ship's type, or LOST once that ship is destroyed.
    /// It lives in the game's HUD, so it shows, hides and scales with it: in a mission under the panel's power row, in the
    /// hub on its own where the hull row would be, from the moment the player has a ship in the lobby until the mission ends.
    /// </summary>
    internal static class CrewBadge
    {
        // The panel's hull row, which this row copies: 363 x 22, its right end under the others', nudged 3 px right.
        private const float RowWidth = 363f;
        private const float RowLeft = 17f;
        private const float RowNudge = 3f;
        // In a mission: under the power row and its breaker pips (the panel is 110 px tall, its rows at 22 and 44).
        private const float RowTopInPanel = 83f;
        // In the hub, where the panel is hidden: where its hull row would be.
        private const float RowTopInHub = 26f;
        // The HUD's red for a value gone wrong (--ui-color__panic-red).
        private static readonly Color LostRed = new Color32(0xFF, 0x00, 0x00, 0xFF);

        private static UIDocument _hud;

        /// <summary>The HUD document's tree the row was built for: a document makes a new one when it is enabled again.</summary>
        private static VisualElement _hudRoot;

        private static VisualElement _shipInfo;
        private static VisualElement _topPanel;
        private static VisualElement _row;
        private static VisualElement _icon;
        private static VisualElement _start;
        private static VisualElement _body;
        private static Label _team;
        private static Label _ship;
        private static Label _type;
        private static StyleBackground _mainStart;
        private static StyleBackground _subStart;
        private static bool _inHub;

        // What the row says, as last written: it is only written again when one of these changes (no text is built until then).
        private static bool _shown;
        private static int _shownSlot;
        private static bool _shownTeams;
        private static int _shownTeam;
        private static bool _shownLost;
        private static string _shownType;

        // The ship type's name (TypeName), and what it was worked out from.
        private static bool _typeKnown;
        private static int _typeSlot;
        private static bool _typeInHub;
        private static int _typeRevision;
        private static FleetPlan _typePlan;
        private static int _typeShip;
        private static string _typeName;

        /// <summary>A few times a second.</summary>
        public static void Tick()
        {
            try
            {
                bool inHub = GameSessionManager.InHub;
                CrewEntry entry;
                bool teams;
                if (!Wanted(inHub, out entry, out teams) || !Place(inHub))
                {
                    Hide();
                    return;
                }
                int team = teams && entry.Team >= 0 ? entry.Team : -1;
                bool lost = entry.State == CrewState.Dead;
                string type = TypeName(entry.Slot, inHub);
                if (_shown && entry.Slot == _shownSlot && teams == _shownTeams && team == _shownTeam && lost == _shownLost && type == _shownType) return;
                _shown = true;
                _shownSlot = entry.Slot;
                _shownTeams = teams;
                _shownTeam = team;
                _shownLost = lost;
                _shownType = type;
                Color colour = team >= 0 ? FleetApi.TeamColor(team) : Color.white;
                _team.text = team >= 0 ? PvpTeams.TeamName(team) + " TEAM" : "";
                _team.style.color = colour;
                _team.style.display = team >= 0 ? DisplayStyle.Flex : DisplayStyle.None;
                _ship.text = FleetSlots.Name(entry.Slot, teams);
                _type.text = lost ? "LOST" : type ?? "";
                _type.style.color = lost ? LostRed : Color.white;
                _icon.style.unityBackgroundImageTintColor = lost ? LostRed : colour;
                _row.style.display = DisplayStyle.Flex;
            }
            catch (Exception e)
            {
                Log.Failure("Showing the team and ship row", e);
            }
        }

        /// <summary>For the self-test's reports: whether and where the row shows, what it says, and whether it has its icon.</summary>
        internal static string Describe()
        {
            if (_row == null) return "not built";
            return (_row.resolvedStyle.display == DisplayStyle.Flex && _row.panel != null ? "shown" : "hidden") + " in " +
                   (_row.parent != null ? _row.parent.name : "nothing") + " at " + _row.worldBound + ": " + _team.text + " / " + _ship.text + " / " +
                   _type.text + ", ship icon " + (_icon.resolvedStyle.backgroundImage.sprite != null);
        }

        private static void Hide()
        {
            if (_row != null) _row.style.display = DisplayStyle.None;
            _shown = false;
        }

        /// <summary>Whether the row shows now, and for which of the local player's places (their crew entry; a team match or not).</summary>
        private static bool Wanted(bool inHub, out CrewEntry entry, out bool teams)
        {
            entry = default(CrewEntry);
            teams = false;
            if (!FleetRuntime.GameReady || !PhotonNetwork.InRoom || Spectator.Active) return false;
            GameStateMachine machine = GameStateMachine.Instance;
            if (machine == null || !(machine.CurrentState is GSIngame)) return false;
            LocalPlayer me = LocalPlayer.Instance;
            if (me == null || me.IsDead) return false;
            // On a terminal's screen the HUD puts its panels away; this row goes with them.
            if (me.InteractionState != null && (me.InteractionState.ActiveModes & InteractionMode.TerminalScreen) != 0) return false;
            if (inHub ? !PvpTeams.HubFleetPlanned : FleetSession.ShipCount < 2) return false;
            if (!FleetShips.TryGetEntry(FleetShips.LocalActor, out entry) || entry.Slot < 0 || entry.State == CrewState.Waiting) return false;
            teams = FleetApi.IsTeamMatch;
            return true;
        }

        /// <summary>
        /// The ship's type ("DESTROYER"): in the hub as chosen now, in a mission the type the host forced, else the type of
        /// the ship as it spawned, else of its planned loadout. Worked out again only when one of those changes.
        /// </summary>
        private static string TypeName(int slot, bool inHub)
        {
            PlayerControlledShip spawned = inHub ? null : FleetShips.BySlot(slot);
            int revision = inHub ? HubFleet.Revision : 0;
            FleetPlan plan = inHub ? null : FleetSession.Current;
            int ship = spawned != null ? spawned.GetInstanceID() : 0;
            if (_typeKnown && slot == _typeSlot && inHub == _typeInHub && revision == _typeRevision && ReferenceEquals(plan, _typePlan) && ship == _typeShip)
                return _typeName;
            _typeKnown = true;
            _typeSlot = slot;
            _typeInHub = inHub;
            _typeRevision = revision;
            _typePlan = plan;
            _typeShip = ship;
            _typeName = null;
            try
            {
                GUIDUnion type;
                if (inHub)
                {
                    type = HubFleet.TypeOf(HubFleet.ShipDef(slot));
                }
                else
                {
                    ShipPlan planned = plan != null ? plan.Ship(slot) : null;
                    type = planned != null ? planned.Type : GUIDUnion.Empty();
                    if (type.IsNull && spawned != null) type = spawned.assetGuid;
                    if (type.IsNull && planned != null && !planned.Loadout.IsNull) type = HubFleet.TypeOf(HubFleet.LoadoutDef(planned.Loadout));
                }
                if (!type.IsNull) _typeName = FleetSlots.TypeName(type, null);
            }
            catch
            {
                // just the ship's name then
            }
            return _typeName;
        }

        /// <summary>
        /// Builds the row in the game's current HUD (again once the HUD's document or tree is replaced, or the panel it was put
        /// in has left the HUD) and puts it in its place. A HUD without that place (a game update renamed it) is logged once,
        /// and the row isn't built again for it: building looks the game's art up, a scan of every loaded asset.
        /// </summary>
        private static bool Place(bool inHub)
        {
            MainHUDUI hudUi = MainHUDUI.Instance;
            UIDocument hud = hudUi != null ? hudUi.GetComponent<UIDocument>() : null;
            VisualElement root = hud != null ? hud.rootVisualElement : null;
            if (root == null) return false;
            bool dropped = _row != null && _row.parent != null && _row.panel == null && root.panel != null;
            if (hud != _hud || root != _hudRoot || _row == null || dropped)
            {
                if (_row != null) _row.RemoveFromHierarchy();
                _hud = hud;
                _hudRoot = root;
                Build(root);
                _shown = false;
            }
            VisualElement parent = inHub ? _topPanel : _shipInfo;
            if (parent == null)
            {
                if (inHub) Log.WarnOnce("crew row hub", "The team and ship row has no place in the hub's HUD (no TopPanel), so it isn't shown there.");
                else Log.WarnOnce("crew row mission", "The team and ship row has no place in a mission's HUD (no ShipInfo panel), so it isn't shown there.");
                return false;
            }
            if (_row.parent == parent && _inHub == inHub) return true;
            _inHub = inHub;
            _row.RemoveFromHierarchy();
            if (inHub)
            {
                // The panel is hidden in the hub: the row stands where its hull row would be, and the crew list moves down for it.
                int at = _shipInfo != null && _shipInfo.parent == _topPanel ? _topPanel.IndexOf(_shipInfo) + 1 : 0;
                _topPanel.Insert(at, _row);
                _row.style.position = Position.Relative;
                _row.style.left = StyleKeyword.Null;
                _row.style.top = StyleKeyword.Null;
                _row.style.marginLeft = RowLeft;
                _row.style.marginTop = RowTopInHub;
                _row.style.marginBottom = 12;
            }
            else
            {
                // One more row of the panel, under its power row and breaker pips, outside the panel's own layout.
                _shipInfo.Add(_row);
                _row.style.position = Position.Absolute;
                _row.style.left = RowLeft;
                _row.style.top = RowTopInPanel;
                _row.style.marginLeft = 0;
                _row.style.marginTop = 0;
                _row.style.marginBottom = 0;
            }
            // A row of its own starts like the power row; a row under another starts like the player panel's oxygen row,
            // cut back for the diamond above. Either way its text lines up with the bars above.
            _start.style.backgroundImage = inHub ? _mainStart : _subStart;
            _start.style.width = inHub ? 22 : 34;
            _body.style.marginLeft = inHub ? 0 : -2;
            _body.style.paddingLeft = inHub ? 25 : 15;
            return true;
        }

        /// <summary>The row, as the panel's hull row is made: diamond, bar start, plate, value plate, with the game's art.</summary>
        private static void Build(VisualElement root)
        {
            _shipInfo = root.Q("ShipInfo");
            _topPanel = root.Q("TopPanel");
            VisualElement powerBar = root.Q("PowerBarVE");
            VisualElement oxygenBar = root.Q("OxygenBarVE");
            _mainStart = Ui.GameArt(powerBar != null ? powerBar.Q("StartOfBar") : null, "main_hud_bars_bar_start");
            _subStart = Ui.GameArt(oxygenBar != null ? oxygenBar.Q("StartOfBar") : null, "main_hud_bars_subbar_start");

            _row = Part("FleetCrewPanel");
            _row.style.flexDirection = FlexDirection.Row;
            _row.style.alignItems = Align.Center;
            _row.style.width = RowWidth;
            _row.style.height = 22;
            _row.style.flexShrink = 0;
            _row.style.translate = new Translate(new Length(RowNudge), new Length(0f), 0f);

            VisualElement iconBackground = Part("FleetCrewIconBg");
            iconBackground.AddToClassList("bg-tint_gray");
            iconBackground.style.backgroundImage = Ui.GameArt(root.Q("HullIconBg"), "shape_diamond_background");
            iconBackground.style.width = 40;
            iconBackground.style.height = 40;
            iconBackground.style.flexShrink = 0;
            VisualElement iconBorder = Part("FleetCrewIconBorder");
            iconBorder.style.backgroundImage = Ui.GameArt(root.Q("HullIconBorder"), "shape_diamond_border");
            iconBorder.style.flexGrow = 1;
            Ui.Padding(iconBorder, 9, 9);
            _icon = Part("FleetCrewIcon");
            _icon.style.flexGrow = 1;
            Sprite shipIcon = Ui.GameSprite("icon_ship_frigate");
            if (shipIcon != null) _icon.style.backgroundImage = new StyleBackground(shipIcon);
            _icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            iconBorder.Add(_icon);
            iconBackground.Add(iconBorder);

            VisualElement bar = Part("FleetCrewBarVE");
            bar.style.flexGrow = 1;
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.height = 22;
            bar.style.alignItems = Align.Center;
            bar.style.translate = new Translate(new Length(-6f), new Length(0f), 0f);
            _start = Part("FleetCrewStartOfBar");
            _start.AddToClassList("bg-tint_gray");
            _start.style.height = Length.Percent(100);
            _start.style.flexShrink = 0;
            _body = Part("FleetCrewPlate");
            _body.AddToClassList("bg-color_gray");
            _body.style.flexGrow = 1;
            _body.style.height = Length.Percent(100);
            _body.style.flexDirection = FlexDirection.Row;
            _body.style.alignItems = Align.Center;
            _team = Text(true);
            _team.style.marginRight = 8;
            _ship = Text(false);
            _body.Add(_team);
            _body.Add(_ship);
            // The game's value plate with its pointed end, as the hull row's 100%.
            _type = new Label { name = "FleetCrewType", pickingMode = PickingMode.Ignore };
            _type.AddToClassList("main-hud-label-bar");
            _type.AddToClassList("hud-resource__value");
            _type.AddToClassList("ui-text-color__white");
            _type.style.fontSize = 14;
            _type.style.whiteSpace = WhiteSpace.NoWrap;
            _type.style.marginLeft = -1;
            _type.style.flexShrink = 0;
            bar.Add(_start);
            bar.Add(_body);
            bar.Add(_type);

            _row.Add(iconBackground);
            _row.Add(bar);
        }

        private static VisualElement Part(string name)
        {
            return new VisualElement { name = name, pickingMode = PickingMode.Ignore };
        }

        /// <summary>Text on the plate as the HUD's values are written: 14 px, white, no shadow.</summary>
        private static Label Text(bool bold)
        {
            var label = new Label { pickingMode = PickingMode.Ignore };
            label.style.fontSize = 14;
            label.style.unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal;
            label.style.color = Color.white;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            Ui.Margin(label, 0, 0, 0, 0);
            Ui.Padding(label, 0, 0);
            return label;
        }
    }
}
