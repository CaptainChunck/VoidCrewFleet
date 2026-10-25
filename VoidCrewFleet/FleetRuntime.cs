using System;
using System.Collections.Generic;
using System.Linq;
using CG.GameLoopStateMachine;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// The plugin's one long-lived object: polls the room a few times a second and gives per-frame work a place to run.
    /// Nothing here runs until the game has finished loading (its own Photon set-up included), so the plugin never wakes
    /// Photon or the game's managers earlier than the game does.
    /// </summary>
    internal sealed class FleetRuntime : MonoBehaviour
    {
        private const float PollSeconds = 0.25f;

        /// <summary>
        /// A handler that fails this many times in a row, over at least <see cref="DropAfterSeconds"/> of play, is switched off
        /// until the game is next in play (a hub or mission loaded): it would only throw again every time. Failures only count
        /// while the game is in play (its GSIngame state): a long loading screen or a teardown, where the game's objects come
        /// and go, switches nothing off.
        /// </summary>
        private const int DropAfterFailures = 30;

        private const float DropAfterSeconds = 10f;

        /// <summary>Whether the game is in play (GSIngame: the hub or a mission), not loading, tearing down or on its end screen.</summary>
        private static bool _playing;

        /// <summary>Goes up by one each time the game comes into play again: handlers switched off before get another chance.</summary>
        private static int _playRound;

        /// <summary>The game state last looked at (the same object while the state lasts).</summary>
        private static object _state;

        private static readonly HandlerList FrameHandlers = new HandlerList("per-frame");
        private static readonly HandlerList SlowHandlers = new HandlerList("periodic");
        private static readonly HandlerList LateHandlers = new HandlerList("late");

        /// <summary>When the current round of slow work began, and the next of its handlers to run.</summary>
        private float _roundStart;
        private int _nextSlow = int.MaxValue;

        /// <summary>The runtime object, for coroutines the plugin runs itself.</summary>
        public static FleetRuntime Instance { get; private set; }

        /// <summary>True once the game is past its loading screen; until then the plugin's own work waits.</summary>
        public static bool GameReady { get; private set; }

        /// <summary>
        /// Per-frame work registered by features (each is guarded separately, and one that keeps failing is switched off).
        /// </summary>
        public static event Action Frame;

        /// <summary>
        /// Slow work registered by features: each handler runs every 0.25 s, in the order they were added, spread over the
        /// frames of that time instead of all in one frame.
        /// </summary>
        public static event Action Slow;

        /// <summary>Per-frame work after the game's animation (poses on the character models).</summary>
        public static event Action Late;

        private void Awake()
        {
            Instance = this;
        }

        private void Update()
        {
            if (!GameReady)
            {
                if (!CheckReady()) return;
                GameReady = true;
                // Each on its own: the fleet's messages must still be heard if a late patch or the profiler fails.
                Plugin.Step("Applying the patches that wait for the game to load", Patcher.ApplyLater);
                Plugin.Step("Starting the profiler", () => FleetProfiler.Install(Patcher.Instance));
                Net.StartListening();
                Log.Info("The game has loaded; Void Crew Fleet is active.");
            }
            WatchState();
            FleetProfiler.Frame();
            FrameHandlers.RunAll(Frame);
            RunSlow();
        }

        /// <summary>
        /// Notes whether the game is in play, and each time it comes into play again. The state is named only when it changes
        /// (compared by name, as in CheckReady).
        /// </summary>
        private static void WatchState()
        {
            object state;
            try
            {
                GameStateMachine machine = GameStateMachine.Instance;
                state = machine != null ? machine.CurrentState : null;
            }
            catch
            {
                state = null;
            }
            if (ReferenceEquals(state, _state)) return;
            _state = state;
            bool playing = state != null && state.GetType().Name == "GSIngame";
            if (playing && !_playing) _playRound++;
            _playing = playing;
        }

        private void LateUpdate()
        {
            if (GameReady) LateHandlers.RunAll(Late);
        }

        /// <summary>
        /// Ready once the game's state machine has left its first state (GSNull, which only switches to the loading screen)
        /// and the loading screen (GSLoading). States are compared by name, so a renamed state can't break this method.
        /// </summary>
        private static bool CheckReady()
        {
            try
            {
                GameStateMachine machine = GameStateMachine.Instance;
                if (machine == null || machine.CurrentState == null) return false;
                string state = machine.CurrentState.GetType().Name;
                return state != "GSNull" && state != "GSLoading";
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Runs the slow handlers whose turn has come: a round gives each of them an equal share of the 0.25 s, so at 90 frames
        /// a second about one runs per frame, and a slow frame runs several. A round starts 0.25 s after the last one, or at
        /// once after a long pause (a loading screen), without catching up the rounds it missed.
        /// </summary>
        private void RunSlow()
        {
            int count = SlowHandlers.Read(Slow);
            if (count == 0) return;
            float now = Time.unscaledTime;
            if (_nextSlow >= count)
            {
                float due = _roundStart + PollSeconds;
                if (now < due) return;
                _roundStart = now - due < PollSeconds ? due : now;
                _nextSlow = 0;
            }
            float share = PollSeconds / count;
            while (_nextSlow < count && now >= _roundStart + _nextSlow * share) SlowHandlers.Run(_nextSlow++);
        }

        /// <summary>
        /// An event's handlers, read again only when its subscriptions changed (subscribing makes a new delegate, so the same
        /// delegate always has the same handlers), and how each has been doing. Each runs on its own, so one failing doesn't
        /// stop the rest; one that keeps failing in play is switched off and logged, and stays off (even if the event's
        /// subscriptions change) until the game is next in play, when it gets another chance.
        /// </summary>
        private sealed class HandlerList
        {
            /// <summary>A handler's failure count once it is switched off.</summary>
            private const int Off = -1;

            private static readonly Action[] None = new Action[0];

            /// <summary>What the handlers are, for the log: "per-frame", "periodic" or "late".</summary>
            private readonly string _what;

            private readonly List<Action> _switchedOff = new List<Action>();

            /// <summary>The play round (_playRound) the handlers were switched off in.</summary>
            private int _round;

            private Action _source;
            private Action[] _handlers = None;

            /// <summary>Per handler: its name, its failure log entry, its failures in a row (Off once switched off), and when they began.</summary>
            private string[] _names = new string[0];
            private string[] _operations = new string[0];
            private int[] _failures = new int[0];
            private float[] _failingSince = new float[0];

            public HandlerList(string what)
            {
                _what = what;
            }

            /// <summary>Brings the list up to date with the event's subscriptions; returns how many handlers it has.</summary>
            public int Read(Action work)
            {
                if (_round != _playRound)
                {
                    // The game is in play again (a hub or mission loaded): what was switched off before runs again.
                    _round = _playRound;
                    if (_switchedOff.Count > 0)
                    {
                        Log.Warn(string.Join(", ", _switchedOff.Select(h => h.Method.DeclaringType.Name + "." + h.Method.Name).ToArray()) + " (" + _what +
                                 " work, switched off after failing) runs again now that the game is in play again; it is switched off again if it keeps failing.");
                        _switchedOff.Clear();
                        _source = null;
                    }
                }
                if (ReferenceEquals(work, _source)) return _handlers.Length;
                _source = work;
                var handlers = new List<Action>();
                if (work != null)
                {
                    foreach (Delegate entry in work.GetInvocationList())
                    {
                        var handler = (Action)entry;
                        if (!_switchedOff.Contains(handler)) handlers.Add(handler);
                    }
                }
                _handlers = handlers.ToArray();
                _names = new string[_handlers.Length];
                _operations = new string[_handlers.Length];
                _failures = new int[_handlers.Length];
                _failingSince = new float[_handlers.Length];
                for (int i = 0; i < _handlers.Length; i++)
                {
                    _names[i] = _handlers[i].Method.DeclaringType.Name + "." + _handlers[i].Method.Name;
                    _operations[i] = "Running " + _what + " work (" + _names[i] + ")";
                }
                return _handlers.Length;
            }

            /// <summary>Runs every handler of the event, each on its own.</summary>
            public void RunAll(Action work)
            {
                int count = Read(work);
                for (int i = 0; i < count; i++) Run(i);
            }

            /// <summary>Runs one handler (by its place in the list as last read), unless it has been switched off.</summary>
            public void Run(int index)
            {
                if (_failures[index] == Off) return;
                Action handler = _handlers[index];
                try
                {
                    if (FleetProfiler.On) FleetProfiler.Measure(handler);
                    else handler();
                    _failures[index] = 0;
                }
                catch (Exception e)
                {
                    Failed(index, e);
                }
            }

            /// <summary>
            /// A handler failed: logged (Log.Failure keeps repeats short), and switched off once it keeps failing in play. Out of
            /// play (loading, teardown) the failure is only logged, and the count starts again.
            /// </summary>
            private void Failed(int index, Exception e)
            {
                Log.Failure(_operations[index], e);
                if (!_playing)
                {
                    _failures[index] = 0;
                    return;
                }
                float now = Time.unscaledTime;
                if (_failures[index]++ == 0) _failingSince[index] = now;
                int failures = _failures[index];
                float seconds = now - _failingSince[index];
                if (failures < DropAfterFailures || seconds < DropAfterSeconds) return;
                _failures[index] = Off;
                _switchedOff.Add(_handlers[index]);
                Log.Error(_names[index] + " (" + _what + " work) has failed " + failures + " times in a row, for " + seconds.ToString("0") +
                          " s, so it is switched off until the next hub or mission loads; the rest of the plugin keeps running. " +
                          "Please send BepInEx\\LogOutput.log with a bug report.");
            }
        }
    }
}
