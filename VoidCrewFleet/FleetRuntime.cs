using System;
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

        private static readonly HandlerList FrameHandlers = new HandlerList();
        private static readonly HandlerList SlowHandlers = new HandlerList();
        private static readonly HandlerList LateHandlers = new HandlerList();

        /// <summary>When the current round of slow work began, and the next of its handlers to run.</summary>
        private float _roundStart;
        private int _nextSlow = int.MaxValue;

        /// <summary>The runtime object, for coroutines the plugin runs itself.</summary>
        public static FleetRuntime Instance { get; private set; }

        /// <summary>True once the game is past its loading screen; until then the plugin's own work waits.</summary>
        public static bool GameReady { get; private set; }

        /// <summary>Per-frame work registered by features (each is guarded separately).</summary>
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
                Patcher.ApplyLater();
                FleetProfiler.Install(Patcher.Instance);
                Net.StartListening();
                Log.Info("The game has loaded; Void Crew Fleet is active.");
            }
            FleetProfiler.Frame();
            Run(FrameHandlers.Of(Frame), "per-frame");
            RunSlow();
        }

        private void LateUpdate()
        {
            if (GameReady) Run(LateHandlers.Of(Late), "late");
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
            Action[] handlers = SlowHandlers.Of(Slow);
            if (handlers.Length == 0) return;
            float now = Time.unscaledTime;
            if (_nextSlow >= handlers.Length)
            {
                float due = _roundStart + PollSeconds;
                if (now < due) return;
                _roundStart = now - due < PollSeconds ? due : now;
                _nextSlow = 0;
            }
            float share = PollSeconds / handlers.Length;
            while (_nextSlow < handlers.Length && now >= _roundStart + _nextSlow * share)
            {
                Action handler = handlers[_nextSlow++];
                Run(handler, "periodic");
            }
        }

        /// <summary>Runs each handler on its own, so one failing doesn't stop the rest.</summary>
        private static void Run(Action[] handlers, string what)
        {
            foreach (Action handler in handlers) Run(handler, what);
        }

        private static void Run(Action handler, string what)
        {
            try
            {
                if (FleetProfiler.On) FleetProfiler.Measure(handler);
                else handler();
            }
            catch (Exception e)
            {
                Log.Failure("Running " + what + " work (" + handler.Method.DeclaringType.Name + "." + handler.Method.Name + ")", e);
            }
        }

        /// <summary>
        /// An event's handlers, read again only when its subscriptions changed: subscribing makes a new delegate, so the same
        /// delegate always has the same handlers.
        /// </summary>
        private sealed class HandlerList
        {
            private static readonly Action[] None = new Action[0];

            private Action _source;
            private Action[] _handlers = None;

            public Action[] Of(Action work)
            {
                if (work == null) return None;
                if (ReferenceEquals(work, _source)) return _handlers;
                Delegate[] list = work.GetInvocationList();
                var handlers = new Action[list.Length];
                for (int i = 0; i < list.Length; i++) handlers[i] = (Action)list[i];
                _source = work;
                _handlers = handlers;
                return handlers;
            }
        }
    }
}
