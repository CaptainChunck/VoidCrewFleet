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
    /// panel's own rows from its own art: the diamond with the ship type's silhouette in the team's colour (the red skull once
    /// the ship is destroyed), the bar plate with "BLUE TEAM  BLUE 2" in the hub and "BLUE 2" in a mission ("SHIP 2" in
    /// co-op), and the value plate with the ship's type, or LOST. It lives in the game's HUD, so it shows, hides and scales
    /// with it: in a mission as the panel's third row (its diamonds making a >), in the hub on its own where the hull row would
    /// be, from the moment the player has a ship in the lobby until the mission ends.
    /// </summary>
    internal static class CrewBadge
    {
        // In a mission: the panel's third row, as far under its power row as that is under its hull row (FollowPanelRows; this
        // until the panel has a layout), its diamond in the hull diamond's column, so the three diamonds make a > down the
        // panel's left edge. The power row's breaker pips hang over the row's middle (drawn over it): the row's plate then
        // names only the ship (BLUE 1, before the pips), with its type in the value plate after them.
        private const float RowTopInPanel = 74f;
        // In the hub, where the panel is hidden: where its hull row would be.
        private const float RowTopInHub = 26f;

        private static UIDocument _hud;

        /// <summary>The HUD document's tree the row was built for: a document makes a new one when it is enabled again.</summary>
        private static VisualElement _hudRoot;

        private static VisualElement _shipInfo;
        private static VisualElement _topPanel;
        private static HudShipRow _row;
        private static bool _inHub;
        private static float _rowLeft = float.NaN;
        private static float _rowTop = float.NaN;

        // What the row says, as last written: it is only written again when one of these changes (no text is built until then).
        private static bool _shown;
        private static int _shownSlot;
        private static bool _shownTeams;
        private static int _shownTeam;
        private static bool _shownLost;
        private static string _shownType;
        private static bool _shownInHub;

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
                string type = FleetSlots.TypeNameOf(entry.Slot, inHub);
                if (_shown && entry.Slot == _shownSlot && teams == _shownTeams && team == _shownTeam && lost == _shownLost && type == _shownType &&
                    inHub == _shownInHub)
                    return;
                _shown = true;
                _shownSlot = entry.Slot;
                _shownTeams = teams;
                _shownTeam = team;
                _shownLost = lost;
                _shownType = type;
                _shownInHub = inHub;
                Color colour = team >= 0 ? FleetApi.TeamColor(team) : Color.white;
                // In the hub: BLUE TEAM  BLUE 2. In a mission the breaker pips leave room for the ship's name only: BLUE 2, in
                // the team's colour.
                string ship = FleetSlots.Name(entry.Slot, teams);
                _row.Bold.text = inHub ? (team >= 0 ? PvpTeams.TeamName(team) + " TEAM" : "") : ship;
                _row.Bold.style.color = colour;
                _row.Bold.style.display = inHub && team < 0 ? DisplayStyle.None : DisplayStyle.Flex;
                _row.Regular.text = inHub ? ship : "";
                _row.Regular.style.display = inHub ? DisplayStyle.Flex : DisplayStyle.None;
                _row.Value.text = lost ? "LOST" : type ?? "";
                _row.Value.style.color = lost ? HudShipRow.LostRed : Color.white;
                // The ship type's silhouette, as the loadout terminal's SHIP tiles show it (the ship cards' too); the skull once lost.
                Sprite typeIcon = FleetSlots.TypeIcon(FleetSlots.TypeOf(entry.Slot, inHub));
                if (lost) _row.Icon.style.backgroundImage = Ui.GameArt(null, "icon_skull");
                else if (typeIcon != null) _row.Icon.style.backgroundImage = new StyleBackground(typeIcon);
                _row.Icon.style.unityBackgroundImageTintColor = lost ? HudShipRow.LostRed : colour;
                _row.Row.style.display = DisplayStyle.Flex;
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
            VisualElement row = _row.Row;
            return (row.resolvedStyle.display == DisplayStyle.Flex && row.panel != null ? "shown" : "hidden") + " in " +
                   (row.parent != null ? row.parent.name : "nothing") + " at " + row.worldBound + ": " + _row.Bold.text + " / " + _row.Regular.text + " / " +
                   _row.Value.text + ", ship icon " + (_row.Icon.resolvedStyle.backgroundImage.sprite != null);
        }

        private static void Hide()
        {
            if (_row != null) _row.Row.style.display = DisplayStyle.None;
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
            bool dropped = _row != null && _row.Row.parent != null && _row.Row.panel == null && root.panel != null;
            if (hud != _hud || root != _hudRoot || _row == null || dropped)
            {
                if (_row != null) _row.Row.RemoveFromHierarchy();
                _hud = hud;
                _hudRoot = root;
                _shipInfo = root.Q("ShipInfo");
                _topPanel = root.Q("TopPanel");
                _row = new HudShipRow(root, "FleetCrew");
                _shown = false;
            }
            VisualElement parent = inHub ? _topPanel : _shipInfo;
            if (parent == null)
            {
                if (inHub) Log.WarnOnce("crew row hub", "The team and ship row has no place in the hub's HUD (no TopPanel), so it isn't shown there.");
                else Log.WarnOnce("crew row mission", "The team and ship row has no place in a mission's HUD (no ShipInfo panel), so it isn't shown there.");
                return false;
            }
            VisualElement row = _row.Row;
            if (row.parent == parent && _inHub == inHub)
            {
                if (!inHub) FollowPanelRows(row);
                return true;
            }
            _inHub = inHub;
            row.RemoveFromHierarchy();
            if (inHub)
            {
                // The panel is hidden in the hub: the row stands where its hull row would be, and the crew list moves down for it.
                int at = _shipInfo != null && _shipInfo.parent == _topPanel ? _topPanel.IndexOf(_shipInfo) + 1 : 0;
                _topPanel.Insert(at, row);
                row.style.position = Position.Relative;
                row.style.left = StyleKeyword.Null;
                row.style.top = StyleKeyword.Null;
                row.style.marginLeft = HudShipRow.Left;
                row.style.marginTop = RowTopInHub;
                row.style.marginBottom = 12;
            }
            else
            {
                // The panel's third row, outside its own layout and drawn under its own rows, so the breaker pips show over it.
                _shipInfo.Insert(0, row);
                row.style.position = Position.Absolute;
                row.style.marginLeft = 0;
                row.style.marginTop = 0;
                row.style.marginBottom = 0;
                _rowLeft = float.NaN;
                _rowTop = float.NaN;
                FollowPanelRows(row);
            }
            // On its own in the hub; in a mission under the power row.
            _row.StartAs(inHub);
            return true;
        }

        /// <summary>
        /// In a mission the row is the panel's third: its diamond as far under the power row's diamond as that is under the hull
        /// row's, in the hull diamond's column, so the three diamonds make a >. Taken from where the panel's diamonds are drawn
        /// (the game draws its rows a few px off their layout boxes), with RowTopInPanel until they are.
        /// </summary>
        private static void FollowPanelRows(VisualElement row)
        {
            float left = HudShipRow.Left;
            float top = RowTopInPanel;
            VisualElement hull = _shipInfo.Q("HullIconBg");
            VisualElement power = _shipInfo.Q("PowerIconBg");
            if (hull != null && power != null)
            {
                Rect hullBox = hull.worldBound;
                Rect powerBox = power.worldBound;
                if (!float.IsNaN(hullBox.y) && !float.IsNaN(powerBox.y) && hullBox.height > 0f && powerBox.height > 0f && powerBox.y > hullBox.y)
                {
                    // The row is 22 px tall with its diamond centred on it.
                    float centre = powerBox.center.y + (powerBox.center.y - hullBox.center.y);
                    Vector2 place = _shipInfo.WorldToLocal(new Vector2(hullBox.x, centre));
                    if (!float.IsNaN(place.x) && !float.IsNaN(place.y))
                    {
                        left = place.x;
                        top = place.y - 11f;
                    }
                }
            }
            if (float.IsNaN(_rowLeft) || Mathf.Abs(left - _rowLeft) > 0.25f)
            {
                _rowLeft = left;
                row.style.left = left;
            }
            if (float.IsNaN(_rowTop) || Mathf.Abs(top - _rowTop) > 0.25f)
            {
                _rowTop = top;
                row.style.top = top;
            }
        }
    }
}
