using System;
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

        private static GameSession _pausedSession;
        private static bool _trackersSkipped;

        /// <summary>
        /// Whether this mission gives no progression. Once a mission is paused it stays paused until the hub, so players
        /// leaving before the end (a 7-player run dropping to 6) don't turn its end-of-run rewards back on.
        /// </summary>
        public static bool Paused
        {
            get
            {
                try
                {
                    GameSession session = GameSessionManager.ActiveSession;
                    if (session == null || session.IsHub || !ModRegistration.SessionUsesFleet) return false;
                    if (ReferenceEquals(session, _pausedSession)) return true;
                    if (FleetSession.Mode == FleetMode.OneShip && PhotonNetwork.PlayerList.Length <= 6) return false;
                    _pausedSession = session;
                    return true;
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

        /// <summary>
        /// Daily and weekly quests: the game marks a quest done (gone until the next day or week) before it pays the
        /// reward, which the guard blocks, so a quest finished in a paused mission would be used up for nothing. It stays
        /// open instead; its progress starts again from zero in the next mission, so nothing carries over.
        /// </summary>
        [GamePatch(Feature, "CG.Profile.PlayerMetaquestData", "SetQuestComplete", PatchKind.Prefix, Optional = true)]
        private static bool BlockMetaquestPrefix(ref bool __result)
        {
            if (!Block("daily and weekly quests")) return true;
            __result = false;
            return false;
        }

        [GamePatch(Feature, "CG.Profile.CloudPlayerMetaquestSync", "SetQuestComplete", PatchKind.Prefix, Optional = true)]
        private static bool BlockCloudMetaquestPrefix(ref bool __result)
        {
            if (!Block("daily and weekly quests")) return true;
            __result = false;
            return false;
        }

        /// <summary>
        /// A paused mission tracks no quest, metric or achievement progress at all, so nothing from it counts in a later
        /// mission either (achievement metrics carry over between missions). Both halves are skipped together: finishing
        /// trackers that never started would save zeros.
        /// </summary>
        [GamePatch(Feature, "PlayerMetricTracker", "ActivateTrackers", PatchKind.Prefix, Optional = true)]
        private static bool ActivateTrackersPrefix()
        {
            _trackersSkipped = Block("quest and achievement tracking");
            return !_trackersSkipped;
        }

        [GamePatch(Feature, "PlayerMetricTracker", "FinalizeTrackers", PatchKind.Prefix, Optional = true)]
        private static bool FinalizeTrackersPrefix()
        {
            return !_trackersSkipped;
        }

        private static bool _saveNoticeShown;

        /// <summary>
        /// Host: fleet runs aren't saved. The game's save holds one ship, so a resumed fleet run would come back as a
        /// one-ship run that earns progression for everything gathered with two ships.
        /// </summary>
        [GamePatch(Feature, "GameSessionManager", "PreserveSession", PatchKind.Prefix, Optional = true)]
        private static bool PreserveSessionPrefix()
        {
            try
            {
                if (FleetSession.ShipCount < 2 && !Paused) return true;
                Log.InfoOnce("no fleet saves", "Not saving this run: fleet runs (and runs without progression) can't be continued later.");
                if (!_saveNoticeShown)
                {
                    _saveNoticeShown = true;
                    Notice.ShowAll("Fleet runs aren't saved: the game's save holds only one ship.");
                }
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking whether to save the run", e);
                return true;
            }
        }
    }
}
