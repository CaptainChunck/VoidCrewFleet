using System;
using System.Collections.Generic;
using CG.Game;
using CG.Game.Player;
using CG.Objects;
using CG.Ship.Hull;
using CG.Space;
using ExitGames.Client.Photon;
using Gameplay.SpacePlatforms;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// The player ships of the session and who crews which. The game has one "player ship" (ClientGame.PlayerShip); this
    /// keeps the real list:
    /// - every PlayerControlledShip that exists, registered as it wakes;
    /// - each ship's slot (0 = ship 1 ... 5 = ship 6), published by the host when it spawns them (room property), with the
    ///   spawn order (root view ID, allocated in sequence by Photon) as the fallback until then;
    /// - each player's team, slot and state (room property written by the host).
    /// With fewer than two ships every answer is the game's own, so one-ship sessions behave exactly as without the mod.
    /// </summary>
    internal static class FleetShips
    {
        public const string Feature = "Two player ships";

        private const string KeyShips = "VCF_Ships";
        private const string KeyCrew = "VCF_Crew";

        private static readonly List<PlayerControlledShip> Registered = new List<PlayerControlledShip>();
        private static readonly Dictionary<int, PlayerControlledShip> OwnerCache = new Dictionary<int, PlayerControlledShip>();
        private static readonly Dictionary<int, float> NotOnShip = new Dictionary<int, float>();

        /// <summary>Each ship's slot once known (so a ship lost from the middle of the list doesn't shift the others).</summary>
        private static readonly Dictionary<int, int> KnownSlots = new Dictionary<int, int>();

        /// <summary>The frame the list was last checked for ships that are gone (Prune runs at most once a frame).</summary>
        private static int _prunedFrame = -1;

        /// <summary>
        /// True when more than one player ship exists: only then does anything here differ from the game. The game's busiest
        /// getter asks this tens of thousands of times a second, so the list is only checked for ships that are gone once a
        /// frame (ships leave it through Unregister as they are destroyed; the check is a safety net).
        /// </summary>
        public static bool Active
        {
            get
            {
                PruneOncePerFrame();
                return Registered.Count > 1;
            }
        }

        /// <summary>Every player ship that exists. A List, so a foreach over it allocates nothing; read it, don't change it.</summary>
        public static List<PlayerControlledShip> All
        {
            get
            {
                PruneOncePerFrame();
                return Registered;
            }
        }

        private static void PruneOncePerFrame()
        {
            int frame = Time.frameCount;
            if (frame == _prunedFrame) return;
            _prunedFrame = frame;
            Prune();
        }

        private static void Prune()
        {
            for (int i = Registered.Count - 1; i >= 0; i--)
            {
                if (Registered[i] == null)
                {
                    Registered.RemoveAt(i);
                    ViewChanged();
                }
            }
        }

        public static void Register(PlayerControlledShip ship)
        {
            if (ship == null || Registered.Contains(ship)) return;
            Registered.Add(ship);
            OwnerCache.Clear();
            NotOnShip.Clear();
            ViewChanged();
            if (Registered.Count > 1) Log.Info("Player ship " + Registered.Count + " is in the game (view " + ViewId(ship) + ").");
        }

        public static void Unregister(PlayerControlledShip ship)
        {
            bool hadTwo = Registered.Count > 1;
            // Its slot stays known: the other ships keep theirs.
            Slot(ship);
            Registered.Remove(ship);
            OwnerCache.Clear();
            NotOnShip.Clear();
            Prune();
            ViewChanged();
            if (Registered.Count == 0)
            {
                // No ship left (between missions): the next mission's ships and screens start afresh.
                KnownSlots.Clear();
                Assigned.Clear();
            }
            // One ship left: the game's own "player ship" becomes that ship (it may still point at the lost one).
            if (!hadTwo || Registered.Count != 1) return;
            try
            {
                if (CG.Game.ClientGame.Current != null) CG.Game.ClientGame.Current.PlayerShip = Registered[0];
                Log.Info("Only " + Describe(Registered[0]) + " is left; it is everyone's ship now.");
            }
            catch (Exception e)
            {
                Log.Failure("Making the remaining ship everyone's ship", e);
            }
        }

        public static int ViewId(PlayerControlledShip ship)
        {
            try
            {
                return ship != null && ship.photonView != null ? ship.photonView.ViewID : 0;
            }
            catch
            {
                return 0;
            }
        }

        // ---------------------------------------------------------------- slots

        /// <summary>The host's own latest ship list: the room property only updates here after a round trip to the server.</summary>
        private static int[] _hostShips;
        private static string _hostShipsRoom;

        private static int[] PublishedShips()
        {
            if (_hostShips != null)
            {
                if (PhotonNetwork.IsMasterClient && PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.Name == _hostShipsRoom) return _hostShips;
                _hostShips = null;
            }
            object value;
            if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom == null || !PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(KeyShips, out value)) return null;
            return value as int[];
        }

        /// <summary>
        /// Host: the ships of this mission by slot (ship 1 first). It is written once, when they spawn: a lost ship keeps
        /// its place, so the others keep their slots.
        /// </summary>
        public static void PublishShips(IList<PlayerControlledShip> bySlot)
        {
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null || bySlot == null) return;
            var views = new int[bySlot.Count];
            for (int i = 0; i < views.Length; i++) views[i] = ViewId(bySlot[i]);
            KnownSlots.Clear();
            _hostShips = views;
            _hostShipsRoom = PhotonNetwork.CurrentRoom.Name;
            ViewChanged();
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyShips, views } });
        }

        /// <summary>How many ships this mission started with (lost ones included); 1 without published ships.</summary>
        public static int PublishedCount
        {
            get
            {
                int[] published = PublishedShips();
                return published != null && published.Length > 0 ? published.Length : 1;
            }
        }

        /// <summary>Host: forget the ships, crews and the void jump's lead ship (back in the hub, or a one-ship mission).</summary>
        public static void ClearRoom()
        {
            _hostCrew = null;
            _hostShips = null;
            KnownSlots.Clear();
            ViewChanged();
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            // Empty here at once (the room's copy changes after a round trip).
            _hostCrew = new int[0];
            _hostCrewRoom = PhotonNetwork.CurrentRoom.Name;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable
            {
                { KeyShips, null }, { KeyCrew, null }, { "VCF_ResStart", null }, { VoidJumpLink.KeyLead, null }
            });
        }

        /// <summary>
        /// Whether a ship this mission started with is lost (gone, or dying), from the ship list the host published: so a host
        /// who joined or took over after the loss knows it too. A ship that never spawned doesn't count.
        /// </summary>
        public static bool PublishedShipLost()
        {
            int[] published = PublishedShips();
            if (published == null || published.Length < 2) return false;
            for (int slot = 0; slot < published.Length; slot++)
            {
                if (published[slot] != 0 && !IsAlive(slot)) return true;
            }
            return false;
        }

        /// <summary>A ship's slot: 0 for ship 1 up to 5 for ship 6; -1 when the ship is unknown.</summary>
        public static int Slot(PlayerControlledShip ship)
        {
            if (ship == null) return -1;
            int view = ViewId(ship);
            int[] published = PublishedShips();
            if (published != null)
            {
                int index = Array.IndexOf(published, view);
                if (index >= 0)
                {
                    KnownSlots[view] = index;
                    return index;
                }
            }
            int known;
            if (KnownSlots.TryGetValue(view, out known)) return known;
            // Not published yet (or a client ahead of the room property): spawn order.
            PruneOncePerFrame();
            int lower = 0;
            foreach (PlayerControlledShip other in Registered)
            {
                if (other != ship && ViewId(other) < view) lower++;
            }
            return lower;
        }

        public static PlayerControlledShip BySlot(int slot)
        {
            if (slot < 0) return null;
            PruneOncePerFrame();
            foreach (PlayerControlledShip ship in Registered)
            {
                if (Slot(ship) == slot) return ship;
            }
            return null;
        }

        /// <summary>Whether a slot's ship exists and isn't dying.</summary>
        public static bool IsAlive(int slot)
        {
            PlayerControlledShip ship = BySlot(slot);
            try
            {
                return ship != null && !ship.StartedDeath;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The slots whose ships exist and aren't dying, lowest first.</summary>
        public static List<int> AliveSlots()
        {
            var slots = new List<int>();
            AliveSlots(slots);
            return slots;
        }

        /// <summary>AliveSlots into a list the caller keeps (emptied first), for code that runs often: nothing is allocated.</summary>
        public static void AliveSlots(List<int> slots)
        {
            slots.Clear();
            PruneOncePerFrame();
            for (int i = 0; i < Registered.Count; i++)
            {
                PlayerControlledShip ship = Registered[i];
                if (ship == null) continue;
                int slot = Slot(ship);
                bool dying;
                try
                {
                    dying = ship.StartedDeath;
                }
                catch
                {
                    dying = true;
                }
                if (slot >= 0 && !dying && !slots.Contains(slot)) slots.Add(slot);
            }
            slots.Sort();
        }

        /// <summary>The team of a slot: 0 BLUE / 1 RED in a team match (a PvP mission, or the hub with FLEET BATTLE), 0 otherwise.</summary>
        public static int TeamOfSlot(int slot)
        {
            return FleetSlots.TeamOf(slot, FleetApi.IsTeamMatch);
        }

        /// <summary>The living ships of a team (PvP), lowest slot first; every living ship outside PvP.</summary>
        public static List<int> AliveSlotsOfTeam(int team)
        {
            List<int> slots = AliveSlots();
            if (!FleetSession.IsPvP) return slots;
            for (int i = slots.Count - 1; i >= 0; i--)
            {
                if (TeamOfSlot(slots[i]) != team) slots.RemoveAt(i);
            }
            return slots;
        }

        // ---------------------------------------------------------------- crews

        private const int EntrySize = 4;

        /// <summary>The host's own latest crew list: the room property only updates after a round trip to the server.</summary>
        private static int[] _hostCrew;
        private static string _hostCrewRoom;

        /// <summary>The room's crew list, flat: actor, team, slot, state for each player the host has placed.</summary>
        private static int[] Crew()
        {
            if (_hostCrew != null)
            {
                // Only while this player is the host, in the room the list was made in: a player who hosted an earlier
                // room and inherits the host role in another must not bring the old room's crews (actor numbers repeat).
                if (PhotonNetwork.IsMasterClient && PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.Name == _hostCrewRoom) return _hostCrew;
                _hostCrew = null;
            }
            object value;
            if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom == null || !PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(KeyCrew, out value)) return null;
            return value as int[];
        }

        /// <summary>Every placed player's entry, by actor number.</summary>
        public static Dictionary<int, CrewEntry> Crews()
        {
            var map = new Dictionary<int, CrewEntry>();
            int[] crew = Crew();
            if (crew == null) return map;
            for (int i = 0; i + EntrySize - 1 < crew.Length; i += EntrySize)
            {
                map[crew[i]] = new CrewEntry { Actor = crew[i], Team = crew[i + 1], Slot = crew[i + 2], State = (CrewState)crew[i + 3] };
            }
            return map;
        }

        /// <summary>A player's entry; false when the host hasn't placed them.</summary>
        public static bool TryGetEntry(int actor, out CrewEntry entry)
        {
            int[] crew = Crew();
            if (crew != null)
            {
                for (int i = 0; i + EntrySize - 1 < crew.Length; i += EntrySize)
                {
                    if (crew[i] != actor) continue;
                    entry = new CrewEntry { Actor = actor, Team = crew[i + 1], Slot = crew[i + 2], State = (CrewState)crew[i + 3] };
                    return true;
                }
            }
            entry = new CrewEntry { Actor = actor, Team = -1, Slot = -1, State = CrewState.Playing };
            return false;
        }

        /// <summary>The slot a player crews; -1 when the host has not placed them yet, or they are a spectator.</summary>
        public static int CrewSlot(int actor)
        {
            CrewEntry entry;
            return TryGetEntry(actor, out entry) && entry.State == CrewState.Playing ? entry.Slot : -1;
        }

        /// <summary>A player's team (0 in co-op), spectators included; -1 when they have none.</summary>
        public static int TeamOfActor(int actor)
        {
            CrewEntry entry;
            return TryGetEntry(actor, out entry) ? entry.Team : -1;
        }

        /// <summary>Whether a player is a spectator (PvP: waiting for a seat, or their ship was destroyed).</summary>
        public static bool IsSpectator(int actor)
        {
            CrewEntry entry;
            return TryGetEntry(actor, out entry) && entry.IsSpectator;
        }

        /// <summary>Host: everyone's team, ship and state.</summary>
        public static void PublishCrew(Dictionary<int, CrewEntry> crews)
        {
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            var flat = new int[crews.Count * EntrySize];
            int i = 0;
            foreach (CrewEntry entry in crews.Values)
            {
                flat[i++] = entry.Actor;
                flat[i++] = entry.Team;
                flat[i++] = entry.Slot;
                flat[i++] = (int)entry.State;
            }
            _hostCrew = flat;
            _hostCrewRoom = PhotonNetwork.CurrentRoom.Name;
            ViewChanged();
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyCrew, flat } });
        }

        /// <summary>Host: nobody has a ship or a team (the hub's picks start over).</summary>
        public static void ClearCrews()
        {
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            PublishCrew(new Dictionary<int, CrewEntry>());
        }

        /// <summary>How many players crew a ship (spectators not counted).</summary>
        public static int CrewCount(int slot)
        {
            int count = 0;
            foreach (CrewEntry entry in Crews().Values)
            {
                if (entry.State == CrewState.Playing && entry.Slot == slot) count++;
            }
            return count;
        }

        /// <summary>Whether a player who crews a ship (not a spectator) is in the room right now.</summary>
        public static bool HasCrewInRoom(int slot)
        {
            Photon.Realtime.Room room = PhotonNetwork.CurrentRoom;
            if (room == null) return false;
            foreach (CrewEntry entry in Crews().Values)
            {
                if (entry.State == CrewState.Playing && entry.Slot == slot && room.GetPlayer(entry.Actor) != null) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- whose ship

        /// <summary>The ship a player crews; null before they are placed, or for a spectator.</summary>
        public static PlayerControlledShip ShipOfActor(int actor)
        {
            int slot = CrewSlot(actor);
            return slot >= 0 ? BySlot(slot) : null;
        }

        public static int LocalActor
        {
            get { return PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : -1; }
        }

        /// <summary>The local player's own ship (their crew's), or null before they are placed.</summary>
        public static PlayerControlledShip LocalShip
        {
            get { return ShipOfActor(LocalActor); }
        }

        /// <summary>The ship a character is aboard (inside its rooms or its inertia zone), or null in open space.</summary>
        public static PlayerControlledShip AboardShip(Player player)
        {
            if (player == null) return null;
            try
            {
                SpacePlatform platform = player.ZonedPlatform != null ? player.ZonedPlatform : player.Platform;
                return platform != null ? platform.GetComponent<PlayerControlledShip>() : null;
            }
            catch
            {
                return null;
            }
        }

        private static PlayerControlledShip _watched;

        /// <summary>The ship a spectator is watching (set by the spectator view), or null.</summary>
        public static PlayerControlledShip Watched
        {
            get { return _watched; }
            set
            {
                if (ReferenceEquals(_watched, value)) return;
                _watched = value;
                ViewChanged();
            }
        }

        /// <summary>ViewShip as last worked out, and the frame it was worked out in.</summary>
        private static PlayerControlledShip _view;

        private static int _viewFrame = -1;
        private static readonly List<int> ViewSlots = new List<int>();

        /// <summary>
        /// The ship the local player's screen is about (HUD, helm warnings, markers, ship status):
        /// - a spectator's: the ship they are watching;
        /// - in co-op: the ship they are aboard (or flying), else their own crew's ship;
        /// - in PvP: always their own ship, so a boarder doesn't see an enemy ship's state; without one, their team's first
        ///   living ship;
        /// - then the lowest living slot, then whichever ship is left.
        /// The game reads it (through its own "player ship") tens of thousands of times a second, so it is worked out once a
        /// frame, and again at once when the ships, the crews or the watched ship change.
        /// </summary>
        public static PlayerControlledShip ViewShip
        {
            get
            {
                int frame = Time.frameCount;
                // A ship that was destroyed since (it reads as null) is replaced at once.
                if (frame != _viewFrame || (!ReferenceEquals(_view, null) && _view == null))
                {
                    _viewFrame = frame;
                    _view = FindViewShip();
                }
                return _view;
            }
        }

        /// <summary>ViewShip is worked out again when next read (the ships, the crews or the watched ship changed).</summary>
        private static void ViewChanged()
        {
            _viewFrame = -1;
        }

        private static PlayerControlledShip FindViewShip()
        {
            if (_watched != null && Registered.Contains(_watched)) return _watched;
            PlayerControlledShip aboard;
            try
            {
                aboard = AboardShip(LocalPlayer.Instance);
            }
            catch
            {
                aboard = null;
            }
            if (aboard != null && !FleetSession.IsPvP) return aboard;
            PlayerControlledShip ship = LocalShip;
            if (ship != null) return ship;
            AliveSlots(ViewSlots);
            if (FleetSession.IsPvP)
            {
                int team = TeamOfActor(LocalActor);
                if (team >= 0)
                {
                    for (int i = 0; i < ViewSlots.Count; i++)
                    {
                        if (TeamOfSlot(ViewSlots[i]) != team) continue;
                        ship = BySlot(ViewSlots[i]);
                        if (ship != null) return ship;
                    }
                }
            }
            if (aboard != null) return aboard;
            for (int i = 0; i < ViewSlots.Count; i++)
            {
                ship = BySlot(ViewSlots[i]);
                if (ship != null) return ship;
            }
            PruneOncePerFrame();
            return Registered.Count > 0 ? Registered[0] : null;
        }

        private static readonly Dictionary<int, PlayerControlledShip> Assigned = new Dictionary<int, PlayerControlledShip>();

        /// <summary>
        /// Says which ship a component works for when it is not part of any ship (a HUD element made for one ship, for
        /// example). OwnerOf then returns that ship, so the game's own code inside it reads that ship.
        /// </summary>
        public static void AssignOwner(Component component, PlayerControlledShip ship)
        {
            if (component == null) return;
            if (ship == null) Assigned.Remove(component.GetInstanceID());
            else Assigned[component.GetInstanceID()] = ship;
        }

        /// <summary>The ship assigned to a component with AssignOwner, or null.</summary>
        public static PlayerControlledShip AssignedOwner(Component component)
        {
            PlayerControlledShip ship;
            return component != null && Assigned.TryGetValue(component.GetInstanceID(), out ship) && ship != null ? ship : null;
        }

        /// <summary>
        /// The player ship a ship-mounted object belongs to: its parent ship, its spatial root, or (for parts under the
        /// hull, which the game detaches from the ship) the hull's ship. Null for things that are not on a player ship.
        /// </summary>
        public static PlayerControlledShip OwnerOf(Component component)
        {
            if (component == null) return null;
            int id;
            try
            {
                id = component.GetInstanceID();
            }
            catch
            {
                return null;
            }
            PlayerControlledShip cached;
            if (Assigned.TryGetValue(id, out cached) && cached != null) return cached;
            if (OwnerCache.TryGetValue(id, out cached) && cached != null) return cached;
            float until;
            if (NotOnShip.TryGetValue(id, out until) && Time.unscaledTime < until) return null;

            PlayerControlledShip ship = component.GetComponentInParent<PlayerControlledShip>();
            if (ship == null)
            {
                var orbitObject = component as OrbitObject;
                if (orbitObject != null) ship = orbitObject.GetRootSpatialParent() as PlayerControlledShip;
            }
            if (ship == null)
            {
                ShipHull hull = component.GetComponentInParent<ShipHull>();
                if (hull != null) ship = hull.Ship;
            }
            if (ship == null)
            {
                var carryable = component as CarryableObject;
                if (carryable != null && carryable.ParentPlatform != null) ship = carryable.ParentPlatform.GetComponent<PlayerControlledShip>();
            }
            // Carryables move between ships, so only fixed parts are remembered. "Not on a ship" is only remembered for a
            // second: a part can be looked up before it is attached to its ship.
            if (ship != null && !(component is CarryableObject)) OwnerCache[id] = ship;
            if (ship == null && !(component is CarryableObject))
            {
                if (NotOnShip.Count > 4096) NotOnShip.Clear();
                NotOnShip[id] = Time.unscaledTime + 1f;
            }
            return ship;
        }

        /// <summary>
        /// The player ship whose interior (one of its rooms, not the space around it) contains a world point, or null in
        /// open space. Works for any character, local or remote, from its world position.
        /// </summary>
        public static PlayerControlledShip InteriorAt(Vector3 point)
        {
            PruneOncePerFrame();
            foreach (PlayerControlledShip ship in Registered)
            {
                try
                {
                    ShipHull hull = ship.Hull;
                    var rooms = hull != null ? hull.RoomCollection : null;
                    if (rooms == null) continue;
                    Bounds bounds = rooms.Bounds;
                    bounds.Expand(1f);
                    if (!bounds.Contains(rooms.transform.InverseTransformPoint(point))) continue;
                    Vector3 hit;
                    foreach (var room in rooms.Rooms)
                    {
                        if (room != null && !room.InSpace && rooms.RoomSphereIntersect(room, point, 0.25f, out hit)) return ship;
                    }
                }
                catch
                {
                    // A ship being torn down: not inside it.
                }
            }
            return null;
        }

        /// <summary>"ship 3" outside PvP, "RED 2" in it; "a ship" when unknown. For logs and notices.</summary>
        public static string Describe(PlayerControlledShip ship)
        {
            int slot = Slot(ship);
            if (slot < 0) return "a ship";
            return FleetSession.IsPvP ? FleetSlots.Name(slot, true) : "ship " + (slot + 1);
        }

        /// <summary>
        /// A player's character by actor number, or null. Unlike the game's own lookup it skips characters that are
        /// already destroyed (which throws while a match ends).
        /// </summary>
        public static Player CharacterOf(int actor)
        {
            ClientGame game = ClientGame.Current;
            if (game == null || game.Players == null) return null;
            foreach (Player player in game.Players)
            {
                if (player == null) continue;
                PhotonView view = player.photonView;
                if (view != null && (view.OwnerActorNr == actor || view.CreatorActorNr == actor)) return player;
            }
            return null;
        }
    }
}
