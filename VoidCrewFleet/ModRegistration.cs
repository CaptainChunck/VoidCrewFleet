using System;
using System.Collections.Generic;
using Photon.Pun;
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
    /// - the plugin is required in those rooms: the host's session lists it, and the game sends anyone without it back
    ///   to the menu with its "missing mods" message (an unmodded player could not survive a 7+ player session anyway);
    /// - a player who has the plugin can still join vanilla rooms: on clients the plugin only counts as required when the
    ///   host's session asks for it.
    /// The plugin is registered twice: as an external mod (for the main menu's mod list) and as a runtime asset, because
    /// the game only fetches readable names from the host for missing runtime assets.
    /// </summary>
    internal static class ModRegistration
    {
        public const string Feature = "Modded-session rules";

        public static readonly GUIDUnion ModId = new GUIDUnion("f1ee7c0de5f1ee7c0de5f1ee7c0de5a1");
        public static readonly GUIDUnion MarkerId = new GUIDUnion("f1ee7c0de5f1ee7c0de5f1ee7c0de5a2");

        private const SessionModificationEffect Effect = SessionModificationEffect.ChangesQuestGeneration | SessionModificationEffect.ProgressionAltering;

        private static bool _dynamic;

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
        /// Whether the plugin counts as required right now: always for sessions this player hosts, and on clients only
        /// when the host's session lists it.
        /// </summary>
        private static bool Required()
        {
            if (!PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient) return true;
            GameSession session = GameSessionManager.ActiveSession;
            return session != null && session.ExternalMods != null && session.ExternalMods.ContainsKey(ModId);
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
                    GameSession session = GameSessionManager.ActiveSession;
                    if (PhotonNetwork.IsMasterClient) return true;
                    return session != null && session.ExternalMods != null && session.ExternalMods.ContainsKey(ModId);
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
