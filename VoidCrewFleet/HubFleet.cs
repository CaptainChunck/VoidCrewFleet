using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using CG.Game.Player;
using CG.GameLoopStateMachine.GameStates;
using Client.Utils;
using ExitGames.Client.Photon;
using Gameplay.Hub;
using Gameplay.Quests;
using HarmonyLib;
using Photon.Pun;
using ResourceAssets;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>The fleet missions offered on the hub's galaxy map, on top of the game's own.</summary>
    internal enum HubVariant : byte
    {
        /// <summary>One of the game's own missions (with one to three ships, as the FLEET panel says).</summary>
        None = 0,

        /// <summary>The standard Pilgrimage for two or three ships: more enemies per sector.</summary>
        FleetPilgrimage = 1,

        /// <summary>PvP: two teams of one to three ships each.</summary>
        FleetBattle = 2
    }

    /// <summary>
    /// The fleet choices made in the hub, kept as room properties that only the host writes (other players ask the host
    /// through Net). The game's own ship selection (HubShipManager, and the Ship Customizer's CUSTOM on top of it) stays
    /// ship 1; ships 2-6 are this plugin's. The host also decides how many ships there are and may force each ship's type.
    /// When the host starts a mission the choices are frozen into the mission's FleetPlan (FleetSession), which reaches
    /// every client inside the game's session data.
    /// </summary>
    internal static class HubFleet
    {
        public const string Feature = "Two ships in the hub";

        public const int MaxCrewPerShip = FleetSlots.MaxCrewPerShip;

        private const string KeyOn = "VCF_On";
        private const string KeyCount = "VCF_Count";
        private const string KeyPerTeam = "VCF_PerTeam";
        private const string KeyVariant = "VCF_Var";
        private const string KeyMirror = "VCF_Mirror";
        private const string KeyType = "VCF_Type";
        private const string KeyLoad = "VCF_Load";
        private const string KeyLoadName = "VCF_LoadName";

        private static bool _applyingVariant;
        private static string _lastSignature;

        /// <summary>Raised (a few times a second at most) when any fleet choice of the room, the saved run's choice or the host changed.</summary>
        public static event Action Changed;

        /// <summary>Goes up by one each time Changed is raised: screens that redraw only on change compare it.</summary>
        public static int Revision { get; private set; }

        // ---------------------------------------------------------------- reading the room

        /// <summary>
        /// The host's own latest writes, for a few seconds: the room's properties only change here once the server has sent
        /// them back, and a choice is often read again right after it is made.
        /// </summary>
        private static readonly Dictionary<string, KeyValuePair<object, float>> HostLatest = new Dictionary<string, KeyValuePair<object, float>>();
        private static string _hostLatestRoom;

        private static bool TryGet(string key, out object value)
        {
            value = null;
            if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom == null) return false;
            KeyValuePair<object, float> latest;
            if (PhotonNetwork.IsMasterClient && PhotonNetwork.CurrentRoom.Name == _hostLatestRoom && HostLatest.TryGetValue(key, out latest) &&
                Time.unscaledTime - latest.Value < 3f)
            {
                value = latest.Key;
                return value != null;
            }
            return PhotonNetwork.CurrentRoom.CustomProperties != null && PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(key, out value) && value != null;
        }

        private static int GetInt(string key, int fallback)
        {
            object value;
            return TryGet(key, out value) && value is int ? (int)value : fallback;
        }

        private static GUIDUnion GetGuid(string key)
        {
            object value;
            if (!TryGet(key, out value) || !(value is string) || string.IsNullOrEmpty((string)value)) return GUIDUnion.Empty();
            try
            {
                return new GUIDUnion((string)value);
            }
            catch
            {
                return GUIDUnion.Empty();
            }
        }

        /// <summary>True when the room's host runs this plugin with the same network protocol (and its fleet choices exist).</summary>
        public static bool HostHasFleet
        {
            get
            {
                object value;
                return TryGet(KeyOn, out value) && value is int && (int)value == FleetPlan.Protocol;
            }
        }

        /// <summary>
        /// Whether fleet missions can run on this host: the patches they can't work without are in. After a game update that
        /// breaks one of them, missions fall back to one ship instead of a half-working fleet.
        /// </summary>
        public static bool FleetReady
        {
            get
            {
                return Patcher.IsReady(FleetSession.Feature) && Patcher.IsReady(FleetShips.Feature) && Patcher.IsReady(VoidJumpLink.Feature) &&
                       Patcher.IsReady(ShipIsolation.Feature) && Patcher.IsReady(ShipResources.Feature);
            }
        }

        public static HubVariant Variant
        {
            get
            {
                object value;
                return TryGet(KeyVariant, out value) && value is byte ? (HubVariant)(byte)value : HubVariant.None;
            }
        }

        /// <summary>How many ships the game's own missions are flown with (1-3): one unless someone chose more.</summary>
        public static int CoopShips
        {
            get { return Mathf.Clamp(GetInt(KeyCount, 1), 1, FleetSlots.MaxCoopShips); }
        }

        /// <summary>Fleet Battle: ships per team (1-3).</summary>
        public static int ShipsPerTeam
        {
            get { return Mathf.Clamp(GetInt(KeyPerTeam, 1), 1, FleetSlots.MaxShipsPerTeam); }
        }

        /// <summary>Fleet Battle: both teams' ship k have the same type (on unless the host turned it off).</summary>
        public static bool MirrorTypes
        {
            get
            {
                object value;
                return !TryGet(KeyMirror, out value) || !(value is bool) || (bool)value;
            }
        }

        /// <summary>
        /// Whether the hub's chosen start is the host's saved run. The game sends every client the host's choice of start, so
        /// everyone sees the same.
        /// </summary>
        public static bool SavedRunChosen
        {
            get
            {
                HubQuestManager hub = HubQuestManager.Instance;
                return hub != null && hub.QuestStartType == HubQuestManager.SessionStartType.Preserved;
            }
        }

        /// <summary>How the next mission will be played, from the room's choices. A saved run is one ship (the game's save holds one).</summary>
        public static FleetMode PlannedMode
        {
            get
            {
                if (!HostHasFleet || SavedRunChosen) return FleetMode.OneShip;
                switch (Variant)
                {
                    case HubVariant.FleetBattle:
                        return FleetMode.PvP;
                    case HubVariant.FleetPilgrimage:
                        return FleetMode.Coop;
                    default:
                        return CoopShips > 1 ? FleetMode.Coop : FleetMode.OneShip;
                }
            }
        }

        /// <summary>How many ships the next mission plans (before the room size raises it).</summary>
        public static int PlannedShips
        {
            get
            {
                switch (PlannedMode)
                {
                    case FleetMode.PvP:
                        return 2 * ShipsPerTeam;
                    case FleetMode.Coop:
                        return Mathf.Max(CoopShips, 2);
                    default:
                        return 1;
                }
            }
        }

        /// <summary>A ship's name for players: "SHIP 3", or "BLUE 2" / "RED 1" in Fleet Battle.</summary>
        public static string ShipLabel(int slot)
        {
            return FleetSlots.Name(slot, FleetApi.IsTeamMatch);
        }

        /// <summary>
        /// The ship type the host forces for a slot, or empty for any. In Fleet Battle every ship has one (until the host sets
        /// them, the team's k-th ship takes the terminal's k-th type).
        /// </summary>
        public static GUIDUnion ForcedType(int slot)
        {
            if (slot < 0 || slot >= FleetSlots.MaxSlots) return GUIDUnion.Empty();
            GUIDUnion type = GetGuid(KeyType + (slot + 1));
            if (type.IsNull && PlannedMode == FleetMode.PvP) type = FleetSlots.DefaultTeamType(slot / 2 + 1);
            return type;
        }

        /// <summary>A loadout by GUID: the game's, or the Ship Customizer's CUSTOM; null (without the game's error log) when neither knows it.</summary>
        public static ShipLoadoutDataDef LoadoutDef(GUIDUnion guid)
        {
            if (guid.IsNull) return null;
            ShipLoadoutDataDef def = null;
            try
            {
                def = ResourceAssetContainer<ShipLoadoutDataContainer, ShipLoadoutData, ShipLoadoutDataDef>.Instance.GetAssetDefById(guid, false);
            }
            catch
            {
                // Not the game's: ask the Customizer.
            }
            return def != null ? def : CustomizerLink.GetDef(guid);
        }

        /// <summary>The type of a loadout, CUSTOM included (a CUSTOM loadout has its stand-in's type).</summary>
        public static GUIDUnion TypeOf(ShipLoadoutDataDef def)
        {
            if (def == null) return GUIDUnion.Empty();
            GUIDUnion type = FleetSlots.TypeOf(def);
            if (!type.IsNull) return type;
            ShipLoadoutDataDef standIn = CustomizerLink.GetStandIn(def.AssetGuid);
            return standIn != null ? FleetSlots.TypeOf(standIn) : GUIDUnion.Empty();
        }

        /// <summary>Ship 1's loadout as chosen at the terminal (the Customizer's CUSTOM included).</summary>
        public static ShipLoadoutDataDef ShipADef
        {
            get
            {
                HubShipManager manager = HubShipManager.Instance;
                if (manager == null) return null;
                ShipLoadoutDataDef def = manager.CurrentShipSelected;
                return def != null ? def : LoadoutDef(manager.SelectedShipGuid);
            }
        }

        /// <summary>The loadout chosen for a ship 2-6; empty when nobody chose one.</summary>
        public static GUIDUnion ChosenLoadout(int slot)
        {
            return slot >= 1 && slot < FleetSlots.MaxSlots ? GetGuid(KeyLoad + (slot + 1)) : GUIDUnion.Empty();
        }

        /// <summary>A ship 2-6's chosen loadout as the host described it, for players who can't look it up (CUSTOM without the Customizer).</summary>
        public static string ChosenName(int slot)
        {
            object value;
            return slot >= 1 && TryGet(KeyLoadName + (slot + 1), out value) ? value as string : null;
        }

        /// <summary>
        /// Whether a ship 2-6 with nothing chosen copies ship 1 (co-op, when its type allows ship 1's); otherwise it gets its
        /// forced type's default loadout.
        /// </summary>
        public static bool CopiesShip1(int slot)
        {
            if (slot < 1 || PlannedMode == FleetMode.PvP || !ChosenLoadout(slot).IsNull) return false;
            GUIDUnion type = ForcedType(slot);
            return type.IsNull || type == TypeOf(ShipADef);
        }

        /// <summary>For the terminal and the lobby: what a ship spawns with if the mission started now.</summary>
        public static ShipLoadoutDataDef ShipDef(int slot)
        {
            if (slot <= 0) return ShipADef;
            GUIDUnion chosen = ChosenLoadout(slot);
            if (!chosen.IsNull)
            {
                ShipLoadoutDataDef def = LoadoutDef(chosen);
                if (def != null) return def;
            }
            if (CopiesShip1(slot)) return ShipADef;
            GUIDUnion type = ForcedType(slot);
            return FleetSlots.DefaultLoadout(type.IsNull ? TypeOf(ShipADef) : type);
        }

        /// <summary>A ship's name for the FLEET panel and lobby, as the host saw it when the def isn't known here.</summary>
        public static string ShipDescription(int slot)
        {
            ShipLoadoutDataDef def = ShipDef(slot);
            if (def != null) return DescribeLoadout(def);
            string name = ChosenName(slot);
            return string.IsNullOrEmpty(name) ? "-" : name;
        }

        // ---------------------------------------------------------------- asking the host

        /// <summary>Host only: how many ships the game's own missions use.</summary>
        public static void RequestShipCount(int ships)
        {
            Net.ToHost(Msg.HubShipCount, ships);
        }

        public static void RequestLoadout(int slot, GUIDUnion loadout)
        {
            Net.ToHost(Msg.HubShipLoadout, slot, loadout.AsHex());
        }

        /// <summary>Host only: force a ship's type (empty: any type, co-op only).</summary>
        public static void RequestType(int slot, GUIDUnion type)
        {
            Net.ToHost(Msg.HubShipType, slot, type.AsHex());
        }

        /// <summary>Host only: Fleet Battle's ships per team.</summary>
        public static void RequestShipsPerTeam(int ships)
        {
            Net.ToHost(Msg.HubShipsPerTeam, ships);
        }

        /// <summary>Host only: Fleet Battle's "both teams get the same ship types".</summary>
        public static void RequestMirror(bool on)
        {
            Net.ToHost(Msg.HubMirror, on);
        }

        public static void RequestVariant(HubVariant variant)
        {
            Net.ToHost(Msg.HubVariant, (byte)variant);
        }

        /// <summary>Message handlers; called once at start-up.</summary>
        public static void Listen()
        {
            Net.On(Msg.HubShipCount, (sender, data) =>
            {
                if (!HostInHub() || !Net.FromHost(sender)) return;
                int asked = Net.Get(data, 0, 1);
                int ships = Mathf.Clamp(asked, 1, FleetSlots.MaxCoopShips);
                if (Variant == HubVariant.FleetBattle) return;
                if (ships < 2 && Variant == HubVariant.FleetPilgrimage)
                {
                    Notice.Show("The Fleet Pilgrimage needs at least two ships. Choose one of the game's own missions on the galaxy map to fly with one ship.");
                    ships = 2;
                }
                // The host's default for new rooms only follows a count they asked for, not one changed here.
                SetCoopShips(ships, ships == asked);
            });
            Net.On(Msg.HubShipLoadout, (sender, data) =>
            {
                if (!HostInHub()) return;
                int slot = Net.Get(data, 0, -1);
                string hex = Net.Get<string>(data, 1, null);
                if (slot < 1 || slot >= PlannedShips || string.IsNullOrEmpty(hex)) return;
                GUIDUnion guid;
                try
                {
                    guid = new GUIDUnion(hex);
                }
                catch
                {
                    return;
                }
                string refusal = CheckLoadout(slot, guid, sender);
                if (refusal != null)
                {
                    Net.ToActor(sender, Msg.Notice, refusal);
                    return;
                }
                ShipLoadoutDataDef def = LoadoutDef(guid);
                SetLoadout(slot, guid, def);
                Log.Info(ShipLabel(slot) + " is now " + DescribeLoadout(def) + ".");
            });
            Net.On(Msg.HubShipType, (sender, data) =>
            {
                if (!HostInHub() || !Net.FromHost(sender)) return;
                int slot = Net.Get(data, 0, -1);
                string hex = Net.Get<string>(data, 1, "");
                if (slot < 0 || slot >= FleetSlots.MaxSlots) return;
                GUIDUnion type = GUIDUnion.Empty();
                if (!string.IsNullOrEmpty(hex))
                {
                    try
                    {
                        type = new GUIDUnion(hex);
                    }
                    catch
                    {
                        return;
                    }
                    if (!FleetSlots.Types.Contains(type)) return;
                }
                if (type.IsNull && Variant == HubVariant.FleetBattle) return;
                SetType(slot, type);
                if (Variant == HubVariant.FleetBattle && MirrorTypes) SetType(slot ^ 1, type);
                SaveTypes();
            });
            Net.On(Msg.HubShipsPerTeam, (sender, data) =>
            {
                if (!HostInHub() || !Net.FromHost(sender) || Variant != HubVariant.FleetBattle) return;
                SetShipsPerTeam(Mathf.Clamp(Net.Get(data, 0, 1), 1, FleetSlots.MaxShipsPerTeam));
            });
            Net.On(Msg.HubMirror, (sender, data) =>
            {
                if (!HostInHub() || !Net.FromHost(sender)) return;
                bool on = Net.Get(data, 0, true);
                Set(KeyMirror, on);
                if (on)
                {
                    // RED's ships take BLUE's types.
                    for (int slot = 0; slot < FleetSlots.MaxSlots; slot += 2) SetType(slot + 1, ForcedType(slot));
                    SaveTypes();
                }
            });
            Net.On(Msg.HubVariant, (sender, data) =>
            {
                if (!HostInHub()) return;
                var variant = (HubVariant)Net.Get(data, 0, (byte)0);
                if (variant != HubVariant.None && variant != HubVariant.FleetPilgrimage && variant != HubVariant.FleetBattle) return;
                // Activating the mission that is already chosen changes nothing (it used to reopen confirmed teams).
                if (variant == Variant) return;
                SelectVariantAsHost(variant);
            });
        }

        /// <summary>Host: null when this player may give a ship 2-6 this loadout now; otherwise why not.</summary>
        private static string CheckLoadout(int slot, GUIDUnion guid, int sender)
        {
            ShipLoadoutDataDef def = LoadoutDef(guid);
            if (def == null || def.Asset == null)
            {
                Log.Warn("Ignored a " + ShipLabel(slot) + " choice neither the game nor the Ship Customizer knows (" + guid.AsHex() + ").");
                return CustomizerLink.Installed && !CustomizerLink.CanCustomize(slot + 1)
                    ? ShipLabel(slot) + " can't have a CUSTOM loadout of its own with this version of Ship Customizer: update it."
                    : ShipLabel(slot) + " can't use that loadout.";
            }
            if (Variant == HubVariant.FleetBattle && FleetShips.TeamOfActor(sender) != FleetSlots.TeamOf(slot, true))
            {
                string team = PvpTeams.TeamName(FleetSlots.TeamOf(slot, true));
                return "Only " + team + " players can change the " + ShipLabel(slot) + " ship.";
            }
            GUIDUnion type = ForcedType(slot);
            if (!type.IsNull && TypeOf(def) != type)
                return ShipLabel(slot) + " must be a " + FleetSlots.TypeName(type) + " (the host's choice).";
            if (CustomizerLink.IsCustom(guid)) return CustomizerLink.CheckShip(slot + 1, guid, sender);
            return null;
        }

        /// <summary>Host: a ship 2-6 uses this loadout (a game loadout, or CUSTOM already checked); the Customizer shares its layout.</summary>
        private static void SetLoadout(int slot, GUIDUnion guid, ShipLoadoutDataDef def)
        {
            SetLoadoutKeys(slot + 1, guid.IsNull ? null : guid.AsHex(), def != null ? DescribeLoadout(def) : null);
            CustomizerLink.SetShip(slot + 1, guid);
        }

        /// <summary>Host: ship n's (2-6) loadout and its name, in one room update so nobody sees one without the other.</summary>
        private static void SetLoadoutKeys(int ship, string hex, string name)
        {
            Set(new[] { KeyLoad + ship, KeyLoadName + ship }, new object[] { hex, name });
        }

        /// <summary>
        /// Host: the game's own missions are flown with this many ships. Confirmed crews are picked again. Remembered as the
        /// host's default for new rooms when they chose it themselves (not a minimum a mission needs).
        /// </summary>
        private static void SetCoopShips(int ships, bool remember)
        {
            int before = CoopShips;
            Set(KeyCount, ships);
            if (ships != before && PvpTeams.Locked && Variant != HubVariant.FleetBattle)
            {
                PvpTeams.Reset();
                if (ships > 1) Notice.ShowAll("The fleet now has " + ships + " ships: pick your ships again.");
            }
            if (remember && Settings.Ships != null) Settings.Ships.Value = ships;
            // Players who picked a ship that is gone pick again (or are placed at the start).
            Dictionary<int, CrewEntry> crews = FleetShips.Crews();
            bool changed = false;
            foreach (CrewEntry entry in crews.Values.ToList())
            {
                if (entry.Slot < ships) continue;
                crews.Remove(entry.Actor);
                changed = true;
            }
            if (changed) FleetShips.PublishCrew(crews);
            Log.Info("Ships: " + ships + ".");
        }

        /// <summary>Host: a ship's forced type; a ship whose loadout is of another type gets that type's default.</summary>
        private static void SetType(int slot, GUIDUnion type)
        {
            if (slot < 0 || slot >= FleetSlots.MaxSlots) return;
            Set(KeyType + (slot + 1), type.IsNull ? null : type.AsHex());
            if (type.IsNull) return;
            if (slot == 0)
            {
                ShipLoadoutDataDef current = ShipADef;
                if (current != null && TypeOf(current) == type) return;
                ShipLoadoutDataDef standard = FleetSlots.DefaultLoadout(type);
                if (standard == null || HubShipManager.Instance == null) return;
                try
                {
                    HubShipManager.Instance.SelectShip(standard.AssetGuid.AsIntArray());
                }
                catch (Exception e)
                {
                    Log.Failure("Giving ship 1 the forced type's loadout", e);
                }
                return;
            }
            GUIDUnion chosen = ChosenLoadout(slot);
            if (chosen.IsNull) return;
            ShipLoadoutDataDef def = LoadoutDef(chosen);
            if (def != null && TypeOf(def) == type) return;
            // Back to "nothing chosen", which is the type's default loadout (or a copy of ship 1 of the same type).
            SetLoadout(slot, GUIDUnion.Empty(), null);
        }

        /// <summary>Host: Fleet Battle's ships per team. Players on a ship that is gone move to their team's first ship.</summary>
        private static void SetShipsPerTeam(int ships)
        {
            Set(KeyPerTeam, ships);
            if (Settings.PvpShipsPerTeam != null) Settings.PvpShipsPerTeam.Value = ships;
            Dictionary<int, CrewEntry> crews = FleetShips.Crews();
            bool changed = false;
            foreach (CrewEntry entry in crews.Values.ToList())
            {
                if (entry.Slot < 2 * ships) continue;
                crews[entry.Actor] = new CrewEntry { Actor = entry.Actor, Team = entry.Team, Slot = entry.Team == 1 ? 1 : 0, State = CrewState.Playing };
                changed = true;
            }
            if (changed) FleetShips.PublishCrew(crews);
            Log.Info("Fleet Battle: " + ships + " ships per team.");
        }

        /// <summary>Host: remember the Fleet Battle ship types for the next lobby.</summary>
        private static void SaveTypes()
        {
            if (Variant != HubVariant.FleetBattle || Settings.PvpShipTypes == null) return;
            var hexes = new string[FleetSlots.MaxSlots];
            for (int slot = 0; slot < hexes.Length; slot++) hexes[slot] = GetGuid(KeyType + (slot + 1)).AsHex();
            Settings.PvpShipTypes.Value = string.Join(",", hexes);
        }

        /// <summary>Host, when FLEET BATTLE is chosen: the last ships per team and ship types, else the defaults.</summary>
        private static void PublishSavedFleetBattle()
        {
            int perTeam = Settings.PvpShipsPerTeam != null ? Mathf.Clamp(Settings.PvpShipsPerTeam.Value, 1, FleetSlots.MaxShipsPerTeam) : 1;
            Set(KeyPerTeam, perTeam);
            string[] saved = Settings.PvpShipTypes != null && !string.IsNullOrEmpty(Settings.PvpShipTypes.Value) ? Settings.PvpShipTypes.Value.Split(',') : new string[0];
            List<GUIDUnion> types = FleetSlots.Types;
            for (int slot = 0; slot < FleetSlots.MaxSlots; slot++)
            {
                GUIDUnion type = GUIDUnion.Empty();
                if (slot < saved.Length && !string.IsNullOrEmpty(saved[slot]))
                {
                    try
                    {
                        type = new GUIDUnion(saved[slot]);
                    }
                    catch
                    {
                        type = GUIDUnion.Empty();
                    }
                }
                if (type.IsNull || !types.Contains(type)) type = FleetSlots.DefaultTeamType(slot / 2 + 1);
                SetType(slot, type);
            }
        }

        /// <summary>
        /// Host: the Ship Customizer switched a ship's CUSTOM off in the hub, because someone without it joined. That ship
        /// takes the game loadout CUSTOM is built on.
        /// </summary>
        public static void ShipCustomCancelled(int ship)
        {
            try
            {
                int slot = ship - 1;
                if (!HostInHub() || slot < 1) return;
                GUIDUnion chosen = ChosenLoadout(slot);
                if (chosen.IsNull || !CustomizerLink.IsCustom(chosen)) return;
                ShipLoadoutDataDef standIn = CustomizerLink.GetStandIn(chosen);
                SetLoadoutKeys(ship, standIn != null ? standIn.AssetGuid.AsHex() : null, standIn != null ? DescribeLoadout(standIn) : null);
                Log.Info(ShipLabel(slot) + "'s CUSTOM loadout was switched off by Ship Customizer; it is now " +
                         (standIn != null ? DescribeLoadout(standIn) : "back to its default") + ".");
            }
            catch (Exception e)
            {
                Log.Failure("Switching a ship away from CUSTOM", e);
            }
        }

        // ---------------------------------------------------------------- starting from the ready room

        private static readonly FieldInfo ZoneChairsField = GameMember.Field(typeof(CG.Client.Quests.Behaviours.QuestStartZone), "takeoverChairs");
        private static CG.Client.Quests.Behaviours.QuestStartZone _zone;
        private static TakeoverChair[] _zoneChairs;
        private static float _lastSeatNotice = -100f;

        /// <summary>
        /// Co-op with several ships: nothing starts before the host has confirmed the crews; then the game's own countdown
        /// once everyone is seated.
        /// Host, FLEET BATTLE: the teams are already picked, so the host sitting in the ready room starts the match (the
        /// game's short countdown for "everyone is seated"); nobody else's seat counts. Before the teams are confirmed,
        /// sitting down starts nothing. Clients don't run their own countdown (it would count their own seat): they show the
        /// host's, which the game sends them.
        /// </summary>
        [GamePatch(Feature, "Gameplay.Quests.QuestStartProcess", "UpdateQuestProcess", PatchKind.Prefix)]
        private static bool StartCountdownPrefix(ref int playersInZoneCount)
        {
            if (!HostHasFleet) return true;
            if (Variant != HubVariant.FleetBattle)
            {
                // Co-op with several ships: the game's own countdown (everyone seated), once the host has confirmed the crews.
                if (PlannedMode != FleetMode.Coop || PvpTeams.Locked) return true;
                playersInZoneCount = 0;
                try
                {
                    if (PhotonNetwork.IsMasterClient && Time.unscaledTime - _lastSeatNotice > 10f && HostSeatedInReadyRoom())
                    {
                        _lastSeatNotice = Time.unscaledTime;
                        Notice.Show("Confirm the crews first (CONFIRM CREWS on the crew screen; CREWS on the FLEET panel brings it back); then sit down here to start.");
                    }
                }
                catch (Exception e)
                {
                    Log.Failure("Holding the co-op start for the crews", e);
                }
                return true;
            }
            if (!PhotonNetwork.IsMasterClient) return false;
            try
            {
                if (!PvpTeams.Locked)
                {
                    playersInZoneCount = 0;
                    if (Time.unscaledTime - _lastSeatNotice > 10f && HostSeatedInReadyRoom())
                    {
                        _lastSeatNotice = Time.unscaledTime;
                        Notice.Show("Confirm the teams first (CONFIRM TEAMS on the team screen); then sit down here to start the match.");
                    }
                    return true;
                }
                playersInZoneCount = HostSeatedInReadyRoom() && PhotonNetwork.CurrentRoom != null ? Mathf.Max(1, (int)PhotonNetwork.CurrentRoom.PlayerCount) : 0;
            }
            catch (Exception e)
            {
                Log.Failure("Starting Fleet Battle from the ready room", e);
            }
            return true;
        }

        /// <summary>Whether the local player sits in one of the ready room's seats.</summary>
        private static bool HostSeatedInReadyRoom()
        {
            LocalPlayer me = LocalPlayer.Instance;
            if (me == null || me.Locomotion == null) return false;
            var sitting = me.Locomotion.GetAbility<SittingInChair>();
            if (sitting == null || !sitting.IsActive) return false;
            TakeoverChair chair = sitting.Chair;
            if (chair == null) return false;
            if (_zone == null)
            {
                // Looked up once per hub, with its seats.
                _zone = UnityEngine.Object.FindObjectOfType<CG.Client.Quests.Behaviours.QuestStartZone>();
                _zoneChairs = _zone != null && ZoneChairsField != null ? ZoneChairsField.GetValue(_zone) as TakeoverChair[] : null;
            }
            if (_zoneChairs == null) return false;
            for (int i = 0; i < _zoneChairs.Length; i++)
            {
                if (ReferenceEquals(_zoneChairs[i], chair)) return true;
            }
            return false;
        }

        private static bool HostInHub()
        {
            try
            {
                return PhotonNetwork.IsMasterClient && GameSessionManager.InHub;
            }
            catch
            {
                return false;
            }
        }

        private static void Set(string key, object value)
        {
            Set(new[] { key }, new[] { value });
        }

        /// <summary>Host: several choices in one room update (values by position; null removes one).</summary>
        private static void Set(string[] keys, object[] values)
        {
            try
            {
                if (!PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
                if (PhotonNetwork.CurrentRoom.Name != _hostLatestRoom)
                {
                    HostLatest.Clear();
                    _hostLatestRoom = PhotonNetwork.CurrentRoom.Name;
                }
                var changes = new Hashtable();
                for (int i = 0; i < keys.Length; i++)
                {
                    HostLatest[keys[i]] = new KeyValuePair<object, float>(values[i], Time.unscaledTime);
                    changes[keys[i]] = values[i];
                }
                PhotonNetwork.CurrentRoom.SetCustomProperties(changes);
            }
            catch (Exception e)
            {
                Log.Failure("Updating the room's fleet choices", e);
            }
        }

        /// <summary>Host: a fleet mission is chosen on the galaxy map (it is the standard Pilgrimage underneath).</summary>
        private static void SelectVariantAsHost(HubVariant variant)
        {
            HubVariant before = Variant;
            Set(KeyVariant, (byte)variant);
            if (before == HubVariant.FleetBattle || variant == HubVariant.FleetBattle)
            {
                // Ship picks mean other ships (and teams) in the other kind of mission: everyone picks again, and the lobby
                // screen opens on every client when the new mission has several ships.
                FleetShips.ClearCrews();
                ClearTypes();
                PvpTeams.Reset();
            }
            if (variant == HubVariant.None) return;
            if (variant == HubVariant.FleetBattle)
            {
                // Under the host's last ship rules, ships per team and ship types.
                ShipRules.PublishSaved();
                PublishSavedFleetBattle();
            }
            else if (CoopShips < 2)
            {
                // The Fleet Pilgrimage's minimum, not the host's choice: their default ship count stays as it was.
                SetCoopShips(2, false);
            }
            try
            {
                _applyingVariant = true;
                QuestAssetRef endless = DataTable<QuestTable>.Instance.EndlessQuest;
                if (HubQuestManager.Instance != null && endless != null) HubQuestManager.Instance.SelectQuest(endless.AssetGuid);
            }
            finally
            {
                _applyingVariant = false;
            }
            Log.Info("Mission: " + (variant == HubVariant.FleetBattle ? "Fleet Battle (PvP)" : "Fleet Pilgrimage (several ships, more enemies)") + ".");
        }

        /// <summary>Host: no forced types (co-op starts free; Fleet Battle sets its own).</summary>
        private static void ClearTypes()
        {
            for (int slot = 0; slot < FleetSlots.MaxSlots; slot++) Set(KeyType + (slot + 1), null);
        }

        public static string DescribeLoadout(ShipLoadoutDataDef def)
        {
            if (def == null) return "none";
            try
            {
                string ship = def.ShipContextInfo != null ? def.ShipContextInfo.HeaderText : "ship";
                string loadout = def.ContextInfo != null ? def.ContextInfo.HeaderText : "";
                return string.IsNullOrEmpty(loadout) ? ship : ship + " - " + loadout;
            }
            catch
            {
                return "ship";
            }
        }

        // ---------------------------------------------------------------- the hub

        /// <summary>The hub loaded: the host (re)publishes that it runs the plugin, and a fleet mission choice is reset.</summary>
        [GamePatch(Feature, "HubShipManager", "Start", PatchKind.Postfix)]
        private static void HubStartPostfix()
        {
            try
            {
                if (!PhotonNetwork.IsMasterClient) return;
                if (!FleetReady)
                {
                    // Clients then treat the room as one without the plugin's fleet features.
                    Set(KeyOn, 0);
                    Log.WarnOnce("fleet not ready", "Some of the patches several ships need didn't apply (a game update?), so missions use one ship.");
                    return;
                }
                Set(KeyOn, FleetPlan.Protocol);
                // The host's last ship count (one for a new player); later changes in this room are kept as they are.
                object count;
                if (!TryGet(KeyCount, out count)) Set(KeyCount, Settings.Ships != null ? Mathf.Clamp(Settings.Ships.Value, 1, FleetSlots.MaxCoopShips) : 1);
                if (Variant != HubVariant.None)
                {
                    if (Variant == HubVariant.FleetBattle) ClearTypes();
                    Set(KeyVariant, (byte)HubVariant.None);
                }
                // The Customizer forgets its ship choices between missions; tell it again what the room's ships use.
                TellCustomizerShips();
                FleetShips.ClearRoom();
                PvpRoster.ClearRoom();
                PvpTeams.Reset();
            }
            catch (Exception e)
            {
                Log.Failure("Publishing the room's fleet choices", e);
            }
        }

        /// <summary>Host: tells the Ship Customizer which loadout each ship 2-6 with a choice uses (CUSTOM shares the host's layout).</summary>
        private static void TellCustomizerShips()
        {
            for (int slot = 1; slot < FleetSlots.MaxSlots; slot++)
            {
                GUIDUnion chosen = ChosenLoadout(slot);
                if (!chosen.IsNull) CustomizerLink.SetShip(slot + 1, chosen);
            }
        }

        /// <summary>Host: anyone choosing a mission on the galaxy map (other than through a fleet entry) ends a fleet mission choice.</summary>
        [GamePatch(Feature, "Gameplay.Hub.HubQuestManager", "NotifyNewQuestSelected", PatchKind.Postfix)]
        private static void QuestSelectedPostfix()
        {
            try
            {
                if (_applyingVariant || !PhotonNetwork.IsMasterClient || Variant == HubVariant.None) return;
                SelectVariantAsHost(HubVariant.None);
            }
            catch (Exception e)
            {
                Log.Failure("Switching from a fleet mission to another mission", e);
            }
        }

        /// <summary>
        /// Host: choosing the saved run ends a fleet mission choice too (the game picks it through its own notification). A
        /// saved run has one ship, so the lobby screen closes and the start isn't held for crews (PlannedMode).
        /// </summary>
        [GamePatch(Feature, "Gameplay.Hub.HubQuestManager", "NotifyPreservedQuestSelected", PatchKind.Postfix, Optional = true)]
        private static void PreservedSelectedPostfix()
        {
            try
            {
                if (!PhotonNetwork.IsMasterClient || Variant == HubVariant.None) return;
                SelectVariantAsHost(HubVariant.None);
            }
            catch (Exception e)
            {
                Log.Failure("Switching from a fleet mission to the saved run", e);
            }
        }

        /// <summary>
        /// Host, just before the game leaves the hub for a mission: freeze the room's fleet choices into the mission. A co-op
        /// mission with more players than its ships' crews take adds ships (and evens out the crews over them). Saved runs are
        /// always one-ship missions (the game's save holds one ship).
        /// </summary>
        [HarmonyAfter(CustomizerLink.PluginGuid)]
        [GamePatch(Feature, "CG.GameLoopStateMachine.GameStates.GSIngame", "TransitionTo", PatchKind.Prefix)]
        private static void TransitionToPrefix(GameSession gameSession, PreservedGameSession preservedGameSession)
        {
            try
            {
                if (!PhotonNetwork.IsMasterClient || gameSession == null || gameSession.IsHub) return;
                if (preservedGameSession != null)
                {
                    FleetSession.Attach(gameSession, null);
                    if (HostHasFleet && CoopShips > 1) Notice.ShowAll("Saved runs continue with one ship: the game's save holds only one.");
                    return;
                }

                FleetMode mode = PlannedMode;
                int ships = PlannedShips;
                int players = PhotonNetwork.PlayerList.Length;
                if (mode != FleetMode.PvP && HostHasFleet)
                {
                    int needed = Mathf.Min(FleetSlots.MaxCoopShips, Mathf.CeilToInt(players / (float)MaxCrewPerShip));
                    if (needed > ships)
                    {
                        Notice.ShowAll(players + " players is more than " + ships + (ships == 1 ? " ship's" : " ships'") + " crews of " + MaxCrewPerShip +
                                       ", so this mission uses " + needed + " ships.");
                        ships = needed;
                        mode = FleetMode.Coop;
                        EvenOutCrews(ships);
                    }
                }
                if (mode == FleetMode.OneShip)
                {
                    FleetSession.Attach(gameSession, null);
                    return;
                }

                var plan = new FleetPlan
                {
                    Mode = mode,
                    ScaleEnemies = Variant == HubVariant.FleetPilgrimage,
                    HackCost = Settings.HackTerminalCost.Value,
                    Arena = Settings.PvpArena.Value,
                    HullMultiplier = mode == FleetMode.PvP ? Mathf.Clamp(Settings.PvpHullMultiplier.Value, 1f, 10f) : 1f
                };
                bool pvp = mode == FleetMode.PvP;
                plan.Ships.Add(PrepareShip1(gameSession, pvp));
                for (int slot = 1; slot < ships; slot++)
                {
                    ShipPlan ship = PrepareShip(gameSession, slot, pvp);
                    if (ship == null)
                    {
                        if (pvp)
                        {
                            // A Fleet Battle needs every ship: start as a normal mission instead of an uneven match.
                            FleetSession.Attach(gameSession, null);
                            Notice.ShowAll(ShipLabel(slot) + " could not be prepared, so this starts as a one-ship mission (details in the host's BepInEx log).");
                            return;
                        }
                        Notice.ShowAll("Ship " + (slot + 1) + " could not be prepared, so this mission starts with " + slot + (slot == 1 ? " ship." : " ships."));
                        break;
                    }
                    plan.Ships.Add(ship);
                }
                if (plan.Ships.Count < 2)
                {
                    FleetSession.Attach(gameSession, null);
                    return;
                }
                FleetSession.Attach(gameSession, plan);
                var names = new StringBuilder();
                foreach (ShipPlan ship in plan.Ships) names.Append(names.Length > 0 ? "; " : "").Append(FleetSlots.Name(ship.Slot, pvp)).Append(": ").Append(ship.Name);
                Log.Info("Starting a fleet mission: " + plan + ". " + names + ".");
            }
            catch (Exception e)
            {
                FleetSession.Attach(gameSession, null);
                Log.Failure("Preparing the fleet mission (it starts with one ship instead)", e);
                Notice.ShowAll("The fleet mission couldn't be prepared, so it starts with one ship (details in the host's BepInEx log).");
            }
        }

        /// <summary>
        /// Host, when the mission adds ships for the room's size: the crews are evened out over every ship, so an added ship
        /// doesn't fly empty and no crew is over the limit. Players who move to another ship than they picked are told.
        /// </summary>
        private static void EvenOutCrews(int ships)
        {
            Dictionary<int, CrewEntry> picks = FleetShips.Crews();
            Dictionary<int, CrewEntry> crews = CrewRules.Balance(picks, ShipSpawn.PlayerActors(), ships, false);
            FleetShips.PublishCrew(crews);
            foreach (CrewEntry entry in crews.Values)
            {
                CrewEntry pick;
                if (picks.TryGetValue(entry.Actor, out pick) && pick.Slot >= 0 && pick.Slot != entry.Slot)
                    Net.ToActor(entry.Actor, Msg.Notice, "You fly " + ShipLabel(entry.Slot) + " in this mission: the crews were evened out over the added ship.");
            }
        }

        /// <summary>
        /// Ship 1 is the game's own ship. When the host forced a type and ship 1's loadout isn't of it (a SELECT that slipped
        /// through), it starts with that type's default loadout instead.
        /// </summary>
        private static ShipPlan PrepareShip1(GameSession session, bool pvp)
        {
            var ship = new ShipPlan { Slot = 0, Team = 0, Type = ForcedType(0) };
            ShipLoadoutDataDef def = LoadoutDef(session.ToLoadShipGuid) ?? ShipADef;
            if (!ship.Type.IsNull && def != null && TypeOf(def) != ship.Type)
            {
                ShipLoadoutDataDef standard = FleetSlots.DefaultLoadout(ship.Type);
                if (standard != null && standard.Asset != null && standard.Asset.ShipLoadout != null)
                {
                    session.ToLoadShipGuid = standard.AssetGuid;
                    session.ToLoadShipData = ShipLoadout.FromJObject(standard.Asset.ShipLoadout.AsJObject());
                    Notice.ShowAll(ShipLabel(0) + " must be a " + FleetSlots.TypeName(ship.Type) + ", so it starts with " + DescribeLoadout(standard) + ".");
                    def = standard;
                }
            }
            ship.Loadout = session.ToLoadShipGuid;
            ship.Name = DescribeLoadout(def);
            return ship;
        }

        /// <summary>
        /// A ship 2-6's loadout for this mission: the one chosen for it when the mission's loadout rules allow it (a CUSTOM one
        /// is built by the Ship Customizer); else, in co-op when its type allows ship 1's, a copy of exactly what ship 1 spawns
        /// with (ship 1's CUSTOM included: this prefix runs after the Customizer's); else its type's default loadout. Null when
        /// nothing can be used.
        /// </summary>
        private static ShipPlan PrepareShip(GameSession session, int slot, bool pvp)
        {
            var ship = new ShipPlan { Slot = slot, Team = FleetSlots.TeamOf(slot, pvp), Type = ForcedType(slot) };
            ShipLoadoutDataDef chosen = LoadoutDef(ChosenLoadout(slot));
            if (chosen != null && !ship.Type.IsNull && TypeOf(chosen) != ship.Type) chosen = null;
            if (chosen != null && !AllowedByQuest(session, chosen))
            {
                Notice.ShowAll("This mission only allows certain loadouts, so " + ShipLabel(slot) + " doesn't get " + DescribeLoadout(chosen) + ".");
                chosen = null;
            }
            if (chosen != null && CustomizerLink.IsCustom(chosen.AssetGuid))
            {
                ShipLoadout custom = CustomizerLink.BuildShip(slot + 1, chosen.AssetGuid);
                if (custom != null)
                {
                    ship.Loadout = chosen.AssetGuid;
                    ship.Name = DescribeLoadout(chosen);
                    ship.Built = custom;
                    // Its CUSTOM starting alloys, or the game's own amount when it has none set.
                    ship.StartAlloys = Math.Max(-1, CustomizerLink.GetShipAlloys(slot + 1, chosen.AssetGuid));
                    ship.Homunculus = HomunculusOf(custom);
                    return ship;
                }
                // CUSTOM can't be used for this mission (the Customizer said why): its game loadout instead.
                chosen = CustomizerLink.GetStandIn(chosen.AssetGuid);
            }

            ShipLoadoutDataDef shipA = LoadoutDef(session.ToLoadShipGuid) ?? ShipADef;
            GUIDUnion shipAType = TypeOf(shipA);
            if (chosen == null && (pvp || (!ship.Type.IsNull && ship.Type != shipAType)))
            {
                ShipLoadoutDataDef standard = FleetSlots.DefaultLoadout(ship.Type.IsNull ? shipAType : ship.Type);
                if (standard != null && AllowedByQuest(session, standard)) chosen = standard;
                else if (standard != null) Notice.ShowAll("This mission only allows certain loadouts, so " + ShipLabel(slot) + " is a copy of ship 1.");
            }
            if (chosen != null && chosen.Asset != null && chosen.Asset.ShipLoadout != null)
            {
                ship.Loadout = chosen.AssetGuid;
                ship.Name = DescribeLoadout(chosen);
                ship.Built = ShipLoadout.FromJObject(chosen.Asset.ShipLoadout.AsJObject());
                ship.StartAlloys = -1;
                ship.Homunculus = HomunculusOf(ship.Built);
                return ship;
            }
            if (session.ToLoadShipData == null) return null;
            // A copy of ship 1: spawned from the very ShipLoadout ship 1 is spawned from.
            ship.Loadout = GUIDUnion.Empty();
            ship.Name = "a copy of ship 1 (" + DescribeLoadout(shipA) + ")";
            ship.Built = null;
            ship.StartAlloys = -2;
            ship.Homunculus = HomunculusOf(session.ToLoadShipData);
            return ship;
        }

        private static GUIDUnion HomunculusOf(ShipLoadout loadout)
        {
            return loadout != null && loadout.HomunculusOverride != null && !loadout.HomunculusOverride.IsNull ? loadout.HomunculusOverride.AssetGuid : GUIDUnion.Empty();
        }

        /// <summary>Whether the mission's loadout rules (challenges, Survivor) allow a loadout; CUSTOM is never on such a list.</summary>
        private static bool AllowedByQuest(GameSession session, ShipLoadoutDataDef chosen)
        {
            try
            {
                QuestAsset quest = session.SessionQuestAsset != null ? session.SessionQuestAsset.Asset : null;
                if (quest == null || quest.ShipRestrictions == null || quest.ShipRestrictions.Count == 0 || HubQuestManager.Instance == null) return true;
                List<ShipLoadoutDataRef> allowed = quest.ShipRestrictions.Pick(HubQuestManager.Instance.ChallengeSeed).Loadouts;
                return allowed.Exists(r => r.AssetGuid == chosen.AssetGuid);
            }
            catch (Exception e)
            {
                Log.Failure("Checking a ship against the mission's loadout rules", e);
                return true;
            }
        }

        private static string _hostRoom;
        private static int _hostActor = -1;

        /// <summary>
        /// A few times a second, from the runtime: raise Changed when the room's fleet choices, the saved run's choice or the
        /// room's host changed (host-only controls), and look after a new host.
        /// </summary>
        public static void Poll()
        {
            WatchHost();
            string signature = null;
            if (PhotonNetwork.InRoom)
            {
                HubShipManager manager = HubShipManager.Instance;
                var text = new StringBuilder();
                text.Append(HostHasFleet ? '1' : '0').Append(SavedRunChosen ? 'S' : '-').Append('|').Append(CoopShips).Append('|').Append(ShipsPerTeam)
                    .Append('|').Append((byte)Variant).Append('|').Append(MirrorTypes ? 'M' : '-').Append('|').Append(_hostActor)
                    .Append('|').Append(manager != null ? manager.SelectedShipGuid.AsHex() : "");
                // Each ship's forced type, loadout and loadout name as the room holds them (hex), without parsing them.
                for (int slot = 0; slot < FleetSlots.MaxSlots; slot++)
                    text.Append('|').Append(Raw(KeyType + (slot + 1))).Append('/').Append(Raw(KeyLoad + (slot + 1))).Append('/').Append(Raw(KeyLoadName + (slot + 1)));
                signature = text.ToString();
            }
            if (signature == _lastSignature) return;
            _lastSignature = signature;
            Revision++;
            Action changed = Changed;
            if (changed == null) return;
            try
            {
                changed();
            }
            catch (Exception e)
            {
                Log.Failure("Updating the hub for new fleet choices", e);
            }
        }

        /// <summary>
        /// Notes the room's host. A player who becomes host in the hub tells their Ship Customizer which ships use CUSTOM, as
        /// the host does when the hub loads: only the old host's Customizer knew.
        /// </summary>
        private static void WatchHost()
        {
            string room = PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.Name : null;
            int actor = room != null && PhotonNetwork.MasterClient != null ? PhotonNetwork.MasterClient.ActorNumber : -1;
            if (actor == _hostActor && room == _hostRoom) return;
            bool newHost = room != null && room == _hostRoom && _hostActor >= 0;
            _hostRoom = room;
            _hostActor = actor;
            if (!newHost || !HostInHub() || !CustomizerLink.Linked) return;
            try
            {
                TellCustomizerShips();
                Log.Info("This player is the host now: Ship Customizer is told which loadout each ship uses.");
            }
            catch (Exception e)
            {
                Log.Failure("Telling Ship Customizer the ships' loadouts after a host change", e);
            }
        }

        /// <summary>A room choice as the room holds it (a GUID as hex), for change checks; null when not set.</summary>
        private static string Raw(string key)
        {
            object value;
            return TryGet(key, out value) ? value as string : null;
        }
    }
}
