using System;
using System.Collections.Generic;
using System.Reflection;
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
        /// <summary>One of the game's own missions (with one or two ships, as the ship terminal says).</summary>
        None = 0,

        /// <summary>The standard Pilgrimage for two ships: more enemies, more objectives per sector.</summary>
        FleetPilgrimage = 1,

        /// <summary>PvP: two crews, one ship each.</summary>
        FleetBattle = 2
    }

    /// <summary>
    /// The fleet choices made in the hub, kept as room properties that only the host writes (other players ask the host
    /// through Net). The game's own ship selection (HubShipManager, and the Ship Customizer's CUSTOM on top of it) stays
    /// ship 1; ship 2 is this plugin's own. When the host starts a mission the choices are frozen into the mission's
    /// FleetPlan (FleetSession), which reaches every client inside the game's session data.
    /// </summary>
    internal static class HubFleet
    {
        public const string Feature = "Two ships in the hub";

        public const int MaxCrewPerShip = 8;

        private const string KeyOn = "VCF_On";
        private const string KeyTwo = "VCF_Two";
        private const string KeyShipB = "VCF_ShipB";
        private const string KeyVariant = "VCF_Var";
        private const string KeyShipBName = "VCF_ShipBName";

        private static bool _applyingVariant;
        private static string _lastSignature;

        /// <summary>Raised (a few times a second at most) when any fleet choice of the room changed.</summary>
        public static event Action Changed;

        // ---------------------------------------------------------------- reading the room

        private static bool TryGet(string key, out object value)
        {
            value = null;
            return PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.CustomProperties != null &&
                   PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(key, out value) && value != null;
        }

        /// <summary>True when the room's host runs this plugin (and its fleet choices exist).</summary>
        public static bool HostHasFleet
        {
            get
            {
                object value;
                return TryGet(KeyOn, out value) && value is int && (int)value >= FleetPlan.Protocol;
            }
        }

        /// <summary>Whether the game's own missions are flown with two ships. On unless the host turned it off.</summary>
        public static bool TwoShips
        {
            get
            {
                object value;
                return !TryGet(KeyTwo, out value) || !(value is bool) || (bool)value;
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

        /// <summary>The second ship's loadout as chosen; empty when nobody chose one (ship 1's loadout is used then).</summary>
        public static GUIDUnion ChosenShipB
        {
            get
            {
                object value;
                if (!TryGet(KeyShipB, out value) || !(value is string) || string.IsNullOrEmpty((string)value)) return GUIDUnion.Empty();
                try
                {
                    return new GUIDUnion((string)value);
                }
                catch
                {
                    return GUIDUnion.Empty();
                }
            }
        }

        /// <summary>
        /// A loadout by GUID: the game's, or the Ship Customizer's CUSTOM; null (without the game's error log) when neither
        /// knows it.
        /// </summary>
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

        /// <summary>For the terminal: ship 2's chosen loadout, or ship 1's (CUSTOM included) while none is chosen.</summary>
        public static ShipLoadoutDataDef ShipBDef
        {
            get
            {
                GUIDUnion chosen = ChosenShipB;
                return !chosen.IsNull ? LoadoutDef(chosen) : ShipADef;
            }
        }

        /// <summary>Ship 2's name as the host described it, for players who can't look the loadout up (CUSTOM without the Customizer).</summary>
        public static string ShipBName
        {
            get
            {
                object value;
                return TryGet(KeyShipBName, out value) ? value as string : null;
            }
        }

        /// <summary>How the next mission will be played, from the room's choices.</summary>
        public static FleetMode PlannedMode
        {
            get
            {
                if (!HostHasFleet) return FleetMode.OneShip;
                switch (Variant)
                {
                    case HubVariant.FleetBattle:
                        return FleetMode.PvP;
                    case HubVariant.FleetPilgrimage:
                        return FleetMode.TwoShips;
                    default:
                        return TwoShips ? FleetMode.TwoShips : FleetMode.OneShip;
                }
            }
        }

        // ---------------------------------------------------------------- asking the host

        public static void RequestTwoShips(bool on)
        {
            Net.ToHost(Msg.HubTwoShips, on);
        }

        public static void RequestShipB(GUIDUnion loadout)
        {
            Net.ToHost(Msg.HubShipB, loadout.AsHex());
        }

        public static void RequestVariant(HubVariant variant)
        {
            Net.ToHost(Msg.HubVariant, (byte)variant);
        }

        /// <summary>Message handlers; called once at start-up.</summary>
        public static void Listen()
        {
            Net.On(Msg.HubTwoShips, (sender, data) =>
            {
                if (!HostInHub()) return;
                bool on = Net.Get(data, 0, true);
                if (!on && Variant != HubVariant.None)
                {
                    Notice.ShowAll("Fleet missions always use two ships. Choose one of the game's own missions on the galaxy map to fly with one ship.");
                    return;
                }
                Set(KeyTwo, on);
                Log.Info("Two ships: " + (on ? "on" : "off") + ".");
            });
            Net.On(Msg.HubShipB, (sender, data) =>
            {
                if (!HostInHub()) return;
                string hex = Net.Get<string>(data, 0, null);
                if (string.IsNullOrEmpty(hex)) return;
                GUIDUnion guid;
                try
                {
                    guid = new GUIDUnion(hex);
                }
                catch
                {
                    return;
                }
                ShipLoadoutDataDef def = LoadoutDef(guid);
                if (def == null || def.Asset == null)
                {
                    Log.Warn("Ignored a ship 2 choice neither the game nor the Ship Customizer knows (" + hex + ").");
                    Net.ToActor(sender, Msg.Notice, CustomizerLink.Installed && !CustomizerLink.Linked
                        ? "Ship 2 can't have a CUSTOM loadout of its own with this version of Ship Customizer: update it, or leave ship 2 as a copy of ship 1."
                        : "Ship 2 can't use that loadout.");
                    return;
                }
                if (Variant == HubVariant.FleetBattle && FleetShips.CrewSlot(sender) != 1)
                {
                    Net.ToActor(sender, Msg.Notice, "Only RED players can change the RED ship (ship 2).");
                    return;
                }
                if (CustomizerLink.IsCustom(guid))
                {
                    string refusal = CustomizerLink.CheckShip2(guid, sender);
                    if (refusal != null)
                    {
                        Net.ToActor(sender, Msg.Notice, refusal);
                        return;
                    }
                }
                Set(KeyShipB, hex);
                Set(KeyShipBName, DescribeLoadout(def));
                CustomizerLink.SetShip2(guid);
                Log.Info("Ship 2 is now " + DescribeLoadout(def) + ".");
            });
            Net.On(Msg.HubVariant, (sender, data) =>
            {
                if (!HostInHub()) return;
                var variant = (HubVariant)Net.Get(data, 0, (byte)0);
                if (variant != HubVariant.None && variant != HubVariant.FleetPilgrimage && variant != HubVariant.FleetBattle) return;
                SelectVariantAsHost(variant);
            });
        }

        /// <summary>
        /// Host: the Ship Customizer switched ship 2's CUSTOM off in the hub, because someone without it joined. Ship 2 takes
        /// the game loadout that CUSTOM is built on.
        /// </summary>
        public static void Ship2CustomCancelled()
        {
            try
            {
                if (!HostInHub()) return;
                GUIDUnion chosen = ChosenShipB;
                if (chosen.IsNull || !CustomizerLink.IsCustom(chosen)) return;
                ShipLoadoutDataDef standIn = CustomizerLink.GetStandIn(chosen);
                Set(KeyShipB, standIn != null ? standIn.AssetGuid.AsHex() : "");
                Set(KeyShipBName, standIn != null ? DescribeLoadout(standIn) : "");
                Log.Info("Ship 2's CUSTOM loadout was switched off by Ship Customizer; ship 2 is now " +
                         (standIn != null ? DescribeLoadout(standIn) : "a copy of ship 1") + ".");
            }
            catch (Exception e)
            {
                Log.Failure("Switching ship 2 away from CUSTOM", e);
            }
        }

        // ---------------------------------------------------------------- Fleet Battle starts from the host's seat

        private static readonly FieldInfo ZoneChairsField = GameMember.Field(typeof(CG.Client.Quests.Behaviours.QuestStartZone), "takeoverChairs");
        private static readonly FieldInfo SeatChairField = GameMember.Field(typeof(SittingInChair), "chair");
        private static CG.Client.Quests.Behaviours.QuestStartZone _zone;
        private static float _lastSeatNotice = -100f;

        /// <summary>
        /// Host, FLEET BATTLE: the teams are already picked, so the host sitting in the ready room starts the match (the
        /// game's short countdown for "everyone is seated"); nobody else's seat counts. Before the teams are confirmed,
        /// sitting down starts nothing.
        /// </summary>
        [GamePatch(Feature, "Gameplay.Quests.QuestStartProcess", "UpdateQuestProcess", PatchKind.Prefix)]
        private static void StartCountdownPrefix(ref int playersInZoneCount)
        {
            if (!PhotonNetwork.IsMasterClient || Variant != HubVariant.FleetBattle || !HostHasFleet) return;
            try
            {
                bool seated = HostSeatedInReadyRoom();
                if (!PvpTeams.Locked)
                {
                    playersInZoneCount = 0;
                    if (seated && Time.unscaledTime - _lastSeatNotice > 10f)
                    {
                        _lastSeatNotice = Time.unscaledTime;
                        Notice.Show("Confirm the teams first (CONFIRM TEAMS on the team screen); then sit down here to start the match.");
                    }
                    return;
                }
                playersInZoneCount = seated && PhotonNetwork.CurrentRoom != null ? Mathf.Max(1, (int)PhotonNetwork.CurrentRoom.PlayerCount) : 0;
            }
            catch (Exception e)
            {
                Log.Failure("Starting Fleet Battle from the ready room", e);
            }
        }

        private static bool HostSeatedInReadyRoom()
        {
            LocalPlayer me = LocalPlayer.Instance;
            if (me == null || me.Locomotion == null || ZoneChairsField == null || SeatChairField == null) return false;
            var sitting = me.Locomotion.GetAbility<SittingInChair>();
            if (sitting == null || !sitting.IsActive) return false;
            object chair = SeatChairField.GetValue(sitting);
            if (chair == null) return false;
            if (_zone == null) _zone = UnityEngine.Object.FindObjectOfType<CG.Client.Quests.Behaviours.QuestStartZone>();
            var chairs = _zone != null ? ZoneChairsField.GetValue(_zone) as Array : null;
            if (chairs == null) return false;
            foreach (object candidate in chairs)
            {
                if (ReferenceEquals(candidate, chair)) return true;
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
            try
            {
                if (!PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
                PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { key, value } });
            }
            catch (Exception e)
            {
                Log.Failure("Updating the room's fleet choices", e);
            }
        }

        /// <summary>Host: a fleet mission is chosen on the galaxy map (it is the standard Pilgrimage underneath).</summary>
        private static void SelectVariantAsHost(HubVariant variant)
        {
            Set(KeyVariant, (byte)variant);
            if (variant == HubVariant.None) return;
            if (variant == HubVariant.FleetBattle)
            {
                Set(KeyTwo, true);
                // Everyone picks a team now (the team screen opens on every client), under the host's last ship rules.
                PvpTeams.Reset();
                ShipRules.PublishSaved();
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
            Log.Info("Mission: " + (variant == HubVariant.FleetBattle ? "Fleet Battle (PvP)" : "Fleet Pilgrimage (two ships, more enemies)") + ".");
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
                Set(KeyOn, FleetPlan.Protocol);
                if (Variant != HubVariant.None) Set(KeyVariant, (byte)HubVariant.None);
                // The Customizer forgets its ship 2 choice between missions; tell it again what the room's ship 2 uses.
                if (!ChosenShipB.IsNull) CustomizerLink.SetShip2(ChosenShipB);
                FleetShips.ClearRoom();
                PvpTeams.Reset();
            }
            catch (Exception e)
            {
                Log.Failure("Publishing the room's fleet choices", e);
            }
        }

        /// <summary>Host: anyone choosing a mission on the galaxy map (other than through a fleet entry) ends a fleet mission choice.</summary>
        [GamePatch(Feature, "Gameplay.Hub.HubQuestManager", "NotifyNewQuestSelected", PatchKind.Postfix)]
        private static void QuestSelectedPostfix()
        {
            if (_applyingVariant || !PhotonNetwork.IsMasterClient || Variant == HubVariant.None) return;
            Set(KeyVariant, (byte)HubVariant.None);
        }

        /// <summary>
        /// Host, just before the game leaves the hub for a mission: freeze the room's fleet choices into the mission.
        /// Saved runs are always one-ship missions (the game's save holds one ship).
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
                    if (PlannedMode != FleetMode.OneShip) Notice.ShowAll("Saved runs continue with one ship: the game's save holds only one.");
                    return;
                }

                FleetMode mode = PlannedMode;
                int players = PhotonNetwork.PlayerList.Length;
                if (mode == FleetMode.OneShip && players > MaxCrewPerShip && HostHasFleet)
                {
                    mode = FleetMode.TwoShips;
                    Notice.ShowAll(players + " players is more than one ship's crew of " + MaxCrewPerShip + ", so this mission uses two ships.");
                }
                if (mode == FleetMode.OneShip)
                {
                    FleetSession.Attach(gameSession, null);
                    return;
                }

                GUIDUnion shipB;
                string shipBName;
                ShipLoadout shipBLoadout = PrepareShipB(gameSession, out shipB, out shipBName);
                if (shipBLoadout == null)
                {
                    FleetSession.Attach(gameSession, null);
                    Notice.ShowAll("Ship 2 could not be prepared, so this mission starts with one ship.");
                    return;
                }
                var plan = new FleetPlan
                {
                    Mode = mode,
                    ScaleEnemies = Variant == HubVariant.FleetPilgrimage,
                    ShipB = shipB,
                    ShipBName = shipBName,
                    ShipBLoadout = shipBLoadout,
                    ShipBAlloys = _shipBAlloys,
                    ShipBHomunculus = shipBLoadout.HomunculusOverride != null && !shipBLoadout.HomunculusOverride.IsNull
                        ? shipBLoadout.HomunculusOverride.AssetGuid
                        : GUIDUnion.Empty(),
                    HackCost = Settings.HackTerminalCost.Value,
                    Arena = Settings.PvpArena.Value
                };
                FleetSession.Attach(gameSession, plan);
                Log.Info("Starting a fleet mission: " + plan + "; ship 2 is " + shipBName + ".");
            }
            catch (Exception e)
            {
                FleetSession.Attach(gameSession, null);
                Log.Failure("Preparing the fleet mission (it starts with one ship instead)", e);
            }
        }

        /// <summary>
        /// Ship 2's loadout for this mission: the one chosen for ship 2 when the mission's loadout rules allow it (a CUSTOM one
        /// is built by the Ship Customizer), otherwise a copy of exactly what ship 1 spawns with. That copy includes ship 1's
        /// CUSTOM loadout, or what the Customizer fell back to, because this prefix runs after the Customizer's.
        /// </summary>
        private static int _shipBAlloys = -2;

        private static ShipLoadout PrepareShipB(GameSession session, out GUIDUnion guid, out string name)
        {
            _shipBAlloys = -2;
            ShipLoadoutDataDef chosen = LoadoutDef(ChosenShipB);
            if (chosen != null && !AllowedByQuest(session, chosen))
            {
                Notice.ShowAll("This mission only allows certain loadouts, so ship 2 starts as a copy of ship 1.");
                chosen = null;
            }
            if (chosen != null && CustomizerLink.IsCustom(chosen.AssetGuid))
            {
                ShipLoadout custom = CustomizerLink.BuildShip2(chosen.AssetGuid);
                if (custom != null)
                {
                    guid = chosen.AssetGuid;
                    name = DescribeLoadout(chosen);
                    // Its CUSTOM starting alloys, or the game's own amount when it has none set.
                    _shipBAlloys = Math.Max(-1, CustomizerLink.GetShip2Alloys(chosen.AssetGuid));
                    return custom;
                }
                // CUSTOM can't be used for this mission (the Customizer said why): its game loadout instead.
                chosen = CustomizerLink.GetStandIn(chosen.AssetGuid);
            }
            if (chosen != null && chosen.Asset != null && chosen.Asset.ShipLoadout != null)
            {
                guid = chosen.AssetGuid;
                name = DescribeLoadout(chosen);
                _shipBAlloys = -1;
                return ShipLoadout.FromJObject(chosen.Asset.ShipLoadout.AsJObject());
            }
            guid = session.ToLoadShipGuid;
            ShipLoadoutDataDef shipA = ShipADef;
            if (shipA == null) shipA = LoadoutDef(session.ToLoadShipGuid);
            name = "a copy of ship 1 (" + DescribeLoadout(shipA) + ")";
            return session.ToLoadShipData != null ? ShipLoadout.FromJObject(session.ToLoadShipData.AsJObject()) : null;
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
                Log.Failure("Checking ship 2 against the mission's loadout rules", e);
                return true;
            }
        }

        /// <summary>A few times a second, from the runtime: raise Changed when the room's fleet choices changed.</summary>
        public static void Poll()
        {
            HubShipManager manager = HubShipManager.Instance;
            ShipLoadoutDataDef shipA = ShipADef;
            string signature = PhotonNetwork.InRoom
                ? (HostHasFleet ? "1" : "0") + "|" + TwoShips + "|" + ChosenShipB.AsHex() + "|" + (byte)Variant + "|" +
                  (shipA != null ? shipA.AssetGuid.AsHex() : "")
                : null;
            if (signature == _lastSignature) return;
            _lastSignature = signature;
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
    }
}
