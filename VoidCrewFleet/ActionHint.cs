using System.Collections.Generic;
using CG.GameLoopStateMachine;
using CG.GameLoopStateMachine.GameStates;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// The one line of action text below the middle of the screen ("V  PUNCH ...", "HOLD FIRE  ATTACH ..."), shared by
    /// the features that show one, so they don't overwrite each other: the highest priority text that is set shows. It is
    /// drawn like the game's own key prompts: the key (the text before the first double space) in a key-cap box, then
    /// the action in the HUD's bold white text.
    /// </summary>
    internal static class ActionHint
    {
        public const int PunchPriority = 0;
        public const int HackPriority = 10;

        private static readonly Dictionary<string, KeyValuePair<int, string>> Texts = new Dictionary<string, KeyValuePair<int, string>>();
        private static UIDocument _document;
        private static VisualElement _row;
        private static Label _key;
        private static Label _label;
        private static float _nextPrepare;

        /// <summary>
        /// Makes the overlay before the first hint, as a PvP match starts: making it holds up a frame (about 20 ms), which would
        /// otherwise land on the first punch or hack hint, in the middle of a fight. Only once the game itself is up (the
        /// overlay copies its screens' settings, not a loading screen's), tried again a second later.
        /// </summary>
        public static void Prepare()
        {
            if (_row != null && _row.panel != null) return;
            if (Time.unscaledTime < _nextPrepare) return;
            _nextPrepare = Time.unscaledTime + 1f;
            GameStateMachine machine = GameStateMachine.Instance;
            if (machine == null || !(machine.CurrentState is GSIngame)) return;
            Ensure();
        }

        /// <summary>Sets (or with null or empty text, clears) one feature's text.</summary>
        public static void Set(string source, int priority, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                if (!Texts.Remove(source)) return;
            }
            else
            {
                KeyValuePair<int, string> current;
                if (Texts.TryGetValue(source, out current) && current.Key == priority && current.Value == text) return;
                Texts[source] = new KeyValuePair<int, string>(priority, text);
            }
            Refresh();
        }

        private static void Refresh()
        {
            string shown = null;
            int best = int.MinValue;
            foreach (KeyValuePair<int, string> entry in Texts.Values)
            {
                if (entry.Key <= best) continue;
                best = entry.Key;
                shown = entry.Value;
            }
            if (shown == null)
            {
                if (_row != null) _row.style.display = DisplayStyle.None;
                return;
            }
            if (!Ensure()) return;
            int split = shown.IndexOf("  ", System.StringComparison.Ordinal);
            string key = split > 0 ? shown.Substring(0, split).Trim() : null;
            _key.text = key ?? "";
            _key.style.display = string.IsNullOrEmpty(key) ? DisplayStyle.None : DisplayStyle.Flex;
            _label.text = split > 0 ? shown.Substring(split).Trim() : shown;
            _row.style.display = DisplayStyle.Flex;
        }

        private static bool Ensure()
        {
            if (_row != null && _row.panel != null) return true;
            if (_document == null)
            {
                _document = Ui.CreateOverlay("Void Crew Fleet action hint", 4000);
                if (_document == null) return false;
            }
            VisualElement root = _document.rootVisualElement;
            if (root == null) return false;
            root.pickingMode = PickingMode.Ignore;
            _row = new VisualElement { pickingMode = PickingMode.Ignore };
            _row.style.position = Position.Absolute;
            _row.style.left = 0;
            _row.style.right = 0;
            _row.style.top = new Length(57, LengthUnit.Percent);
            _row.style.flexDirection = FlexDirection.Row;
            _row.style.justifyContent = Justify.Center;
            _row.style.alignItems = Align.Center;

            _key = Ui.HudLabel("", 17, Color.white);
            _key.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            Ui.Border(_key, 2, Color.white);
            Ui.Radius(_key, 3);
            Ui.Padding(_key, 1, 8);
            _key.style.marginRight = 10;
            _key.style.unityTextAlign = TextAnchor.MiddleCenter;
            _row.Add(_key);
            _label = Ui.HudLabel("", 19, Color.white);
            _row.Add(_label);
            _row.style.display = DisplayStyle.None;
            root.Add(_row);
            return true;
        }
    }
}
