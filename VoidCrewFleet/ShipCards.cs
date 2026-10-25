using System;
using System.Collections.Generic;
using CG.Game.Player;
using CG.Ship.Modules;
using CG.Ship.Repair;
using CG.Space;
using Photon.Pun;
using ResourceAssets;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// In a fleet mission, the other ships as cards in the style of the HUD's ship panel:
    /// - under the local player's crew rows, a card for each other ship on their side (every other ship in co-op, their team's
    ///   in Fleet Battle): its name and type in the HUD's shadowed text, a head for each of its crew (black while they live,
    ///   red once dead), and a copy of the panel's hull row (HudHullRow) with the ship type's silhouette in the diamond, as
    ///   the loadout terminal shows it; a lost ship shows the game's red skull and LOST;
    /// - in Fleet Battle, on the right under the objectives, a card for each enemy ship while it is within the local ship's
    ///   scan range (its helm's ScanRange, as the game's own scans): its name and its type's silhouette in its team's colour,
    ///   and its hull in red; nothing of its crew.
    /// The cards are worked out a few times a second; each part is only written again when what it shows changes.
    /// </summary>
    internal static class ShipCards
    {
        private sealed class Card
        {
            public VisualElement Root;
            public Label Plate;
            public VisualElement Heads;
            public HudHullRow Hull;
            public string Text;
            public GUIDUnion Type = GUIDUnion.Empty();
            public readonly List<HeadIcon> HeadIcons = new List<HeadIcon>();

            /// <summary>What the name line was made from: it is only made again when one of these changes (not every tick).</summary>
            public int TextSlot = -1;

            public bool TextTeams;
            public string TextType;
        }

        /// <summary>The card's plate starts where the panel's resource plates do over its hull row (76 px in).</summary>
        private const float PlateIndent = 76f;
        private const float CardGap = 6f;
        private const float HeadGap = 3f;
        /// <summary>The enemy cards' highest place on the right (under the objectives when they reach lower).</summary>
        private const float EnemiesTopMin = 150f;
        private const float EnemiesBelowObjectives = 16f;

        private static readonly List<Card> TeamCards = new List<Card>();
        private static readonly List<Card> EnemyCards = new List<Card>();
        private static readonly List<int> TeamSlots = new List<int>();
        private static readonly List<int> EnemySlots = new List<int>();
        private static readonly Dictionary<int, List<int>> CrewBySlot = new Dictionary<int, List<int>>();
        private static readonly List<int> NoCrew = new List<int>();
        private static readonly Dictionary<PlayerControlledShip, HullDamageController> Hulls = new Dictionary<PlayerControlledShip, HullDamageController>();

        private static VisualElement _root;
        private static VisualElement _list;
        private static VisualElement _team;
        private static VisualElement _enemies;
        private static VisualElement _missionList;
        private static float _enemiesTop = -1f;

        // The slots the cards are for, worked out again when one of these changes.
        private static int _mySlot = int.MinValue;
        private static int _ships = -1;
        private static bool _teams;

        // The crews by slot, from the crew list as stored and the room's players.
        private static object _crewStamp;
        private static int _players = -1;

        private static PlayerControlledShip _helmShip;
        private static Helm _helm;

        /// <summary>Self-test: the enemy cards show whatever the range (the arena's teams start out of range).</summary>
        internal static bool EnemiesInRangeForTest;

        /// <summary>Self-test, alone in the room: each team card shows this many stand-in heads, every third one dead.</summary>
        internal static int HeadsForTest;

        /// <summary>The cards' places in a HUD document or tree that is new (CrewList): the crew list, and the HUD's right column.</summary>
        public static void Attach(VisualElement root, VisualElement list)
        {
            if (_team != null) _team.RemoveFromHierarchy();
            if (_enemies != null) _enemies.RemoveFromHierarchy();
            TeamCards.Clear();
            EnemyCards.Clear();
            Hulls.Clear();
            _root = root;
            _list = list;
            _missionList = null;
            _enemiesTop = -1f;
            _mySlot = int.MinValue;
            _helmShip = null;
            _helm = null;

            _team = new VisualElement { name = "FleetShipCards", pickingMode = PickingMode.Ignore };
            _team.style.flexDirection = FlexDirection.Column;
            _team.style.marginTop = CardGap;
            _team.style.display = DisplayStyle.None;
            list.Add(_team);

            VisualElement right = root.Q("RightSide") ?? root;
            _enemies = new VisualElement { name = "FleetEnemyCards", pickingMode = PickingMode.Ignore };
            _enemies.style.position = Position.Absolute;
            _enemies.style.right = 0;
            _enemies.style.top = EnemiesTopMin;
            _enemies.style.flexDirection = FlexDirection.Column;
            _enemies.style.alignItems = Align.FlexEnd;
            _enemies.style.display = DisplayStyle.None;
            right.Add(_enemies);
        }

        /// <summary>The team cards stand out of the crew list's indent, under the ship panel's rows (as CrewList's player rows don't).</summary>
        public static void SetIndent(float indent)
        {
            if (_team != null) _team.style.marginLeft = HudShipRow.Left - indent;
        }

        /// <summary>A few times a second (CrewList), with active false outside a fleet mission.</summary>
        public static void Tick(bool active)
        {
            if (_team == null) return;
            try
            {
                if (!active)
                {
                    _team.style.display = DisplayStyle.None;
                    _enemies.style.display = DisplayStyle.None;
                    return;
                }
                CrewEntry me;
                FleetShips.TryGetEntry(FleetShips.LocalActor, out me);
                int ships = FleetSession.ShipCount;
                bool teams = FleetApi.IsTeamMatch;
                if (me.Slot != _mySlot || ships != _ships || teams != _teams)
                {
                    _mySlot = me.Slot;
                    _ships = ships;
                    _teams = teams;
                    TeamSlots.Clear();
                    TeamSlots.AddRange(CrewRules.OtherShipsOfSide(me.Slot, ships, teams));
                    EnemySlots.Clear();
                    EnemySlots.AddRange(CrewRules.EnemyShips(me.Slot, ships, teams));
                }
                ReadCrews();

                Ensure(TeamCards, TeamSlots.Count, _team, true);
                for (int i = 0; i < TeamSlots.Count; i++) ShowTeamShip(TeamCards[i], TeamSlots[i], teams);
                _team.style.display = TeamSlots.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                // The game adds a joining player's row at the end of the list: the cards go back under it.
                if (_team.parent == _list && _list.IndexOf(_team) != _list.childCount - 1) _team.BringToFront();

                Helm helm = LocalHelm();
                Ensure(EnemyCards, EnemySlots.Count, _enemies, false);
                int shown = 0;
                for (int i = 0; i < EnemySlots.Count; i++)
                {
                    if (ShowEnemyShip(EnemyCards[i], EnemySlots[i], helm)) shown++;
                }
                _enemies.style.display = shown > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                if (shown > 0) PlaceEnemies();
            }
            catch (Exception e)
            {
                Log.Failure("Showing the other ships' cards", e);
            }
        }

        /// <summary>For the self-test's reports: what the cards show.</summary>
        internal static string Describe()
        {
            if (_team == null) return "not set up";
            var parts = new List<string>();
            foreach (Card card in TeamCards) parts.Add(DescribeCard(card));
            foreach (Card card in EnemyCards) parts.Add(DescribeCard(card));
            return parts.Count > 0 ? string.Join("; ", parts.ToArray()) : "none";
        }

        private static string DescribeCard(Card card)
        {
            if (card.Root.resolvedStyle.display == DisplayStyle.None) return card.Root.name + " hidden";
            int dead = 0;
            foreach (HeadIcon head in card.HeadIcons)
            {
                if (head.Dead) dead++;
            }
            return card.Root.name + " " + card.Text + " at " + card.Root.worldBound + ", " + card.HeadIcons.Count + " heads (" + dead + " dead), icon " +
                   (card.Hull.Icon.resolvedStyle.backgroundImage.sprite != null || card.Hull.Icon.resolvedStyle.backgroundImage.texture != null);
        }

        private static void Ensure(List<Card> cards, int count, VisualElement parent, bool team)
        {
            while (cards.Count < count)
            {
                Card card = Build(team ? "FleetShipCard" + (cards.Count + 1) : "FleetEnemyCard" + (cards.Count + 1), team);
                cards.Add(card);
                parent.Add(card.Root);
            }
            for (int i = count; i < cards.Count; i++) cards[i].Root.style.display = DisplayStyle.None;
        }

        /// <summary>
        /// A card as the ship panel is laid out: the value plate where the panel's resource plates are (with the heads after it on
        /// a team card), over the hull row.
        /// </summary>
        private static Card Build(string name, bool team)
        {
            var card = new Card();
            card.Root = new VisualElement { name = name, pickingMode = PickingMode.Ignore };
            card.Root.style.flexDirection = FlexDirection.Column;
            card.Root.style.marginBottom = CardGap + HudShipRow.DiamondOverhang;
            card.Root.style.display = DisplayStyle.None;

            VisualElement top = new VisualElement { pickingMode = PickingMode.Ignore };
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;
            top.style.height = 22;
            top.style.marginLeft = PlateIndent;
            // Written as the crew rows write their names: plain white HUD text with its shadow (the panel's value plates are a
            // fixed 75 px, too short for a ship's name and type).
            card.Plate = Ui.HudLabel("", 14, Color.white);
            card.Plate.name = name + "Name";
            card.Plate.enableRichText = true;
            card.Plate.style.unityFontStyleAndWeight = FontStyle.Normal;
            card.Plate.style.whiteSpace = WhiteSpace.NoWrap;
            card.Plate.style.unityTextAlign = TextAnchor.MiddleLeft;
            card.Plate.style.flexShrink = 0;
            top.Add(card.Plate);
            if (team)
            {
                card.Heads = new VisualElement { name = name + "Crew", pickingMode = PickingMode.Ignore };
                card.Heads.style.flexDirection = FlexDirection.Row;
                card.Heads.style.alignItems = Align.Center;
                card.Heads.style.marginLeft = 10;
                top.Add(card.Heads);
            }
            card.Root.Add(top);

            card.Hull = new HudHullRow(_root, name);
            // An enemy ship's hull in the HUD's red, whatever its state.
            if (!team) card.Hull.FillTint = "ui-tint-color__bad";
            card.Root.Add(card.Hull.Row);
            return card;
        }

        private static void ShowTeamShip(Card card, int slot, bool teams)
        {
            PlayerControlledShip ship = FleetShips.BySlot(slot);
            bool lost = FleetShips.IsLost(slot);
            // A ship this client hasn't seen spawn yet has no card until it does.
            if (ship == null && !lost)
            {
                card.Root.style.display = DisplayStyle.None;
                return;
            }
            card.Hull.Show(Hull(ship), Repairable(ship), lost);
            string type = FleetSlots.TypeNameOf(slot, false);
            if (TextChanged(card, slot, teams, type))
                SetText(card, "<b>" + FleetSlots.Name(slot, teams) + "</b>" + (string.IsNullOrEmpty(type) ? "" : "  " + type), Color.white);
            GUIDUnion shipType = FleetSlots.TypeOf(slot, false);
            if (!(shipType == card.Type))
            {
                card.Type = shipType;
                card.Hull.SetIcon(FleetSlots.TypeIcon(shipType));
            }
            List<int> crew;
            if (!CrewBySlot.TryGetValue(slot, out crew)) crew = NoCrew;
            int heads = HeadsForTest > 0 ? HeadsForTest : crew.Count;
            while (card.HeadIcons.Count < heads)
            {
                var head = new HeadIcon();
                if (card.HeadIcons.Count > 0) head.style.marginLeft = HeadGap;
                card.HeadIcons.Add(head);
                card.Heads.Add(head);
            }
            while (card.HeadIcons.Count > heads)
            {
                card.HeadIcons[card.HeadIcons.Count - 1].RemoveFromHierarchy();
                card.HeadIcons.RemoveAt(card.HeadIcons.Count - 1);
            }
            for (int i = 0; i < heads; i++) card.HeadIcons[i].Dead = HeadsForTest > 0 ? i % 3 == 2 : !Alive(crew[i]);
            card.Root.style.display = DisplayStyle.Flex;
        }

        private static bool ShowEnemyShip(Card card, int slot, Helm helm)
        {
            PlayerControlledShip ship = FleetShips.BySlot(slot);
            bool show = ship != null && !FleetShips.IsLost(slot) && (EnemiesInRangeForTest || InRange(ship, helm));
            if (!show)
            {
                card.Root.style.display = DisplayStyle.None;
                return false;
            }
            card.Hull.Show(Hull(ship), Repairable(ship), false);
            // Its name and its type's silhouette in its team's colour.
            if (TextChanged(card, slot, true, null))
            {
                Color colour = FleetApi.TeamColor(CrewRules.TeamOf(slot, true));
                if (SetText(card, "<b>" + FleetSlots.Name(slot, true) + "</b>", colour)) card.Hull.Icon.style.unityBackgroundImageTintColor = colour;
            }
            GUIDUnion shipType = FleetSlots.TypeOf(slot, false);
            if (!(shipType == card.Type))
            {
                card.Type = shipType;
                card.Hull.SetIcon(FleetSlots.TypeIcon(shipType));
            }
            card.Root.style.display = DisplayStyle.Flex;
            return true;
        }

        /// <summary>Whether the card's name line must be made again: it now shows another ship, mode or type (notes the new ones).</summary>
        private static bool TextChanged(Card card, int slot, bool teams, string type)
        {
            if (card.Text != null && slot == card.TextSlot && teams == card.TextTeams && string.Equals(type, card.TextType)) return false;
            card.TextSlot = slot;
            card.TextTeams = teams;
            card.TextType = type;
            return true;
        }

        /// <summary>The card's name line; false when it already said that.</summary>
        private static bool SetText(Card card, string text, Color colour)
        {
            if (text == card.Text) return false;
            card.Text = text;
            card.Plate.text = text;
            card.Plate.style.color = colour;
            return true;
        }

        /// <summary>Within the local ship's scan range, measured from its helm as the game's scans are.</summary>
        private static bool InRange(PlayerControlledShip ship, Helm helm)
        {
            if (helm == null) return false;
            float range = helm.ScanRange.Value;
            return (ship.transform.position - helm.transform.position).sqrMagnitude < range * range;
        }

        /// <summary>The helm of the local player's own ship, else of the ship they are aboard; looked up once per ship.</summary>
        private static Helm LocalHelm()
        {
            PlayerControlledShip mine = FleetShips.LocalShip;
            if (mine == null || FleetShips.IsLost(FleetShips.Slot(mine))) mine = FleetShips.AboardShip(LocalPlayer.Instance);
            if (mine != _helmShip)
            {
                _helmShip = mine;
                _helm = mine != null ? mine.GetModule<Helm>() : null;
            }
            return _helm;
        }

        private static float Hull(PlayerControlledShip ship)
        {
            if (ship == null) return 0f;
            float max = ship.MaxHitPoints.Value;
            return max > 0f ? ship.HitPoints / max : 0f;
        }

        /// <summary>The repairable part of a ship's hull, from its hull damage controller (as the ship panel's bar shows it).</summary>
        private static float Repairable(PlayerControlledShip ship)
        {
            if (ship == null) return 0f;
            HullDamageController hull;
            if (!Hulls.TryGetValue(ship, out hull))
            {
                hull = ship.GetComponentInChildren<HullDamageController>();
                Hulls[ship] = hull;
            }
            if (hull == null || hull.State == null) return 0f;
            return hull.State.RepairableHpFraction;
        }

        /// <summary>The players crewing each ship (spectators of a lost ship too: they were its crew), worked out when the crews or room change.</summary>
        private static void ReadCrews()
        {
            object stamp = FleetShips.CrewStamp;
            Photon.Realtime.Room room = PhotonNetwork.CurrentRoom;
            int players = room != null ? room.PlayerCount : 0;
            if (ReferenceEquals(stamp, _crewStamp) && players == _players) return;
            _crewStamp = stamp;
            _players = players;
            foreach (List<int> crew in CrewBySlot.Values) crew.Clear();
            foreach (CrewEntry entry in FleetShips.Crews().Values)
            {
                if (entry.Slot < 0 || entry.State == CrewState.Waiting || room == null || room.GetPlayer(entry.Actor) == null) continue;
                List<int> crew;
                if (!CrewBySlot.TryGetValue(entry.Slot, out crew)) CrewBySlot[entry.Slot] = crew = new List<int>();
                crew.Add(entry.Actor);
            }
            foreach (List<int> crew in CrewBySlot.Values) crew.Sort();
        }

        private static bool Alive(int actor)
        {
            Player character = actor == FleetShips.LocalActor ? LocalPlayer.Instance : FleetShips.CharacterOf(actor);
            return character != null && !PlayerLife.IsDead(character);
        }

        /// <summary>The enemy cards under the objectives at the top right (they grow and shrink with the mission).</summary>
        private static void PlaceEnemies()
        {
            if (_missionList == null || _missionList.panel == null) _missionList = _root.Q("MissionList");
            float top = EnemiesTopMin;
            if (_missionList != null && _enemies.parent != null && _missionList.resolvedStyle.display != DisplayStyle.None)
            {
                float bottom = _enemies.parent.WorldToLocal(new Vector2(0f, _missionList.worldBound.yMax)).y;
                if (!float.IsNaN(bottom)) top = Mathf.Max(top, bottom + EnemiesBelowObjectives);
            }
            if (Mathf.Abs(top - _enemiesTop) < 0.5f) return;
            _enemiesTop = top;
            _enemies.style.top = top;
        }
    }
}
