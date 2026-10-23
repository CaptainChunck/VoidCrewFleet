using System;
using System.Collections.Generic;
using Photon.Pun;
using Photon.Realtime;
using ResourceAssets;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>Inert object registered as the plugin's runtime asset; only its name is ever shown.</summary>
    internal sealed class FleetMarker : ScriptableObject
    {
    }

    /// <summary>
    /// Void Crew's modding rules for mods that change the session:
    /// - every room this player hosts is marked [mod_session] before it is listed (ModdingUtils.RegisterSessionMod);
    /// - the plugin is required in Fleet rooms: the host's session lists it, and the game sends anyone without it back to
    ///   the menu with its "missing mods" message (an unmodded player could not survive a 7+ player session anyway). A Fleet
    ///   room is one this player created, or one whose sessions or fleet choices show its earlier host ran the plugin;
    /// - a player who has the plugin can still join vanilla rooms: on clients the plugin only counts as required when the
    ///   host's session asks for it, and a vanilla room stays one when such a player becomes its host.
    /// The plugin is registered twice: as an external mod (for the main menu's mod list) and as a runtime asset, because
    /// the game only fetches readable names from the host for missing runtime assets.
    /// </summary>
    internal static class ModRegistration
    {
        public const string Feature = "Modded-session rules";

        public static readonly GUIDUnion ModId = new GUIDUnion("f1ee7c0de5f1ee7c0de5f1ee7c0de5a1");
        /// <summary>
        /// The runtime asset the game's mod check requires in rooms this player hosts. It carries the network protocol, so a
        /// player with an incompatible version is sent back by the game with its "missing mods" message naming the host's
        /// version, instead of joining and desyncing.
        /// </summary>
        public static readonly GUIDUnion MarkerId = new GUIDUnion("f1ee7c0de5f1ee7c0de5f1ee7c0de5" + (0x80 + FleetPlan.Protocol).ToString("x2"));

        /// <summary>The room property a Fleet host publishes in the hub (HubFleet); only a Fleet room ever has it.</summary>
        private const string FleetChoicesKey = "VCF_On";

        private const SessionModificationEffect Effect = SessionModificationEffect.ChangesQuestGeneration | SessionModificationEffect.ProgressionAltering;

        private static bool _dynamic;

        /// <summary>The name of the room known to be a Fleet room (created here, or seen with the plugin's marks), or null.</summary>
        private static string _fleetRoom;

        /// <summary>Called once at start-up, after the patches. Without the patches the plugin is simply always required.</summary>
        public static void Register()
        {
            _dynamic = Patcher.IsReady(Feature);
            SessionModificationEffect effect = _dynamic ? SessionModificationEffect.None : Effect;
            string name = "VoidCrewFleet " + Plugin.Version;
            RuntimeAssetsRegister register = RuntimeAssetsRegister.Instance;
            if (!register.GetRegisteredModIds().Contains(ModId))
            {
                register.RegisterExternalMod(ModId, effect, new RuntimeAssetInfo { Name = name, DisplayName = "Void Crew Fleet" });
            }
            if (!register.HasAsset(MarkerId))
            {
                var marker = ScriptableObject.CreateInstance<FleetMarker>();
                marker.name = "VoidCrewFleet";
                marker.hideFlags = HideFlags.HideAndDontSave;
                register.RegisterAsset(MarkerId, marker, effect,
                    new RuntimeAssetInfo { Name = "Void Crew Fleet " + Plugin.Version + " (BepInEx plugin)", DisplayName = "Void Crew Fleet" });
            }
            ModdingUtils.RegisterSessionMod();
            Log.Info(_dynamic
                ? "Rooms you host are marked [mod_session] and need Void Crew Fleet; you can still join rooms without it."
                : "Rooms you host are marked [mod_session] and need Void Crew Fleet. (The plugin could not hook the game's mod check, so it also only joins rooms that use it.)");
        }

        /// <summary>
        /// Whether the room is a Fleet room, where the plugin is required: this player created it, the loaded session lists
        /// the plugin, or a host published its fleet choices (VCF_On) in it. Once seen, a room stays a Fleet room, so a player
        /// who takes it over keeps it one. A vanilla room this player only took over as host is not one: its players without
        /// the plugin can stay. False outside a room.
        /// </summary>
        public static bool IsFleetRoom
        {
            get
            {
                try
                {
                    Room room = PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom : null;
                    if (room == null) return false;
                    if (room.Name != _fleetRoom && (SessionListsFleet() || HasFleetChoices(room))) _fleetRoom = room.Name;
                    return room.Name == _fleetRoom;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Whether the loaded session asks for the plugin (its host required it).</summary>
        private static bool SessionListsFleet()
        {
            GameSession session = GameSessionManager.ActiveSession;
            return session != null && session.ExternalMods != null && session.ExternalMods.ContainsKey(ModId);
        }

        private static bool HasFleetChoices(Room room)
        {
            object value;
            return room.CustomProperties != null && room.CustomProperties.TryGetValue(FleetChoicesKey, out value) && value is int;
        }

        /// <summary>Every room this player creates is a Fleet room (it is created through the game's PhotonService).</summary>
        [GamePatch(Feature, "PhotonService", "OnCreatedRoom", PatchKind.Postfix)]
        private static void CreatedRoomPostfix()
        {
            try
            {
                if (PhotonNetwork.CurrentRoom != null) _fleetRoom = PhotonNetwork.CurrentRoom.Name;
            }
            catch (Exception e)
            {
                Log.Failure("Noting the room you created", e);
            }
        }

        /// <summary>
        /// Whether the plugin counts as required right now: for sessions this player hosts in a Fleet room (or outside a
        /// room), and on clients only when the host's session lists it.
        /// </summary>
        private static bool Required()
        {
            if (!PhotonNetwork.InRoom) return true;
            // Asked on clients too: a client checking a Fleet room's session notes the room, so it stays a Fleet room if this
            // player becomes its host.
            bool fleetRoom = IsFleetRoom;
            return PhotonNetwork.IsMasterClient ? fleetRoom : SessionListsFleet();
        }

        /// <summary>True when the session that is loaded is hosted with this plugin (so its effects may apply).</summary>
        public static bool SessionUsesFleet
        {
            get
            {
                try
                {
                    if (!PhotonNetwork.InRoom) return false;
                    if (!ModdingUtils.IsCurrentSessionModded()) return false;
                    if (PhotonNetwork.IsMasterClient) return IsFleetRoom;
                    return SessionListsFleet();
                }
                catch
                {
                    return false;
                }
            }
        }

        [GamePatch(Feature, "ResourceAssets.RuntimeAssetsRegister", "GetRequiredExternalMods", PatchKind.Postfix)]
        private static void RequiredModsPostfix(Dictionary<GUIDUnion, SessionModificationEffect> __result)
        {
            try
            {
                if (_dynamic && __result != null && Required()) __result[ModId] = Effect;
            }
            catch (Exception e)
            {
                Log.Failure("Listing Void Crew Fleet as a required mod", e);
            }
        }

        [GamePatch(Feature, "ResourceAssets.RuntimeAssetsRegister", "GetRequiredAssets", PatchKind.Postfix)]
        private static void RequiredAssetsPostfix(Dictionary<GUIDUnion, SessionModificationEffect> __result)
        {
            try
            {
                if (_dynamic && __result != null && Required()) __result[MarkerId] = Effect;
            }
            catch (Exception e)
            {
                Log.Failure("Listing Void Crew Fleet as a required asset", e);
            }
        }
    }
}
