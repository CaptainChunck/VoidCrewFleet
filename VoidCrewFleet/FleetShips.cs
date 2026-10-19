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
    /// - each ship's slot (0 = ship 1, 1 = ship 2), published by the host when it spawns them (room property), with the
    ///   spawn order (root view ID, allocated in sequence by Photon) as the fallback;
    /// - which slot each player belongs to (room property written by the host).
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

        /// <summary>True when more than one player ship exists: only then does anything here differ from the game.</summary>
        public static bool Active
        {
            get
            {
                Prune();
                return Registered.Count > 1;
            }
        }

        public static IList<PlayerControlledShip> All
        {
            get
            {
                Prune();
                return Registered;
            }
        }

        private static void Prune()
        {
            for (int i = Registered.Count - 1; i >= 0; i--)
            {
                if (Registered[i] == null) Registered.RemoveAt(i);
            }
        }

        public static void Register(PlayerControlledShip ship)
        {
            if (ship == null || Registered.Contains(ship)) return;
            Registered.Add(ship);
            OwnerCache.Clear();
            NotOnShip.Clear();
            if (Registered.Count > 1) Log.Info("Player ship " + Registered.Count + " is in the game (view " + ViewId(ship) + ").");
        }

        public static void Unregister(PlayerControlledShip ship)
        {
            bool hadTwo = Registered.Count > 1;
            Registered.Remove(ship);
            OwnerCache.Clear();
            NotOnShip.Clear();
            Prune();
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

        private static int[] PublishedShips()
        {
            object value;
            if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom == null || !PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(KeyShips, out value)) return null;
            return value as int[];
        }

        /// <summary>Host: the ships of this mission, ship 1 first.</summary>
        public static void PublishShips(PlayerControlledShip shipA, PlayerControlledShip shipB)
        {
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyShips, new[] { ViewId(shipA), ViewId(shipB) } } });
        }

        /// <summary>Host: forget the ships and crews (back in the hub, or a one-ship mission).</summary>
        public static void ClearRoom()
        {
            _hostCrew = null;
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyShips, null }, { KeyCrew, null }, { "VCF_ResStart", null } });
        }

        /// <summary>0 for ship 1, 1 for ship 2, -1 when the ship is unknown.</summary>
        public static int Slot(PlayerControlledShip ship)
        {
            if (ship == null) return -1;
            int view = ViewId(ship);
            int[] published = PublishedShips();
            if (published != null)
            {
                int index = Array.IndexOf(published, view);
                if (index >= 0) return index;
            }
            // Not published yet (or a client ahead of the room property): spawn order.
            Prune();
            int lower = 0;
            foreach (PlayerControlledShip other in Registered)
            {
                if (other != ship && ViewId(other) < view) lower++;
            }
            return lower;
        }

        public static PlayerControlledShip BySlot(int slot)
        {
            Prune();
            foreach (PlayerControlledShip ship in Registered)
            {
                if (Slot(ship) == slot) return ship;
            }
            return null;
        }

        // ---------------------------------------------------------------- crews

        /// <summary>The host's own latest crew list: the room property only updates after a round trip to the server.</summary>
        private static int[] _hostCrew;
        private static string _hostCrewRoom;

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

        /// <summary>The slot a player crews, or -1 when the host has not placed them yet.</summary>
        public static int CrewSlot(int actor)
        {
            int[] crew = Crew();
            if (crew == null) return -1;
            for (int i = 0; i + 1 < crew.Length; i += 2)
            {
                if (crew[i] == actor) return crew[i + 1];
            }
            return -1;
        }

        public static Dictionary<int, int> CrewMap()
        {
            var map = new Dictionary<int, int>();
            int[] crew = Crew();
            if (crew == null) return map;
            for (int i = 0; i + 1 < crew.Length; i += 2) map[crew[i]] = crew[i + 1];
            return map;
        }

        /// <summary>Host: who crews which ship.</summary>
        public static void PublishCrew(Dictionary<int, int> map)
        {
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            var flat = new int[map.Count * 2];
            int i = 0;
            foreach (KeyValuePair<int, int> pair in map)
            {
                flat[i++] = pair.Key;
                flat[i++] = pair.Value;
            }
            _hostCrew = flat;
            _hostCrewRoom = PhotonNetwork.CurrentRoom.Name;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeyCrew, flat } });
        }

        public static int CrewCount(int slot)
        {
            int count = 0;
            foreach (KeyValuePair<int, int> pair in CrewMap())
            {
                if (pair.Value == slot) count++;
            }
            return count;
        }

        // ---------------------------------------------------------------- whose ship

        /// <summary>The ship a player crews (their team's ship); null before they are placed.</summary>
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

        /// <summary>
        /// The ship the local player's screen is about (HUD, helm warnings, markers, ship status). In co-op it is the ship
        /// they are aboard (or flying), else their own crew's ship; in PvP always their own team's ship, so a boarder doesn't
        /// see the enemy ship's state. Then ship 1, then whichever ship is left.
        /// </summary>
        public static PlayerControlledShip ViewShip
        {
            get
            {
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
                ship = aboard;
                if (ship != null) return ship;
                ship = BySlot(0);
                if (ship != null) return ship;
                Prune();
                return Registered.Count > 0 ? Registered[0] : null;
            }
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
            Prune();
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

        public static string Describe(PlayerControlledShip ship)
        {
            int slot = Slot(ship);
            return slot == 0 ? "ship 1" : (slot == 1 ? "ship 2" : "a ship");
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
