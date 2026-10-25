using System;
using System.Collections.Generic;
using System.Reflection;
using CG.GameLoopStateMachine;
using CG.GameLoopStateMachine.GameStates;
using UI.Core;
using UI.MainHUD;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// The one action prompt of the PvP features (punch, raise your fists, plug in or pull out a hacking terminal), shared by
    /// the features that show one, so they don't overwrite each other: the highest priority one that is set shows. It is one
    /// of the game's own key prompts (KeyBindVE: the key's own icon for the keyboard and mouse or the gamepad in use, the hold
    /// mark under it, its fill for a hold's progress, and the action's text), in the game's own HUD, in its row of prompts at
    /// the bottom of the screen, where the game shows its own ("HOLD [F] Leave"). So it shows, hides and scales with the HUD,
    /// and hides with the game's prompts when a player turns key prompts off in the game's settings.
    /// </summary>
    internal static class ActionHint
    {
        public const int PunchPriority = 0;
        public const int HackPriority = 10;

        /// <summary>The game's prompt's key (a private property), given once the prompt is in the HUD, as CrewList gives its own.</summary>
        private static readonly PropertyInfo PromptKey = GameMember.Property(typeof(KeyBindVE), "Key");

        /// <summary>One feature's prompt: its key (an input control path, null for none), whether it is held, its text and a hold's progress.</summary>
        private struct Hint
        {
            public int Priority;
            public string Key;
            public bool Hold;
            public string Action;
            public float Progress;
        }

        private static readonly Dictionary<string, Hint> Hints = new Dictionary<string, Hint>();

        /// <summary>
        /// An input action of the prompt's own for each key it has shown: the game's prompts take their icon from an action's
        /// binding. These are never switched on (nothing reads them as input), and their bindings are none of the game's
        /// settings, so the game never writes a setting's name over the prompt's text.
        /// </summary>
        private static readonly Dictionary<string, InputAction> KeyActions = new Dictionary<string, InputAction>();

        // The HUD the prompt was put in (a document makes a new tree when it is enabled again), the prompt, and what it shows.
        private static UIDocument _hud;
        private static VisualElement _hudRoot;
        private static KeyBindVE _prompt;
        private static Label _action;
        private static string _shownKey;
        private static bool _shownHold;
        private static float _shownProgress = -1f;
        private static float _nextPrepare;

        /// <summary>
        /// Puts the prompt into the game's HUD ahead of the first hint, as a PvP match starts (tried once a second until the
        /// game is in the match and its HUD is there).
        /// </summary>
        public static void Prepare()
        {
            // A new HUD (the next mission, or its document enabled again) leaves the old prompt without a panel.
            if (_prompt != null && _prompt.panel != null) return;
            if (Time.unscaledTime < _nextPrepare) return;
            _nextPrepare = Time.unscaledTime + 1f;
            GameStateMachine machine = GameStateMachine.Instance;
            if (machine == null || !(machine.CurrentState is GSIngame)) return;
            try
            {
                // A prompt set while there was no HUD (or a HUD that has since been replaced) shows now.
                if (Ensure() && Hints.Count > 0) Refresh();
            }
            catch (Exception e)
            {
                Log.Failure("Putting the action prompt into the HUD", e);
            }
        }

        /// <summary>
        /// Sets one feature's prompt: key, the input control path of the key to press (for example "&lt;Mouse&gt;/leftButton"), or
        /// null for a text without a key; hold, whether the key is held (the game's hold mark); progress, how far a hold has got
        /// (0 to 1, the key's fill). Null or empty text clears it.
        /// </summary>
        public static void Set(string source, int priority, string key, bool hold, string action, float progress = 0f)
        {
            if (string.IsNullOrEmpty(action))
            {
                Clear(source);
                return;
            }
            Hint current;
            if (Hints.TryGetValue(source, out current) && current.Priority == priority && current.Key == key && current.Hold == hold && current.Action == action &&
                current.Progress == progress)
                return;
            Hints[source] = new Hint { Priority = priority, Key = key, Hold = hold, Action = action, Progress = progress };
            Refresh();
        }

        /// <summary>Clears one feature's prompt.</summary>
        public static void Clear(string source)
        {
            if (Hints.Remove(source)) Refresh();
        }

        private static void Refresh()
        {
            try
            {
                Hint shown = default(Hint);
                bool any = false;
                foreach (Hint hint in Hints.Values)
                {
                    if (any && hint.Priority <= shown.Priority) continue;
                    shown = hint;
                    any = true;
                }
                if (!any)
                {
                    if (_prompt != null) _prompt.style.display = DisplayStyle.None;
                    return;
                }
                if (!Ensure()) return;
                string key = shown.Key ?? "";
                if (key != _shownKey) SetKey(key);
                if (shown.Hold != _shownHold)
                {
                    _shownHold = shown.Hold;
                    // The game's mark under a key that is held (its prompts' "hold" state), not shown for a press.
                    _prompt.SetClassState("keybind-key-interaction", shown.Hold ? "hold" : "press");
                }
                if (_action != null && _action.text != shown.Action) _action.text = shown.Action;
                if (shown.Progress != _shownProgress)
                {
                    _shownProgress = shown.Progress;
                    _prompt.SetProgress(shown.Progress);
                }
                _prompt.style.display = DisplayStyle.Flex;
            }
            catch (Exception e)
            {
                Log.Failure("Showing the action prompt", e);
            }
        }

        /// <summary>The game's HUD document now, or null (outside a mission).</summary>
        private static UIDocument MainHud()
        {
            MainHUDUI hud = MainHUDUI.Instance;
            return hud != null ? hud.GetComponent<UIDocument>() : null;
        }

        /// <summary>
        /// The prompt, in the game's HUD's row of prompts (made again when the HUD's document or tree is replaced). False while
        /// there is no HUD, and when the HUD has no such row (a game update renamed it: logged once, and nothing shows).
        /// </summary>
        private static bool Ensure()
        {
            UIDocument hud = MainHud();
            VisualElement root = hud != null ? hud.rootVisualElement : null;
            if (root == null) return false;
            if (_prompt != null && hud == _hud && root == _hudRoot && _prompt.panel != null) return true;
            if (_prompt != null) _prompt.RemoveFromHierarchy();
            _prompt = null;
            _action = null;
            _hud = hud;
            _hudRoot = root;
            VisualElement row = root.Q("InteractionInfoContainer");
            if (row == null)
            {
                Log.WarnOnce("action prompt row", "The game's HUD has no row of key prompts (InteractionInfoContainer), so the punch and hacking prompts don't show.");
                return false;
            }
            MakePrompt(row, "");
            return _prompt != null;
        }

        /// <summary>
        /// A new prompt at the end of the HUD's row. It is made without a key (attaching makes a prompt look its key up among the
        /// game's own actions, and warn when it isn't one of them); SetKey gives it one.
        /// </summary>
        private static void MakePrompt(VisualElement row, string key)
        {
            var prompt = new KeyBindVE("FleetActionHint", key, false) { pickingMode = PickingMode.Ignore };
            prompt.style.display = DisplayStyle.None;
            row.Add(prompt);
            _prompt = prompt;
            _action = prompt.Q<Label>("Action");
            // An enemy's name in the text shows as typed, never as markup.
            if (_action != null) _action.enableRichText = false;
            _shownKey = key;
            _shownHold = false;
            _shownProgress = -1f;
            _prompt.SetClassState("keybind-key-interaction", "press");
        }

        /// <summary>The prompt's key: the icon of that control path for the device in use, or none ("").</summary>
        private static void SetKey(string key)
        {
            InputAction input = key.Length > 0 ? KeyAction(key, null) : null;
            if (!GiveKey(_prompt, key, input))
            {
                // Without the property, a new prompt made with the key (it warns once in the game's own log as it attaches).
                VisualElement row = _prompt.parent;
                _prompt.RemoveFromHierarchy();
                MakePrompt(row, key);
                _prompt.Init(null, input);
            }
            _shownKey = key;
            // Init starts the fill again.
            _shownProgress = -1f;
        }

        /// <summary>
        /// Gives one of the game's key prompts that is in a panel its key ("" for none) and the input action whose binding
        /// gives its icon (null for none). False when the game's prompt no longer has the property the key is set through.
        /// </summary>
        internal static bool GiveKey(KeyBindVE prompt, string key, InputAction input)
        {
            if (PromptKey == null) return false;
            PromptKey.SetValue(prompt, key, null);
            prompt.Init(null, input);
            return true;
        }

        /// <summary>
        /// The prompts' own input action for a key: its binding, and a gamepad binding last when given (the game's prompts take
        /// the first binding's icon for the keyboard and mouse, the last one's for a gamepad). Made once for each key.
        /// </summary>
        internal static InputAction KeyAction(string path, string padPath)
        {
            string id = padPath == null ? path : path + "|" + padPath;
            InputAction action;
            if (KeyActions.TryGetValue(id, out action)) return action;
            // Made with no binding and empty interactions, then given its bindings: the game's prompts read an action's own
            // interactions, which an action made with its binding leaves null (CrewList's keys are made the same way).
            action = new InputAction("Fleet prompt " + KeyActions.Count, InputActionType.Button, null, "");
            action.AddBinding(path);
            if (padPath != null) action.AddBinding(padPath);
            KeyActions[id] = action;
            return action;
        }
    }
}
