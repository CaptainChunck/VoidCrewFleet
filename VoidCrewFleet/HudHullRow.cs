using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// A copy of the ship panel's hull row (top left) for another ship, built from the HUD's own parts and art: the diamond with
    /// an icon, the bar start, the bordered bar with the hull's fill and its repairable part, and the value plate with the
    /// percentage. Fill and icon take the game's colours at its own thresholds (green from 70%, orange from 40%, red below), and
    /// a lost ship shows the game's red skull and LOST. Used for the ship cards (ShipCards).
    /// </summary>
    internal sealed class HudHullRow
    {
        // The game's hull row: 363 x 22, a 40 px diamond, the bar start 31, a spacer 17, the bar 202, the value plate 75.
        private const float BarWidth = 202f;
        private const float ValueWidth = 75f;

        // The game's thresholds and colour classes for the hull (ShipInfoHUD).
        private const float OptimalPercent = 70f;
        private const float MediumPercent = 40f;
        private const string Optimal = "ui-tint-color__optimal";
        private const string Medium = "ui-tint-color__medium";
        private const string Bad = "ui-tint-color__bad";

        public readonly VisualElement Row;
        public readonly VisualElement Icon;
        private readonly VisualElement _fill;
        private readonly VisualElement _repairable;
        private readonly Label _value;
        private readonly StyleBackground _skull;
        private StyleBackground _shipIcon;

        // What the row shows, as last written: it is only written again when one of these changes.
        private int _percent = -1;
        private int _repairablePercent = -1;
        private bool _lost;
        private string _tint;
        private string _fillTint;

        /// <summary>The fill's colour class whatever the hull (an enemy ship's: red), or null for the game's own thresholds.</summary>
        public string FillTint;

        /// <summary>Builds the row from the art of the game's HUD (root: its document's tree); its parts are named after prefix.</summary>
        public HudHullRow(VisualElement root, string prefix)
        {
            VisualElement hullBar = root.Q("HullBarVE");
            _skull = Ui.GameArt(null, "icon_skull");

            Row = Part(prefix + "Panel");
            Row.style.flexDirection = FlexDirection.Row;
            Row.style.alignItems = Align.Center;
            Row.style.width = HudShipRow.Width;
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
            Ui.Padding(iconBorder, 10, 10);
            Icon = Part(prefix + "Icon");
            Icon.style.flexGrow = 1;
            Icon.style.backgroundImage = Ui.GameArt(root.Q("HullIcon"), "icon_shield");
            Icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            _shipIcon = Icon.style.backgroundImage;
            iconBorder.Add(Icon);
            iconBackground.Add(iconBorder);

            VisualElement bar = Part(prefix + "BarVE");
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.flexGrow = 1;
            bar.style.height = 22;
            bar.style.alignItems = Align.Center;
            VisualElement start = Part(prefix + "StartOfBar");
            start.AddToClassList("bg-tint_gray");
            start.style.backgroundImage = Ui.GameArt(hullBar != null ? hullBar.Q("StartOfBar") : null, "main_hud_bars_subbar_start_inverted");
            start.style.width = 31;
            start.style.height = Length.Percent(100);
            start.style.flexShrink = 0;
            VisualElement spacer = Part(prefix + "Spacer");
            spacer.AddToClassList("bg-color_gray");
            spacer.style.width = 17;
            spacer.style.height = Length.Percent(100);
            spacer.style.marginLeft = -1;
            spacer.style.flexShrink = 0;
            VisualElement border = Part(prefix + "BarBorder");
            border.AddToClassList("bar-border_gray");
            border.style.width = BarWidth;
            border.style.height = Length.Percent(100);
            border.style.borderTopWidth = 1;
            border.style.borderBottomWidth = 1;
            border.style.flexShrink = 0;
            VisualElement bars = Part(prefix + "HullBarsContainer");
            bars.style.flexDirection = FlexDirection.Row;
            bars.style.flexGrow = 1;
            bars.style.overflow = Overflow.Hidden;
            bars.style.paddingTop = 1;
            bars.style.paddingRight = 1;
            bars.style.paddingBottom = 1;
            Ui.Margin(bars, 1, 1, 1, 1);
            _fill = Part(prefix + "CurrentHPBar");
            _fill.style.backgroundImage = Ui.GameArt(root.Q("CurrentHPBar"), "shape_filledsquare");
            _fill.style.height = Length.Percent(100);
            _fill.style.flexShrink = 0;
            _repairable = Part(prefix + "RepairableHPBar");
            _repairable.style.height = Length.Percent(100);
            _repairable.style.overflow = Overflow.Hidden;
            _repairable.style.flexShrink = 0;
            VisualElement repairableImage = Part(prefix + "RepairableHPImage");
            repairableImage.style.backgroundImage = Ui.GameArt(root.Q("RepairableHPImage"), "RepairableHP");
            repairableImage.style.width = 228;
            repairableImage.style.height = Length.Percent(100);
            repairableImage.style.flexShrink = 0;
            _repairable.Add(repairableImage);
            bars.Add(_fill);
            bars.Add(_repairable);
            border.Add(bars);
            _value = new Label { name = prefix + "Amount", pickingMode = PickingMode.Ignore };
            _value.AddToClassList("main-hud-label-bar");
            _value.AddToClassList("hud-resource__value");
            _value.AddToClassList("label_color_neutral");
            _value.AddToClassList("ui-text-color__white");
            _value.style.width = ValueWidth;
            _value.style.fontSize = 14;
            _value.style.unityTextAlign = TextAnchor.MiddleRight;
            _value.style.whiteSpace = WhiteSpace.NoWrap;
            _value.style.flexShrink = 0;
            bar.Add(start);
            bar.Add(spacer);
            bar.Add(border);
            bar.Add(_value);

            Row.Add(iconBackground);
            Row.Add(bar);
        }

        /// <summary>The diamond's icon while the ship lasts (the shield, as the game's own row has, unless set).</summary>
        public void SetIcon(Sprite icon)
        {
            if (icon == null) return;
            _shipIcon = new StyleBackground(icon);
            if (!_lost) Icon.style.backgroundImage = _shipIcon;
        }

        /// <summary>The hull (fractions of its hit points), as the game writes its own: the percentage rounded up.</summary>
        public void Show(float hull, float repairable, bool lost)
        {
            int percent = lost ? 0 : Mathf.CeilToInt(Mathf.Clamp01(hull) * 100f);
            int repairablePercent = lost ? 0 : Mathf.RoundToInt(Mathf.Clamp01(repairable) * 100f);
            if (percent == _percent && repairablePercent == _repairablePercent && lost == _lost) return;
            if (lost != _lost || _percent < 0) Icon.style.backgroundImage = lost ? _skull : _shipIcon;
            _percent = percent;
            _repairablePercent = repairablePercent;
            _lost = lost;
            _fill.style.width = Length.Percent(lost ? 0f : Mathf.Clamp01(hull) * 100f);
            _repairable.style.width = Length.Percent(repairablePercent);
            _value.text = lost ? "LOST" : percent + "%";
            _value.style.color = lost ? HudShipRow.LostRed : Color.white;
            string tint = lost || percent < MediumPercent ? Bad : (percent < OptimalPercent ? Medium : Optimal);
            string fillTint = FillTint ?? tint;
            if (fillTint != _fillTint)
            {
                if (_fillTint != null) _fill.RemoveFromClassList(_fillTint);
                _fillTint = fillTint;
                _fill.AddToClassList(fillTint);
            }
            if (tint == _tint) return;
            if (_tint != null) Icon.RemoveFromClassList(_tint);
            _tint = tint;
            Icon.AddToClassList(tint);
        }

        private static VisualElement Part(string name)
        {
            return new VisualElement { name = name, pickingMode = PickingMode.Ignore };
        }
    }
}
