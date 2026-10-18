using System;
using CG.Client;
using UI;
using UI.Core.Audio;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// Small helpers for building UI Toolkit elements in code. Everything is styled inline, so it looks the same whatever
    /// style sheets the panel has loaded, and clicks use the game's own click filters so they behave like its buttons.
    /// Only UI Toolkit (or uGUI) is used, never IMGUI: VR UI Fix draws screen-space UI Toolkit panels into the headset.
    /// </summary>
    internal static class Ui
    {
        public static readonly Color Gold = new Color(0.96f, 0.71f, 0.22f, 1f);
        public static readonly Color Window = new Color(0.06f, 0.065f, 0.07f, 0.97f);
        public static readonly Color Backdrop = new Color(0f, 0f, 0f, 0.6f);
        public static readonly Color Row = new Color(0.13f, 0.135f, 0.14f, 1f);
        public static readonly Color RowHover = new Color(0.22f, 0.225f, 0.23f, 1f);
        public static readonly Color ButtonColor = new Color(0.17f, 0.175f, 0.18f, 0.95f);
        public static readonly Color ButtonHover = new Color(0.3f, 0.3f, 0.31f, 0.95f);
        public static readonly Color Text = new Color(0.93f, 0.93f, 0.93f, 1f);
        public static readonly Color TextDim = new Color(0.62f, 0.63f, 0.65f, 1f);
        public static readonly Color Warning = new Color(1f, 0.45f, 0.35f, 1f);
        public static readonly Color TeamA = new Color(0.3f, 0.65f, 1f, 1f);
        public static readonly Color TeamB = new Color(1f, 0.42f, 0.3f, 1f);

        public static Label Label(string text, int size, Color color, bool bold = false)
        {
            var label = new Label(text);
            label.pickingMode = PickingMode.Ignore;
            label.style.fontSize = size;
            label.style.color = color;
            label.style.unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal;
            Margin(label, 0, 0, 0, 0);
            Padding(label, 0, 0);
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        /// <summary>A flat button: a box with a label, highlighted on hover.</summary>
        public static VisualElement Button(string text, Action onClick, int size = 20)
        {
            var button = new VisualElement();
            button.style.flexDirection = FlexDirection.Row;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;
            Padding(button, 6, 14);
            Margin(button, 0, 6, 0, 0);
            Radius(button, 3);
            Border(button, 1, new Color(1f, 1f, 1f, 0.18f));
            button.style.backgroundColor = ButtonColor;
            button.Add(Label(text, size, Text, true));
            Hover(button, ButtonColor, ButtonHover);
            Click(button, onClick);
            return button;
        }

        /// <summary>Changes the text of a button made by <see cref="Button"/>.</summary>
        public static void SetButtonText(VisualElement button, string text)
        {
            var label = button.Q<Label>();
            if (label != null) label.text = text;
        }

        /// <summary>Makes any element clickable with the left mouse button, the same way the game's lists are.</summary>
        public static Clickable Click(VisualElement element, Action onClick)
        {
            var clickable = new Clickable(() =>
            {
                try
                {
                    onClick();
                }
                catch (Exception e)
                {
                    Log.Failure("Handling a click in Void Crew Fleet", e);
                }
            });
            element.AddUnfilteredClickable(clickable);
            try
            {
                UIAudioProvider.AddSounds(element);
            }
            catch
            {
                // Sounds are a nicety.
            }
            return clickable;
        }

        public static void Hover(VisualElement element, Color normal, Color hover)
        {
            element.RegisterCallback<PointerEnterEvent>(e => element.style.backgroundColor = hover);
            element.RegisterCallback<PointerLeaveEvent>(e => element.style.backgroundColor = normal);
        }

        /// <summary>Tells the game a new element is on screen, as it does for its own (sounds, navigation, localisation).</summary>
        public static void Announce(VisualElement element)
        {
            try
            {
                if (element.panel != null && ViewEventBus.Instance != null) ViewEventBus.Instance.VEAddedtoPanel.Publish(element, element.panel);
            }
            catch
            {
                // Only a nicety.
            }
        }

        public static void Border(VisualElement element, float width, Color color)
        {
            element.style.borderLeftWidth = width;
            element.style.borderRightWidth = width;
            element.style.borderTopWidth = width;
            element.style.borderBottomWidth = width;
            element.style.borderLeftColor = color;
            element.style.borderRightColor = color;
            element.style.borderTopColor = color;
            element.style.borderBottomColor = color;
        }

        public static void Radius(VisualElement element, float radius)
        {
            element.style.borderTopLeftRadius = radius;
            element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = radius;
            element.style.borderBottomRightRadius = radius;
        }

        public static void Padding(VisualElement element, float vertical, float horizontal)
        {
            element.style.paddingTop = vertical;
            element.style.paddingBottom = vertical;
            element.style.paddingLeft = horizontal;
            element.style.paddingRight = horizontal;
        }

        public static void Margin(VisualElement element, float top, float right, float bottom, float left)
        {
            element.style.marginTop = top;
            element.style.marginRight = right;
            element.style.marginBottom = bottom;
            element.style.marginLeft = left;
        }

        public static void Fill(VisualElement element)
        {
            element.style.position = Position.Absolute;
            element.style.left = 0;
            element.style.top = 0;
            element.style.right = 0;
            element.style.bottom = 0;
        }

        public static VisualElement HorizontalRow(float gap = 0)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            if (gap > 0) row.style.marginBottom = gap;
            return row;
        }

        public static VisualElement Column()
        {
            var column = new VisualElement();
            column.style.flexDirection = FlexDirection.Column;
            return column;
        }

        /// <summary>
        /// A full-screen UI Toolkit panel of the plugin's own, drawn above the game's screens: it uses a copy of the game's own
        /// panel settings (same theme and fonts) with a higher sort order. VR UI Fix draws it into the headset like any
        /// other screen-space panel. Null until the game has a screen-space panel to copy.
        /// </summary>
        public static UIDocument CreateOverlay(string name, int sortingOrder)
        {
            // The top panel gives the scaling; the theme most panels use gives the fonts (a fade panel may have none).
            PanelSettings top = null;
            var themes = new System.Collections.Generic.Dictionary<ThemeStyleSheet, int>();
            foreach (UIDocument document in UnityEngine.Object.FindObjectsOfType<UIDocument>())
            {
                PanelSettings panel = document != null ? document.panelSettings : null;
                if (panel == null || panel.targetTexture != null) continue;
                if (top == null || panel.sortingOrder > top.sortingOrder) top = panel;
                if (panel.themeStyleSheet == null) continue;
                int count;
                themes.TryGetValue(panel.themeStyleSheet, out count);
                themes[panel.themeStyleSheet] = count + 1;
            }
            if (top == null) return null;
            PanelSettings settings = UnityEngine.Object.Instantiate(top);
            settings.name = name + " panel";
            settings.sortingOrder = Mathf.Max(sortingOrder, top.sortingOrder + 10);
            settings.clearColor = false;
            ThemeStyleSheet theme = null;
            int uses = 0;
            foreach (System.Collections.Generic.KeyValuePair<ThemeStyleSheet, int> pair in themes)
            {
                if (pair.Value <= uses) continue;
                theme = pair.Key;
                uses = pair.Value;
            }
            if (theme != null) settings.themeStyleSheet = theme;
            var holder = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(holder);
            UIDocument overlay = holder.AddComponent<UIDocument>();
            overlay.panelSettings = settings;
            return overlay;
        }

        /// <summary>A dark rounded window, centred by its parent.</summary>
        public static VisualElement WindowBox(float width)
        {
            var box = new VisualElement();
            box.style.width = width;
            box.style.backgroundColor = Window;
            Padding(box, 18, 22);
            Radius(box, 6);
            Border(box, 1, new Color(1f, 1f, 1f, 0.15f));
            return box;
        }
    }
}
