using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>The subset of <see cref="Painter2D"/> used by the icon set, so icons can also be rasterized to textures.</summary>
    internal interface IIconPainter
    {
        Color strokeColor { get; set; }
        Color fillColor { get; set; }
        float lineWidth { get; set; }
        LineCap lineCap { get; set; }
        LineJoin lineJoin { get; set; }
        void BeginPath();
        void MoveTo(Vector2 p);
        void LineTo(Vector2 p);
        void ClosePath();
        void Arc(Vector2 center, float radius, Angle start, Angle end);
        void Fill();
        void Stroke();
    }

    internal readonly struct Painter2DAdapter : IIconPainter
    {
        readonly Painter2D _p;
        public Painter2DAdapter(Painter2D p) => _p = p;
        public Color strokeColor { get => _p.strokeColor; set => _p.strokeColor = value; }
        public Color fillColor { get => _p.fillColor; set => _p.fillColor = value; }
        public float lineWidth { get => _p.lineWidth; set => _p.lineWidth = value; }
        public LineCap lineCap { get => _p.lineCap; set => _p.lineCap = value; }
        public LineJoin lineJoin { get => _p.lineJoin; set => _p.lineJoin = value; }
        public void BeginPath() => _p.BeginPath();
        public void MoveTo(Vector2 p) => _p.MoveTo(p);
        public void LineTo(Vector2 p) => _p.LineTo(p);
        public void ClosePath() => _p.ClosePath();
        public void Arc(Vector2 c, float r, Angle a, Angle b) => _p.Arc(c, r, a, b);
        public void Fill() => _p.Fill();
        public void Stroke() => _p.Stroke();
    }

    /// <summary>
    /// CPU rasterizer for <see cref="IIconPainter"/> paths: strokes = distance to segments (round caps/joins),
    /// fills = non-zero winding, 4x4 supersampled coverage. Small and exact enough for 16-64 px editor icons.
    /// </summary>
    internal sealed class IconRaster : IIconPainter
    {
        const int SS = 4;
        readonly int _w, _h;
        readonly Color[] _pixels;
        readonly List<List<Vector2>> _subpaths = new List<List<Vector2>>();
        readonly List<bool> _closed = new List<bool>();

        public Color strokeColor { get; set; } = Color.white;
        public Color fillColor { get; set; } = Color.white;
        public float lineWidth { get; set; } = 1f;
        public LineCap lineCap { get; set; }
        public LineJoin lineJoin { get; set; }

        public IconRaster(int width, int height)
        {
            _w = width;
            _h = height;
            _pixels = new Color[width * height];
        }

        public void BeginPath() { _subpaths.Clear(); _closed.Clear(); }

        public void MoveTo(Vector2 p)
        {
            _subpaths.Add(new List<Vector2> { p });
            _closed.Add(false);
        }

        public void LineTo(Vector2 p)
        {
            if (_subpaths.Count == 0) MoveTo(p);
            else _subpaths[_subpaths.Count - 1].Add(p);
        }

        public void ClosePath()
        {
            if (_closed.Count > 0) _closed[_closed.Count - 1] = true;
        }

        public void Arc(Vector2 c, float r, Angle start, Angle end)
        {
            float a0 = start.ToRadians(), a1 = end.ToRadians();
            int steps = Mathf.Max(8, Mathf.CeilToInt(Mathf.Abs(a1 - a0) * r * 2f));
            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.Lerp(a0, a1, i / (float)steps);
                var p = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                if (i == 0 && _subpaths.Count == 0) MoveTo(p); else LineTo(p);
            }
        }

        public void Fill() => Rasterize(fill: true);
        public void Stroke() => Rasterize(fill: false);

        void Rasterize(bool fill)
        {
            Color color = fill ? fillColor : strokeColor;
            float half = lineWidth * 0.5f;
            var segs = new List<(Vector2 a, Vector2 b)>();
            for (int s = 0; s < _subpaths.Count; s++)
            {
                var path = _subpaths[s];
                for (int i = 0; i + 1 < path.Count; i++) segs.Add((path[i], path[i + 1]));
                if ((_closed[s] || fill) && path.Count > 2) segs.Add((path[path.Count - 1], path[0]));
            }
            if (segs.Count == 0) return;

            Vector2 min = segs[0].a, max = segs[0].a;
            foreach (var (a, b) in segs) { min = Vector2.Min(min, Vector2.Min(a, b)); max = Vector2.Max(max, Vector2.Max(a, b)); }
            float pad = fill ? 1f : half + 1f;
            int x0 = Mathf.Max(0, Mathf.FloorToInt(min.x - pad)), x1 = Mathf.Min(_w - 1, Mathf.CeilToInt(max.x + pad));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(min.y - pad)), y1 = Mathf.Min(_h - 1, Mathf.CeilToInt(max.y + pad));

            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < SS; sy++)
                for (int sx = 0; sx < SS; sx++)
                {
                    var p = new Vector2(x + (sx + 0.5f) / SS, y + (sy + 0.5f) / SS);
                    if (fill ? Winding(segs, p) != 0 : Near(segs, p, half)) hits++;
                }
                if (hits == 0) continue;
                float cov = hits / (float)(SS * SS) * color.a;
                // Painter space is y-down, textures are y-up.
                int i = (_h - 1 - y) * _w + x;
                Color dst = _pixels[i];
                float outA = cov + dst.a * (1f - cov);
                Color rgb = outA > 0f ? (color * cov + dst * dst.a * (1f - cov)) / outA : Color.clear;
                rgb.a = outA;
                _pixels[i] = rgb;
            }
        }

        static bool Near(List<(Vector2 a, Vector2 b)> segs, Vector2 p, float r)
        {
            float r2 = r * r;
            foreach (var (a, b) in segs)
            {
                Vector2 ab = b - a;
                float t = ab.sqrMagnitude > 1e-12f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                if ((a + ab * t - p).sqrMagnitude <= r2) return true;
            }
            return false;
        }

        static int Winding(List<(Vector2 a, Vector2 b)> segs, Vector2 p)
        {
            int w = 0;
            foreach (var (a, b) in segs)
            {
                if (a.y <= p.y)
                {
                    if (b.y > p.y && Cross(a, b, p) > 0f) w++;
                }
                else if (b.y <= p.y && Cross(a, b, p) < 0f) w--;
            }
            return w;
        }

        static float Cross(Vector2 a, Vector2 b, Vector2 p) => (b.x - a.x) * (p.y - a.y) - (p.x - a.x) * (b.y - a.y);

        public Color[] Pixels => _pixels;

        public Texture2D ToTexture()
        {
            var tex = new Texture2D(_w, _h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixels(_pixels);
            tex.Apply();
            return tex;
        }

        public static IconRaster Draw(HudIcon icon, int size, Color color, float padding = 0.06f)
        {
            var raster = new IconRaster(size, size);
            float pad = size * padding;
            HudIcons.Draw(raster, icon, new Rect(pad, pad, size - 2 * pad, size - 2 * pad), color);
            return raster;
        }

        public static Texture2D Render(HudIcon icon, int size, Color color, float padding = 0.06f) =>
            Draw(icon, size, color, padding).ToTexture();
    }

    /// <summary>Cached editor icons rasterized from the vector icon set.</summary>
    internal static class SmartCollidersIcons
    {
        static Texture2D s_Window, s_Tool;

        static int Px(int points) => Mathf.RoundToInt(points * Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint));

        public static Texture2D Window => s_Window != null ? s_Window
            : s_Window = IconRaster.Render(HudIcon.Hull, Px(16), SmartCollidersTheme.Accent);

        public static Texture2D Tool => s_Tool != null ? s_Tool
            : s_Tool = IconRaster.Render(HudIcon.Hull, Px(32), new Color(0.84f, 0.85f, 0.88f), 0.1f);
    }
}
