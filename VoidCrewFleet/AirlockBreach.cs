using System;
using System.Collections.Generic;
using System.Reflection;
using CG.Client.Ship;
using CG.Client.Ship.Views;
using CG.Game.Player;
using CG.Ship.Hull;
using CG.Space;
using Gameplay;
using Gameplay.Atmosphere;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// What a breaching charge does to an airlock, with the game's own airlock and air: the host turns the airlock's
    /// safety interlock off (the game's own "both doors may open" state) and opens both doors, and the ship vents through
    /// it as it does in the game. The blast also empties the airlock at once and drains the rooms open to it faster. The
    /// door, airlock and air states reach every client by the game's own streaming. Also the look and sound of a charge.
    /// </summary>
    internal static class AirlockBreach
    {
        private const float VentRate = 0.35f;
        private const string BeepEvent = "event:/SFX/Metem/Metem_Mine_SlowBeep";
        private const string BlastEvent = "event:/SFX/Defects/Explosions/Defect_ExplosionNormal";

        private static readonly FieldInfo DiodeSettingField = GameMember.Field(typeof(Diode), "setting");

        /// <summary>True while the breach itself opens doors or changes safety (the jam rules let it through).</summary>
        public static bool Bypass { get; private set; }

        private static bool _beepBroken;
        private static Color? _red;
        private static GameObject _blastTemplate;
        private static bool _blastLooked;

        public static AirlockDoor Door(Airlock airlock, AirlockDoorType type)
        {
            if (airlock == null || airlock.airlockDoors == null) return null;
            foreach (AirlockDoor door in airlock.airlockDoors)
            {
                if (door != null && door.airlockDoorType == type) return door;
            }
            return null;
        }

        // ---------------------------------------------------------------- host: the doors

        /// <summary>Host: safety off, both doors open. Returns the safety setting to restore later.</summary>
        public static bool ForceOpen(Airlock airlock)
        {
            bool safety = airlock.IsSafetyEnabled;
            Bypass = true;
            try
            {
                airlock.SetDoorSafety(false);
                airlock.SetDoorOpen(AirlockDoorType.Outer, true);
                airlock.SetDoorOpen(AirlockDoorType.Inner, true);
            }
            finally
            {
                Bypass = false;
            }
            Room chamber = Chamber(airlock);
            if (chamber != null) SetAir(chamber, 0f);
            return safety;
        }

        /// <summary>Host, while jammed: the doors stay open and the safety off, and the rooms open to it drain fast.</summary>
        public static void KeepOpen(Airlock airlock, float seconds)
        {
            Bypass = true;
            try
            {
                if (airlock.IsSafetyEnabled) airlock.SetDoorSafety(false);
                foreach (AirlockDoor door in airlock.airlockDoors)
                {
                    if (door != null && !door.IsOpen) airlock.SetDoorOpen(door.airlockDoorType, true);
                }
            }
            finally
            {
                Bypass = false;
            }
            float keep = Mathf.Exp(-VentRate * seconds);
            foreach (Room room in OpenRooms(Chamber(airlock))) SetAir(room, keep);
        }

        /// <summary>Host, when the jam ends: the airlock's safety as it was. The crew can close the doors again.</summary>
        public static void Release(Airlock airlock, bool safety)
        {
            Bypass = true;
            try
            {
                airlock.SetDoorSafety(safety);
            }
            finally
            {
                Bypass = false;
            }
        }

        // ---------------------------------------------------------------- host: the air

        /// <summary>The airlock's chamber: the room on the ship side of its outer door.</summary>
        private static Room Chamber(Airlock airlock)
        {
            AirlockDoor outer = Door(airlock, AirlockDoorType.Outer);
            RoomPortal portal = outer != null ? outer.Portal : null;
            if (portal == null || portal.RoomConnections == null) return null;
            foreach (Room room in portal.RoomConnections)
            {
                if (room != null && !room.InSpace) return room;
            }
            return null;
        }

        /// <summary>The chamber and every room of the ship joined to it by open doorways.</summary>
        private static List<Room> OpenRooms(Room chamber)
        {
            var rooms = new List<Room>();
            if (chamber == null) return rooms;
            var seen = new HashSet<Room> { chamber };
            var queue = new Queue<Room>();
            queue.Enqueue(chamber);
            while (queue.Count > 0)
            {
                Room room = queue.Dequeue();
                rooms.Add(room);
                if (room.PortalConnections == null) continue;
                foreach (RoomPortal portal in room.PortalConnections)
                {
                    if (portal == null || portal.Opening <= 0.01f || portal.RoomConnections == null) continue;
                    foreach (Room next in portal.RoomConnections)
                    {
                        if (next == null || next.InSpace || !seen.Add(next)) continue;
                        queue.Enqueue(next);
                    }
                }
            }
            return rooms;
        }

        /// <summary>Scales a room's air pressure and oxygen (0 empties it); the game streams the values to everyone.</summary>
        private static void SetAir(Room room, float keep)
        {
            Atmosphere atmosphere = room.RoomCollection != null ? room.RoomCollection.FindRoomCollectionMonoBehaviour<Atmosphere>() : null;
            if (atmosphere == null || atmosphere.RoomAtmospheres == null) return;
            int index = atmosphere.RoomAtmospheres.IndexOfKey(room);
            if (index < 0) return;
            AtmosphereValues values = atmosphere.RoomAtmospheres.GetElementAt(index);
            values.Pressure *= keep;
            values.Oxygen *= keep;
            atmosphere.RoomAtmospheres.SetElementAt(index, values);
        }

        /// <summary>For the self-test: the airlock chamber's air.</summary>
        public static string DescribeAir(Airlock airlock)
        {
            Room chamber = Chamber(airlock);
            if (chamber == null) return "no chamber";
            Atmosphere atmosphere = chamber.RoomCollection != null ? chamber.RoomCollection.FindRoomCollectionMonoBehaviour<Atmosphere>() : null;
            int index = atmosphere != null ? atmosphere.RoomAtmospheres.IndexOfKey(chamber) : -1;
            if (index < 0) return "no air data";
            AtmosphereValues values = atmosphere.RoomAtmospheres.GetElementAt(index);
            List<Room> open = OpenRooms(chamber);
            float pressure = 0f;
            foreach (Room room in open)
            {
                int i = atmosphere.RoomAtmospheres.IndexOfKey(room);
                if (i >= 0) pressure += atmosphere.RoomAtmospheres.GetElementAt(i).Pressure;
            }
            return "chamber pressure " + values.Pressure.ToString("0.00") + ", oxygen " + values.Oxygen.ToString("0.00") + "; " + open.Count +
                   " rooms open to it, mean pressure " + (open.Count > 0 ? pressure / open.Count : 0f).ToString("0.00");
        }

        // ---------------------------------------------------------------- where the charge goes

        /// <summary>The middle of an outer door's face on the space side, and the direction out to space.</summary>
        public static bool DoorFace(AirlockDoor door, out Vector3 point, out Vector3 outward)
        {
            RoomPortal portal = door.Portal;
            Vector3 centre = portal != null ? portal.transform.position : door.transform.position + door.transform.up * 1.7f;
            outward = door.transform.forward;
            if (portal != null && portal.RoomConnections != null)
            {
                foreach (Room room in portal.RoomConnections)
                {
                    Vector3 local;
                    if (room == null || !room.InSpace || room.PortalConnectionPoints == null || !room.PortalConnectionPoints.TryGetValue(portal, out local)) continue;
                    if (Vector3.Dot(portal.transform.TransformPoint(local) - centre, outward) < 0f) outward = -outward;
                    break;
                }
            }
            // The door leaves' surface (just below the middle, where the three leaves meet).
            point = centre + outward * 0.15f;
            float best = float.MaxValue;
            Vector3 from = centre + outward * 2f - door.transform.up * 0.35f;
            foreach (RaycastHit hit in Physics.RaycastAll(from, -outward, 2.5f, 1 << 27, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider != null && hit.collider.transform.IsChildOf(door.transform) && hit.distance < best)
                {
                    best = hit.distance;
                    point = hit.point;
                }
            }
            return true;
        }

        // ---------------------------------------------------------------- look and sound (every client, locally)

        /// <summary>The game's red diode colour (HDR), for the charge's blinking.</summary>
        public static Color Red
        {
            get
            {
                if (_red.HasValue) return _red.Value;
                Color red = new Color(1f, 0.05f, 0.02f) * 8f;
                try
                {
                    foreach (PlayerControlledShip ship in FleetShips.All)
                    {
                        foreach (Diode diode in ship.GetComponentsInChildren<Diode>(true))
                        {
                            var setting = DiodeSettingField != null ? DiodeSettingField.GetValue(diode) as DiodeSetting : null;
                            if (setting == null) continue;
                            red = setting.GetColorCode(DiodeSetting.ColorState.Negative);
                            break;
                        }
                        break;
                    }
                }
                catch
                {
                    // Keep the fallback colour.
                }
                _red = red;
                return red;
            }
        }

        public static void PlayBeep(GameObject at)
        {
            if (_beepBroken || at == null) return;
            try
            {
                FMODUnity.RuntimeManager.PlayOneShotAttached(BeepEvent, at);
            }
            catch (Exception e)
            {
                _beepBroken = true;
                Log.Warn("Breaching charge: no fuse beep (" + e.Message + ").");
            }
        }

        /// <summary>The blast: the ship's own defect explosion (it hurts and pushes only this client's player), or a fallback.</summary>
        public static void PlayBlast(Vector3 position, Quaternion rotation, Transform parent)
        {
            try
            {
                GameObject template = BlastTemplate();
                if (template != null)
                {
                    GameObject blast = UnityEngine.Object.Instantiate(template, position, rotation, parent);
                    blast.name = "VCF_BreachBlast";
                    blast.SetActive(true);
                    UnityEngine.Object.Destroy(blast, 6f);
                    return;
                }
                FMODUnity.RuntimeManager.PlayOneShot(BlastEvent, position);
                LocalPlayer me = LocalPlayer.Instance;
                if (me != null && !me.IsDead && Vector3.Distance(me.transform.position, position) < 4f)
                    PvpPunch.TakePunch(me, position, 0.25f);
            }
            catch (Exception e)
            {
                Log.Failure("Playing a breaching charge's blast", e);
            }
        }

        private static GameObject BlastTemplate()
        {
            if (_blastTemplate != null || _blastLooked) return _blastTemplate;
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
                foreach (PlayerDamageVolumeTrigger trigger in ship.GetComponentsInChildren<PlayerDamageVolumeTrigger>(true))
                {
                    string name = trigger.name;
                    if (name.IndexOf("DemolishDamage", StringComparison.OrdinalIgnoreCase) < 0 || name.IndexOf("Physical", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (trigger.GetComponent<DisableSelfTimer>() == null) continue;
                    _blastTemplate = trigger.gameObject;
                    break;
                }
                if (_blastTemplate != null) break;
            }
            _blastLooked = FleetShips.All.Count > 0;
            if (_blastLooked) Log.Verbose("Breaching charge blast: " + (_blastTemplate != null ? _blastTemplate.name : "none found, using a sound only"));
            return _blastTemplate;
        }

        /// <summary>A new mission: look for the ships' explosion again.</summary>
        public static void Forget()
        {
            _blastTemplate = null;
            _blastLooked = false;
            _red = null;
        }
    }
}
