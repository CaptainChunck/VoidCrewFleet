using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CG.Game;
using CG.Game.Player;
using CG.Space;
using FMOD.Studio;
using Photon.Pun;
using Unity.Services.Vivox;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Voice chat in PvP: you hear the players aboard the same ship as you, whatever their team, and in open space the
    /// players within 50 m; nobody else. So each team talks privately aboard its own ship, and a boarder can be overheard.
    /// - The game's "Immersive Voice Chat" route (each voice played at the speaker's body) is forced on for the match,
    ///   without touching the player's saved setting, and turned back to their choice afterwards.
    /// - The game never makes anyone inaudible (beyond about 28 m a voice turns into full-volume radio), so each client
    ///   turns every remote voice up or down itself, every frame, with a short fade.
    /// - A player without a body (waiting to respawn) is heard by their own team only, like a team radio. Before the teams
    ///   are confirmed everyone hears everyone.
    /// - Nobody sees the talking indicator of a player they can't hear.
    /// </summary>
    internal static class PvpVoice
    {
        public const string Feature = "PvP proximity voice";

        private const float OpenSpaceRange = 50f;
        private const float FadePerSecond = 5f;
        private const int VivoxLow = -50;

        private static readonly Dictionary<string, float> Volumes = new Dictionary<string, float>();
        private static readonly Dictionary<string, bool> Audibility = new Dictionary<string, bool>();
        private static readonly HashSet<string> LoweredInVivox = new HashSet<string>();
        private static bool _on;
        private static float _nextVivoxPass;

        public static bool Active
        {
            get
            {
                try
                {
                    return FleetSession.IsPvP && PhotonNetwork.InRoom && !GameSessionManager.InHub;
                }
                catch
                {
                    return false;
                }
            }
        }

        // ---------------------------------------------------------------- the voices are played at the speakers' bodies

        /// <summary>The game's tap manager plays voices at the speakers' bodies when on: on for PvP, else the player's choice.</summary>
        public static void EnforceTaps()
        {
            try
            {
                VivoxTapManager manager = VivoxTapManager.Instance;
                if (manager == null || !SettingsManager.IsRunning) return;
                bool chosen = SettingsManager.CurrentSettings.AudioSettings.VivoxToFmod.Value == 1;
                bool wanted = _on || chosen;
                if (manager.enabled != wanted) manager.enabled = wanted;
            }
            catch (Exception e)
            {
                Log.Failure("Switching immersive voice chat", e);
            }
        }

        [GamePatch(Feature, "VolumeSettings", "OnApplySettings", PatchKind.Postfix)]
        [GamePatch(Feature, "VolumeSettings", "ApplyVolumeSettings", PatchKind.Postfix)]
        [GamePatch(Feature, "VivoxTapManager", "CreateTap", PatchKind.Postfix)]
        private static void KeepTapsPostfix()
        {
            if (_on) EnforceTaps();
        }

        // ---------------------------------------------------------------- who hears whom

        /// <summary>Every frame: each remote voice fades toward audible or silent for the local player.</summary>
        public static void Frame()
        {
            if (!Active)
            {
                if (_on) Release();
                return;
            }
            if (!_on)
            {
                _on = true;
                EnforceTaps();
                Log.Info("PvP: proximity voice is on (same ship, or within " + OpenSpaceRange + " m in open space).");
            }
            VivoxTapManager manager = VivoxTapManager.Instance;
            if (manager == null) return;
            Player listener = LocalPlayer.Instance;
            int myTeam = FleetShips.CrewSlot(FleetShips.LocalActor);
            float step = Time.unscaledDeltaTime * FadePerSecond;
            foreach (KeyValuePair<string, EventInstance> pair in manager.FmodEvents)
            {
                EventInstance voice = pair.Value;
                if (!voice.isValid()) continue;
                bool audible = Audible(pair.Key, listener, myTeam);
                Audibility[pair.Key] = audible;
                float current;
                if (!Volumes.TryGetValue(pair.Key, out current)) current = 1f;
                float next = Mathf.MoveTowards(current, audible ? 1f : 0f, step);
                Volumes[pair.Key] = next;
                voice.setVolume(next);
            }
            if (Time.unscaledTime >= _nextVivoxPass)
            {
                _nextVivoxPass = Time.unscaledTime + 0.25f;
                GateUntapped(manager, listener, myTeam);
            }
        }

        private static bool Audible(string cloudId, Player listener, int myTeam)
        {
            Photon.Realtime.Player speakerPlayer = VoipService.CloudIDToPlayer(cloudId);
            if (speakerPlayer == null) return false;
            if (!PvpTeams.Locked) return true;
            int speakerTeam = FleetShips.CrewSlot(speakerPlayer.ActorNumber);
            bool teammate = myTeam >= 0 && speakerTeam == myTeam;
            Player speaker = FleetShips.CharacterOf(speakerPlayer.ActorNumber);
            if (speaker == null || speaker.IsDead || listener == null || listener.IsDead) return teammate;
            PlayerControlledShip speakerShip = FleetShips.InteriorAt(speaker.transform.position);
            PlayerControlledShip listenerShip = FleetShips.InteriorAt(listener.transform.position);
            if (speakerShip != null || listenerShip != null) return speakerShip == listenerShip;
            return Vector3.Distance(speaker.transform.position, listener.transform.position) <= OpenSpaceRange;
        }

        /// <summary>
        /// A participant whose voice is not going through the game's taps (no body tap running) is played by Vivox itself:
        /// turn them down there instead.
        /// </summary>
        private static void GateUntapped(VivoxTapManager manager, Player listener, int myTeam)
        {
            try
            {
                var adapter = VoipService.Instance as VivoxAdapter;
                if (adapter == null || string.IsNullOrEmpty(adapter.AudioChannel) || VivoxService.Instance == null) return;
                ReadOnlyCollection<VivoxParticipant> participants;
                if (!VivoxService.Instance.ActiveChannels.TryGetValue(adapter.AudioChannel, out participants) || participants == null) return;
                string self = VoipService.PlayerToCloudID(PhotonNetwork.LocalPlayer);
                foreach (VivoxParticipant participant in participants)
                {
                    string id = participant.PlayerId;
                    if (string.IsNullOrEmpty(id) || id == self) continue;
                    GameObject tap;
                    bool tapped = manager.enabled && manager.Taps.TryGetValue(id, out tap) && tap != null && manager.FmodEvents.ContainsKey(id);
                    bool lower = !tapped && !Audible(id, listener, myTeam);
                    if (!tapped) Audibility[id] = !lower;
                    if (lower && LoweredInVivox.Add(id)) participant.SetLocalVolume(VivoxLow);
                    else if (!lower && LoweredInVivox.Remove(id)) participant.SetLocalVolume(0);
                }
            }
            catch (Exception e)
            {
                Log.WarnOnce("pvp vivox gate", "Could not adjust a voice chat participant's volume: " + e.Message);
            }
        }

        /// <summary>The match is over: every voice back to normal and the player's own immersive-voice choice back.</summary>
        private static void Release()
        {
            _on = false;
            try
            {
                VivoxTapManager manager = VivoxTapManager.Instance;
                if (manager != null)
                {
                    foreach (KeyValuePair<string, EventInstance> pair in manager.FmodEvents)
                    {
                        if (pair.Value.isValid()) pair.Value.setVolume(1f);
                    }
                }
                var adapter = VoipService.Instance as VivoxAdapter;
                ReadOnlyCollection<VivoxParticipant> participants;
                if (LoweredInVivox.Count > 0 && adapter != null && !string.IsNullOrEmpty(adapter.AudioChannel) && VivoxService.Instance != null &&
                    VivoxService.Instance.ActiveChannels.TryGetValue(adapter.AudioChannel, out participants) && participants != null)
                {
                    foreach (VivoxParticipant participant in participants)
                    {
                        if (LoweredInVivox.Contains(participant.PlayerId)) participant.SetLocalVolume(0);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Failure("Turning proximity voice off", e);
            }
            LoweredInVivox.Clear();
            Volumes.Clear();
            Audibility.Clear();
            EnforceTaps();
            Log.Info("PvP: proximity voice is off.");
        }

        private static bool Silenced(string cloudId)
        {
            bool audible;
            return _on && cloudId != null && Audibility.TryGetValue(cloudId, out audible) && !audible;
        }

        // ---------------------------------------------------------------- talking indicators

        [GamePatch(Feature, "VivoxAdapter", "IsSpeaking", PatchKind.Postfix)]
        private static void IsSpeakingPostfix(string player, ref bool __result)
        {
            if (__result && Silenced(player)) __result = false;
        }

        [GamePatch(Feature, "VivoxAdapter", "SpeechDetected", PatchKind.Prefix)]
        private static void SpeechDetectedPrefix(string player, ref bool state)
        {
            if (state && Silenced(player)) state = false;
        }

        [GamePatch(Feature, "VivoxAdapter", "ParticipantSpeechDetected", PatchKind.Prefix)]
        private static bool ParticipantSpeechPrefix(VivoxParticipant participant)
        {
            return participant == null || !participant.SpeechDetected || !Silenced(participant.PlayerId);
        }
    }
}
