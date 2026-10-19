using System;
using System.Collections.Generic;
using CG.Client;
using UI;
using UI.Core.Audio;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// Small helpers for building UI Toolkit elements in code. Screens meant to look like the game's own use the game's
    /// style classes (its buttons, headers, decorative lines and popup frames, from its own style sheets); small overlays
    /// are styled inline. Clicks use the game's own click filters so they behave like its buttons. Only UI Toolkit (or
    /// uGUI) is used, never IMGUI: VR UI Fix draws screen-space UI Toolkit panels into the headset.
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

        // The game's own palette (its SharedUIColorTheme).
        public static readonly Color GameOrange = new Color32(0xF6, 0xAE, 0x3C, 0xFF);
        public static readonly Color GameDarkGrey = new Color32(0x30, 0x30, 0x30, 0xFF);
        public static readonly Color GameLightGrey = new Color32(0x69, 0x6A, 0x6D, 0xFF);
        public static readonly Color GameBrightGrey = new Color32(0xDF, 0xDF, 0xDF, 0xFF);
        public static readonly Color GameLightBlue = new Color32(0xBD, 0xE7, 0xFB, 0xFF);
        public static readonly Color GameDecoBrown = new Color32(0x5E, 0x54, 0x4A, 0xFF);
        public static readonly Color GameBackdrop = new Color32(0x00, 0x00, 0x00, 0xEB);
        public static readonly Color GameItemBackground = new Color32(0x14, 0x13, 0x10, 0xFF);

        private static readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();

        /// <summary>The game's own style sheets its popups use. Without the first four a screen keeps its own look.</summary>
        private static readonly string[] GameSheetNames =
        {
            "SharedUIColorTheme", "SharedUI_TextStyles", "SharedUI_Styles", "PopupBaseStyle",
            // Optional: the panel theme already brings the buttons; the scrollbars and sounds are niceties, and the loadout
            // terminal's sheet (loaded in the hub) makes section lines light up under the pointer as the terminal's do.
            "SharedUI_Buttons", "SharedUI_AudioStyles", "ListScrollStyle", "ShipLoadoutStyles"
        };

        private const int RequiredGameSheets = 4;

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

        // ---------------------------------------------------------------- the game's own look

        /// <summary>
        /// Attaches the game's own style sheets (found by name among the loaded ones) to an element of one of our overlays,
        /// so the game's classes style it the way they style the game's popups. False when one isn't loaded: the caller then
        /// sets the same look inline.
        /// </summary>
        public static bool UseGameStyles(VisualElement element)
        {
            var found = new Dictionary<string, StyleSheet>();
            foreach (StyleSheet sheet in Resources.FindObjectsOfTypeAll<StyleSheet>())
            {
                if (sheet != null && Array.IndexOf(GameSheetNames, sheet.name) >= 0 && !found.ContainsKey(sheet.name)) found[sheet.name] = sheet;
            }
            var missing = new List<string>();
            for (int i = 0; i < GameSheetNames.Length; i++)
            {
                StyleSheet sheet;
                if (found.TryGetValue(GameSheetNames[i], out sheet))
                {
                    if (!element.styleSheets.Contains(sheet)) element.styleSheets.Add(sheet);
                }
                else if (i < RequiredGameSheets)
                {
                    missing.Add(GameSheetNames[i]);
                }
            }
            if (missing.Count > 0) Log.Warn("The game's style sheets " + string.Join(", ", missing.ToArray()) + " aren't loaded: a Fleet screen uses its own look instead.");
            return missing.Count == 0;
        }

        /// <summary>
        /// One of the game's own buttons: its style classes (for example metem-button-size__small, the small version of
        /// SELECT) with its hover and click sounds, and clicks wired the way the game wires its buttons.
        /// </summary>
        public static Button GameButton(string text, Action onClick, params string[] classes)
        {
            var button = new Button { text = text };
            foreach (string name in classes) button.AddToClassList(name);
            button.AddToClassList("audio-hover__button");
            button.AddToClassList("audio-click__button");
            button.AddClickedEvent(() =>
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
            try
            {
                UIAudioProvider.AddSounds(button);
            }
            catch
            {
                // Sounds are a nicety.
            }
            return button;
        }

        /// <summary>A label in the game's own text style: its classes, for example ui-text-size__large and ui-text-dropshadow.</summary>
        public static Label GameLabel(string text, params string[] classes)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            foreach (string name in classes) label.AddToClassList(name);
            return label;
        }

        /// <summary>Text in the style of the game's HUD: bold, upper case by convention, with its one-pixel drop shadow.</summary>
        public static Label HudLabel(string text, int size, Color color)
        {
            Label label = Label(text, size, color, true);
            label.style.textShadow = new StyleTextShadow(new TextShadow { offset = new Vector2(1f, 1f), blurRadius = 1f, color = Color.black });
            return label;
        }

        /// <summary>One of the game's UI sprites by name, while a screen that uses it is loaded; null otherwise.</summary>
        public static Sprite GameSprite(string name)
        {
            Sprite sprite;
            if (Sprites.TryGetValue(name, out sprite) && sprite != null) return sprite;
            foreach (Sprite candidate in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (candidate == null || candidate.name != name) continue;
                Sprites[name] = candidate;
                return candidate;
            }
            return null;
        }

        /// <summary>The meander bar along the top and bottom of the game's terminal screens (.metem-decorative-bar-repeating).</summary>
        public static VisualElement GameDecoBar()
        {
            var bar = new VisualElement { pickingMode = PickingMode.Ignore };
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.height = 15;
            bar.style.flexShrink = 0;
            var pattern = new VisualElement { pickingMode = PickingMode.Ignore };
            pattern.AddToClassList("metem-decorative-bar-repeating");
            bar.Add(pattern);
            return bar;
        }

        /// <summary>
        /// A section in the style of the loadout terminal's (SHIP SYSTEMS, RESOURCES): a big white header over the game's
        /// decorative line, then the content, and the line again below. With the terminal's style sheet loaded the lines
        /// turn white under the pointer, as its own do.
        /// </summary>
        public static VisualElement GameSection(string title, float width, out Label header, out VisualElement content)
        {
            var section = new VisualElement();
            section.AddToClassList("ship-loadout-subpanel");
            section.style.width = width;
            section.style.minWidth = 0;
            section.style.paddingLeft = 10;
            section.style.paddingRight = 10;
            header = GameLabel(title, "ui-text-size__large", "ui-text-color__white", "loadout-panel-header", "ui-text-dropshadow");
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            section.Add(header);
            section.Add(GameDivider());
            content = new VisualElement();
            content.style.flexGrow = 1;
            content.style.paddingLeft = 5;
            content.style.paddingRight = 5;
            content.style.marginTop = 6;
            content.style.marginBottom = 6;
            section.Add(content);
            section.Add(GameDivider());
            return section;
        }

        /// <summary>A row like the loadout terminal's RESOURCES list: dark grey, 34 px, its content spread along it.</summary>
        public static VisualElement GameListRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.flexShrink = 0;
            row.style.minHeight = 34;
            row.style.backgroundColor = GameDarkGrey;
            row.style.paddingLeft = 10;
            row.style.paddingRight = 10;
            row.style.paddingTop = 3;
            row.style.paddingBottom = 3;
            row.style.marginBottom = 4;
            return row;
        }

        /// <summary>The game's decorative line under a section header (.metem-decorative__box-border), optionally recoloured.</summary>
        public static VisualElement GameDivider(Color? color = null)
        {
            var line = new VisualElement { pickingMode = PickingMode.Ignore };
            line.AddToClassList("metem-decorative__box-border");
            line.style.flexShrink = 0;
            if (color.HasValue)
            {
                line.style.borderLeftColor = color.Value;
                line.style.borderRightColor = color.Value;
                line.style.borderBottomColor = color.Value;
            }
            return line;
        }
    }
}
