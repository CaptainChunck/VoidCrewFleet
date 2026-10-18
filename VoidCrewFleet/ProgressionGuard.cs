using Photon.Pun;

namespace VoidCrewFleet
{
    /// <summary>
    /// Void Crew's modding rules: a mod that gives a non-trivial advantage must make sure nobody in the room earns
    /// progression. A second ship, a crew of more than six and PvP all change the game far beyond its tuning, so those
    /// missions give no XP, rewards, quest progress or achievements, from mission start until everyone is back in the hub.
    /// One-ship missions of up to six players are the game as it is and keep their progression. Everyone in such a room
    /// has this plugin (the session requires it), so everyone's progression is covered.
    /// The patch targets are the ones the Ship Customizer pauses; both plugins can be installed together.
    /// </summary>
    internal static class ProgressionGuard
    {
        public const string Feature = "Progression pause";

        public static bool Paused
        {
            get
            {
                try
                {
                    GameSession session = GameSessionManager.ActiveSession;
                    if (session == null || session.IsHub || !ModRegistration.SessionUsesFleet) return false;
                    return FleetSession.Mode != FleetMode.OneShip || PhotonNetwork.PlayerList.Length > 6;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static bool Block(string what)
        {
            if (!Paused) return false;
            Log.InfoOnce("blocked " + what, "Skipped " + what + ": missions with two ships, PvP or more than six players give no progression (Void Crew's modding rules).");
            return true;
        }

        [GamePatch(Feature, "CG.Profile.PlayerRewardsCollection", "AddItem", PatchKind.Prefix)]
        private static bool BlockRewardsPrefix()
        {
            return !Block("mission rewards and XP");
        }

        [GamePatch(Feature, "CG.Cloud.CloudRewardsProfileSync", "AddItem", PatchKind.Prefix, Optional = true)]
        private static bool BlockCloudRewardsPrefix()
        {
            return !Block("mission rewards and XP");
        }

        [GamePatch(Feature, "CG.Profile.PlayerAchievements", "SetAchievementComplete", PatchKind.Prefix, Optional = true)]
        private static bool BlockAchievementsPrefix()
        {
            return !Block("achievements");
        }

        [GamePatch(Feature, "SteamPlayerAchievementsSync", "SetAchievementComplete", PatchKind.Prefix, Optional = true)]
        private static bool BlockSteamAchievementsPrefix()
        {
            return !Block("achievements");
        }

        [GamePatch(Feature, "CG.Cloud.CloudPlayerAchievementsSync", "SetAchievementComplete", PatchKind.Prefix, Optional = true)]
        private static bool BlockCloudAchievementsPrefix()
        {
            return !Block("achievements");
        }

        [GamePatch(Feature, "CG.Profile.PlayerProfileData", "AddQuestProgress", PatchKind.Prefix, Optional = true)]
        private static bool BlockQuestProgressPrefix()
        {
            return !Block("quest progress");
        }

        [GamePatch(Feature, "CG.Cloud.CloudPlayerProfileDataSync", "AddQuestProgress", PatchKind.Prefix, Optional = true)]
        private static bool BlockCloudQuestProgressPrefix()
        {
            return !Block("quest progress");
        }

        [GamePatch(Feature, "CG.Profile.PhotonPlayerDataSync", "AddQuestProgress", PatchKind.Prefix, Optional = true)]
        private static bool BlockPhotonQuestProgressPrefix()
        {
            return !Block("quest progress");
        }
    }
}
