using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// One row in the style of the game's ship panel (top left), built like the panel's hull row from the game's own art: the
    /// diamond with the game's ship icon, the bar start, the plate with a bold and a regular label, and the value plate with
    /// its pointed end. Used for the local player's team and ship (CrewBadge). (The other ships' cards under the crew list
    /// copy the hull row itself: HudHullRow, in ShipCards; they use this row's measurements.)
    /// </summary>
    internal sealed class HudShipRow
    {
        // The panel's hull row, which this row copies: 363 x 22 from x 17, its diamond in the hull diamond's column, 3 px longer
        // so its right end comes under the bars' value plates.
        public const float Width = 363f;
        public const float Left = 17f;
        private const float Reach = 3f;

        /// <summary>The diamond is 40 px tall on the 22 px row: it reaches this far above and below it.</summary>
        public const float DiamondOverhang = 9f;

        // The HUD's red for a value gone wrong (--ui-color__panic-red).
        public static readonly Color LostRed = new Color32(0xFF, 0x00, 0x00, 0xFF);

        public readonly VisualElement Row;
        public readonly VisualElement Icon;
        public readonly Label Bold;
        public readonly Label Regular;
        public readonly Label Value;
        private readonly VisualElement _start;
        private readonly VisualElement _body;
        private readonly StyleBackground _mainStart;
        private readonly StyleBackground _subStart;

        /// <summary>
        /// Builds the row from the art of the game's HUD (root: its document's tree): diamond, bar start, plate, value plate.
        /// Its parts are named after prefix ("FleetCrew": FleetCrewPanel, FleetCrewIcon and so on).
        /// </summary>
        public HudShipRow(VisualElement root, string prefix)
        {
            VisualElement powerBar = root.Q("PowerBarVE");
            VisualElement oxygenBar = root.Q("OxygenBarVE");
            _mainStart = Ui.GameArt(powerBar != null ? powerBar.Q("StartOfBar") : null, "main_hud_bars_bar_start");
            _subStart = Ui.GameArt(oxygenBar != null ? oxygenBar.Q("StartOfBar") : null, "main_hud_bars_subbar_start");

            Row = Part(prefix + "Panel");
            Row.style.flexDirection = FlexDirection.Row;
            Row.style.alignItems = Align.Center;
            Row.style.width = Width + Reach;
            Row.style.height = 22;
            Row.style.flexShrink = 0;

            VisualElement iconBackground = Part(prefix + "IconBg");
            iconBackground.AddToClassList("bg-tint_gray");
            iconBackground.style.backgroundImage = Ui.GameArt(root.Q("HullIconBg"), "shape_diamond_background");
            iconBackground.style.width = 40;
            iconBackground.style.height = 40;
            iconBackground.style.flexShrink = 0;
            VisualElement iconBorder = Part(prefix + "IconBorder");
            iconBorder.style.backgroundImage = Ui.GameArt(root.Q("HullIconBorder"), "shape_diamond_border");
            iconBorder.style.flexGrow = 1;
            Ui.Padding(iconBorder, 9, 9);
            Icon = Part(prefix + "Icon");
            Icon.style.flexGrow = 1;
            Sprite shipIcon = Ui.GameSprite("icon_ship_frigate");
            if (shipIcon != null) Icon.style.backgroundImage = new StyleBackground(shipIcon);
            Icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            iconBorder.Add(Icon);
            iconBackground.Add(iconBorder);

            VisualElement bar = Part(prefix + "BarVE");
            bar.style.flexGrow = 1;
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.height = 22;
            bar.style.alignItems = Align.Center;
            bar.style.translate = new Translate(new Length(-6f), new Length(0f), 0f);
            _start = Part(prefix + "StartOfBar");
            _start.AddToClassList("bg-tint_gray");
            _start.style.height = Length.Percent(100);
            _start.style.flexShrink = 0;
            _body = Part(prefix + "Plate");
            _body.AddToClassList("bg-color_gray");
            _body.style.flexGrow = 1;
            _body.style.height = Length.Percent(100);
            _body.style.flexDirection = FlexDirection.Row;
            _body.style.alignItems = Align.Center;
            Bold = Text(true);
            Bold.style.marginRight = 8;
            Regular = Text(false);
            _body.Add(Bold);
            _body.Add(Regular);
            // The game's value plate with its pointed end, as the hull row's 100%.
            Value = new Label { name = prefix + "Value", pickingMode = PickingMode.Ignore };
            Value.AddToClassList("main-hud-label-bar");
            Value.AddToClassList("hud-resource__value");
            Value.AddToClassList("ui-text-color__white");
            Value.style.fontSize = 14;
            Value.style.whiteSpace = WhiteSpace.NoWrap;
            Value.style.marginLeft = -1;
            Value.style.flexShrink = 0;
            bar.Add(_start);
            bar.Add(_body);
            bar.Add(Value);

            Row.Add(iconBackground);
            Row.Add(bar);
            StartAs(true);
        }

        /// <summary>
        /// How the bar starts: a row of its own like the power row; a row under another like the player panel's oxygen row,
        /// cut back for the diamond above. Either way its text lines up with the bars above.
        /// </summary>
        public void StartAs(bool own)
        {
            _start.style.backgroundImage = own ? _mainStart : _subStart;
            _start.style.width = own ? 22 : 34;
            _body.style.marginLeft = own ? 0 : -2;
            _body.style.paddingLeft = own ? 25 : 15;
        }

        private static VisualElement Part(string name)
        {
            return new VisualElement { name = name, pickingMode = PickingMode.Ignore };
        }

        /// <summary>Text on the plate as the HUD's values are written: 14 px, white, no shadow.</summary>
        private static Label Text(bool bold)
        {
            var label = new Label { pickingMode = PickingMode.Ignore };
            label.style.fontSize = 14;
            label.style.unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal;
            label.style.color = Color.white;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            Ui.Margin(label, 0, 0, 0, 0);
            Ui.Padding(label, 0, 0);
            return label;
        }
    }
}
