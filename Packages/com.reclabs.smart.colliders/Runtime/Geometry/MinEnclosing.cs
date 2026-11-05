using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace RecLabs.SmartColliders.Geometry
{
    /// <summary>Exact minimal enclosing sphere/circle (iterative Welzl) and 2D helpers for OBB fitting.</summary>
    public static class MinEnclosing
    {
        const uint ShuffleSeed = 0x6E624EB7u;

        // ---------- 3D sphere ----------

        public static void Sphere(NativeArray<float3> points, out float3 center, out float radius)
        {
            center = float3.zero;
            radius = 0f;
            int n = points.Length;
            if (n == 0) return;

            var p = new NativeArray<float3>(points, Allocator.Temp);
            var rng = new Random(ShuffleSeed);
            for (int i = n - 1; i > 0; i--)
            {
                int j = rng.NextInt(i + 1);
                float3 t = p[i]; p[i] = p[j]; p[j] = t;
            }

            float tol = GeometryUtils.ComputeEpsilon(p);
            float3 c = p[0];
            float r = 0f;

            for (int i = 1; i < n; i++)
            {
                if (Inside(c, r, p[i], tol)) continue;
                c = p[i]; r = 0f;
                for (int j = 0; j < i; j++)
                {
                    if (Inside(c, r, p[j], tol)) continue;
                    Sphere2(p[i], p[j], out c, out r);
                    for (int k = 0; k < j; k++)
                    {
                        if (Inside(c, r, p[k], tol)) continue;
                        Sphere3(p[i], p[j], p[k], out c, out r);
                        for (int l = 0; l < k; l++)
                        {
                            if (Inside(c, r, p[l], tol)) continue;
                            Sphere4(p[i], p[j], p[k], p[l], out c, out r);
                        }
                    }
                }
            }
            p.Dispose();
            center = c;
            radius = r;
        }

        static bool Inside(float3 c, float r, float3 p, float tol) => math.distance(c, p) <= r + tol;

        static void Sphere2(float3 a, float3 b, out float3 c, out float r)
        {
            c = (a + b) * 0.5f;
            r = math.distance(a, b) * 0.5f;
        }

        static void Sphere3(float3 a, float3 b, float3 c3, out float3 c, out float r)
        {
            float3 ab = b - a, ac = c3 - a;
            float3 n = math.cross(ab, ac);
            float denom = 2f * math.dot(n, n);
            if (denom < 1e-20f)
            {
                LargestPair(a, b, c3, out c, out r);
                return;
            }
            float3 off = (math.cross(n, ab) * math.dot(ac, ac) + math.cross(ac, n) * math.dot(ab, ab)) / denom;
            c = a + off;
            r = math.length(off);
        }

        static void Sphere4(float3 a, float3 b, float3 c3, float3 d, out float3 c, out float r)
        {
            float3 ab = b - a, ac = c3 - a, ad = d - a;
            float det = math.dot(ab, math.cross(ac, ad));
            if (math.abs(det) < 1e-20f)
            {
                // Coplanar: best of the 3-point spheres that contains the 4th.
                Sphere3(a, b, c3, out c, out r);
                float3 c2; float r2;
                Sphere3(a, b, d, out c2, out r2);
                if (r2 > r) { c = c2; r = r2; }
                Sphere3(a, c3, d, out c2, out r2);
                if (r2 > r) { c = c2; r = r2; }
                Sphere3(b, c3, d, out c2, out r2);
                if (r2 > r) { c = c2; r = r2; }
                return;
            }
            float3 off = (math.dot(ad, ad) * math.cross(ab, ac)
                          + math.dot(ac, ac) * math.cross(ad, ab)
                          + math.dot(ab, ab) * math.cross(ac, ad)) / (2f * det);
            c = a + off;
            r = math.length(off);
        }

        static void LargestPair(float3 a, float3 b, float3 c3, out float3 c, out float r)
        {
            float dab = math.distancesq(a, b), dac = math.distancesq(a, c3), dbc = math.distancesq(b, c3);
            if (dab >= dac && dab >= dbc) Sphere2(a, b, out c, out r);
            else if (dac >= dbc) Sphere2(a, c3, out c, out r);
            else Sphere2(b, c3, out c, out r);
        }

        // ---------- 2D circle ----------

        public static void Circle(NativeArray<float2> points, out float2 center, out float radius)
        {
            center = float2.zero;
            radius = 0f;
            int n = points.Length;
            if (n == 0) return;

            var p = new NativeArray<float2>(points, Allocator.Temp);
            var rng = new Random(ShuffleSeed);
            float2 m = float2.zero;
            for (int i = n - 1; i >= 0; i--)
            {
                m = math.max(m, math.abs(p[i]));
                if (i == 0) break;
                int j = rng.NextInt(i + 1);
                float2 t = p[i]; p[i] = p[j]; p[j] = t;
            }
            float tol = math.max((m.x + m.y) * 1e-6f, 1e-7f);

            float2 c = p[0];
            float r = 0f;
            for (int i = 1; i < n; i++)
            {
                if (math.distance(c, p[i]) <= r + tol) continue;
                c = p[i]; r = 0f;
                for (int j = 0; j < i; j++)
                {
                    if (math.distance(c, p[j]) <= r + tol) continue;
                    c = (p[i] + p[j]) * 0.5f;
                    r = math.distance(p[i], p[j]) * 0.5f;
                    for (int k = 0; k < j; k++)
                    {
                        if (math.distance(c, p[k]) <= r + tol) continue;
                        Circle3(p[i], p[j], p[k], out c, out r);
                    }
                }
            }
            p.Dispose();
            center = c;
            radius = r;
        }

        static void Circle3(float2 a, float2 b, float2 c3, out float2 c, out float r)
        {
            float2 bb = b - a, cc = c3 - a;
            float d = 2f * (bb.x * cc.y - bb.y * cc.x);
            if (math.abs(d) < 1e-20f)
            {
                float dab = math.distancesq(a, b), dac = math.distancesq(a, c3), dbc = math.distancesq(b, c3);
                float2 p0 = a, p1 = b;
                if (dac >= dab && dac >= dbc) p1 = c3;
                else if (dbc >= dab) { p0 = b; p1 = c3; }
                c = (p0 + p1) * 0.5f;
                r = math.distance(p0, p1) * 0.5f;
                return;
            }
            float b2 = math.dot(bb, bb), c2 = math.dot(cc, cc);
            float2 off = new float2(cc.y * b2 - bb.y * c2, bb.x * c2 - cc.x * b2) / d;
            c = a + off;
            r = math.length(off);
        }

        // ---------- 2D hull / min-area rectangle ----------

        struct LexicographicComparer : IComparer<float2>
        {
            public int Compare(float2 a, float2 b) =>
                a.x < b.x ? -1 : a.x > b.x ? 1 : a.y < b.y ? -1 : a.y > b.y ? 1 : 0;
        }

        static float Cross(float2 o, float2 a, float2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        /// <summary>Andrew's monotone chain. Sorts <paramref name="points"/> in place. Output is CCW, no collinear points.</summary>
        public static void ConvexHull2D(NativeArray<float2> points, NativeList<float2> hull)
        {
            hull.Clear();
            int n = points.Length;
            if (n == 0) return;
            points.Sort(new LexicographicComparer());
            if (n < 3)
            {
                hull.Add(points[0]);
                if (n == 2 && math.any(points[1] != points[0])) hull.Add(points[1]);
                return;
            }

            for (int i = 0; i < n; i++) // lower
            {
                while (hull.Length >= 2 && Cross(hull[hull.Length - 2], hull[hull.Length - 1], points[i]) <= 0f)
                    hull.RemoveAt(hull.Length - 1);
                hull.Add(points[i]);
            }
            int lowerLen = hull.Length + 1;
            for (int i = n - 2; i >= 0; i--) // upper
            {
                while (hull.Length >= lowerLen && Cross(hull[hull.Length - 2], hull[hull.Length - 1], points[i]) <= 0f)
                    hull.RemoveAt(hull.Length - 1);
                hull.Add(points[i]);
            }
            hull.RemoveAt(hull.Length - 1); // last == first
        }

        /// <summary>Minimum-area enclosing rectangle of a convex polygon (edge-aligned search).</summary>
        public static void MinAreaRect(NativeArray<float2> hull, out float2 center, out float2 axis, out float2 halfExtents)
        {
            center = float2.zero;
            axis = new float2(1f, 0f);
            halfExtents = float2.zero;
            int h = hull.Length;
            if (h == 0) return;
            if (h == 1) { center = hull[0]; return; }

            float bestArea = float.MaxValue;
            for (int i = 0; i < h; i++)
            {
                float2 e = hull[(i + 1) % h] - hull[i];
                float len = math.length(e);
                if (len < 1e-12f) continue;
                e /= len;
                float2 perp = new float2(-e.y, e.x);

                float2 min = new float2(float.MaxValue), max = new float2(float.MinValue);
                for (int k = 0; k < h; k++)
                {
                    float2 q = new float2(math.dot(hull[k], e), math.dot(hull[k], perp));
                    min = math.min(min, q);
                    max = math.max(max, q);
                }
                float2 size = max - min;
                float area = size.x * size.y;
                if (area < bestArea)
                {
                    bestArea = area;
                    axis = e;
                    halfExtents = size * 0.5f;
                    float2 mid = (min + max) * 0.5f;
                    center = e * mid.x + perp * mid.y;
                }
            }
        }
    }
}
