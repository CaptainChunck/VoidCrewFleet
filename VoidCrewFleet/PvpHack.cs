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
    /// The host decides everything and keeps the hacks in a room property, so everyone (late joiners too) sees them.
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
        private const int SightMask = (1 << 10) | (1 << 12) | (1 << 27);

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
            public double Until;
            public int Actor;
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
                    Ship, Index, (byte)State, Until, Actor, Team,
                    new[] { Position.x, Position.y, Position.z, Rotation.x, Rotation.y, Rotation.z, Rotation.w }
                };
            }

            public static Entry FromData(object entry)
            {
                var data = entry as object[];
                if (data == null || data.Length < 7) return null;
                var pose = data[6] as float[];
                if (!(data[0] is int) || !(data[1] is int) || !(data[2] is byte) || !(data[3] is double) || !(data[4] is int) || !(data[5] is int) ||
                    pose == null || pose.Length < 7)
                    return null;
                return new Entry
                {
                    Ship = (int)data[0],
                    Index = (int)data[1],
                    State = (State)(byte)data[2],
                    Until = (double)data[3],
                    Actor = (int)data[4],
                    Team = (int)data[5],
                    Position = new Vector3(pose[0], pose[1], pose[2]),
                    Rotation = new Quaternion(pose[3], pose[4], pose[5], pose[6])
                };
            }
        }

        private sealed class Shown
        {
            public GameObject Model;
            public Material[] Materials;
        }

        private static readonly Dictionary<long, Entry> Entries = new Dictionary<long, Entry>();
        private static readonly Dictionary<long, Shown> Models = new Dictionary<long, Shown>();
        private static readonly Dictionary<int, long> AirlockKeys = new Dictionary<int, long>();
        private static readonly Dictionary<int, Room> CockpitRooms = new Dictionary<int, Room>();
        private static object _roomData;

        private static Step _holding;
        private static long _holdTarget;
        private static float _holdStart;
        private static bool _needRelease;
        private static float _nextRequest;
        private static float _nextPush;
        private static float _lastCockpitWarning = -100f;
        private static float _inCockpitSince = -1f;
        private static Color? _glow;

        private static UIDocument _timerDocument;
        private static Label _timerLabel;

        private static long KeyOf(int ship, int index)
        {
            return ((long)ship << 8) | (uint)(index & 0xff);
        }

        public static bool Active
        {
            get { return PvpCombat.Active && HackTerminal.Registered && PvpTeams.Locked; }
        }

        /// <summary>A hacking terminal action is under the local player's aim (Fire belongs to it, not to punching).</summary>
        public static bool Busy { get; private set; }

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
            Airlock[] airlocks = owner.GetComponentsInChildren<Airlock>(true);
            return index >= 0 && index < airlocks.Length ? airlocks[index] : null;
        }

        private static bool Identify(Airlock airlock, out long key)
        {
            key = 0;
            if (airlock == null) return false;
            if (AirlockKeys.TryGetValue(airlock.GetInstanceID(), out key)) return true;
            PlayerControlledShip ship = FleetShips.OwnerOf(airlock);
            if (ship == null) return false;
            int index = Array.IndexOf(ship.GetComponentsInChildren<Airlock>(true), airlock);
            if (index < 0) return false;
            key = KeyOf(FleetShips.ViewId(ship), index);
            AirlockKeys[airlock.GetInstanceID()] = key;
            return true;
        }

        private static AirlockDoor OuterDoor(Airlock airlock)
        {
            if (airlock == null || airlock.airlockDoors == null) return null;
            foreach (AirlockDoor door in airlock.airlockDoors)
            {
                if (door != null && door.airlockDoorType == AirlockDoorType.Outer) return door;
            }
            return null;
        }

        /// <summary>The direction from an outer airlock door out to space.</summary>
        private static Vector3 Outward(AirlockDoor door)
        {
            Vector3 outward = door.transform.forward;
            RoomPortal portal = door.Portal;
            if (portal == null || portal.RoomConnections == null) return outward;
            foreach (Room room in portal.RoomConnections)
            {
                Vector3 local;
                if (room == null || !room.InSpace || room.PortalConnectionPoints == null || !room.PortalConnectionPoints.TryGetValue(portal, out local)) continue;
                if (Vector3.Dot(portal.transform.TransformPoint(local) - portal.transform.position, outward) < 0f) outward = -outward;
                break;
            }
            return outward;
        }

        private static Bounds BoundsOf(Component component)
        {
            Renderer[] renderers = component.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(component.transform.position, Vector3.one * 0.3f);
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        /// <summary>The airlock's control panel outside the ship (the one furthest out past the outer door), or null.</summary>
        private static AirlockButtonPanel OutsidePanel(Airlock airlock, out Vector3 outward)
        {
            outward = Vector3.forward;
            AirlockDoor door = OuterDoor(airlock);
            if (door == null || airlock.ButtonPanels == null) return null;
            outward = Outward(door);
            Vector3 doorPoint = door.Portal != null ? door.Portal.transform.position : door.transform.position;
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
            double now = PhotonNetwork.Time;
            foreach (Entry entry in Entries.Values)
            {
                if (entry.State == State.Access && entry.Ship == view && entry.Team == team && now < entry.Until) return true;
            }
            return false;
        }

        public static bool LocalHasAccess(PlayerControlledShip ship)
        {
            ReadRoom();
            return HasAccess(FleetShips.CrewSlot(FleetShips.LocalActor), ship);
        }

        // ---------------------------------------------------------------- the room property

        private static void ReadRoom()
        {
            object data = null;
            Photon.Realtime.Room room = PhotonNetwork.CurrentRoom;
            if (room != null && room.CustomProperties != null) room.CustomProperties.TryGetValue(RoomKey, out data);
            if (ReferenceEquals(data, _roomData)) return;
            _roomData = data;
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
        }

        /// <summary>Host, at the start of a PvP match: no hacks from an earlier one.</summary>
        public static void ResetRoom()
        {
            Entries.Clear();
            AirlockKeys.Clear();
            CockpitRooms.Clear();
            _roomData = null;
            _glow = null;
            if (PhotonNetwork.IsMasterClient && PhotonNetwork.CurrentRoom != null)
                PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { RoomKey, null } });
        }

        // ---------------------------------------------------------------- every frame, every client

        public static void Frame()
        {
            if (!Active)
            {
                if (Models.Count > 0) ClearModels();
                ShowTimers(null);
                ActionHint.Set("hack", ActionHint.HackPriority, null);
                Busy = false;
                return;
            }
            try
            {
                ReadRoom();
                ShowModels();
                ShowTimers(TimerText());
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
                    shown.Materials = materials.ToArray();
                    Models[entry.Key] = shown;
                    if (FleetShips.Slot(ship) == FleetShips.CrewSlot(FleetShips.LocalActor)) BigWarning("THE ENEMY IS HACKING OUR AIRLOCK! PULL THEIR TERMINAL OUT OF THE OUTSIDE PANEL");
                }
                // A working pulse while it hacks.
                SetGlow(shown, Mathf.Repeat(Time.time, 1f) < 0.5f);
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
                if (material != null && material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", color);
            }
        }

        private static void DestroyModel(Shown shown)
        {
            if (shown.Materials != null)
            {
                foreach (Material material in shown.Materials)
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
            int mine = FleetShips.CrewSlot(FleetShips.LocalActor);
            if (mine < 0 || Entries.Count == 0) return null;
            double now = PhotonNetwork.Time;
            var lines = new List<string>();
            foreach (Entry entry in Entries.Values)
            {
                double left = entry.Until - now;
                if (left <= 0) continue;
                PlayerControlledShip ship = ShipOfView(entry.Ship);
                int shipSlot = FleetShips.Slot(ship);
                string shipName = PvpTeams.TeamName(shipSlot) + " SHIP";
                // The game's colours: light blue for our own progress, light green for what we won, orange and red for threats.
                if (entry.State == State.Hacking)
                {
                    if (entry.Team == mine) lines.Add("<color=#BDE7FB>HACKING THE " + shipName + "'S AIRLOCK  " + Clock(left) + "</color>");
                    else if (shipSlot == mine) lines.Add("<color=#F6AE3C>ENEMY HACKING OUR AIRLOCK  " + Clock(left) + "</color>");
                }
                else
                {
                    if (entry.Team == mine) lines.Add("<color=#83C886>DOOR ACCESS TO THE " + shipName + "  " + Clock(left) + "</color>");
                    else if (shipSlot == mine) lines.Add("<color=#EC1E24>THE ENEMY CAN OPEN OUR DOORS  " + Clock(left) + "</color>");
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
            Step step = PvpPunch.CanPunch() ? FindStep(out target) : Step.None;
            Busy = step != Step.None;
            bool pressed = fire != null && fire.IsPressed();
            if (!pressed) _needRelease = false;
            if (step == Step.None)
            {
                _holding = Step.None;
                ActionHint.Set("hack", ActionHint.HackPriority, null);
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
            ActionHint.Set("hack", ActionHint.HackPriority, "HOLD " + PvpPunch.FireName(fire) + "  " +
                (step == Step.Plug ? "PLUG IN THE HACKING TERMINAL" : "PULL OUT THE ENEMY TERMINAL") +
                (progress > 0f ? "  " + Mathf.RoundToInt(progress * 100f) + "%" : ""));
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

        private static Step FindStep(out long target)
        {
            target = 0;
            LocalPlayer me = LocalPlayer.Instance;
            Vector3 eye;
            Vector3 aim;
            if (me == null || !Aim.Get(out eye, out aim)) return Step.None;
            int myTeam = FleetShips.CrewSlot(FleetShips.LocalActor);
            if (myTeam < 0) return Step.None;
            CarryableObject held = me.Payload;

            // An enemy terminal on our own airlock: pull it out (not with a usable item in hand, which Fire would use too).
            if (held == null || !(held is InteractiveCarryable) && !HackTerminal.Is(held))
            {
                foreach (KeyValuePair<long, Shown> shown in Models)
                {
                    Entry entry;
                    if (shown.Value.Model == null || !Entries.TryGetValue(shown.Key, out entry)) continue;
                    if (FleetShips.Slot(ShipOfView(entry.Ship)) != myTeam) continue;
                    if (!InSight(eye, aim, shown.Value.Model.transform.position)) continue;
                    target = shown.Key;
                    return Step.Pull;
                }
            }

            // Holding a terminal, looking at the outside panel of an enemy airlock that isn't being hacked.
            if (!HackTerminal.Is(held)) return Step.None;
            PlayerControlledShip enemy = null;
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
                if (PvpBoarding.IsEnemyShip(ship)) enemy = ship;
            }
            if (enemy == null) return Step.None;
            float bestAngle = AimAngle;
            foreach (Airlock airlock in enemy.GetComponentsInChildren<Airlock>(true))
            {
                Vector3 outward;
                AirlockButtonPanel panel = OutsidePanel(airlock, out outward);
                long key;
                if (panel == null || !Identify(airlock, out key) || Entries.ContainsKey(key)) continue;
                Vector3 centre = BoundsOf(panel).center;
                Vector3 to = centre - eye;
                float distance = to.magnitude;
                float angle = Vector3.Angle(aim, to);
                if (distance > Reach + 0.4f || angle >= bestAngle) continue;
                if (Physics.Linecast(eye, centre - to.normalized * 0.25f, SightMask, QueryTriggerInteraction.Ignore)) continue;
                bestAngle = angle;
                target = key;
            }
            return target != 0 ? Step.Plug : Step.None;
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
                    if (CockpitExit(cockpit, me.transform.position, out exit))
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

        /// <summary>A point just outside the cockpit, in the room past its nearest doorway.</summary>
        private static bool CockpitExit(Room cockpit, Vector3 from, out Vector3 exit)
        {
            exit = Vector3.zero;
            float best = float.MaxValue;
            if (cockpit.PortalConnections == null) return false;
            foreach (RoomPortal portal in cockpit.PortalConnections)
            {
                if (portal == null || portal.RoomConnections == null) continue;
                foreach (Room other in portal.RoomConnections)
                {
                    Vector3 local;
                    if (other == null || other == cockpit || other.InSpace || other.PortalConnectionPoints == null || !other.PortalConnectionPoints.TryGetValue(portal, out local)) continue;
                    Vector3 doorway = portal.transform.position;
                    Vector3 beyond = portal.transform.TransformPoint(local);
                    Vector3 point = doorway + (beyond - doorway).normalized * 1.5f;
                    float distance = Vector3.Distance(from, doorway);
                    if (distance >= best) continue;
                    best = distance;
                    exit = point;
                }
            }
            return best < float.MaxValue;
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
                else if (!HackTerminal.Is(item) || itemObject.OwnerActorNr != sender) refusal = "You need to be holding a hacking terminal.";
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
                    Until = PhotonNetwork.Time + HackSeconds,
                    Actor = sender,
                    Team = FleetShips.CrewSlot(sender),
                    Position = ship.transform.InverseTransformPoint(point),
                    Rotation = Quaternion.Inverse(ship.transform.rotation) * rotation
                };
                WriteRoom();
                Net.ToActor(sender, Msg.HackTaken, itemView);
                Log.Info("PvP: " + NameOf(sender) + " started hacking an airlock of " + FleetShips.Describe(ship) + ".");
                Notice.ShowAll(NameOf(sender) + " (" + PvpTeams.TeamName(FleetShips.CrewSlot(sender)) + ") is hacking the " + PvpTeams.TeamName(FleetShips.Slot(ship)) +
                               " ship's airlock: one minute to pull the terminal out!");
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
                if (!Entries.TryGetValue(key, out entry) || entry.State != State.Hacking || PhotonNetwork.Time > entry.Until - 0.25) return;
                Airlock airlock = AirlockAt(entry.Ship, entry.Index);
                PlayerControlledShip ship = ShipOfView(entry.Ship);
                Vector3 outward = Vector3.forward;
                AirlockButtonPanel panel = airlock != null ? OutsidePanel(airlock, out outward) : null;
                if (ship == null || FleetShips.CrewSlot(sender) != FleetShips.Slot(ship) || !Near(sender, panel, HostReach)) return;
                Entries.Remove(key);
                WriteRoom();
                Log.Info("PvP: " + NameOf(sender) + " pulled a hacking terminal out of " + FleetShips.Describe(ship) + "'s airlock.");
                Notice.ShowAll(NameOf(sender) + " pulled the hacking terminal out of the " + PvpTeams.TeamName(FleetShips.Slot(ship)) + " ship's airlock.");
            });
        }

        private static bool Near(int actor, Component target, float reach)
        {
            if (target == null) return false;
            Player player = FleetShips.CharacterOf(actor);
            return player != null && Vector3.Distance(player.transform.position, BoundsOf(target).center) <= reach;
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
                ReadRoom();
                if (Entries.Count == 0) return;
                bool changed = false;
                double now = PhotonNetwork.Time;
                foreach (Entry entry in new List<Entry>(Entries.Values))
                {
                    PlayerControlledShip ship = ShipOfView(entry.Ship);
                    if (ship == null || AirlockAt(entry.Ship, entry.Index) == null)
                    {
                        Entries.Remove(entry.Key);
                        changed = true;
                        continue;
                    }
                    if (now < entry.Until) continue;
                    if (entry.State == State.Hacking)
                    {
                        entry.State = State.Access;
                        entry.Until = now + AccessSeconds;
                        Log.Info("PvP: " + PvpTeams.TeamName(entry.Team) + " hacked an airlock of " + FleetShips.Describe(ship) + ".");
                        Notice.ShowAll(PvpTeams.TeamName(entry.Team) + " hacked the " + PvpTeams.TeamName(FleetShips.Slot(ship)) + " ship's airlock: " +
                                       PvpTeams.TeamName(entry.Team) + " can open its doors for five minutes!");
                    }
                    else
                    {
                        Entries.Remove(entry.Key);
                        Notice.ShowAll(PvpTeams.TeamName(entry.Team) + "'s door access to the " + PvpTeams.TeamName(FleetShips.Slot(ship)) + " ship is over.");
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
            Airlock[] airlocks = ship.GetComponentsInChildren<Airlock>(true);
            if (airlocks.Length == 0) return "the ship has no airlock";
            ReadRoom();
            int view = FleetShips.ViewId(ship);
            Entries[KeyOf(view, 0)] = new Entry
            {
                Ship = view, Index = 0, State = State.Access, Until = PhotonNetwork.Time + seconds, Actor = PhotonNetwork.LocalPlayer.ActorNumber, Team = team,
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
