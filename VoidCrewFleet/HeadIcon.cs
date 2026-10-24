using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// One crew member as a head and shoulders, drawn flat like the HUD's icons (the game has no such icon): black with a
    /// white outline while they live, as the HUD's dark diamonds are drawn, and the HUD's red once they are dead.
    /// </summary>
    internal sealed class HeadIcon : VisualElement
    {
        public const float Width = 13f;
        public const float Height = 15f;

        // The HUD's own near-black (its plates and diamonds, #141310).
        private static readonly Color Alive = new Color32(0x14, 0x13, 0x10, 0xFF);

        private bool _dead;

        public HeadIcon()
        {
            pickingMode = PickingMode.Ignore;
            style.width = Width;
            style.height = Height;
            style.flexShrink = 0;
            generateVisualContent += Draw;
        }

        public bool Dead
        {
            get { return _dead; }
            set
            {
                if (_dead == value) return;
                _dead = value;
                MarkDirtyRepaint();
            }
        }

        private void Draw(MeshGenerationContext context)
        {
            Painter2D painter = context.painter2D;
            float width = contentRect.width;
            float height = contentRect.height;
            if (width <= 0f || height <= 0f) return;
            painter.fillColor = _dead ? HudShipRow.LostRed : Alive;
            painter.strokeColor = Color.white;
            painter.lineWidth = 1.2f;
            painter.lineJoin = LineJoin.Round;
            // The shoulders, a dome along the bottom edge.
            painter.BeginPath();
            painter.MoveTo(new Vector2(0.6f, height - 0.6f));
            painter.BezierCurveTo(new Vector2(0.6f, height * 0.5f), new Vector2(width - 0.6f, height * 0.5f), new Vector2(width - 0.6f, height - 0.6f));
            painter.ClosePath();
            painter.Fill();
            painter.Stroke();
            // The head above them.
            painter.BeginPath();
            painter.Arc(new Vector2(width * 0.5f, height * 0.3f), width * 0.27f, Angle.Degrees(0f), Angle.Degrees(360f));
            painter.ClosePath();
            painter.Fill();
            painter.Stroke();
        }
    }
}
