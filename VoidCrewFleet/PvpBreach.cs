using System;
using System.Collections.Generic;
using CG;
using CG.Client;
using CG.Client.Ship;
using CG.Game;
using CG.Game.Player;
using CG.Input;
using CG.Objects;
using CG.Ship.Hull;
using CG.Ship.Object;
using CG.Space;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace VoidCrewFleet
{
    /// <summary>
    /// PvP: breaching an enemy airlock with an Airlock Breaching Charge (<see cref="BreachCharge"/>).
    /// 1. Carry a charge to the outer door of an enemy airlock and hold Fire on the door: the charge is attached.
    /// 2. Hold Fire on the attached charge: it is armed, and blows after a short fuse.
    /// 3. The blast forces both doors of that airlock open and vents the enemy ship (<see cref="AirlockBreach"/>). The
    ///    doors stay jammed open for a while (no one can close them or pressurise), then the airlock works again.
    /// Until it blows, the ship's own crew can pull the charge off (hold Fire on it), and opening the door knocks it off.
    /// The host decides everything and keeps the charges in a room property, so everyone (late joiners included) sees
    /// them; each client shows its own copy of the charge's model on the door.
    /// </summary>
    internal static class PvpBreach
    {
        public const string Feature = BreachCharge.Feature;
        private const string RoomKey = "VCF_Breach";

        private const float Reach = 2.6f;
        private const float HostReach = 5f;
        private const float AttachHold = 1.5f;
        private const float ArmHold = 1f;
        private const float DefuseHold = 3f;
        private const float AimAngle = 14f;
        public const float FuseSeconds = 6f;
        public const float JammedSeconds = 30f;
        private const float CooldownSeconds = 20f;
        private const int SightMask = (1 << 10) | (1 << 12) | (1 << 27);

        private enum State : byte
        {
            Attached = 1,
            Armed = 2,
            Blown = 3,
            Cooldown = 4
        }

        private enum Step : byte
        {
            None,
            Attach,
            Arm,
            Defuse
        }

        private sealed class Charge
        {
            public int Ship;
            public int Index;
            public State State;
            public double Until;
            public int Actor;
            public bool Safety;
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
                    Ship, Index, (byte)State, Until, Actor, Safety,
                    new[] { Position.x, Position.y, Position.z, Rotation.x, Rotation.y, Rotation.z, Rotation.w }
                };
            }

            public static Charge FromData(object entry)
            {
                var data = entry as object[];
                if (data == null || data.Length < 7) return null;
                var pose = data[6] as float[];
                if (!(data[0] is int) || !(data[1] is int) || !(data[2] is byte) || !(data[3] is double) || !(data[4] is int) || !(data[5] is bool) ||
                    pose == null || pose.Length < 7)
                    return null;
                return new Charge
                {
                    Ship = (int)data[0],
                    Index = (int)data[1],
                    State = (State)(byte)data[2],
                    Until = (double)data[3],
                    Actor = (int)data[4],
                    Safety = (bool)data[5],
                    Position = new Vector3(pose[0], pose[1], pose[2]),
                    Rotation = new Quaternion(pose[3], pose[4], pose[5], pose[6])
                };
            }
        }

        /// <summary>A charge's model on this client.</summary>
        private sealed class Shown
        {
            public GameObject Model;
            public Material[] Materials;
            public State State;
            public float NextBeep;
        }

        private static readonly Dictionary<long, Charge> Charges = new Dictionary<long, Charge>();
        private static readonly Dictionary<long, Shown> Models = new Dictionary<long, Shown>();
        private static readonly Dictionary<int, long> AirlockKeys = new Dictionary<int, long>();
        private static readonly HashSet<long> LockedPanels = new HashSet<long>();
        private static object _roomData;

        /// <summary>A breaching charge action is under the local player's aim (Fire belongs to it, not to punching).</summary>
        public static bool Busy { get; private set; }

        private static Step _holding;
        private static long _holdTarget;
        private static float _holdStart;
        private static bool _needRelease;
        private static float _nextRequest;
        private static float _lastJamWarning = -100f;

        private static long KeyOf(int ship, int index)
        {
            return ((long)ship << 8) | (uint)(index & 0xff);
        }

        public static bool Active
        {
            get { return PvpCombat.Active && BreachCharge.Registered && PvpTeams.Locked; }
        }

        // ---------------------------------------------------------------- airlocks: the ship's view id and the airlock's place in the ship

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

        private static IEnumerable<Airlock> AirlocksOf(PlayerControlledShip ship)
        {
            if (ship == null) yield break;
            foreach (Airlock airlock in ship.GetComponentsInChildren<Airlock>(true))
            {
                if (airlock != null) yield return airlock;
            }
        }

        // ---------------------------------------------------------------- the room property

        private static void ReadRoom()
        {
            object data = null;
            Photon.Realtime.Room room = PhotonNetwork.CurrentRoom;
            if (room != null && room.CustomProperties != null) room.CustomProperties.TryGetValue(RoomKey, out data);
            if (ReferenceEquals(data, _roomData)) return;
            _roomData = data;
            Charges.Clear();
            var entries = data as object[];
            if (entries == null) return;
            foreach (object entry in entries)
            {
                Charge charge = Charge.FromData(entry);
                if (charge != null) Charges[charge.Key] = charge;
            }
        }

        private static void WriteRoom()
        {
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            var entries = new List<object>();
            foreach (Charge charge in Charges.Values) entries.Add(charge.ToData());
            object data = entries.Count > 0 ? entries.ToArray() : null;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { RoomKey, data } });
            _roomData = data;
        }

        /// <summary>Host, at the start of a PvP match: no charges from an earlier one.</summary>
        public static void ResetRoom()
        {
            Charges.Clear();
            AirlockKeys.Clear();
            _roomData = null;
            AirlockBreach.Forget();
            if (PhotonNetwork.IsMasterClient && PhotonNetwork.CurrentRoom != null)
                PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { RoomKey, null } });
        }

        /// <summary>Whether this airlock is breached right now (its doors are jammed open).</summary>
        public static bool IsJammed(Airlock airlock)
        {
            if (airlock == null || Charges.Count == 0) return false;
            long key;
            Charge charge;
            return Identify(airlock, out key) && Charges.TryGetValue(key, out charge) && charge.State == State.Blown;
        }

        // ---------------------------------------------------------------- the jam: nobody closes the doors or pressurises

        [GamePatch(Feature, "CG.Ship.Hull.AbstractDoor", "SetOpen", PatchKind.Prefix)]
        private static bool SetOpenPrefix(AbstractDoor __instance, bool open)
        {
            if (open || AirlockBreach.Bypass || Charges.Count == 0) return true;
            var door = __instance as AirlockDoor;
            if (door == null || !IsJammed(door.OwningAirlock)) return true;
            if (Time.unscaledTime - _lastJamWarning > 3f)
            {
                _lastJamWarning = Time.unscaledTime;
                try
                {
                    if (ViewEventBus.Instance != null) ViewEventBus.Instance.OnShowQuickWarning.Publish("AIRLOCK JAMMED OPEN BY A BREACHING CHARGE");
                }
                catch
                {
                    // Only a hint.
                }
            }
            return false;
        }

        [GamePatch(Feature, "CG.Client.Ship.Airlock", "set_IsSafetyEnabled", PatchKind.Prefix)]
        private static bool SafetyPrefix(Airlock __instance, bool value)
        {
            return !value || AirlockBreach.Bypass || !PhotonNetwork.IsMasterClient || !IsJammed(__instance);
        }

        [GamePatch(Feature, "CG.Client.Ship.Airlock", "StartPressurization", PatchKind.Prefix)]
        private static bool PressurizePrefix(Airlock __instance)
        {
            return AirlockBreach.Bypass || !IsJammed(__instance);
        }

        // ---------------------------------------------------------------- every frame, every client

        public static void Frame()
        {
            if (!Active)
            {
                if (Models.Count > 0 || LockedPanels.Count > 0) ClearAll();
                ActionHint.Set("breach", ActionHint.BreachPriority, null);
                return;
            }
            try
            {
                ReadRoom();
                ShowModels();
                LockPanels();
                HandleInput();
            }
            catch (Exception e)
            {
                Log.Failure("Breaching charges", e);
            }
        }

        private static bool Shows(Charge charge)
        {
            return charge.State == State.Attached || charge.State == State.Armed;
        }

        private static void ShowModels()
        {
            // Models of charges that are gone or blew (a blast first) go.
            List<long> gone = null;
            foreach (KeyValuePair<long, Shown> entry in Models)
            {
                Charge charge;
                bool exists = Charges.TryGetValue(entry.Key, out charge);
                if (exists && Shows(charge)) continue;
                GameObject model = entry.Value.Model;
                if (model != null)
                {
                    if (exists && charge.State == State.Blown) Blast(model.transform);
                    DestroyModel(entry.Value);
                }
                if (gone == null) gone = new List<long>();
                gone.Add(entry.Key);
            }
            if (gone != null)
            {
                foreach (long key in gone) Models.Remove(key);
            }

            int myTeam = FleetShips.CrewSlot(FleetShips.LocalActor);
            foreach (Charge charge in Charges.Values)
            {
                if (!Shows(charge)) continue;
                Shown shown;
                if (!Models.TryGetValue(charge.Key, out shown) || shown.Model == null)
                {
                    PlayerControlledShip ship = ShipOfView(charge.Ship);
                    if (ship == null) continue;
                    shown = new Shown { Model = BreachCharge.BuildModel() };
                    shown.Model.transform.SetParent(ship.transform, false);
                    shown.Model.transform.localPosition = charge.Position;
                    shown.Model.transform.localRotation = charge.Rotation;
                    var materials = new List<Material>();
                    foreach (Renderer renderer in shown.Model.GetComponentsInChildren<Renderer>()) materials.AddRange(renderer.materials);
                    shown.Materials = materials.ToArray();
                    Models[charge.Key] = shown;
                    if (FleetShips.Slot(ship) == myTeam) BigWarning("ENEMY BREACHING CHARGE ON OUR AIRLOCK! PULL IT OFF BEFORE IT BLOWS");
                }
                if (charge.State == State.Armed && shown.State != State.Armed)
                {
                    PlayerControlledShip ship = ShipOfView(charge.Ship);
                    if (FleetShips.Slot(ship) == myTeam) BigWarning("BREACHING CHARGE ARMED ON OUR AIRLOCK!");
                }
                shown.State = charge.State;

                bool lit;
                if (charge.State == State.Armed)
                {
                    double left = charge.Until - PhotonNetwork.Time;
                    float period = left < 2.0 ? 0.25f : 0.6f;
                    lit = Mathf.Repeat(Time.time, period) < period * 0.5f;
                    if (Time.time >= shown.NextBeep)
                    {
                        shown.NextBeep = Time.time + period;
                        AirlockBreach.PlayBeep(shown.Model);
                    }
                }
                else lit = Mathf.Repeat(Time.time, 2f) < 0.15f;
                SetGlow(shown, lit);
            }
        }

        private static void SetGlow(Shown shown, bool lit)
        {
            if (shown.Materials == null) return;
            Color color = lit ? AirlockBreach.Red : Color.black;
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

        /// <summary>A breached airlock's panels can't be used until the jam ends.</summary>
        private static void LockPanels()
        {
            var jammed = new HashSet<long>();
            foreach (Charge charge in Charges.Values)
            {
                if (charge.State == State.Blown) jammed.Add(charge.Key);
            }
            foreach (long key in jammed)
            {
                if (LockedPanels.Contains(key)) continue;
                if (SetPanels(key, false)) LockedPanels.Add(key);
            }
            List<long> free = null;
            foreach (long key in LockedPanels)
            {
                if (jammed.Contains(key)) continue;
                SetPanels(key, true);
                if (free == null) free = new List<long>();
                free.Add(key);
            }
            if (free != null)
            {
                foreach (long key in free) LockedPanels.Remove(key);
            }
        }

        private static bool SetPanels(long key, bool clickable)
        {
            Airlock airlock = AirlockAt((int)(key >> 8), (int)(key & 0xff));
            if (airlock == null || airlock.ButtonPanels == null) return false;
            foreach (AirlockButtonPanel panel in airlock.ButtonPanels)
            {
                if (panel != null) panel.ToggleClickable(clickable);
            }
            return true;
        }

        private static void ClearAll()
        {
            foreach (Shown shown in Models.Values) DestroyModel(shown);
            Models.Clear();
            foreach (long key in LockedPanels) SetPanels(key, true);
            LockedPanels.Clear();
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

        private static void Blast(Transform model)
        {
            AirlockBreach.PlayBlast(model.position, model.rotation, model.parent);
            LocalPlayer me = LocalPlayer.Instance;
            if (me != null && Vector3.Distance(me.transform.position, model.position) < 15f) PvpPunch.Shake(1f);
        }

        // ---------------------------------------------------------------- the local player's actions

        internal static InputAction Fire()
        {
            InputService service = ServiceBase<InputService>.Instance;
            return service != null && service.InputActionReferences != null && service.InputActionReferences.Fire1 != null
                ? service.InputActionReferences.Fire1.action
                : null;
        }

        internal static string FireName(InputAction fire)
        {
            string name = null;
            try
            {
                name = fire != null ? fire.GetBindingDisplayString() : null;
            }
            catch
            {
                // Just the label.
            }
            return string.IsNullOrEmpty(name) ? "FIRE" : name.ToUpperInvariant();
        }

        private static void HandleInput()
        {
            InputAction fire = Fire();
            long target = 0;
            Step step = PvpPunch.CanPunch() ? FindStep(out target) : Step.None;
            bool pressed = fire != null && fire.IsPressed();
            if (!pressed) _needRelease = false;

            Busy = step != Step.None;
            if (step == Step.None)
            {
                _holding = Step.None;
                ActionHint.Set("breach", ActionHint.BreachPriority, ArmedCountdown());
                return;
            }
            if (!pressed || _needRelease || step != _holding || target != _holdTarget)
            {
                _holding = pressed && !_needRelease ? step : Step.None;
                _holdTarget = target;
                _holdStart = Time.unscaledTime;
            }
            float needed = step == Step.Attach ? AttachHold : (step == Step.Arm ? ArmHold : DefuseHold);
            float progress = _holding == step ? Mathf.Clamp01((Time.unscaledTime - _holdStart) / needed) : 0f;
            ActionHint.Set("breach", ActionHint.BreachPriority,
                "HOLD " + FireName(fire) + "  " + Describe(step) + (progress > 0f ? "  " + Mathf.RoundToInt(progress * 100f) + "%" : ""));
            if (progress < 1f || Time.unscaledTime < _nextRequest) return;

            _needRelease = true;
            _holding = Step.None;
            _nextRequest = Time.unscaledTime + 1f;
            Request(step, target);
        }

        private static string Describe(Step step)
        {
            switch (step)
            {
                case Step.Attach: return "ATTACH BREACHING CHARGE";
                case Step.Arm: return "ARM BREACHING CHARGE";
                default: return "PULL OFF THE ENEMY CHARGE";
            }
        }

        /// <summary>"BREACHING CHARGE ARMED: 4" when an armed charge is near the local player.</summary>
        private static string ArmedCountdown()
        {
            LocalPlayer me = LocalPlayer.Instance;
            if (me == null) return null;
            foreach (KeyValuePair<long, Shown> entry in Models)
            {
                Charge charge;
                if (entry.Value.Model == null || !Charges.TryGetValue(entry.Key, out charge) || charge.State != State.Armed) continue;
                if (Vector3.Distance(entry.Value.Model.transform.position, me.transform.position) > 25f) continue;
                double left = Math.Max(0.0, charge.Until - PhotonNetwork.Time);
                return "BREACHING CHARGE ARMED: " + Math.Ceiling(left).ToString("0");
            }
            return null;
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

            // An attached charge in sight: arm it (the enemy's) or pull it off (our own). Not with an item in hand that Fire
            // would use as well.
            if (held == null || !(held is InteractiveCarryable) && !BreachCharge.Is(held))
            {
                foreach (KeyValuePair<long, Shown> entry in Models)
                {
                    Charge charge;
                    if (entry.Value.Model == null || !Charges.TryGetValue(entry.Key, out charge)) continue;
                    if (!InSight(eye, aim, entry.Value.Model.transform.position)) continue;
                    int shipSlot = FleetShips.Slot(ShipOfView(charge.Ship));
                    if (shipSlot < 0) continue;
                    target = entry.Key;
                    if (shipSlot == myTeam) return Step.Defuse;
                    if (charge.State == State.Attached) return Step.Arm;
                    target = 0;
                }
            }

            // Holding a charge, looking at the closed outer door of an enemy airlock that has none.
            if (!BreachCharge.Is(held)) return Step.None;
            Airlock airlock = AimedEnemyAirlock(eye, aim);
            long key;
            if (airlock == null || !Identify(airlock, out key) || Charges.ContainsKey(key)) return Step.None;
            target = key;
            return Step.Attach;
        }

        private static bool InSight(Vector3 eye, Vector3 aim, Vector3 point)
        {
            Vector3 to = point - eye;
            float distance = to.magnitude;
            if (distance > Reach || Vector3.Angle(aim, to) > (distance < 1f ? AimAngle * 2f : AimAngle)) return false;
            Vector3 near = point - to.normalized * 0.15f;
            return !Physics.Linecast(eye, near, SightMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>The airlock whose closed outer door, on the enemy ship, is in front of the camera within reach.</summary>
        private static Airlock AimedEnemyAirlock(Vector3 eye, Vector3 aim)
        {
            PlayerControlledShip enemy = null;
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
                if (PvpBoarding.IsEnemyShip(ship)) enemy = ship;
            }
            if (enemy == null) return null;

            AirlockDoor best = null;
            RaycastHit hit;
            if (Physics.Raycast(eye, aim, out hit, Reach, ~0, QueryTriggerInteraction.Ignore))
            {
                AirlockDoor door = hit.collider.GetComponentInParent<AirlockDoor>();
                if (door != null && door.airlockDoorType == AirlockDoorType.Outer && FleetShips.OwnerOf(door) == enemy) best = door;
            }
            if (best == null)
            {
                // The door's colliders may sit elsewhere: the nearest enemy outer door in front of the camera.
                float bestAngle = AimAngle * 2f;
                foreach (Airlock airlock in AirlocksOf(enemy))
                {
                    AirlockDoor door = AirlockBreach.Door(airlock, AirlockDoorType.Outer);
                    if (door == null) continue;
                    Vector3 point;
                    Vector3 outward;
                    AirlockBreach.DoorFace(door, out point, out outward);
                    Vector3 to = point - eye;
                    if (to.magnitude > Reach + 0.8f) continue;
                    float angle = Vector3.Angle(aim, to);
                    if (angle >= bestAngle) continue;
                    bestAngle = angle;
                    best = door;
                }
            }
            return best != null && !best.IsOpen ? best.OwningAirlock : null;
        }

        private static void Request(Step step, long target)
        {
            int ship = (int)(target >> 8);
            int index = (int)(target & 0xff);
            switch (step)
            {
                case Step.Attach:
                    CarryableObject held = BreachCharge.LocalHeld;
                    if (held != null && held.photonView != null) Net.ToHost(Msg.BreachAttach, held.photonView.ViewID, ship, index);
                    break;
                case Step.Arm:
                    Net.ToHost(Msg.BreachArm, ship, index);
                    break;
                case Step.Defuse:
                    Net.ToHost(Msg.BreachDefuse, ship, index);
                    break;
            }
        }

        // ---------------------------------------------------------------- the host

        public static void Listen()
        {
            Net.On(Msg.BreachAttach, (sender, data) =>
            {
                if (!PhotonNetwork.IsMasterClient || !Active) return;
                ReadRoom();
                int chargeView = Net.Get(data, 0, -1);
                int shipView = Net.Get(data, 1, -1);
                int index = Net.Get(data, 2, -1);
                Airlock airlock = AirlockAt(shipView, index);
                PlayerControlledShip ship = ShipOfView(shipView);
                AirlockDoor door = AirlockBreach.Door(airlock, AirlockDoorType.Outer);
                PhotonView chargeObject = PhotonView.Find(chargeView);
                var item = chargeObject != null ? chargeObject.GetComponent<CarryableObject>() : null;
                string refusal = null;
                if (airlock == null || ship == null || door == null) refusal = "That airlock can't be breached.";
                else if (!PvpBoarding.IsEnemyOf(sender, ship)) refusal = "That's your own ship's airlock.";
                else if (Charges.ContainsKey(KeyOf(shipView, index))) refusal = "That airlock already has a charge on it, or was just breached.";
                else if (door.IsOpen) refusal = "That airlock is open already.";
                else if (!BreachCharge.Is(item) || chargeObject.OwnerActorNr != sender) refusal = "You need to be holding a breaching charge.";
                else if (!Near(sender, door, HostReach)) refusal = "Get closer to the airlock door.";
                if (refusal != null)
                {
                    Net.ToActor(sender, Msg.Notice, refusal);
                    return;
                }
                Charges[KeyOf(shipView, index)] = NewCharge(ship, shipView, index, door, sender, State.Attached, 0);
                WriteRoom();
                Net.ToActor(sender, Msg.BreachTaken, chargeView);
                Log.Info("PvP: " + NameOf(sender) + " attached a breaching charge to an airlock of " + FleetShips.Describe(ship) + ".");
                Notice.ShowAll(NameOf(sender) + " (" + PvpTeams.TeamName(FleetShips.CrewSlot(sender)) + ") attached a breaching charge to the " +
                               PvpTeams.TeamName(FleetShips.Slot(ship)) + " ship's airlock!");
            });

            Net.On(Msg.BreachTaken, (sender, data) =>
            {
                if (!Net.FromHost(sender)) return;
                PhotonView view = PhotonView.Find(Net.Get(data, 0, -1));
                var item = view != null ? view.GetComponent<CarryableObject>() : null;
                if (BreachCharge.Is(item)) BreachCharge.Consume(item);
            });

            Net.On(Msg.BreachArm, (sender, data) =>
            {
                if (!PhotonNetwork.IsMasterClient || !Active) return;
                ReadRoom();
                Charge charge;
                if (!Charges.TryGetValue(KeyOf(Net.Get(data, 0, -1), Net.Get(data, 1, -1)), out charge) || charge.State != State.Attached) return;
                Airlock airlock = AirlockAt(charge.Ship, charge.Index);
                PlayerControlledShip ship = ShipOfView(charge.Ship);
                if (!PvpBoarding.IsEnemyOf(sender, ship) || !Near(sender, AirlockBreach.Door(airlock, AirlockDoorType.Outer), HostReach)) return;
                charge.State = State.Armed;
                charge.Until = PhotonNetwork.Time + FuseSeconds;
                WriteRoom();
                Log.Info("PvP: " + NameOf(sender) + " armed the breaching charge on " + FleetShips.Describe(ship) + ".");
                Notice.ShowAll("A breaching charge is armed on the " + PvpTeams.TeamName(FleetShips.Slot(ship)) + " ship's airlock!");
            });

            Net.On(Msg.BreachDefuse, (sender, data) =>
            {
                if (!PhotonNetwork.IsMasterClient || !Active) return;
                ReadRoom();
                long key = KeyOf(Net.Get(data, 0, -1), Net.Get(data, 1, -1));
                Charge charge;
                if (!Charges.TryGetValue(key, out charge) || !Shows(charge)) return;
                if (charge.State == State.Armed && PhotonNetwork.Time > charge.Until - 0.25) return;
                Airlock airlock = AirlockAt(charge.Ship, charge.Index);
                PlayerControlledShip ship = ShipOfView(charge.Ship);
                if (ship == null || FleetShips.CrewSlot(sender) != FleetShips.Slot(ship) || !Near(sender, AirlockBreach.Door(airlock, AirlockDoorType.Outer), HostReach)) return;
                Charges.Remove(key);
                WriteRoom();
                Log.Info("PvP: " + NameOf(sender) + " pulled a breaching charge off " + FleetShips.Describe(ship) + ".");
                Notice.ShowAll(NameOf(sender) + " pulled the breaching charge off the " + PvpTeams.TeamName(FleetShips.Slot(ship)) + " ship's airlock.");
            });
        }

        /// <summary>A charge on an outer door, its pose worked out by the host (the same for every client).</summary>
        private static Charge NewCharge(PlayerControlledShip ship, int shipView, int index, AirlockDoor door, int actor, State state, double until)
        {
            Vector3 point;
            Vector3 outward;
            AirlockBreach.DoorFace(door, out point, out outward);
            Transform root = ship.transform;
            Quaternion world = Quaternion.LookRotation(outward, root.up);
            return new Charge
            {
                Ship = shipView,
                Index = index,
                State = state,
                Until = until,
                Actor = actor,
                Safety = true,
                Position = root.InverseTransformPoint(point + outward * 0.02f),
                Rotation = Quaternion.Inverse(root.rotation) * world
            };
        }

        private static bool Near(int actor, Component door, float reach)
        {
            if (door == null) return false;
            Player player = FleetShips.CharacterOf(actor);
            if (player == null) return false;
            Vector3 point;
            Vector3 outward;
            AirlockBreach.DoorFace((AirlockDoor)door, out point, out outward);
            return Vector3.Distance(player.transform.position, point) <= reach;
        }

        private static string NameOf(int actor)
        {
            Photon.Realtime.Player player = PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.GetPlayer(actor) : null;
            return player != null && !string.IsNullOrEmpty(player.NickName) ? player.NickName : "A player";
        }

        /// <summary>Host, a few times a second: fuses burn down, opening the door knocks a charge off, blown airlocks stay jammed open.</summary>
        public static void Tick()
        {
            if (!PhotonNetwork.IsMasterClient || !Active) return;
            try
            {
                ReadRoom();
                if (Charges.Count == 0) return;
                bool changed = false;
                double now = PhotonNetwork.Time;
                foreach (Charge charge in new List<Charge>(Charges.Values))
                {
                    Airlock airlock = AirlockAt(charge.Ship, charge.Index);
                    PlayerControlledShip ship = ShipOfView(charge.Ship);
                    AirlockDoor door = AirlockBreach.Door(airlock, AirlockDoorType.Outer);
                    if (airlock == null || ship == null || door == null)
                    {
                        Charges.Remove(charge.Key);
                        changed = true;
                        continue;
                    }
                    switch (charge.State)
                    {
                        case State.Attached:
                        case State.Armed:
                            if (door.IsOpen)
                            {
                                Charges.Remove(charge.Key);
                                changed = true;
                                Notice.ShowAll("The breaching charge on the " + PvpTeams.TeamName(FleetShips.Slot(ship)) + " ship fell off when its airlock opened.");
                            }
                            else if (charge.State == State.Armed && now >= charge.Until)
                            {
                                charge.Safety = AirlockBreach.ForceOpen(airlock);
                                charge.State = State.Blown;
                                charge.Until = now + JammedSeconds;
                                changed = true;
                                Log.Info("PvP: a breaching charge blew an airlock of " + FleetShips.Describe(ship) + " open.");
                                Notice.ShowAll("The " + PvpTeams.TeamName(FleetShips.Slot(ship)) + " ship's airlock was breached! Its doors are jammed open and its air is venting.");
                            }
                            break;
                        case State.Blown:
                            if (now >= charge.Until)
                            {
                                AirlockBreach.Release(airlock, charge.Safety);
                                charge.State = State.Cooldown;
                                charge.Until = now + CooldownSeconds;
                                changed = true;
                                Log.Info("PvP: the breached airlock of " + FleetShips.Describe(ship) + " works again.");
                            }
                            else AirlockBreach.KeepOpen(airlock, 0.25f);
                            break;
                        case State.Cooldown:
                            if (now >= charge.Until)
                            {
                                Charges.Remove(charge.Key);
                                changed = true;
                            }
                            break;
                    }
                }
                if (changed) WriteRoom();
            }
            catch (Exception e)
            {
                Log.Failure("Running breaching charges", e);
            }
        }

        // ---------------------------------------------------------------- self-test

        /// <summary>Self-test (host): arm a charge on the first airlock of this ship, as if an enemy had.</summary>
        public static string DebugArm(PlayerControlledShip ship, float fuse)
        {
            if (!PhotonNetwork.IsMasterClient || ship == null) return "not the host, or no ship";
            Airlock[] airlocks = ship.GetComponentsInChildren<Airlock>(true);
            if (airlocks.Length == 0) return "the ship has no airlock";
            AirlockDoor door = AirlockBreach.Door(airlocks[0], AirlockDoorType.Outer);
            if (door == null) return "the airlock has no outer door";
            int view = FleetShips.ViewId(ship);
            ReadRoom();
            Charges[KeyOf(view, 0)] = NewCharge(ship, view, 0, door, PhotonNetwork.LocalPlayer.ActorNumber, State.Armed, PhotonNetwork.Time + fuse);
            WriteRoom();
            return "armed a charge on " + airlocks[0].name + " (" + airlocks.Length + " airlocks)";
        }

        /// <summary>Self-test: the first airlock of a ship, for the report.</summary>
        public static string Describe(PlayerControlledShip ship)
        {
            Airlock[] airlocks = ship != null ? ship.GetComponentsInChildren<Airlock>(true) : new Airlock[0];
            if (airlocks.Length == 0) return "no airlock";
            Airlock airlock = airlocks[0];
            AirlockDoor outer = AirlockBreach.Door(airlock, AirlockDoorType.Outer);
            AirlockDoor inner = AirlockBreach.Door(airlock, AirlockDoorType.Inner);
            long key;
            Charge charge = null;
            if (Identify(airlock, out key)) Charges.TryGetValue(key, out charge);
            return "airlock " + airlock.name + ": outer " + (outer != null && outer.IsOpen ? "open" : "closed") + ", inner " + (inner != null && inner.IsOpen ? "open" : "closed") +
                   ", safety " + (airlock.IsSafetyEnabled ? "on" : "off") + ", charge " + (charge != null ? charge.State.ToString() : "none") +
                   ", models shown " + Models.Count + "; " + AirlockBreach.DescribeAir(airlock);
        }
    }
}
