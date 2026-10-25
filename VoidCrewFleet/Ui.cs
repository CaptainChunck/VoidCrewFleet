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
        public static readonly Color Warning = new Color(1f, 0.45f, 0.35f, 1f);

        // The game's own palette (its SharedUIColorTheme).
        public static readonly Color GameOrange = new Color32(0xF6, 0xAE, 0x3C, 0xFF);
        public static readonly Color GameDarkGrey = new Color32(0x30, 0x30, 0x30, 0xFF);
        public static readonly Color GameLightGrey = new Color32(0x69, 0x6A, 0x6D, 0xFF);
        public static readonly Color GameBrightGrey = new Color32(0xDF, 0xDF, 0xDF, 0xFF);
        public static readonly Color GameBackdrop = new Color32(0x00, 0x00, 0x00, 0xEB);
        public static readonly Color GameItemBackground = new Color32(0x14, 0x13, 0x10, 0xFF);

        private static readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();

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

        /// <summary>The game's panel settings the last overlay copied, and the theme it gave it (FindTemplate).</summary>
        private static PanelSettings _templatePanel;

        private static ThemeStyleSheet _templateTheme;

        /// <summary>
        /// A full-screen UI Toolkit panel of the plugin's own, drawn above the game's screens: it uses a copy of the game's own
        /// panel settings (same theme and fonts) with a higher sort order. VR UI Fix draws it into the headset like any
        /// other screen-space panel. Null until the game has a screen-space panel to copy.
        /// </summary>
        public static UIDocument CreateOverlay(string name, int sortingOrder)
        {
            // The game's panel found for an earlier overlay serves again while it exists (the top one is the voice panel's,
            // which lasts as long as the game): finding it is a search of the whole scene.
            if (_templatePanel == null) FindTemplate();
            PanelSettings top = _templatePanel;
            if (top == null) return null;
            PanelSettings settings = UnityEngine.Object.Instantiate(top);
            settings.name = name + " panel";
            settings.sortingOrder = Mathf.Min(Mathf.Max(sortingOrder, top.sortingOrder + 10), 29990);
            settings.clearColor = false;
            if (_templateTheme != null) settings.themeStyleSheet = _templateTheme;
            var holder = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(holder);
            UIDocument overlay = holder.AddComponent<UIDocument>();
            overlay.panelSettings = settings;
            return overlay;
        }

        /// <summary>
        /// The game's screen-space panel overlays copy: the top one gives the scaling, and the theme most panels use gives the
        /// fonts (a fade panel may have none).
        /// </summary>
        private static void FindTemplate()
        {
            PanelSettings top = null;
            var themes = new Dictionary<ThemeStyleSheet, int>();
            foreach (UIDocument document in UnityEngine.Object.FindObjectsOfType<UIDocument>())
            {
                PanelSettings panel = document != null ? document.panelSettings : null;
                if (panel == null || panel.targetTexture != null) continue;
                // The Steam Deck's software cursor lives in a panel at 30000; ours stay under it.
                if (panel.sortingOrder < 30000 && (top == null || panel.sortingOrder > top.sortingOrder)) top = panel;
                if (panel.themeStyleSheet == null) continue;
                int count;
                themes.TryGetValue(panel.themeStyleSheet, out count);
                themes[panel.themeStyleSheet] = count + 1;
            }
            ThemeStyleSheet theme = null;
            int uses = 0;
            foreach (KeyValuePair<ThemeStyleSheet, int> pair in themes)
            {
                if (pair.Value <= uses) continue;
                theme = pair.Key;
                uses = pair.Value;
            }
            _templatePanel = top;
            _templateTheme = theme;
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
            return Find(name, Sprites, SpriteMisses);
        }

        /// <summary>One of the game's UI textures by name, while a screen that uses it is loaded; null otherwise.</summary>
        public static Texture2D GameTexture(string name)
        {
            return Find(name, Textures, TextureMisses);
        }

        /// <summary>When a search last didn't find each name (GameSprite, GameTexture).</summary>
        private static readonly Dictionary<string, float> SpriteMisses = new Dictionary<string, float>();

        private static readonly Dictionary<string, float> TextureMisses = new Dictionary<string, float>();

        /// <summary>How long a name a search didn't find is taken as not loaded before it is looked for again.</summary>
        private const float MissSeconds = 2f;

        /// <summary>
        /// An asset of the game's by name. Looking one up searches every loaded asset of its type, comparing names (each read
        /// makes a string), so each search also looks for every other name still missing, and a name it didn't find is only
        /// looked for again after MissSeconds: a screen built of several missing pieces of art searches once, not once per
        /// piece. Only the names asked for are kept (keeping every asset would keep the game from unloading them).
        /// </summary>
        private static T Find<T>(string name, Dictionary<string, T> found, Dictionary<string, float> missed) where T : UnityEngine.Object
        {
            T asset;
            if (found.TryGetValue(name, out asset) && asset != null) return asset;
            float now = Time.unscaledTime;
            float at;
            if (missed.TryGetValue(name, out at) && now - at < MissSeconds) return null;
            missed[name] = now;
            // Every name to look for: this one, those not found before, and those whose asset is gone (unloaded with its screen).
            var wanted = new HashSet<string>(missed.Keys);
            foreach (KeyValuePair<string, T> known in found)
            {
                if (known.Value == null) wanted.Add(known.Key);
            }
            foreach (T candidate in Resources.FindObjectsOfTypeAll<T>())
            {
                if (candidate == null) continue;
                string candidateName = candidate.name;
                // The first asset of a name, as before.
                if (!wanted.Remove(candidateName)) continue;
                found[candidateName] = candidate;
                missed.Remove(candidateName);
                if (wanted.Count == 0) break;
            }
            foreach (string gone in wanted) missed[gone] = now;
            return found.TryGetValue(name, out asset) && asset != null ? asset : null;
        }

        /// <summary>
        /// The picture one of the game's own elements shows (its resolved background image), else the game's texture of that
        /// name: how a copy of a game element gets the same art.
        /// </summary>
        public static StyleBackground GameArt(VisualElement source, string texture)
        {
            if (source != null)
            {
                Background shown = source.resolvedStyle.backgroundImage;
                if (shown.texture != null || shown.sprite != null || shown.vectorImage != null || shown.renderTexture != null) return new StyleBackground(shown);
            }
            Texture2D found = GameTexture(texture);
            return found != null ? new StyleBackground(found) : new StyleBackground(StyleKeyword.None);
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

        /// <summary>A GameSection width: the sections of a row share its width equally.</summary>
        public const float SharedWidth = 0f;

        /// <summary>
        /// A section in the style of the loadout terminal's (SHIP SYSTEMS, RESOURCES): a big white header over the game's
        /// decorative line, then the content, and the line again below. With the terminal's style sheet loaded the lines
        /// turn white under the pointer, as its own do. Its width in reference pixels, or SharedWidth.
        /// </summary>
        public static VisualElement GameSection(string title, float width, out Label header, out VisualElement content)
        {
            var section = new VisualElement();
            section.AddToClassList("ship-loadout-subpanel");
            if (width > 0f)
            {
                section.style.width = width;
            }
            else
            {
                // SharedWidth: an equal share of its row, whatever the screen's width.
                section.style.flexGrow = 1;
                section.style.flexBasis = 0;
            }
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
