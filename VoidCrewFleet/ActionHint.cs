using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// The one line of action text below the middle of the screen ("V  PUNCH ...", "HOLD FIRE  ATTACH ..."), shared by
    /// the features that show one, so they don't overwrite each other: the highest priority text that is set shows.
    /// </summary>
    internal static class ActionHint
    {
        public const int PunchPriority = 0;
        public const int BreachPriority = 10;

        private static readonly Dictionary<string, KeyValuePair<int, string>> Texts = new Dictionary<string, KeyValuePair<int, string>>();
        private static UIDocument _document;
        private static Label _label;

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
                if (_label != null) _label.style.display = DisplayStyle.None;
                return;
            }
            if (!Ensure()) return;
            _label.text = shown;
            _label.style.display = DisplayStyle.Flex;
        }

        private static bool Ensure()
        {
            if (_label != null && _label.panel != null) return true;
            if (_document == null)
            {
                _document = Ui.CreateOverlay("Void Crew Fleet action hint", 4000);
                if (_document == null) return false;
            }
            VisualElement root = _document.rootVisualElement;
            if (root == null) return false;
            root.pickingMode = PickingMode.Ignore;
            _label = Ui.Label("", 22, Ui.Warning, true);
            _label.style.position = Position.Absolute;
            _label.style.left = 0;
            _label.style.right = 0;
            _label.style.top = new Length(57, LengthUnit.Percent);
            _label.style.unityTextAlign = TextAnchor.MiddleCenter;
            _label.style.display = DisplayStyle.None;
            root.Add(_label);
            return true;
        }
    }
}
