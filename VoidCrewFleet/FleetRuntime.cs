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
        private float _nextPoll;

        /// <summary>The runtime object, for coroutines the plugin runs itself.</summary>
        public static FleetRuntime Instance { get; private set; }

        /// <summary>True once the game is past its loading screen; until then the plugin's own work waits.</summary>
        public static bool GameReady { get; private set; }

        /// <summary>Per-frame work registered by features (each is guarded separately).</summary>
        public static event Action Frame;

        /// <summary>Slow work registered by features, a few times a second.</summary>
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
                Net.StartListening();
                Log.Info("The game has loaded; Void Crew Fleet is active.");
            }
            Run(Frame, "per-frame");
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + PollSeconds;
            Run(Slow, "periodic");
        }

        private void LateUpdate()
        {
            if (GameReady) Run(Late, "late");
        }

        private static bool CheckReady()
        {
            try
            {
                GameStateMachine machine = GameStateMachine.Instance;
                if (machine == null || machine.CurrentState == null) return false;
                return machine.CurrentState.GetType().Name != "GSLoading";
            }
            catch
            {
                return false;
            }
        }

        private static readonly System.Collections.Generic.Dictionary<Action, Delegate[]> Lists = new System.Collections.Generic.Dictionary<Action, Delegate[]>();

        /// <summary>Runs each handler on its own, so one failing doesn't stop the rest; the handler list is cached per subscription set.</summary>
        private static void Run(Action work, string what)
        {
            if (work == null) return;
            Delegate[] handlers;
            if (!Lists.TryGetValue(work, out handlers))
            {
                if (Lists.Count > 8) Lists.Clear();
                handlers = work.GetInvocationList();
                Lists[work] = handlers;
            }
            foreach (Delegate handler in handlers)
            {
                try
                {
                    ((Action)handler)();
                }
                catch (Exception e)
                {
                    Log.Failure("Running " + what + " work (" + handler.Method.DeclaringType.Name + "." + handler.Method.Name + ")", e);
                }
            }
        }
    }
}
