using System;
using System.Collections.Generic;
using System.Linq;
using CG.Game.Scenarios;
using CG.Game.Scenarios.Actions;
using CG.Space;
using Code.Gameplay;
using Gameplay.Quests;
using Photon.Pun;
using ResourceAssets;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace VoidCrewFleet
{
    /// <summary>
    /// The Fleet Battle (PvP) arena. The Pilgrimage the match runs on starts in an empty staging sector; in PvP every client
    /// swaps that first sector for one of the game's own asteroid sectors (each client builds the same field from the same
    /// asset, as the game does), the host starts the ships on opposite sides of it with rocks between them, and spawns
    /// mirrored pairs of the game's own neutral salvage (containers to shoot open, a wreck and an asteroid base to search,
    /// a VoidFish) so both teams have the same resources nearby. Every few minutes an alloy supply drop arrives at a spot
    /// the same distance from both ships: the next drop's time is a room property in server time, so a new host keeps to
    /// the schedule. No enemy NPCs are added, and jumps stay blocked.
    /// </summary>
    internal static class PvpArena
    {
        public const string Feature = "PvP arena";
        private const string KeySpawned = "VCF_ArenaPoi";

        /// <summary>The next supply drop's server time, and the match (session ID) it belongs to; always written together.</summary>
        private const string KeyNextDrop = "VCF_ArenaDrop";

        private const string KeyDropMatch = "VCF_ArenaDropFor";
        private const int SupplyDropMilliseconds = 300000;

        private static readonly GUIDUnion SuppliesContainer = new GUIDUnion("1faa2a89e1fbe204aa6795d1d39284e4");
        private static readonly GUIDUnion GenericContainer = new GUIDUnion("71d99eb5d27424a42bb5fc15a1510834");
        private static readonly GUIDUnion AmmoContainer = new GUIDUnion("ee5f2badd858fa04abb02fd032d40452");
        private static readonly GUIDUnion PipeWreck = new GUIDUnion("93a24c8bac4cbde4e9bea38df82dfe0e");
        private static readonly GUIDUnion AsteroidBase = new GUIDUnion("d5fc3398f2236f34cb1953f11238e7f0");
        private static readonly GUIDUnion VoidFish = new GUIDUnion("c0d322724a23ac34fb4c3454792d2c38");
        private static readonly GUIDUnion AlloyCluster = new GUIDUnion("8dc9db663aad48142bc22b5114c0d4ca");

        private sealed class Poi
        {
            public GUIDUnion Prefab;
            public Vector3 Position;

            public Poi(GUIDUnion prefab, float x, float y, float z)
            {
                Prefab = prefab;
                Position = new Vector3(x, y, z);
            }
        }

        private sealed class Preset
        {
            public string Id;
            public string SolarSystem;
            public string SectorGuid;
            public Vector3 Centre;
            public float Yaw;
            public Vector3 DropSpot;
            public Poi[] Pois; // BLUE's side; RED's are mirrored through the centre
        }

        /// <summary>Presets checked offline for clear ship starts 3.6 km apart and rocks between them.</summary>
        private static readonly Preset[] Presets =
        {
            new Preset
            {
                Id = "Dz-A", SolarSystem = "Temperance", SectorGuid = "1398b9464af249c49898fff513e06a7e",
                Centre = new Vector3(0f, 500f, -3000f), Yaw = 15f, DropSpot = new Vector3(1545f, 800f, -3414f),
                Pois = Standard(new Vector3(-499f, 500f, -5248f), new Vector3(-1542f, 800f, -5278f), new Vector3(2266f, 800f, -3814f))
            },
            new Preset
            {
                Id = "Or-A", SolarSystem = "Vigilance", SectorGuid = "27679b89574e4424783c34494dba7cef",
                Centre = new Vector3(2500f, 250f, -3000f), Yaw = 30f, DropSpot = new Vector3(3106f, 550f, -3350f),
                Pois = Standard(new Vector3(1373f, 550f, -5352f), new Vector3(247f, 400f, -4702f), new Vector3(58f, 550f, -2629f))
            },
            new Preset
            {
                Id = "Oz-B", SolarSystem = "Bravery", SectorGuid = "441af99136f7a654586222e31dbb3f19",
                Centre = new Vector3(2250f, 500f, -250f), Yaw = 45f, DropSpot = new Vector3(2780f, 200f, -780f),
                Pois = Standard(new Vector3(58f, 200f, -1735f), new Vector3(-791f, 650f, -886f), new Vector3(-1003f, 200f, -109f))
            },
            new Preset
            {
                Id = "Defiance", SolarSystem = "Defiance", SectorGuid = "01e307992f03d3f4ea241409c5a5cfd9",
                Centre = new Vector3(250f, -500f, -500f), Yaw = 15f, DropSpot = new Vector3(878f, -800f, -668f),
                Pois = Standard(new Vector3(-1182f, -800f, -1980f), new Vector3(-1825f, -800f, -1290f), null)
            }
        };

        /// <summary>
        /// The salvage of one side: two containers near home, a wreck on the flank, and further out an asteroid base, an
        /// ammo container and a VoidFish (without a far spot: the ammo container goes on the flank).
        /// </summary>
        private static Poi[] Standard(Vector3 home, Vector3 flank, Vector3? far)
        {
            var pois = new List<Poi>
            {
                new Poi(SuppliesContainer, home.x, home.y, home.z),
                new Poi(GenericContainer, home.x + 60f, home.y, home.z),
                new Poi(PipeWreck, flank.x, flank.y, flank.z)
            };
            if (far.HasValue)
            {
                Vector3 f = far.Value;
                pois.Add(new Poi(AsteroidBase, f.x, f.y, f.z));
                pois.Add(new Poi(AmmoContainer, f.x + 180f, f.y, f.z));
                pois.Add(new Poi(VoidFish, f.x, f.y + 250f, f.z));
            }
            else pois.Add(new Poi(AmmoContainer, flank.x + 120f, flank.y, flank.z));
            return pois.ToArray();
        }

        private static Preset _current;

        /// <summary>The host's start-of-match check that the ships are clear of the rocks (unscaled time; -1 for none).</summary>
        private static float _checkAt = -1f;

        /// <summary>
        /// The host's own latest next-drop time: a room property only changes on the host once the server sends it back,
        /// so for as long as it hosts this match in this room, the host goes by what it last wrote.
        /// </summary>
        private static int _hostNextDrop;

        private static bool _hostHasNextDrop;
        private static string _hostDropRoom;
        private static string _hostDropSession;

        // ---------------------------------------------------------------- the sector (every client)

        /// <summary>Every client generates the quest itself: in PvP its first sector becomes the arena.</summary>
        [GamePatch(Feature, "EndlessQuestGenerator", "CreateEndlessQuest", PatchKind.Postfix)]
        private static void QuestCreatedPostfix(Quest __result, int randomSeed)
        {
            _current = null;
            try
            {
                FleetPlan plan = FleetSession.Current;
                var quest = __result as EndlessQuest;
                if (plan == null || plan.Mode != FleetMode.PvP || quest == null || quest.StartSector == null) return;
                GameSessionSection first = quest.Context.CurrentSection;
                Preset preset = Choose(plan.Arena, first != null && first.SolarSystem != null ? first.SolarSystem.name : null, randomSeed);
                if (preset == null) return;
                var sector = new ScriptableObjectRef(new GUIDUnion(preset.SectorGuid)).Asset as Sector;
                if (sector == null || sector.EnvironmentObj == null || sector.EnvironmentObj.IsNull)
                {
                    Log.Warn("PvP arena " + preset.Id + " could not be loaded; the match uses the empty starting sector.");
                    return;
                }
                GameSessionSector start = quest.StartSector;
                Log.Info("PvP arena: the match is in " + sector.name + " (" + preset.Id + ") instead of " + (start.SectorAsset != null ? start.SectorAsset.name : "?") + ".");
                start.SectorAsset = sector;
                start.Name = sector.DisplayName;
                start.PositionAU = sector.PositionAU;
                start.AtmosphereData = sector.Atmosphere;
                _current = preset;
            }
            catch (Exception e)
            {
                Log.Failure("Choosing the PvP arena", e);
            }
        }

        private static Preset Choose(string wanted, string system, int seed)
        {
            if (string.Equals(wanted, "off", StringComparison.OrdinalIgnoreCase)) return null;
            Preset named = Presets.FirstOrDefault(p => string.Equals(p.Id, wanted, StringComparison.OrdinalIgnoreCase));
            if (named != null) return named;
            Preset own = Presets.FirstOrDefault(p => system != null && p.SolarSystem == system);
            return own ?? Presets[(seed & int.MaxValue) % Presets.Length];
        }

        // ---------------------------------------------------------------- the ships' starts (host)

        /// <summary>The PvP start positions: BLUE and RED on opposite sides of the arena's centre, facing each other.</summary>
        public static bool StartPositions(float distance, out Vector3 blue, out Quaternion blueRotation, out Vector3 red, out Quaternion redRotation)
        {
            Preset preset = _current;
            Vector3 centre = preset != null ? preset.Centre : Vector3.zero;
            Quaternion facing = Quaternion.Euler(0f, preset != null ? preset.Yaw : 0f, 0f);
            Vector3 axis = facing * Vector3.forward;
            float half = distance * 0.5f;
            blue = centre - axis * half;
            blueRotation = facing;
            red = centre + axis * half;
            redRotation = facing * Quaternion.Euler(0f, 180f, 0f);
            return preset != null;
        }

        private static Vector3 Mirror(Preset preset, Vector3 point)
        {
            return new Vector3(2f * preset.Centre.x - point.x, point.y, 2f * preset.Centre.z - point.z);
        }

        // ---------------------------------------------------------------- salvage, supply drops, clear starts (host)

        /// <summary>
        /// The host starting a match: the ships' clear-of-rocks check in 8 seconds, the salvage, and the first supply drop
        /// in 5 minutes. (Only that host checks the ships: a player who becomes host later never moves them mid-match.)
        /// </summary>
        [GamePatch(Feature, "CG.GameLoopStateMachine.GameStates.GSIngame", "OnEnter", PatchKind.Postfix)]
        private static void InGamePostfix()
        {
            _checkAt = PhotonNetwork.IsMasterClient ? Time.unscaledTime + 8f : -1f;
            try
            {
                Preset preset = _current;
                GameSession session = GameSessionManager.ActiveSession;
                GameSessionSector sector = GameSessionManager.ActiveSector;
                if (preset == null || !PhotonNetwork.IsMasterClient || !FleetSession.IsPvP || session == null || sector == null || sector.Id != 0) return;
                object done;
                if (PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(KeySpawned, out done) && (done as string) == session.GameSessionID) return;
                int spawned = 0;
                foreach (Poi poi in preset.Pois)
                {
                    spawned += Spawn(sector, poi.Prefab, poi.Position, Quaternion.Euler(0f, preset.Yaw, 0f));
                    spawned += Spawn(sector, poi.Prefab, Mirror(preset, poi.Position), Quaternion.Euler(0f, preset.Yaw + 180f, 0f));
                }
                PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeySpawned, session.GameSessionID } });
                SetNextDrop(unchecked(PhotonNetwork.ServerTimestamp + SupplyDropMilliseconds), session.GameSessionID);
                Log.Info("PvP arena: placed " + spawned + " salvage spots, mirrored for both teams.");
            }
            catch (Exception e)
            {
                Log.Failure("Placing the PvP arena's salvage", e);
            }
        }

        private static int Spawn(GameSessionSector sector, GUIDUnion prefab, Vector3 position, Quaternion rotation)
        {
            try
            {
                List<OrbitObject> objects = SpawnUtils.Spawn(new SpawnSetup(new CloneStarObjectRef(prefab)), new SpawnHandle(sector, position, rotation));
                return objects != null ? objects.Count : 0;
            }
            catch (Exception e)
            {
                Log.Failure("Spawning PvP arena salvage " + prefab.AsHex(), e);
                return 0;
            }
        }

        /// <summary>
        /// Host, a few times a second: a ship that starts inside rock is moved clear (only by the host that started the
        /// match); supply drops arrive on the room's schedule, which a new host carries on.
        /// </summary>
        public static void Tick()
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                _checkAt = -1f;
                return;
            }
            Preset preset = _current;
            if (preset == null || !PvpCombat.Active) return;
            try
            {
                if (_checkAt > 0f && Time.unscaledTime >= _checkAt)
                {
                    _checkAt = -1f;
                    foreach (PlayerControlledShip ship in FleetShips.All) KeepClear(preset, ship);
                }
                if (!PvpTeams.Locked) return;
                GameSession session = GameSessionManager.ActiveSession;
                if (session == null) return;
                int next;
                if (!NextDrop(session.GameSessionID, out next))
                {
                    // No schedule for this match yet (it is only starting, or its first host left before the room had it):
                    // the first drop is a full interval away, never at once.
                    SetNextDrop(unchecked(PhotonNetwork.ServerTimestamp + SupplyDropMilliseconds), session.GameSessionID);
                    return;
                }
                if (unchecked(PhotonNetwork.ServerTimestamp - next) < 0) return;
                SetNextDrop(unchecked(PhotonNetwork.ServerTimestamp + SupplyDropMilliseconds), session.GameSessionID);
                SpawnUtils.SpawnCarePackage(AlloyCluster, 2, preset.DropSpot);
                Notice.ShowAll("A supply drop of alloys is arriving between the teams. Shoot it open and grab the alloys!");
                Log.Info("PvP arena: a supply drop is arriving at " + preset.DropSpot + ".");
            }
            catch (Exception e)
            {
                Log.Failure("Running the PvP arena", e);
            }
        }

        /// <summary>
        /// The next supply drop's server time for this match: the host's own latest, else the room's (only when it is this
        /// match's). False when this match has none.
        /// </summary>
        private static bool NextDrop(string sessionId, out int next)
        {
            next = 0;
            Photon.Realtime.Room room = PhotonNetwork.CurrentRoom;
            if (room == null) return false;
            if (_hostHasNextDrop && PhotonNetwork.IsMasterClient && room.Name == _hostDropRoom && _hostDropSession == sessionId)
            {
                next = _hostNextDrop;
                return true;
            }
            _hostHasNextDrop = false;
            object match;
            object value;
            if (!room.CustomProperties.TryGetValue(KeyDropMatch, out match) || (match as string) != sessionId) return false;
            if (!room.CustomProperties.TryGetValue(KeyNextDrop, out value) || !(value is int)) return false;
            next = (int)value;
            return true;
        }

        /// <summary>Host: the next supply drop's server time, for everyone (a host after this one carries on from it).</summary>
        private static void SetNextDrop(int next, string sessionId)
        {
            Photon.Realtime.Room room = PhotonNetwork.CurrentRoom;
            if (room == null) return;
            room.SetCustomProperties(new Hashtable { { KeyNextDrop, next }, { KeyDropMatch, sessionId } });
            _hostNextDrop = next;
            _hostHasNextDrop = true;
            _hostDropRoom = room.Name;
            _hostDropSession = sessionId;
        }

        private static void KeepClear(Preset preset, PlayerControlledShip ship)
        {
            if (ship == null || ship.Platform == null) return;
            const int Rocks = 1 << 10;
            const float Clearance = 300f;
            Vector3 position = ship.transform.position;
            if (!Physics.CheckSphere(position, Clearance, Rocks, QueryTriggerInteraction.Ignore))
            {
                Log.Info("PvP arena: " + FleetShips.Describe(ship) + " starts clear of the rocks.");
                return;
            }
            Vector3 outward = position - preset.Centre;
            outward.y = 0f;
            if (outward.sqrMagnitude < 1f) outward = ship.transform.forward;
            outward.Normalize();
            for (int step = 1; step <= 20; step++)
            {
                Vector3 candidate = position + outward * (100f * step);
                if (Physics.CheckSphere(candidate, Clearance, Rocks, QueryTriggerInteraction.Ignore) || NearAnotherShip(ship, candidate)) continue;
                ship.Platform.Teleport(candidate, ship.transform.rotation);
                Log.Info("PvP arena: moved " + FleetShips.Describe(ship) + " " + (100 * step) + " m out of the rocks.");
                return;
            }
            Log.Warn("PvP arena: " + FleetShips.Describe(ship) + " starts near rocks and no clear spot was found nearby.");
        }

        /// <summary>Whether a spot is closer than a team's own spacing to another ship (so a ship moved out of rocks doesn't land on a teammate).</summary>
        private static bool NearAnotherShip(PlayerControlledShip ship, Vector3 spot)
        {
            float clear = ShipSpawn.TeamSpacing * 0.8f;
            foreach (PlayerControlledShip other in FleetShips.All)
            {
                if (other != null && other != ship && (other.transform.position - spot).sqrMagnitude < clear * clear) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- the self-test's camera tour

        /// <summary>A salvage spot on the self-test's camera tour.</summary>
        internal sealed class TourSpot
        {
            public string Label;
            public GUIDUnion Prefab;
            public Vector3 Position;
            public int Team;
        }

        /// <summary>
        /// For the self-test's camera tour: the arena's name, centre and BLUE-to-RED axis, the supply drop's spot, and both
        /// sides' salvage spots (into spots, BLUE's then RED's of each kind). Null when the match has no arena.
        /// </summary>
        internal static string TourArena(out Vector3 centre, out Vector3 axis, out Vector3 dropSpot, List<TourSpot> spots)
        {
            Preset preset = _current;
            centre = preset != null ? preset.Centre : Vector3.zero;
            axis = Quaternion.Euler(0f, preset != null ? preset.Yaw : 0f, 0f) * Vector3.forward;
            dropSpot = preset != null ? preset.DropSpot : Vector3.zero;
            if (preset == null) return null;
            foreach (Poi poi in preset.Pois)
            {
                spots.Add(new TourSpot { Label = "BLUE " + SalvageName(poi.Prefab), Prefab = poi.Prefab, Position = poi.Position, Team = 0 });
                spots.Add(new TourSpot { Label = "RED " + SalvageName(poi.Prefab), Prefab = poi.Prefab, Position = Mirror(preset, poi.Position), Team = 1 });
            }
            GameSessionSector sector = GameSessionManager.ActiveSector;
            return preset.Id + " (" + preset.SolarSystem + ", sector " + (sector != null && sector.SectorAsset != null ? sector.SectorAsset.name : "?") + ")";
        }

        private static string SalvageName(GUIDUnion prefab)
        {
            if (prefab == SuppliesContainer) return "supplies container";
            if (prefab == GenericContainer) return "container";
            if (prefab == AmmoContainer) return "ammo container";
            if (prefab == PipeWreck) return "wreck";
            if (prefab == AsteroidBase) return "asteroid base";
            if (prefab == VoidFish) return "VoidFish";
            return prefab.AsHex();
        }

        /// <summary>Self-test, host: the next supply drop is due now (it arrives through a void jump a few seconds later).</summary>
        internal static void DropNowForTest()
        {
            GameSession session = GameSessionManager.ActiveSession;
            if (!PhotonNetwork.IsMasterClient || session == null || _current == null) return;
            SetNextDrop(unchecked(PhotonNetwork.ServerTimestamp - 1), session.GameSessionID);
        }

        /// <summary>For the self-test.</summary>
        public static string Describe()
        {
            GameSessionSector sector = GameSessionManager.ActiveSector;
            return (_current != null ? _current.Id : "none") + ", sector " + (sector != null && sector.SectorAsset != null ? sector.SectorAsset.name : "?") +
                   ", rocks near the centre " + (_current != null ? Physics.OverlapSphere(_current.Centre, 6000f, 1 << 10, QueryTriggerInteraction.Ignore).Length.ToString() : "-");
        }
    }
}
