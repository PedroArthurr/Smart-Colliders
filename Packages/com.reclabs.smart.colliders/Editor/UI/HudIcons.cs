using UnityEngine;
using UnityEngine.UIElements;

namespace RecLabs.SmartColliders.UI
{
    internal enum HudIcon
    {
        Select, Move, Rotate, Resize,
        FitToMesh, AlignAxes, Reset,
        Sphere, Capsule, Box, Hull,
        Generate, Delete,
        Paint, Merge, Local, Global, Grow, Connected, Clear, Settings, ChevronDown,
        Pause, Play, Stop, Decompose, Bake, Folder, Prefab, Search, Eye, EyeOff, Bone, Sprite,
    }

    /// <summary>Vector icons on a 24-unit grid, drawn with Painter2D (crisp at any size/DPI).</summary>
    internal static class HudIcons
    {
        public static void Draw(Painter2D painter, HudIcon icon, Rect r, Color c) => Draw(new Painter2DAdapter(painter), icon, r, c);

        public static void Draw(IIconPainter p, HudIcon icon, Rect r, Color c)
        {
            float s = Mathf.Min(r.width, r.height) / 24f;
            Vector2 o = r.center - new Vector2(12f, 12f) * s;
            Vector2 P(float x, float y) => o + new Vector2(x, y) * s;

            p.strokeColor = c;
            p.fillColor = c;
            p.lineWidth = 1.9f * s;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;

            void Line(float x0, float y0, float x1, float y1)
            {
                p.BeginPath(); p.MoveTo(P(x0, y0)); p.LineTo(P(x1, y1)); p.Stroke();
            }

            void Poly(bool fill, params float[] xy)
            {
                p.BeginPath();
                p.MoveTo(P(xy[0], xy[1]));
                for (int i = 2; i + 1 < xy.Length; i += 2) p.LineTo(P(xy[i], xy[i + 1]));
                p.ClosePath();
                if (fill) p.Fill();
                p.Stroke();
            }

            void Polyline(params float[] xy)
            {
                p.BeginPath();
                p.MoveTo(P(xy[0], xy[1]));
                for (int i = 2; i + 1 < xy.Length; i += 2) p.LineTo(P(xy[i], xy[i + 1]));
                p.Stroke();
            }

            void Ellipse(float cx, float cy, float rx, float ry, float fromDeg = 0f, float toDeg = 360f, bool close = true)
            {
                p.BeginPath();
                const int steps = 40;
                for (int i = 0; i <= steps; i++)
                {
                    float a = Mathf.Lerp(fromDeg, toDeg, i / (float)steps) * Mathf.Deg2Rad;
                    var pt = P(cx + Mathf.Cos(a) * rx, cy + Mathf.Sin(a) * ry);
                    if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
                }
                if (close) p.ClosePath();
                p.Stroke();
            }

            void ArrowHead(float tipX, float tipY, float dirX, float dirY, float size = 3.4f)
            {
                var d = new Vector2(dirX, dirY).normalized;
                var n = new Vector2(-d.y, d.x);
                var tip = new Vector2(tipX, tipY);
                var b1 = tip - d * size + n * size * 0.8f;
                var b2 = tip - d * size - n * size * 0.8f;
                Poly(true, b1.x, b1.y, tip.x, tip.y, b2.x, b2.y);
            }

            switch (icon)
            {
                case HudIcon.Select:
                    Poly(true, 6, 3.5f, 6, 19, 10, 15.2f, 12.8f, 21, 15.6f, 19.8f, 12.8f, 14, 18, 14);
                    break;

                case HudIcon.Move:
                    Line(12, 4, 12, 20); Line(4, 12, 20, 12);
                    ArrowHead(12, 2.5f, 0, -1); ArrowHead(12, 21.5f, 0, 1);
                    ArrowHead(2.5f, 12, -1, 0); ArrowHead(21.5f, 12, 1, 0);
                    break;

                case HudIcon.Rotate:
                    Ellipse(12, 12, 7.5f, 7.5f, -40f, 250f, false);
                    {
                        float a = 250f * Mathf.Deg2Rad;
                        ArrowHead(12 + Mathf.Cos(a) * 7.5f, 12 + Mathf.Sin(a) * 7.5f, Mathf.Sin(a), -Mathf.Cos(a), 3.6f);
                    }
                    break;

                case HudIcon.Resize:
                    Poly(false, 6, 6, 18, 6, 18, 18, 6, 18);
                    foreach (var (x, y) in new[] { (6f, 6f), (18f, 6f), (18f, 18f), (6f, 18f) })
                        Poly(true, x - 1.8f, y - 1.8f, x + 1.8f, y - 1.8f, x + 1.8f, y + 1.8f, x - 1.8f, y + 1.8f);
                    break;

                case HudIcon.FitToMesh:
                    Poly(true, 9.5f, 9.5f, 14.5f, 9.5f, 14.5f, 14.5f, 9.5f, 14.5f);
                    Line(3.5f, 3.5f, 7, 7); ArrowHead(7.8f, 7.8f, 1, 1, 3);
                    Line(20.5f, 3.5f, 17, 7); ArrowHead(16.2f, 7.8f, -1, 1, 3);
                    Line(3.5f, 20.5f, 7, 17); ArrowHead(7.8f, 16.2f, 1, -1, 3);
                    Line(20.5f, 20.5f, 17, 17); ArrowHead(16.2f, 16.2f, -1, -1, 3);
                    break;

                case HudIcon.AlignAxes:
                    Line(5, 19, 5, 6); ArrowHead(5, 3.5f, 0, -1);
                    Line(5, 19, 18, 19); ArrowHead(20.5f, 19, 1, 0);
                    Poly(false, 9, 9, 15, 9, 15, 15, 9, 15);
                    break;

                case HudIcon.Reset:
                    Ellipse(12, 12, 7.5f, 7.5f, 200f, 480f, false);
                    {
                        float a = 200f * Mathf.Deg2Rad;
                        ArrowHead(12 + Mathf.Cos(a) * 7.5f, 12 + Mathf.Sin(a) * 7.5f, Mathf.Sin(a), -Mathf.Cos(a), 3.6f);
                    }
                    break;

                case HudIcon.Sphere:
                    Ellipse(12, 12, 8.5f, 8.5f);
                    p.lineWidth = 1.3f * s;
                    Ellipse(12, 12, 8.5f, 3.2f);
                    break;

                case HudIcon.Capsule:
                    p.BeginPath();
                    {
                        const int steps = 20;
                        for (int i = 0; i <= steps; i++) // top cap
                        {
                            float a = Mathf.Lerp(180f, 360f, i / (float)steps) * Mathf.Deg2Rad;
                            var pt = P(12 + Mathf.Cos(a) * 5.5f, 8.5f + Mathf.Sin(a) * 5.5f);
                            if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
                        }
                        for (int i = 0; i <= steps; i++) // bottom cap
                        {
                            float a = Mathf.Lerp(0f, 180f, i / (float)steps) * Mathf.Deg2Rad;
                            p.LineTo(P(12 + Mathf.Cos(a) * 5.5f, 15.5f + Mathf.Sin(a) * 5.5f));
                        }
                        p.ClosePath();
                        p.Stroke();
                    }
                    p.lineWidth = 1.3f * s;
                    Ellipse(12, 12, 5.5f, 2f, 0f, 180f, false);
                    break;

                case HudIcon.Box:
                    Poly(false, 12, 3, 20, 7.5f, 20, 16.5f, 12, 21, 4, 16.5f, 4, 7.5f);
                    Polyline(4, 7.5f, 12, 12, 20, 7.5f);
                    Line(12, 12, 12, 21);
                    break;

                case HudIcon.Hull:
                    Poly(false, 5, 8, 11, 3, 19, 6, 21, 14, 15, 21, 6, 18);
                    p.lineWidth = 1.3f * s;
                    Polyline(11, 3, 12, 11, 21, 14);
                    Polyline(5, 8, 12, 11, 15, 21);
                    break;

                case HudIcon.Delete:
                    Line(4.5f, 6.5f, 19.5f, 6.5f);
                    Polyline(9.5f, 6.5f, 9.5f, 4, 14.5f, 4, 14.5f, 6.5f);
                    Polyline(6.5f, 6.5f, 7.5f, 20, 16.5f, 20, 17.5f, 6.5f);
                    p.lineWidth = 1.5f * s;
                    Line(10.3f, 10, 10.6f, 16.5f);
                    Line(13.7f, 10, 13.4f, 16.5f);
                    break;

                case HudIcon.Generate:
                    p.lineWidth = 2.4f * s;
                    Line(4, 20, 14, 10);
                    p.lineWidth = 1.6f * s;
                    Poly(true, 17, 2.5f, 18.2f, 5.8f, 21.5f, 7, 18.2f, 8.2f, 17, 11.5f, 15.8f, 8.2f, 12.5f, 7, 15.8f, 5.8f);
                    Poly(true, 8, 3, 8.6f, 4.4f, 10, 5, 8.6f, 5.6f, 8, 7, 7.4f, 5.6f, 6, 5, 7.4f, 4.4f);
                    break;

                case HudIcon.Paint:
                    p.lineWidth = 2.4f * s;
                    Line(19.5f, 4.5f, 13f, 11f);
                    p.lineWidth = 1.4f * s;
                    Poly(true, 11.2f, 10.8f, 13.4f, 13f, 11f, 17.6f, 4.5f, 20f, 6.6f, 14f);
                    break;

                case HudIcon.Merge:
                    Polyline(4, 5.5f, 9, 5.5f, 13.5f, 12);
                    Polyline(4, 18.5f, 9, 18.5f, 13.5f, 12);
                    Line(13.5f, 12, 19, 12);
                    ArrowHead(21, 12, 1, 0);
                    break;

                case HudIcon.Local:
                    Poly(false, 12, 3, 20, 7.5f, 20, 16.5f, 12, 21, 4, 16.5f, 4, 7.5f);
                    p.lineWidth = 1.5f * s;
                    Polyline(4, 7.5f, 12, 12, 20, 7.5f);
                    Line(12, 12, 12, 21);
                    break;

                case HudIcon.Global:
                    Ellipse(12, 12, 8.5f, 8.5f);
                    p.lineWidth = 1.5f * s;
                    Ellipse(12, 12, 3.8f, 8.5f);
                    Line(3.5f, 12, 20.5f, 12);
                    break;

                case HudIcon.Grow:
                    Poly(true, 10, 10, 14, 10, 14, 14, 10, 14);
                    p.lineWidth = 1.6f * s;
                    Line(12, 7.5f, 12, 4.5f); ArrowHead(12, 2.6f, 0, -1, 2.6f);
                    Line(12, 16.5f, 12, 19.5f); ArrowHead(12, 21.4f, 0, 1, 2.6f);
                    Line(7.5f, 12, 4.5f, 12); ArrowHead(2.6f, 12, -1, 0, 2.6f);
                    Line(16.5f, 12, 19.5f, 12); ArrowHead(21.4f, 12, 1, 0, 2.6f);
                    break;

                case HudIcon.Connected:
                    p.lineWidth = 1.5f * s;
                    Polyline(6, 17.5f, 12, 6.5f, 18, 17.5f);
                    Line(6, 17.5f, 18, 17.5f);
                    Disc(6, 17.5f, 2.6f);
                    Disc(12, 6.5f, 2.6f);
                    Disc(18, 17.5f, 2.6f);
                    break;

                case HudIcon.Clear:
                    Line(6.5f, 6.5f, 17.5f, 17.5f);
                    Line(17.5f, 6.5f, 6.5f, 17.5f);
                    break;

                case HudIcon.Settings:
                    Ellipse(12, 12, 3f, 3f);
                    Ellipse(12, 12, 6.6f, 6.6f);
                    p.lineWidth = 2.6f * s;
                    for (int k = 0; k < 8; k++)
                    {
                        float ang = k * Mathf.PI / 4f;
                        Line(12 + Mathf.Cos(ang) * 7f, 12 + Mathf.Sin(ang) * 7f, 12 + Mathf.Cos(ang) * 9.4f, 12 + Mathf.Sin(ang) * 9.4f);
                    }
                    break;

                case HudIcon.ChevronDown:
                    Polyline(7, 9.5f, 12, 14.5f, 17, 9.5f);
                    break;

                case HudIcon.Pause:
                    Poly(true, 7, 5, 10, 5, 10, 19, 7, 19);
                    Poly(true, 14, 5, 17, 5, 17, 19, 14, 19);
                    break;

                case HudIcon.Play:
                    Poly(true, 8, 5, 19, 12, 8, 19);
                    break;

                case HudIcon.Stop:
                    Poly(true, 6.5f, 6.5f, 17.5f, 6.5f, 17.5f, 17.5f, 6.5f, 17.5f);
                    break;

                case HudIcon.Decompose:
                    Poly(false, 6, 3.5f, 18, 3.5f, 18, 10, 6, 10);
                    Poly(false, 3, 13.5f, 10, 13.5f, 10, 20.5f, 3, 20.5f);
                    Poly(false, 14, 13.5f, 21, 13.5f, 21, 20.5f, 14, 20.5f);
                    break;

                case HudIcon.Bake:
                    Poly(false, 4, 9, 20, 9, 18.5f, 20, 5.5f, 20);
                    p.lineWidth = 1.5f * s;
                    Polyline(8, 6.5f, 9, 5, 8, 3.5f);
                    Polyline(12, 6.5f, 13, 5, 12, 3.5f);
                    Polyline(16, 6.5f, 17, 5, 16, 3.5f);
                    break;

                case HudIcon.Folder:
                    Poly(false, 3, 6, 9.5f, 6, 11.5f, 8.5f, 21, 8.5f, 21, 19, 3, 19);
                    break;

                case HudIcon.Prefab:
                    Poly(false, 12, 3, 20, 7.5f, 20, 16.5f, 12, 21, 4, 16.5f, 4, 7.5f);
                    Poly(true, 12, 8, 16, 10.2f, 16, 14.2f, 12, 16.4f, 8, 14.2f, 8, 10.2f);
                    break;

                case HudIcon.Search:
                    Ellipse(10.5f, 10.5f, 6, 6);
                    p.lineWidth = 2.4f * s;
                    Line(15, 15, 20, 20);
                    break;

                case HudIcon.Eye:
                case HudIcon.EyeOff:
                    Ellipse(12, 12, 9, 5.4f);
                    Disc(12, 12, 2.6f);
                    if (icon == HudIcon.EyeOff) Line(4.5f, 19.5f, 19.5f, 4.5f);
                    break;

                case HudIcon.Bone:
                    p.lineWidth = 2.6f * s;
                    Line(7.5f, 16.5f, 16.5f, 7.5f);
                    Disc(5.2f, 16.2f, 2.3f); Disc(7.8f, 18.8f, 2.3f);
                    Disc(16.2f, 5.2f, 2.3f); Disc(18.8f, 7.8f, 2.3f);
                    break;

                case HudIcon.Sprite:
                    Poly(false, 4, 4, 20, 4, 20, 20, 4, 20);
                    p.lineWidth = 1.5f * s;
                    Polyline(4, 17, 9.5f, 11.5f, 13.5f, 15.5f, 16, 13, 20, 17);
                    Disc(15.5f, 8.5f, 1.8f);
                    break;
            }

            void Disc(float cx, float cy, float rad)
            {
                p.BeginPath();
                p.Arc(P(cx, cy), rad * s, Angle.Degrees(0f), Angle.Degrees(360f));
                p.ClosePath();
                p.Fill();
            }
        }
    }

    /// <summary>VisualElement that paints a <see cref="HudIcon"/> with its own color.</summary>
    internal sealed class HudIconElement : VisualElement
    {
        HudIcon _icon;
        Color _color = Color.white;

        public HudIconElement(HudIcon icon)
        {
            _icon = icon;
            AddToClassList("sc-icon");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += ctx => HudIcons.Draw(ctx.painter2D, _icon, contentRect, _color);
        }

        public HudIcon Icon
        {
            get => _icon;
            set
            {
                if (_icon == value) return;
                _icon = value;
                MarkDirtyRepaint();
            }
        }

        public Color Color
        {
            get => _color;
            set
            {
                if (_color == value) return;
                _color = value;
                MarkDirtyRepaint();
            }
        }
    }
}
