using System;
using System.Collections.Generic;
using CG.Client;
using CG.Client.Ship;
using CG.Game.Player;
using CG.Objects;
using CG.Ship.Hull;
using CG.Ship.Modules;
using CG.Ship.Object;
using CG.Space;
using Gameplay.Atmosphere;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace VoidCrewFleet
{
    /// <summary>
    /// PvP: hacking an enemy airlock with an Airlock Hacking Terminal (<see cref="HackTerminal"/>).
    /// 1. Carry a terminal to the control panel outside an enemy airlock and hold Fire on the panel: it is plugged in.
    /// 2. The hack takes a minute. The ship's own crew can pull the terminal out (hold Fire on it) until then.
    /// 3. Then the whole hacking team can use every door of that ship for five minutes (not the cockpit's), including the
    ///    airlock's door buttons and pressurise lever (not its safety override). Both teams see a countdown.
    /// Enemies never get into the cockpit: its doors stay shut to them, and one who walks in anyway is pushed back out.
    /// The host decides everything and keeps the hacks in a room property, so everyone (late joiners too) sees them. Their
    /// end times are the server's clock in milliseconds, an int that wraps around, so they are compared by difference.
    /// </summary>
    internal static class PvpHack
    {
        public const string Feature = HackTerminal.Feature;
        private const string RoomKey = "VCF_Hack";

        private const float Reach = 2.6f;
        private const float HostReach = 5f;
        private const float PlugHold = 1.5f;
        private const float PullHold = 3f;
        private const float AimAngle = 16f;
        public const float HackSeconds = 60f;
        public const float AccessSeconds = 300f;
        private const int HackMs = (int)(HackSeconds * 1000f);
        private const int AccessMs = (int)(AccessSeconds * 1000f);
        private const int SightMask = (1 << 10) | (1 << 12) | (1 << 27);

        /// <summary>How long the hacker's game has to put a plugged-in terminal away before the host removes it itself.</summary>
        private const float RemoveSeconds = 3f;

        /// <summary>Further than this from the enemy ship's rooms, none of its airlock panels can be in reach.</summary>
        private const float NearShip = 30f;

        /// <summary>How long a found outside panel is used before it is looked up again.</summary>
        private const float PanelSeconds = 10f;

        /// <summary>How often the countdown text is rebuilt (it only shows whole seconds).</summary>
        private const float TimerSeconds = 0.25f;

        private enum State : byte
        {
            Hacking = 1,
            Access = 2
        }

        private enum Step : byte
        {
            None,
            Plug,
            Pull
        }

        private sealed class Entry
        {
            public int Ship;
            public int Index;
            public State State;

            /// <summary>When the hack or the door access ends (server milliseconds).</summary>
            public int Until;

            public int Team;
            public Vector3 Position;
            public Quaternion Rotation;

            public long Key
            {
                get { return KeyOf(Ship, Index); }
            }

            public object[] ToData()
            {
                return new object[]
                {
                    Ship, Index, (byte)State, Until, Team,
                    new[] { Position.x, Position.y, Position.z, Rotation.x, Rotation.y, Rotation.z, Rotation.w }
                };
            }

            /// <summary>
            /// An entry from the room property. Version 0.4.0 wrote seven values: the end as PhotonNetwork.Time (seconds of the
            /// same clock) and the hacker's actor number before the team; those still read.
            /// </summary>
            public static Entry FromData(object entry)
            {
                var data = entry as object[];
                if (data == null || data.Length < 6) return null;
                bool old = data.Length >= 7 && data[3] is double;
                int team = old ? 5 : 4;
                var pose = data[team + 1] as float[];
                if (!(data[0] is int) || !(data[1] is int) || !(data[2] is byte) || !(old || data[3] is int) || !(data[team] is int) || pose == null || pose.Length < 7)
                    return null;
                return new Entry
                {
                    Ship = (int)data[0],
                    Index = (int)data[1],
                    State = (State)(byte)data[2],
                    // The low 32 bits of the milliseconds: the server's clock, wrapped as it wraps.
                    Until = old ? unchecked((int)(long)Math.Round((double)data[3] * 1000.0)) : (int)data[3],
                    Team = (int)data[team],
                    Position = new Vector3(pose[0], pose[1], pose[2]),
                    Rotation = new Quaternion(pose[3], pose[4], pose[5], pose[6])
                };
            }
        }

        private sealed class Shown
        {
            public GameObject Model;

            /// <summary>The model's materials that glow (copies, destroyed with it).</summary>
            public Material[] Materials;

            /// <summary>Every material of the model, to destroy with it.</summary>
            public Material[] AllMaterials;

            /// <summary>Whether the glow is on now; null until first set.</summary>
            public bool? Lit;
        }

        /// <summary>A terminal plugged into a panel, which its holder's game puts away and destroys; the host checks it did.</summary>
        private sealed class Plugged
        {
            public CarryableObject Terminal;
            public float CheckAt;
        }

        /// <summary>A ship's airlocks, in the order every client finds them (an airlock's index names it in messages).</summary>
        private sealed class ShipAirlocks
        {
            public PlayerControlledShip Ship;
            public Airlock[] Airlocks;
        }

        /// <summary>An airlock's control panel outside the ship (Panel is never set when it has none), and where it is.</summary>
        private sealed class Outside
        {
            public Airlock Airlock;
            public AirlockButtonPanel Panel;

            /// <summary>The middle of the panel, in the panel's own space.</summary>
            public Vector3 Middle;

            /// <summary>Out of the outer door to space, in the airlock's own space.</summary>
            public Vector3 Outward;

            public float Until;
        }

        private static readonly Dictionary<long, Entry> Entries = new Dictionary<long, Entry>();
        private static readonly Dictionary<long, Shown> Models = new Dictionary<long, Shown>();
        private static readonly Dictionary<int, ShipAirlocks> AirlocksByShip = new Dictionary<int, ShipAirlocks>();
        private static readonly Dictionary<int, Outside> OutsideByAirlock = new Dictionary<int, Outside>();
        private static readonly Dictionary<int, Room> CockpitRooms = new Dictionary<int, Room>();

        /// <summary>Host: every terminal plugged in this match (by object), so none is plugged in twice.</summary>
        private static readonly HashSet<int> UsedTerminals = new HashSet<int>();

        /// <summary>Host: plugged-in terminals to check on (see Plugged).</summary>
        private static readonly List<Plugged> PluggedTerminals = new List<Plugged>();

        private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");

        /// <summary>Stands for "not read yet": never the value of a room property.</summary>
        private static readonly object NotRead = new object();

        private static object _roomData;

        /// <summary>
        /// The host's own list (Entries) is the latest one. A room property only changes on the host itself once the server
        /// sends the change back (the game broadcasts property changes to everyone), so right after a write the room still
        /// holds the old list, and a second change built on it would undo the first. So from its first write in a room the
        /// host builds on its own list, for as long as it is that room's host; any other client, and a new host until its
        /// first write, reads the room property.
        /// </summary>
        private static bool _hostWrote;

        private static string _hostRoom;
        private static int _hostActor = -1;

        private static string _timerText;
        private static float _nextTimers;
        private static long _takenKey;
        private static int _takenSeconds = -1;
        private static string _takenText;

        private static Step _holding;
        private static long _holdTarget;
        private static float _holdStart;
        private static bool _needRelease;
        private static float _nextRequest;
        private static float _nextPush;
        private static float _lastCockpitWarning = -100f;
        private static float _inCockpitSince = -1f;
        private static Color? _glow;

        // The action hint as last built: its step, the Fire key's name (looked up when a step begins) and the percentage.
        private static Step _hintStep;
        private static string _hintKey;
        private static int _hintPercent = -1;
        private static string _hintText;

        private static UIDocument _timerDocument;
        private static Label _timerLabel;

        private static long KeyOf(int ship, int index)
        {
            return ((long)ship << 8) | (uint)(index & 0xff);
        }

        /// <summary>The server's clock in milliseconds (the same on every client; it wraps around).</summary>
        private static int Now
        {
            get { return PhotonNetwork.ServerTimestamp; }
        }

        /// <summary>Milliseconds left until a server time, right across the clock's wrap (negative once it has passed).</summary>
        private static int MsUntil(int time)
        {
            return unchecked(time - Now);
        }

        public static bool Active
        {
            get { return PvpCombat.Active && HackTerminal.Registered && PvpTeams.Locked; }
        }

        // ---------------------------------------------------------------- ships, airlocks, panels

        private static PlayerControlledShip ShipOfView(int view)
        {
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
                if (ship != null && FleetShips.ViewId(ship) == view) return ship;
            }
            return null;
        }

        private static Airlock AirlockAt(int ship, int index)
        {
            PlayerControlledShip owner = ShipOfView(ship);
            if (owner == null) return null;
            Airlock[] airlocks = AirlocksOf(owner);
            return index >= 0 && index < airlocks.Length ? airlocks[index] : null;
        }

        /// <summary>
        /// A ship's airlocks in hierarchy order, the same on every client. Found once per ship, and again when one of them is
        /// destroyed.
        /// </summary>
        private static Airlock[] AirlocksOf(PlayerControlledShip ship)
        {
            int id = ship.GetInstanceID();
            ShipAirlocks known;
            if (AirlocksByShip.TryGetValue(id, out known) && known.Ship != null && AllThere(known.Airlocks)) return known.Airlocks;
            if (known == null) ForgetLostShips();
            known = new ShipAirlocks { Ship = ship, Airlocks = ship.GetComponentsInChildren<Airlock>(true) };
            AirlocksByShip[id] = known;
            return known.Airlocks;
        }

        private static bool AllThere(Airlock[] airlocks)
        {
            foreach (Airlock airlock in airlocks)
            {
                if (airlock == null) return false;
            }
            return true;
        }

        /// <summary>Drops what is remembered about ships and airlocks that are gone (when a new ship turns up).</summary>
        private static void ForgetLostShips()
        {
            var lostShips = new List<int>();
            foreach (KeyValuePair<int, ShipAirlocks> known in AirlocksByShip)
            {
                if (known.Value.Ship == null) lostShips.Add(known.Key);
            }
            foreach (int id in lostShips) AirlocksByShip.Remove(id);
            var lostAirlocks = new List<int>();
            foreach (KeyValuePair<int, Outside> known in OutsideByAirlock)
            {
                if (known.Value.Airlock == null) lostAirlocks.Add(known.Key);
            }
            foreach (int id in lostAirlocks) OutsideByAirlock.Remove(id);
        }

        private static AirlockDoor Door(Airlock airlock, AirlockDoorType type)
        {
            if (airlock == null || airlock.airlockDoors == null) return null;
            foreach (AirlockDoor door in airlock.airlockDoors)
            {
                if (door != null && door.airlockDoorType == type) return door;
            }
            return null;
        }

        private static Vector3 DoorPoint(AbstractDoor door)
        {
            return door.Portal != null ? door.Portal.transform.position : door.transform.position;
        }

        /// <summary>
        /// The direction from an outer airlock door out to space: the door's own axis, pointing away from the inside of the
        /// ship. The inside is the airlock's sample point of the ship's air, else its inner door, else the middle of the
        /// ship's rooms. (The space room's connection point used before can be the doorway itself or the world's origin.)
        /// </summary>
        private static Vector3 Outward(Airlock airlock, AirlockDoor door)
        {
            Vector3 axis = door.transform.forward;
            Vector3 doorPoint = DoorPoint(door);
            float side = 0f;
            string from = null;
            RoomPoint air = airlock.ShipAtmospherePoint;
            if (air != null)
            {
                side = Vector3.Dot(doorPoint - air.transform.position, axis);
                from = "the ship's air next to the airlock";
            }
            if (Mathf.Abs(side) < 0.1f)
            {
                AirlockDoor inner = Door(airlock, AirlockDoorType.Inner);
                if (inner != null)
                {
                    side = Vector3.Dot(doorPoint - DoorPoint(inner), axis);
                    from = "the airlock's inner door";
                }
            }
            if (Mathf.Abs(side) < 0.1f)
            {
                PlayerControlledShip ship = FleetShips.OwnerOf(airlock);
                var rooms = ship != null && ship.Hull != null ? ship.Hull.RoomCollection : null;
                if (rooms != null)
                {
                    side = Vector3.Dot(doorPoint - rooms.transform.TransformPoint(rooms.Bounds.center), axis);
                    from = "the middle of the ship";
                }
            }
            if (Mathf.Abs(side) < 0.1f)
            {
                Log.InfoOnce("hack outward door", "PvP: an airlock's outside is taken from its outer door's own facing (nothing inside the ship to go by).");
                return axis;
            }
            Log.InfoOnce("hack outward " + from, "PvP: an airlock's outside is found from " + from + ".");
            return side > 0f ? axis : -axis;
        }

        private static Bounds BoundsOf(Component component)
        {
            Renderer[] renderers = component.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(component.transform.position, Vector3.one * 0.3f);
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        /// <summary>True for a reference to something that has been destroyed since (not for one that was never set).</summary>
        private static bool Destroyed(UnityEngine.Object thing)
        {
            return !ReferenceEquals(thing, null) && thing == null;
        }

        /// <summary>
        /// The airlock's control panel outside the ship and where it is, looked up again after a few seconds or when the
        /// airlock or its panel is destroyed.
        /// </summary>
        private static Outside OutsideOf(Airlock airlock)
        {
            if (airlock == null) return null;
            int id = airlock.GetInstanceID();
            Outside outside;
            if (OutsideByAirlock.TryGetValue(id, out outside) && Time.unscaledTime < outside.Until && outside.Airlock != null && !Destroyed(outside.Panel)) return outside;
            Vector3 outward;
            AirlockButtonPanel panel = FindOutsidePanel(airlock, out outward);
            outside = new Outside
            {
                Airlock = airlock,
                Panel = panel,
                Outward = airlock.transform.InverseTransformDirection(outward),
                Until = Time.unscaledTime + PanelSeconds
            };
            if (panel != null) outside.Middle = panel.transform.InverseTransformPoint(BoundsOf(panel).center);
            OutsideByAirlock[id] = outside;
            return outside;
        }

        /// <summary>The airlock's control panel outside the ship, or null; and the direction out of its outer door.</summary>
        private static AirlockButtonPanel OutsidePanel(Airlock airlock, out Vector3 outward)
        {
            Outside outside = OutsideOf(airlock);
            outward = outside != null ? airlock.transform.TransformDirection(outside.Outward) : Vector3.forward;
            return outside != null ? outside.Panel : null;
        }

        /// <summary>The airlock's control panel outside the ship (the one furthest out past the outer door), or null.</summary>
        private static AirlockButtonPanel FindOutsidePanel(Airlock airlock, out Vector3 outward)
        {
            outward = Vector3.forward;
            AirlockDoor door = Door(airlock, AirlockDoorType.Outer);
            if (door == null || airlock.ButtonPanels == null) return null;
            outward = Outward(airlock, door);
            Vector3 doorPoint = DoorPoint(door);
            AirlockButtonPanel best = null;
            float bestOut = 0.3f;
            foreach (AirlockButtonPanel panel in airlock.ButtonPanels)
            {
                if (panel == null) continue;
                float outside = Vector3.Dot(BoundsOf(panel).center - doorPoint, outward);
                if (outside <= bestOut) continue;
                bestOut = outside;
                best = panel;
            }
            return best;
        }

        /// <summary>Where a terminal sits on an outside panel: on its face, facing out.</summary>
        private static void PanelPose(AirlockButtonPanel panel, Vector3 outward, out Vector3 point, out Quaternion rotation)
        {
            Bounds bounds = BoundsOf(panel);
            Vector3 extents = bounds.extents;
            float depth = Mathf.Abs(outward.x) * extents.x + Mathf.Abs(outward.y) * extents.y + Mathf.Abs(outward.z) * extents.z;
            point = bounds.center + outward * (depth + 0.01f);
            PlayerControlledShip ship = FleetShips.OwnerOf(panel);
            rotation = Quaternion.LookRotation(outward, ship != null ? ship.transform.up : Vector3.up);
        }

        // ---------------------------------------------------------------- the cockpit

        /// <summary>The room the ship's helm is in (cached per ship), or null.</summary>
        public static Room CockpitRoom(PlayerControlledShip ship)
        {
            if (ship == null) return null;
            Room room;
            if (CockpitRooms.TryGetValue(ship.GetInstanceID(), out room)) return room;
            room = null;
            try
            {
                Helm helm = ship.GetModule<Helm>();
                var rooms = ship.Hull != null ? ship.Hull.RoomCollection : null;
                if (helm != null && rooms != null)
                {
                    Vector3 hit;
                    foreach (Room candidate in rooms.Rooms)
                    {
                        if (candidate == null || candidate.InSpace) continue;
                        if (!rooms.RoomSphereIntersect(candidate, helm.transform.position + ship.transform.up * 0.5f, 0.5f, out hit)) continue;
                        room = candidate;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Failure("Finding a ship's cockpit", e);
            }
            CockpitRooms[ship.GetInstanceID()] = room;
            int doors = 0;
            if (room != null)
            {
                foreach (AbstractDoor door in ship.GetComponentsInChildren<AbstractDoor>(true))
                {
                    if (IsCockpitDoor(door, room)) doors++;
                }
            }
            Log.Info("PvP: " + FleetShips.Describe(ship) + "'s cockpit is " + (room != null ? room.name + " (" + doors + " door(s) into it)" : "not found") + ".");
            return room;
        }

        private static bool IsCockpitDoor(AbstractDoor door, Room cockpit)
        {
            return door != null && cockpit != null && door.Portal != null && door.Portal.RoomConnections != null && door.Portal.RoomConnections.Contains(cockpit);
        }

        /// <summary>A door into its ship's cockpit (never opened for enemies).</summary>
        public static bool IsCockpitDoor(AbstractDoor door)
        {
            return door != null && IsCockpitDoor(door, CockpitRoom(FleetShips.OwnerOf(door)));
        }

        // ---------------------------------------------------------------- access

        /// <summary>Whether this team (0 BLUE, 1 RED) may use the doors of this ship right now.</summary>
        public static bool HasAccess(int team, PlayerControlledShip ship)
        {
            if (team < 0 || ship == null || Entries.Count == 0) return false;
            int view = FleetShips.ViewId(ship);
            foreach (Entry entry in Entries.Values)
            {
                if (entry.State == State.Access && entry.Ship == view && entry.Team == team && MsUntil(entry.Until) > 0) return true;
            }
            return false;
        }

        public static bool LocalHasAccess(PlayerControlledShip ship)
        {
            ReadRoom();
            return HasAccess(FleetShips.TeamOfActor(FleetShips.LocalActor), ship);
        }

        // ---------------------------------------------------------------- the room property

        /// <summary>True while the host's own list is the one to use (see _hostWrote); forgets that once it no longer holds.</summary>
        private static bool HostHasLatest()
        {
            if (!_hostWrote) return false;
            Photon.Realtime.Room room = PhotonNetwork.CurrentRoom;
            Photon.Realtime.Player master = PhotonNetwork.MasterClient;
            if (room != null && PhotonNetwork.IsMasterClient && master != null && master.ActorNumber == _hostActor && room.Name == _hostRoom) return true;
            // Another room, or not its host any more: the room property is the list again.
            _hostWrote = false;
            _roomData = NotRead;
            return false;
        }

        private static void ReadRoom()
        {
            if (HostHasLatest()) return;
            object data = null;
            Photon.Realtime.Room room = PhotonNetwork.CurrentRoom;
            if (room != null && room.CustomProperties != null) room.CustomProperties.TryGetValue(RoomKey, out data);
            if (ReferenceEquals(data, _roomData)) return;
            _roomData = data;
            _nextTimers = 0f;
            Entries.Clear();
            var entries = data as object[];
            if (entries == null) return;
            foreach (object item in entries)
            {
                Entry entry = Entry.FromData(item);
                if (entry != null) Entries[entry.Key] = entry;
            }
        }

        private static void WriteRoom()
        {
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            var entries = new List<object>();
            foreach (Entry entry in Entries.Values) entries.Add(entry.ToData());
            object data = entries.Count > 0 ? entries.ToArray() : null;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { RoomKey, data } });
            _roomData = data;
            _hostWrote = true;
            _hostRoom = PhotonNetwork.CurrentRoom.Name;
            _hostActor = FleetShips.LocalActor;
            _nextTimers = 0f;
        }

        /// <summary>Host, at the start of a PvP match: no hacks from an earlier one.</summary>
        public static void ResetRoom()
        {
            Entries.Clear();
            AirlocksByShip.Clear();
            OutsideByAirlock.Clear();
            CockpitRooms.Clear();
            UsedTerminals.Clear();
            PluggedTerminals.Clear();
            _roomData = NotRead;
            _glow = null;
            _nextTimers = 0f;
            WriteRoom();
        }

        // ---------------------------------------------------------------- every frame, every client

        public static void Frame()
        {
            // Notices a new room or host between matches too.
            HostHasLatest();
            if (!Active)
            {
                if (Models.Count > 0) ClearModels();
                ShowTimers(null);
                _nextTimers = 0f;
                SetHint(Step.None, null);
                return;
            }
            try
            {
                ReadRoom();
                ShowModels();
                // The countdowns only show whole seconds: their text is rebuilt a few times a second, not every frame.
                if (Time.unscaledTime >= _nextTimers)
                {
                    _timerText = TimerText();
                    _nextTimers = Time.unscaledTime + TimerSeconds;
                }
                ShowTimers(_timerText);
                HandleInput();
                KeepOutOfCockpit();
            }
            catch (Exception e)
            {
                Log.Failure("Hacking terminals", e);
            }
        }

        private static void ShowModels()
        {
            List<long> gone = null;
            foreach (KeyValuePair<long, Shown> shown in Models)
            {
                Entry entry;
                if (Entries.TryGetValue(shown.Key, out entry) && entry.State == State.Hacking) continue;
                DestroyModel(shown.Value);
                if (gone == null) gone = new List<long>();
                gone.Add(shown.Key);
            }
            if (gone != null)
            {
                foreach (long key in gone) Models.Remove(key);
            }

            foreach (Entry entry in Entries.Values)
            {
                if (entry.State != State.Hacking) continue;
                Shown shown;
                if (!Models.TryGetValue(entry.Key, out shown) || shown.Model == null)
                {
                    PlayerControlledShip ship = ShipOfView(entry.Ship);
                    if (ship == null) continue;
                    shown = new Shown { Model = HackTerminal.BuildModel() };
                    shown.Model.transform.SetParent(ship.transform, false);
                    shown.Model.transform.localPosition = entry.Position;
                    shown.Model.transform.localRotation = entry.Rotation;
                    var materials = new List<Material>();
                    foreach (Renderer renderer in shown.Model.GetComponentsInChildren<Renderer>()) materials.AddRange(renderer.materials);
                    shown.AllMaterials = materials.ToArray();
                    shown.Materials = materials.FindAll(m => m != null && m.HasProperty(EmissiveColorId)).ToArray();
                    Models[entry.Key] = shown;
                    if (FleetShips.Slot(ship) == FleetShips.CrewSlot(FleetShips.LocalActor)) BigWarning("THE ENEMY IS HACKING OUR AIRLOCK! PULL THEIR TERMINAL OUT OF THE OUTSIDE PANEL");
                    else if (!PvpBoarding.IsEnemyShip(ship)) BigWarning("THE ENEMY IS HACKING " + FleetShips.Describe(ship) + "'S AIRLOCK!");
                }
                // A working pulse while it hacks (the materials change only when it turns on or off).
                bool lit = Mathf.Repeat(Time.time, 1f) < 0.5f;
                if (shown.Lit == lit) continue;
                shown.Lit = lit;
                SetGlow(shown, lit);
            }
        }

        private static Color Glow
        {
            get
            {
                if (!_glow.HasValue) _glow = new Color(0.2f, 0.9f, 1f) * 6f;
                return _glow.Value;
            }
        }

        private static void SetGlow(Shown shown, bool lit)
        {
            if (shown.Materials == null) return;
            Color color = lit ? Glow : Color.black;
            foreach (Material material in shown.Materials)
            {
                if (material != null) material.SetColor(EmissiveColorId, color);
            }
        }

        private static void DestroyModel(Shown shown)
        {
            if (shown.AllMaterials != null)
            {
                foreach (Material material in shown.AllMaterials)
                {
                    if (material != null) UnityEngine.Object.Destroy(material);
                }
            }
            if (shown.Model != null) UnityEngine.Object.Destroy(shown.Model);
        }

        private static void ClearModels()
        {
            foreach (Shown shown in Models.Values) DestroyModel(shown);
            Models.Clear();
        }

        private static void BigWarning(string text)
        {
            try
            {
                if (ViewEventBus.Instance != null) ViewEventBus.Instance.OnWarningMessage.Publish(text, null, 4000);
            }
            catch
            {
                Notice.Show(text);
            }
        }

        // ---------------------------------------------------------------- the countdowns

        private static string Clock(double seconds)
        {
            int total = Mathf.Max(0, Mathf.CeilToInt((float)seconds));
            return (total / 60) + ":" + (total % 60).ToString("00");
        }

        /// <summary>The local team's hacks and door access, and the enemy's on our ship.</summary>
        private static string TimerText()
        {
            int mine = FleetShips.TeamOfActor(FleetShips.LocalActor);
            if (mine < 0 || Entries.Count == 0) return null;
            var lines = new List<string>();
            foreach (Entry entry in Entries.Values)
            {
                int msLeft = MsUntil(entry.Until);
                if (msLeft <= 0) continue;
                double left = msLeft / 1000.0;
                PlayerControlledShip ship = ShipOfView(entry.Ship);
                int shipSlot = FleetShips.Slot(ship);
                if (shipSlot < 0) continue;
                string shipName = FleetSlots.Name(shipSlot, true);
                bool ours = FleetShips.TeamOfSlot(shipSlot) == mine;
                // The game's colours: light blue for our own progress, light green for what we won, orange and red for threats.
                if (entry.State == State.Hacking)
                {
                    if (entry.Team == mine) lines.Add("<color=#BDE7FB>HACKING " + shipName + "'S AIRLOCK  " + Clock(left) + "</color>");
                    else if (ours) lines.Add("<color=#F6AE3C>ENEMY HACKING " + shipName + "'S AIRLOCK  " + Clock(left) + "</color>");
                }
                else
                {
                    if (entry.Team == mine) lines.Add("<color=#83C886>DOOR ACCESS TO " + shipName + "  " + Clock(left) + "</color>");
                    else if (ours) lines.Add("<color=#EC1E24>THE ENEMY CAN OPEN " + shipName + "'S DOORS  " + Clock(left) + "</color>");
                }
            }
            return lines.Count > 0 ? string.Join("\n", lines.ToArray()) : null;
        }

        private static void ShowTimers(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                if (_timerLabel != null) _timerLabel.style.display = DisplayStyle.None;
                return;
            }
            if (_timerLabel == null || _timerLabel.panel == null)
            {
                if (_timerDocument == null) _timerDocument = Ui.CreateOverlay("Void Crew Fleet hack timers", 3900);
                if (_timerDocument == null || _timerDocument.rootVisualElement == null) return;
                VisualElement root = _timerDocument.rootVisualElement;
                root.pickingMode = PickingMode.Ignore;
                _timerLabel = Ui.HudLabel("", 20, Color.white);
                _timerLabel.style.position = Position.Absolute;
                _timerLabel.style.left = 0;
                _timerLabel.style.right = 0;
                _timerLabel.style.top = new Length(9, LengthUnit.Percent);
                _timerLabel.style.unityTextAlign = TextAnchor.UpperCenter;
                root.Add(_timerLabel);
            }
            _timerLabel.text = text;
            _timerLabel.style.display = DisplayStyle.Flex;
        }

        // ---------------------------------------------------------------- the local player's actions

        private static void HandleInput()
        {
            InputAction fire = PvpPunch.Fire();
            long target = 0;
            string taken = null;
            Step step = PvpPunch.CanPunch() ? FindStep(out target, out taken) : Step.None;
            // With the fists up, Fire punches.
            bool pressed = fire != null && fire.IsPressed() && !PvpPunch.LocalStanceUp;
            if (!pressed) _needRelease = false;
            if (step == Step.None)
            {
                _holding = Step.None;
                SetHint(Step.None, taken);
                return;
            }
            if (!pressed || _needRelease || step != _holding || target != _holdTarget)
            {
                _holding = pressed && !_needRelease ? step : Step.None;
                _holdTarget = target;
                _holdStart = Time.unscaledTime;
            }
            float needed = step == Step.Plug ? PlugHold : PullHold;
            float progress = _holding == step ? Mathf.Clamp01((Time.unscaledTime - _holdStart) / needed) : 0f;
            if (step != _hintStep) _hintKey = PvpPunch.FireName(fire);
            int percent = progress > 0f ? Mathf.RoundToInt(progress * 100f) : 0;
            if (step != _hintStep || percent != _hintPercent)
            {
                _hintPercent = percent;
                _hintText = "HOLD " + _hintKey + "  " + (step == Step.Plug ? "PLUG IN THE HACKING TERMINAL" : "PULL OUT THE ENEMY TERMINAL") +
                            (percent > 0 ? "  " + percent + "%" : "");
            }
            SetHint(step, _hintText);
            if (progress < 1f || Time.unscaledTime < _nextRequest) return;
            _needRelease = true;
            _holding = Step.None;
            _nextRequest = Time.unscaledTime + 1f;
            int ship = (int)(target >> 8);
            int index = (int)(target & 0xff);
            if (step == Step.Plug)
            {
                CarryableObject held = HackTerminal.LocalHeld;
                if (held != null && held.photonView != null) Net.ToHost(Msg.HackPlug, held.photonView.ViewID, ship, index);
            }
            else Net.ToHost(Msg.HackPull, ship, index);
        }

        /// <summary>The hacking line of the action hint, for a step (None: the text given, or none).</summary>
        private static void SetHint(Step step, string text)
        {
            _hintStep = step;
            if (step == Step.None) _hintPercent = -1;
            ActionHint.Set("hack", ActionHint.HackPriority, text);
        }

        /// <summary>
        /// What Fire does where the local player looks: pull an enemy terminal out of our airlock, or plug the held one into
        /// an enemy airlock's outside panel. At an enemy airlock that is being hacked or is hacked already, nothing, but
        /// "taken" says so.
        /// </summary>
        private static Step FindStep(out long target, out string taken)
        {
            target = 0;
            taken = null;
            LocalPlayer me = LocalPlayer.Instance;
            Vector3 eye;
            Vector3 aim;
            if (me == null || !Aim.Get(out eye, out aim)) return Step.None;
            int myTeam = FleetShips.TeamOfActor(FleetShips.LocalActor);
            if (myTeam < 0 || Spectator.WatchState(FleetShips.LocalActor) != CrewState.Playing) return Step.None;
            CarryableObject held = me.Payload;

            // An enemy terminal on our own airlock: pull it out (not with a usable item in hand, which Fire would use too).
            if (held == null || !(held is InteractiveCarryable) && !HackTerminal.Is(held))
            {
                foreach (KeyValuePair<long, Shown> shown in Models)
                {
                    Entry entry;
                    if (shown.Value.Model == null || !Entries.TryGetValue(shown.Key, out entry)) continue;
                    // Any ship of our own team: a teammate may pull an enemy terminal out of it.
                    int slot = FleetShips.Slot(ShipOfView(entry.Ship));
                    if (slot < 0 || FleetShips.TeamOfSlot(slot) != myTeam) continue;
                    if (!InSight(eye, aim, shown.Value.Model.transform.position)) continue;
                    target = shown.Key;
                    return Step.Pull;
                }
            }

            // Holding a terminal, looking at the outside panel of an enemy airlock (from close by: nothing to do further off).
            if (!HackTerminal.Is(held)) return Step.None;
            float bestAngle = AimAngle;
            foreach (PlayerControlledShip enemy in FleetShips.All)
            {
                // Every enemy ship close enough: the airlock panel aimed at most directly wins.
                if (!PvpBoarding.IsEnemyShip(enemy) || FarFrom(enemy, eye)) continue;
                Airlock[] airlocks = AirlocksOf(enemy);
                int view = FleetShips.ViewId(enemy);
                FindPanel(airlocks, view, eye, aim, ref bestAngle, ref target);
            }
            if (target == 0) return Step.None;
            Entry hack;
            if (!Entries.TryGetValue(target, out hack)) return Step.Plug;
            // Being hacked, or hacked already: nothing to plug in there.
            taken = TakenText(hack);
            target = 0;
            return Step.None;
        }

        /// <summary>The outside panel of one ship's airlocks most directly aimed at, in reach and in sight; better than bestAngle.</summary>
        private static void FindPanel(Airlock[] airlocks, int view, Vector3 eye, Vector3 aim, ref float bestAngle, ref long target)
        {
            for (int i = 0; i < airlocks.Length; i++)
            {
                Outside outside = OutsideOf(airlocks[i]);
                if (outside == null || outside.Panel == null) continue;
                Vector3 centre = outside.Panel.transform.TransformPoint(outside.Middle);
                Vector3 to = centre - eye;
                float distance = to.magnitude;
                if (distance > Reach + 0.4f) continue;
                float angle = Vector3.Angle(aim, to);
                if (angle >= bestAngle) continue;
                if (Physics.Linecast(eye, centre - to.normalized * 0.25f, SightMask, QueryTriggerInteraction.Ignore)) continue;
                bestAngle = angle;
                target = KeyOf(view, i);
            }
        }

        /// <summary>True when a point is further than NearShip from a ship's rooms, so none of its airlock panels is in reach.</summary>
        private static bool FarFrom(PlayerControlledShip ship, Vector3 point)
        {
            var rooms = ship.Hull != null ? ship.Hull.RoomCollection : null;
            if (rooms == null) return false;
            Bounds bounds = rooms.Bounds;
            Vector3 size = bounds.size;
            if (!(size.x > 0f && size.y > 0f && size.z > 0f)) return false;
            return bounds.SqrDistance(rooms.transform.InverseTransformPoint(point)) > NearShip * NearShip;
        }

        /// <summary>The hint at an enemy airlock that is being hacked or is hacked already (nothing to do there, so no key).</summary>
        private static string TakenText(Entry entry)
        {
            int seconds = Mathf.Max(0, Mathf.CeilToInt(MsUntil(entry.Until) / 1000f));
            if (_takenText == null || entry.Key != _takenKey || seconds != _takenSeconds)
            {
                _takenKey = entry.Key;
                _takenSeconds = seconds;
                _takenText = (entry.State == State.Hacking ? "AIRLOCK BEING HACKED " : "AIRLOCK ALREADY HACKED ") + Clock(seconds);
            }
            return _takenText;
        }

        private static bool InSight(Vector3 eye, Vector3 aim, Vector3 point)
        {
            Vector3 to = point - eye;
            float distance = to.magnitude;
            if (distance > Reach || Vector3.Angle(aim, to) > (distance < 1f ? AimAngle * 2f : AimAngle)) return false;
            return !Physics.Linecast(eye, point - to.normalized * 0.15f, SightMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// An enemy who gets into the cockpit (an open archway, or a door left open) is pushed back out, and put just
        /// outside it if they are still in after a moment.
        /// </summary>
        private static void KeepOutOfCockpit()
        {
            if (Time.unscaledTime < _nextPush) return;
            _nextPush = Time.unscaledTime + 0.1f;
            LocalPlayer me = LocalPlayer.Instance;
            PlayerControlledShip ship = me != null && !me.IsDead ? FleetShips.AboardShip(me) : null;
            Room cockpit = PvpBoarding.IsEnemyShip(ship) ? CockpitRoom(ship) : null;
            var rooms = ship != null && ship.Hull != null ? ship.Hull.RoomCollection : null;
            Vector3 hit;
            if (cockpit == null || rooms == null || !rooms.RoomSphereIntersect(cockpit, me.transform.position + me.transform.up, 0.3f, out hit))
            {
                _inCockpitSince = -1f;
                return;
            }
            if (_inCockpitSince < 0f) _inCockpitSince = Time.unscaledTime;
            Helm helm = ship.GetModule<Helm>();
            Vector3 away = Vector3.ProjectOnPlane(me.transform.position - (helm != null ? helm.transform.position : ship.transform.position), ship.transform.up);
            if (away.sqrMagnitude < 0.01f) away = -me.transform.forward;
            try
            {
                if (Time.unscaledTime - _inCockpitSince > 1.2f)
                {
                    Vector3 exit;
                    if (CockpitExit(ship, cockpit, me.transform.position, out exit))
                        me.Locomotion.SetPositionAndRotation(exit, me.transform.rotation, true, true);
                    _inCockpitSince = -1f;
                }
                else if (me.Character != null) me.Character.AddForce(away.normalized * 0.5f);
            }
            catch (Exception e)
            {
                Log.Failure("Keeping an enemy out of the cockpit", e);
            }
            if (Time.unscaledTime - _lastCockpitWarning > 3f)
            {
                _lastCockpitWarning = Time.unscaledTime;
                try
                {
                    if (ViewEventBus.Instance != null) ViewEventBus.Instance.OnShowQuickWarning.Publish("ENEMY COCKPIT: YOU CAN'T GO IN HERE");
                }
                catch
                {
                    // Only a hint.
                }
            }
        }

        /// <summary>
        /// A point just outside the cockpit, in the room past its nearest doorway (preferring a point known to be in that
        /// room). See PastDoorway for the direction.
        /// </summary>
        private static bool CockpitExit(PlayerControlledShip ship, Room cockpit, Vector3 from, out Vector3 exit)
        {
            exit = Vector3.zero;
            var rooms = ship.Hull != null ? ship.Hull.RoomCollection : null;
            if (rooms == null || cockpit.PortalConnections == null) return false;
            Vector3 middle;
            if (!RoomMiddle(cockpit, out middle))
            {
                Helm helm = ship.GetModule<Helm>();
                middle = helm != null ? helm.transform.position : cockpit.transform.position;
            }
            float best = float.MaxValue;
            string method = null;
            foreach (RoomPortal portal in cockpit.PortalConnections)
            {
                if (portal == null || portal.RoomConnections == null) continue;
                float distance = Vector3.Distance(from, portal.transform.position);
                foreach (Room other in portal.RoomConnections)
                {
                    if (other == null || other == cockpit || other.InSpace) continue;
                    Vector3 point;
                    string how;
                    bool inside;
                    if (!PastDoorway(rooms, cockpit, other, portal, middle, ship.transform.up, out point, out how, out inside)) continue;
                    float score = inside ? distance : distance + 1000f;
                    if (score >= best) continue;
                    best = score;
                    exit = point;
                    method = how;
                }
            }
            if (method == null) return false;
            Log.InfoOnce("cockpit exit " + method, "PvP: an enemy is put out of a cockpit " + method + ".");
            return true;
        }

        /// <summary>
        /// 1.5 m past a doorway out of the cockpit into another room: away from the cockpit's middle, else toward that room's
        /// own point at the doorway, else toward that room's middle; the first one found inside that room (and not in the
        /// cockpit), or else the first one at all. (That room's point at the doorway is the doorway itself for a box room
        /// around it, and the world's origin for a mesh room, so it only counts when it is near the doorway.)
        /// </summary>
        private static bool PastDoorway(RoomCollection rooms, Room cockpit, Room other, RoomPortal portal, Vector3 middle, Vector3 up, out Vector3 point,
            out string how, out bool inside)
        {
            Vector3 doorway = portal.transform.position;
            Vector3 local;
            Vector3 toPoint = other.PortalConnectionPoints != null && other.PortalConnectionPoints.TryGetValue(portal, out local)
                ? portal.transform.TransformPoint(local) - doorway
                : Vector3.zero;
            if (toPoint.magnitude > portal.RoomConnectRadius + 0.5f) toPoint = Vector3.zero;
            Vector3 otherMiddle;
            Vector3 toMiddle = RoomMiddle(other, out otherMiddle) ? otherMiddle - doorway : Vector3.zero;
            Vector3[] directions = { doorway - middle, toPoint, toMiddle };
            string[] methods = { "away from the cockpit's middle", "toward the next room's point at the doorway", "toward the next room's middle" };
            point = doorway;
            how = null;
            inside = false;
            for (int i = 0; i < directions.Length; i++)
            {
                Vector3 direction = Vector3.ProjectOnPlane(directions[i], up);
                if (direction.sqrMagnitude < 0.01f) continue;
                Vector3 candidate = doorway + direction.normalized * 1.5f;
                Vector3 hit;
                if (rooms.RoomSphereIntersect(other, candidate, 0.3f, out hit) && !rooms.RoomSphereIntersect(cockpit, candidate, 0.1f, out hit))
                {
                    point = candidate;
                    how = methods[i];
                    inside = true;
                    return true;
                }
                if (how != null) continue;
                point = candidate;
                how = methods[i] + " (not checked to be in the next room)";
            }
            return how != null;
        }

        /// <summary>The middle of a room: of its collider's bounds, else of its box; false when neither is known.</summary>
        private static bool RoomMiddle(Room room, out Vector3 middle)
        {
            middle = room.transform.position;
            Collider collider = room.GetComponent<Collider>();
            if (collider != null)
            {
                // Empty while the collider is switched off.
                Bounds bounds = collider.bounds;
                if (bounds.size.sqrMagnitude > 0.01f)
                {
                    middle = bounds.center;
                    return true;
                }
            }
            if (room.ColliderType != ColliderType.Box) return false;
            middle = room.transform.TransformPoint(room.Center);
            return true;
        }

        // ---------------------------------------------------------------- the host

        public static void Listen()
        {
            Net.On(Msg.HackPlug, (sender, data) =>
            {
                if (!PhotonNetwork.IsMasterClient || !Active) return;
                ReadRoom();
                int itemView = Net.Get(data, 0, -1);
                int shipView = Net.Get(data, 1, -1);
                int index = Net.Get(data, 2, -1);
                Airlock airlock = AirlockAt(shipView, index);
                PlayerControlledShip ship = ShipOfView(shipView);
                Vector3 outward = Vector3.forward;
                AirlockButtonPanel panel = airlock != null ? OutsidePanel(airlock, out outward) : null;
                PhotonView itemObject = PhotonView.Find(itemView);
                var item = itemObject != null ? itemObject.GetComponent<CarryableObject>() : null;
                string refusal = null;
                if (airlock == null || ship == null || panel == null) refusal = "That airlock can't be hacked.";
                else if (!PvpBoarding.IsEnemyOf(sender, ship)) refusal = "That's your own ship's airlock.";
                else if (Entries.ContainsKey(KeyOf(shipView, index))) refusal = "That airlock is being hacked already, or is hacked.";
                else if (!HackTerminal.Is(item) || itemObject.OwnerActorNr != sender || !CarriedBy(sender, item)) refusal = "You need to be holding a hacking terminal.";
                else if (UsedTerminals.Contains(item.GetInstanceID())) refusal = "That hacking terminal has been used already.";
                else if (!Near(sender, panel, HostReach)) refusal = "Get closer to the airlock's outside panel.";
                if (refusal != null)
                {
                    Net.ToActor(sender, Msg.Notice, refusal);
                    return;
                }
                Vector3 point;
                Quaternion rotation;
                PanelPose(panel, outward, out point, out rotation);
                Entries[KeyOf(shipView, index)] = new Entry
                {
                    Ship = shipView,
                    Index = index,
                    State = State.Hacking,
                    Until = unchecked(Now + HackMs),
                    Team = FleetShips.TeamOfActor(sender),
                    Position = ship.transform.InverseTransformPoint(point),
                    Rotation = Quaternion.Inverse(ship.transform.rotation) * rotation
                };
                WriteRoom();
                // The hacker's game puts the terminal down and destroys it, as the game does with a used item; the host
                // makes sure it is gone (and never takes it again).
                UsedTerminals.Add(item.GetInstanceID());
                PluggedTerminals.Add(new Plugged { Terminal = item, CheckAt = Time.unscaledTime + RemoveSeconds });
                Net.ToActor(sender, Msg.HackTaken, itemView);
                Log.Info("PvP: " + NameOf(sender) + " started hacking an airlock of " + FleetShips.Describe(ship) + ".");
                Notice.ShowAll(NameOf(sender) + " (" + PvpTeams.TeamName(FleetShips.TeamOfActor(sender)) + ") is hacking " + FleetShips.Describe(ship) +
                               "'s airlock: one minute to pull the terminal out!");
            });

            Net.On(Msg.HackTaken, (sender, data) =>
            {
                if (!Net.FromHost(sender)) return;
                PhotonView view = PhotonView.Find(Net.Get(data, 0, -1));
                var item = view != null ? view.GetComponent<CarryableObject>() : null;
                if (HackTerminal.Is(item)) HackTerminal.Consume(item);
            });

            Net.On(Msg.HackPull, (sender, data) =>
            {
                if (!PhotonNetwork.IsMasterClient || !Active) return;
                ReadRoom();
                long key = KeyOf(Net.Get(data, 0, -1), Net.Get(data, 1, -1));
                Entry entry;
                if (!Entries.TryGetValue(key, out entry) || entry.State != State.Hacking || MsUntil(entry.Until) < 250) return;
                Airlock airlock = AirlockAt(entry.Ship, entry.Index);
                PlayerControlledShip ship = ShipOfView(entry.Ship);
                Vector3 outward = Vector3.forward;
                AirlockButtonPanel panel = airlock != null ? OutsidePanel(airlock, out outward) : null;
                if (ship == null || Spectator.WatchState(sender) != CrewState.Playing || FleetShips.TeamOfActor(sender) != FleetShips.TeamOfSlot(FleetShips.Slot(ship)) ||
                    !Near(sender, panel, HostReach)) return;
                Entries.Remove(key);
                WriteRoom();
                Log.Info("PvP: " + NameOf(sender) + " pulled a hacking terminal out of " + FleetShips.Describe(ship) + "'s airlock.");
                Notice.ShowAll(NameOf(sender) + " pulled the hacking terminal out of " + FleetShips.Describe(ship) + "'s airlock.");
            });
        }

        /// <summary>Whether a player's living character is within reach of something (where the host sees it).</summary>
        private static bool Near(int actor, Component target, float reach)
        {
            if (target == null) return false;
            Player player = FleetShips.CharacterOf(actor);
            return player != null && !PlayerLife.IsDead(player) && Vector3.Distance(player.transform.position, BoundsOf(target).center) <= reach;
        }

        /// <summary>
        /// Whether a player's living character carries this item, as the host sees it: every character's carried item is
        /// synced to everyone (the game's Carrier), and only an item its carrier owns is taken as carried.
        /// </summary>
        private static bool CarriedBy(int actor, CarryableObject item)
        {
            Player player = FleetShips.CharacterOf(actor);
            return player != null && !PlayerLife.IsDead(player) && player.Carrier != null && player.Carrier.Payload == item;
        }

        /// <summary>
        /// Host: a plugged-in terminal still there a few seconds after its holder's game was told to destroy it is destroyed
        /// with the game's own call (which has its owner's game remove it for everyone).
        /// </summary>
        private static void RemovePluggedTerminals()
        {
            for (int i = PluggedTerminals.Count - 1; i >= 0; i--)
            {
                Plugged plugged = PluggedTerminals[i];
                if (plugged.Terminal != null && Time.unscaledTime < plugged.CheckAt) continue;
                PluggedTerminals.RemoveAt(i);
                if (plugged.Terminal == null) continue;
                HackTerminal.Destroy(plugged.Terminal);
                Log.Info("PvP: a plugged-in hacking terminal was still there after " + RemoveSeconds + " s; the host removed it.");
            }
        }

        private static string NameOf(int actor)
        {
            Photon.Realtime.Player player = PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.GetPlayer(actor) : null;
            return player != null && !string.IsNullOrEmpty(player.NickName) ? player.NickName : "A player";
        }

        /// <summary>Host, a few times a second: hacks finish, and door access runs out.</summary>
        public static void Tick()
        {
            if (!PhotonNetwork.IsMasterClient || !Active) return;
            try
            {
                RemovePluggedTerminals();
                ReadRoom();
                if (Entries.Count == 0) return;
                bool changed = false;
                foreach (Entry entry in new List<Entry>(Entries.Values))
                {
                    PlayerControlledShip ship = ShipOfView(entry.Ship);
                    if (ship == null || AirlockAt(entry.Ship, entry.Index) == null)
                    {
                        Entries.Remove(entry.Key);
                        changed = true;
                        continue;
                    }
                    if (MsUntil(entry.Until) > 0) continue;
                    if (entry.State == State.Hacking)
                    {
                        entry.State = State.Access;
                        entry.Until = unchecked(Now + AccessMs);
                        Log.Info("PvP: " + PvpTeams.TeamName(entry.Team) + " hacked an airlock of " + FleetShips.Describe(ship) + ".");
                        Notice.ShowAll(PvpTeams.TeamName(entry.Team) + " hacked " + FleetShips.Describe(ship) + "'s airlock: " +
                                       PvpTeams.TeamName(entry.Team) + " can open its doors for five minutes!");
                    }
                    else
                    {
                        Entries.Remove(entry.Key);
                        Notice.ShowAll(PvpTeams.TeamName(entry.Team) + "'s door access to " + FleetShips.Describe(ship) + " is over.");
                    }
                    changed = true;
                }
                if (changed) WriteRoom();
            }
            catch (Exception e)
            {
                Log.Failure("Running hacks", e);
            }
        }

        // ---------------------------------------------------------------- self-test

        /// <summary>Self-test (host): give a team door access to a ship, as if it had hacked an airlock.</summary>
        public static string DebugAccess(PlayerControlledShip ship, int team, float seconds)
        {
            if (!PhotonNetwork.IsMasterClient || ship == null) return "not the host, or no ship";
            Airlock[] airlocks = AirlocksOf(ship);
            if (airlocks.Length == 0) return "the ship has no airlock";
            ReadRoom();
            int view = FleetShips.ViewId(ship);
            Entries[KeyOf(view, 0)] = new Entry
            {
                Ship = view, Index = 0, State = State.Access, Until = unchecked(Now + (int)(seconds * 1000f)), Team = team,
                Position = Vector3.zero, Rotation = Quaternion.identity
            };
            WriteRoom();
            Vector3 outward;
            int outside = 0;
            foreach (Airlock airlock in airlocks)
            {
                if (OutsidePanel(airlock, out outward) != null) outside++;
            }
            return PvpTeams.TeamName(team) + " has door access to " + FleetShips.Describe(ship) + " for " + seconds + " s (" + airlocks.Length + " airlocks, " + outside +
                   " with an outside panel)";
        }
    }
}
