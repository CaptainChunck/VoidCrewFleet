using System;
using System.Collections.Generic;
using System.Reflection;
using CG.Game.Player;
using Photon.Pun;
using UI.MainHUD;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// The game's crew list under the ship panel has an 80 px row for each other player: made for Void Crew's 4 players, it
    /// runs off the screen in a big room (9 rows fit above the player's own panel). It lists instead:
    /// - for a player with a ship (picked in the hub, or theirs in a mission), the players on that ship: 7 rows at most, a
    ///   full crew (CrewRules.MaxCrew), and in Fleet Battle nothing of the other team's health and whereabouts;
    /// - under them, a row for each other ship on their side (every other ship in co-op, their team's in Fleet Battle) with
    ///   its type and how many crew it has, or LOST, built like the ship panel's own rows (HudShipRow);
    /// - for a player without a ship (the hub before picking, a one-ship game), the first 7 players.
    /// Everyone stays in the escape menu's player list, with mute, kick, block and report. The game's rows are only hidden,
    /// so their voice and health updates carry on; the ship rows sit at the end of the list, so they show and hide with it.
    /// </summary>
    internal static class CrewList
    {
        public const string Feature = "Crew list per ship";

        /// <summary>The most player rows: a full crew's others.</summary>
        private const int MaxRows = CrewRules.MaxCrew - 1;

        /// <summary>Between the last player row and the first ship row.</summary>
        private const float ShipsGap = 6f;

        private static readonly FieldInfo ViewsField = GameMember.Field(typeof(CoopBuddiesPanel), "view");
        private static readonly FieldInfo RowField = GameMember.Field(typeof(CoopBuddyView), "container");

        private static readonly StyleEnum<DisplayStyle> Shown = new StyleEnum<DisplayStyle>(StyleKeyword.Null);
        private static readonly StyleEnum<DisplayStyle> Hidden = new StyleEnum<DisplayStyle>(DisplayStyle.None);

        private static readonly Dictionary<VisualElement, int> ActorOfRow = new Dictionary<VisualElement, int>();
        private static readonly List<VisualElement> Rows = new List<VisualElement>();
        private static readonly List<int> RowActors = new List<int>();
        private static readonly List<HudShipRow> ShipRows = new List<HudShipRow>();

        // The game's HUD and its crew list panel.
        private static MainHUDUI _hudUi;
        private static UIDocument _hudDocument;
        private static CoopBuddiesPanel _panel;

        // The HUD the ship rows were built in: built again for a new document or tree.
        private static UIDocument _hud;
        private static VisualElement _hudRoot;
        private static VisualElement _list;
        private static VisualElement _ships;
        private static float _indent = float.NaN;

        // What the list was last set for: nothing is worked out again while all of these stay the same.
        private static object _crewStamp;
        private static int _rowCount = -1;
        private static int _players = -1;
        private static bool _inHub;
        private static int _shipCount = -1;
        private static bool _teams;
        private static int _revision = -1;
        private static FleetPlan _plan;
        private static int _lost = -1;

        /// <summary>A few times a second.</summary>
        public static void Tick()
        {
            Refresh(false);
        }

        /// <summary>A player's row is added: shown or hidden at once, not a quarter of a second later.</summary>
        [GamePatch(Feature, "UI.MainHUD.CoopBuddiesPanel", "PlayerSpawned", PatchKind.Postfix, Optional = true)]
        private static void PlayerSpawnedPostfix()
        {
            Refresh(true);
        }

        /// <summary>For the self-test's reports: the player rows shown, and what the ship rows say.</summary>
        internal static string Describe()
        {
            if (_list == null) return "not set up";
            int shown = 0;
            foreach (VisualElement row in Rows)
            {
                if (row.resolvedStyle.display != DisplayStyle.None) shown++;
            }
            var ships = new List<string>();
            foreach (HudShipRow row in ShipRows)
            {
                if (row.Row.resolvedStyle.display == DisplayStyle.Flex)
                    ships.Add(row.Bold.text + " " + row.Regular.text + " / " + row.Value.text + " at " + row.Row.worldBound + ", icon " +
                              (row.Icon.resolvedStyle.backgroundImage.sprite != null));
            }
            return shown + " of " + Rows.Count + " player rows shown; ship rows: " + (ships.Count > 0 ? string.Join("; ", ships.ToArray()) : "none");
        }

        private static void Refresh(bool force)
        {
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
                // The list's own indent (40 px in the game's style sheet): the ship rows stand out of it, under the ship
                // panel's rows, with the player rows indented between them.
                float indent = _list.resolvedStyle.paddingLeft;
                if (!float.IsNaN(indent) && indent != _indent)
                {
                    _indent = indent;
                    _ships.style.marginLeft = HudShipRow.Left - indent;
                }

                bool inHub = GameSessionManager.InHub;
                bool teams = FleetApi.IsTeamMatch;
                int shipCount = (inHub ? PvpTeams.HubFleetPlanned : FleetSession.ShipCount >= 2) ? PvpTeams.ShipCount : 1;
                object crewStamp = FleetShips.CrewStamp;
                int players = PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.PlayerCount : 0;
                int revision = inHub ? HubFleet.Revision : 0;
                FleetPlan plan = inHub ? null : FleetSession.Current;
                int lost = inHub ? 0 : LostShips(shipCount);
                if (!force && ReferenceEquals(crewStamp, _crewStamp) && views.Count == _rowCount && players == _players && inHub == _inHub &&
                    shipCount == _shipCount && teams == _teams && revision == _revision && ReferenceEquals(plan, _plan) && lost == _lost)
                    return;
                _crewStamp = crewStamp;
                _rowCount = views.Count;
                _players = players;
                _inHub = inHub;
                _shipCount = shipCount;
                _teams = teams;
                _revision = revision;
                _plan = plan;
                _lost = lost;

                // Ships count only for a fleet (as for CrewBadge): picks left from a fleet the host has since turned off don't.
                Dictionary<int, CrewEntry> crews = shipCount >= 2 ? FleetShips.Crews() : new Dictionary<int, CrewEntry>();
                ShowPlayers(views, crews);
                ShowShips(crews, inHub, shipCount, teams);
            }
            catch (Exception e)
            {
                Log.Failure("Keeping the crew list to the local player's ship", e);
            }
        }

        /// <summary>The ship rows' place at the end of the game's list, in a HUD document or tree that is new.</summary>
        private static void Build(UIDocument hud, VisualElement root)
        {
            if (_ships != null) _ships.RemoveFromHierarchy();
            ShipRows.Clear();
            Rows.Clear();
            _hud = hud;
            _hudRoot = root;
            _list = root.Q("CoopBuddiesContainer");
            _ships = null;
            _indent = float.NaN;
            if (_list == null)
            {
                Log.WarnOnce("crew list", "The game's HUD has no crew list (CoopBuddiesContainer), so it isn't kept to the local player's ship.");
                return;
            }
            _ships = new VisualElement { name = "FleetShipRows", pickingMode = PickingMode.Ignore };
            _ships.style.flexDirection = FlexDirection.Column;
            _ships.style.marginTop = ShipsGap;
            _ships.style.display = DisplayStyle.None;
            _list.Add(_ships);
        }

        /// <summary>Hides the rows of the players CrewRules.Listed leaves out, in the list's own order.</summary>
        private static void ShowPlayers(Dictionary<Player, CoopBuddyView> views, Dictionary<int, CrewEntry> crews)
        {
            ActorOfRow.Clear();
            foreach (KeyValuePair<Player, CoopBuddyView> view in views)
            {
                VisualElement row = GameMember.Read<VisualElement>(RowField, view.Value, null);
                Photon.Realtime.Player owner = view.Key != null ? view.Key.PhotonPlayer : null;
                if (row != null && owner != null) ActorOfRow[row] = owner.ActorNumber;
            }
            Rows.Clear();
            RowActors.Clear();
            for (int i = 0; i < _list.childCount; i++)
            {
                VisualElement row = _list[i];
                int actor;
                if (!ActorOfRow.TryGetValue(row, out actor)) continue;
                Rows.Add(row);
                RowActors.Add(actor);
            }
            List<int> listed = CrewRules.Listed(RowActors, crews, FleetShips.LocalActor, MaxRows);
            for (int i = 0; i < Rows.Count; i++) Rows[i].style.display = listed.Contains(RowActors[i]) ? Shown : Hidden;
        }

        /// <summary>A row for each other ship on the local player's side, under the players; none without a ship of their own.</summary>
        private static void ShowShips(Dictionary<int, CrewEntry> crews, bool inHub, int shipCount, bool teams)
        {
            if (_ships == null) return;
            CrewEntry me;
            List<int> others = crews.TryGetValue(FleetShips.LocalActor, out me) && me.Slot >= 0
                ? CrewRules.OtherShipsOfSide(me.Slot, shipCount, teams)
                : new List<int>();
            while (ShipRows.Count < others.Count)
            {
                var row = new HudShipRow(_hudRoot, "FleetShip" + (ShipRows.Count + 1));
                // The diamonds reach out of their rows: the rows keep that far apart.
                row.Row.style.marginTop = HudShipRow.DiamondOverhang + 2;
                row.Row.style.marginBottom = HudShipRow.DiamondOverhang;
                ShipRows.Add(row);
                _ships.Add(row.Row);
            }
            ICollection<int> players = PhotonNetwork.CurrentRoom != null ? (ICollection<int>)PhotonNetwork.CurrentRoom.Players.Keys : new int[0];
            for (int i = 0; i < ShipRows.Count; i++)
            {
                HudShipRow row = ShipRows[i];
                if (i >= others.Count)
                {
                    row.Row.style.display = DisplayStyle.None;
                    continue;
                }
                int slot = others[i];
                bool lost = !inHub && FleetShips.IsLost(slot);
                int crew = CrewRules.CrewOf(crews, players, slot);
                Color colour = teams ? FleetApi.TeamColor(CrewRules.TeamOf(slot, true)) : Color.white;
                row.Bold.text = FleetSlots.Name(slot, teams);
                row.Regular.text = FleetSlots.TypeNameOf(slot, inHub) ?? "";
                row.Value.text = lost ? "LOST" : (crew == 0 ? "NO CREW" : crew + " CREW");
                row.Value.style.color = lost ? HudShipRow.LostRed : Color.white;
                row.Icon.style.unityBackgroundImageTintColor = lost ? HudShipRow.LostRed : colour;
                row.Row.style.display = DisplayStyle.Flex;
            }
            _ships.style.display = others.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            // The game adds a joining player's row at the end of the list: the ship rows go back under it.
            if (_ships.parent == _list && _list.IndexOf(_ships) != _list.childCount - 1) _ships.BringToFront();
        }

        /// <summary>The lost ships of this mission, one bit per slot.</summary>
        private static int LostShips(int shipCount)
        {
            int lost = 0;
            for (int slot = 0; slot < shipCount && slot < 31; slot++)
            {
                if (FleetShips.IsLost(slot)) lost |= 1 << slot;
            }
            return lost;
        }
    }
}
