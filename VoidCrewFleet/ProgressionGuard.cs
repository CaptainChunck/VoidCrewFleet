using System;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using ResourceAssets;

namespace VoidCrewFleet
{
    /// <summary>
    /// Void Crew's modding rules: a mod that gives a non-trivial advantage must make sure nobody in the room earns
    /// progression. A second ship, a crew of more than six and PvP all change the game far beyond its tuning, so those
    /// missions give no XP, rewards, quest progress, codex entries or achievements, from mission start until everyone is
    /// back in the hub. One-ship missions of up to six players are the game as it is and keep their progression. Everyone in
    /// such a room has this plugin (the session requires it), so everyone's progression is covered.
    /// The host decides as the mission starts, and again whenever a player joins (a seventh player pauses it), and publishes
    /// the pause in the room keyed by the mission's session ID, so late joiners and a later host agree with it. Once paused,
    /// a mission stays paused until the hub, even if players leave.
    /// The patch targets are the ones the Ship Customizer pauses; both plugins can be installed together.
    /// </summary>
    internal static class ProgressionGuard
    {
        public const string Feature = "Progression pause";

        /// <summary>Room property: the GameSessionID of the mission without progression (written by the host only).</summary>
        private const string PausedKey = "VCF_NoProg";

        private static GameSession _pausedSession;
        private static GameSession _publishedSession;

        /// <summary>The mission whose quest and achievement trackers were started, and whether they are off now.</summary>
        private static GameSession _trackedSession;
        private static bool _trackersOff;

        /// <summary>
        /// Whether this mission gives no progression: the host published so, or this client sees it by the rules (fleet and
        /// PvP missions, more than six players). Once a mission is paused it stays paused until the hub, so players leaving
        /// before the end (a 7-player run dropping to 6) don't turn its end-of-run rewards back on. On the host, a pause is
        /// published as soon as it is known.
        /// </summary>
        public static bool Paused
        {
            get
            {
                try
                {
                    GameSession session = GameSessionManager.ActiveSession;
                    if (session == null || session.IsHub || !ModRegistration.SessionUsesFleet) return false;
                    if (!ReferenceEquals(session, _pausedSession))
                    {
                        if (!PublishedFor(session) && !ByTheRules()) return false;
                        _pausedSession = session;
                    }
                    if (PhotonNetwork.IsMasterClient) Publish(session);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Fleet and PvP missions, and missions of more than six players, give no progression.</summary>
        private static bool ByTheRules()
        {
            return FleetSession.Mode != FleetMode.OneShip || PhotonNetwork.PlayerList.Length > 6;
        }

        /// <summary>Whether the room says this mission gives no progression.</summary>
        private static bool PublishedFor(GameSession session)
        {
            Room room = PhotonNetwork.CurrentRoom;
            object value;
            return room != null && room.CustomProperties != null && !string.IsNullOrEmpty(session.GameSessionID) &&
                   room.CustomProperties.TryGetValue(PausedKey, out value) && value as string == session.GameSessionID;
        }

        /// <summary>Host: tells the room this mission gives no progression (once per mission, unless the room already says so).</summary>
        private static void Publish(GameSession session)
        {
            if (ReferenceEquals(session, _publishedSession) || string.IsNullOrEmpty(session.GameSessionID) || PublishedFor(session)) return;
            _publishedSession = session;
            try
            {
                PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { PausedKey, session.GameSessionID } });
                Log.Info("This mission gives no progression (two or more ships, PvP or more than six players; Void Crew's modding rules).");
            }
            catch (Exception e)
            {
                Log.Failure("Telling the room this mission gives no progression", e);
            }
        }

        private static bool Block(string what)
        {
            if (!Paused) return false;
            Log.InfoOnce("blocked " + what, "Skipped " + what + ": missions with two ships, PvP or more than six players give no progression (Void Crew's modding rules).");
            return true;
        }

        /// <summary>
        /// Host, as a mission starts (before its session data goes out to the other players): decide whether it gives
        /// progression, so the pause reaches the room before anyone starts tracking.
        /// </summary>
        [GamePatch(Feature, "GameSessionManager", "DistributeSessionData", PatchKind.Prefix, Optional = true)]
        private static void SessionStartPrefix()
        {
            try
            {
                GameSession session = GameSessionManager.ActiveSession;
                if (session != null && !session.IsHub && !Paused) Log.Verbose("This mission gives progression as usual.");
            }
            catch (Exception e)
            {
                Log.Failure("Deciding whether the mission gives progression", e);
            }
        }

        /// <summary>
        /// A player joined: a seventh player pauses the mission. Every client checks: the host publishes the pause, and a
        /// client that was tracking quests and achievements stops them, so nothing more is counted or saved.
        /// </summary>
        [GamePatch(Feature, "PhotonService", "OnPlayerEnteredRoom", PatchKind.Postfix, Optional = true)]
        private static void PlayerJoinedPostfix()
        {
            try
            {
                if (Paused && !_trackersOff) StopTrackers();
            }
            catch (Exception e)
            {
                Log.Failure("Pausing progression when a player joined", e);
            }
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
        /// Data shards: one delivered to a data shard socket unlocks a codex entry, on the socket's controller and (through
        /// this RPC) on everyone else. A paused mission unlocks none.
        /// </summary>
        [GamePatch(Feature, "RewardPoolItemUnlocker", "DataShardsSocketOnAcquireCarryable", PatchKind.Prefix, Optional = true)]
        private static bool BlockDataShardPrefix()
        {
            return !Block("codex entries from data shards");
        }

        [GamePatch(Feature, "RewardPoolItemUnlocker", "OnHostUnlockedReward", PatchKind.Prefix, Optional = true)]
        private static bool BlockDataShardRewardPrefix()
        {
            return !Block("codex entries from data shards");
        }

        /// <summary>
        /// A paused mission tracks no quest, metric or achievement progress at all, so nothing from it counts in a later
        /// mission either (achievement metrics carry over between missions). Both halves are skipped together: finishing
        /// trackers that never started would save zeros.
        /// </summary>
        [GamePatch(Feature, "PlayerMetricTracker", "ActivateTrackers", PatchKind.Prefix, Optional = true)]
        private static bool ActivateTrackersPrefix()
        {
            _trackedSession = GameSessionManager.ActiveSession;
            _trackersOff = Block("quest and achievement tracking");
            return !_trackersOff;
        }

        /// <summary>
        /// The end of a mission saves what its trackers counted: never for a paused mission. Trackers that run when the
        /// mission turns out paused (it was paused after they started) are stopped without saving.
        /// </summary>
        [GamePatch(Feature, "PlayerMetricTracker", "FinalizeTrackers", PatchKind.Prefix, Optional = true)]
        private static bool FinalizeTrackersPrefix(ref bool ___progressSubmitted)
        {
            try
            {
                if (___progressSubmitted) return true;
                bool paused = _trackersOff || Paused || (_trackedSession != null && ReferenceEquals(_trackedSession, _pausedSession));
                if (!paused) return true;
                if (!_trackersOff) StopTrackers();
                ___progressSubmitted = true;
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking whether to save the mission's quest and achievement progress", e);
                return true;
            }
        }

        /// <summary>
        /// Stops every quest, metric and achievement tracker as the game's FinalizeTrackers does, without submitting or saving
        /// what they hold. Stopping a tracker that isn't running does nothing.
        /// </summary>
        private static void StopTrackers()
        {
            _trackersOff = true;
            foreach (MetaquestDef def in ResourceAssetContainer<MetaquestContainer, Metaquest, MetaquestDef>.Instance.AssetDescriptions)
            {
                if (def.Asset != null && def.Asset.IsActive) def.Asset.Deactivate();
            }
            foreach (TrackedMetricDef def in ResourceAssetContainer<TrackedMetricContainer, TrackedMetric, TrackedMetricDef>.Instance.AssetDescriptions)
            {
                if (def.Asset != null) def.Asset.Deactivate();
            }
            foreach (AchievementDef def in ResourceAssetContainer<AchievementContainer, Achievement, AchievementDef>.Instance.AssetDescriptions)
            {
                if (def.Asset != null) def.Asset.Deactivate();
            }
            Log.Info("Quest and achievement tracking is off for the rest of this mission: it gives no progression.");
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
