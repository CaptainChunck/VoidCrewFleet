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
        // In a mission: under the power row and its breaker pips (the panel is 110 px tall, its rows at 22 and 44).
        private const float RowTopInPanel = 83f;
        // In the hub, where the panel is hidden: where its hull row would be.
        private const float RowTopInHub = 26f;

        private static UIDocument _hud;

        /// <summary>The HUD document's tree the row was built for: a document makes a new one when it is enabled again.</summary>
        private static VisualElement _hudRoot;

        private static VisualElement _shipInfo;
        private static VisualElement _topPanel;
        private static HudShipRow _row;
        private static bool _inHub;

        // What the row says, as last written: it is only written again when one of these changes (no text is built until then).
        private static bool _shown;
        private static int _shownSlot;
        private static bool _shownTeams;
        private static int _shownTeam;
        private static bool _shownLost;
        private static string _shownType;

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
                if (_shown && entry.Slot == _shownSlot && teams == _shownTeams && team == _shownTeam && lost == _shownLost && type == _shownType) return;
                _shown = true;
                _shownSlot = entry.Slot;
                _shownTeams = teams;
                _shownTeam = team;
                _shownLost = lost;
                _shownType = type;
                Color colour = team >= 0 ? FleetApi.TeamColor(team) : Color.white;
                _row.Bold.text = team >= 0 ? PvpTeams.TeamName(team) + " TEAM" : "";
                _row.Bold.style.color = colour;
                _row.Bold.style.display = team >= 0 ? DisplayStyle.Flex : DisplayStyle.None;
                _row.Regular.text = FleetSlots.Name(entry.Slot, teams);
                _row.Value.text = lost ? "LOST" : type ?? "";
                _row.Value.style.color = lost ? HudShipRow.LostRed : Color.white;
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
            if (row.parent == parent && _inHub == inHub) return true;
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
                // One more row of the panel, under its power row and breaker pips, outside the panel's own layout.
                _shipInfo.Add(row);
                row.style.position = Position.Absolute;
                row.style.left = HudShipRow.Left;
                row.style.top = RowTopInPanel;
                row.style.marginLeft = 0;
                row.style.marginTop = 0;
                row.style.marginBottom = 0;
            }
            // On its own in the hub; in a mission under the power row.
            _row.StartAs(inHub);
            return true;
        }
    }
}
