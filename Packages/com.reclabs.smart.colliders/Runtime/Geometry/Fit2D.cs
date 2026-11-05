using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace RecLabs.SmartColliders.Geometry
{
    public enum Shape2DType : byte
    {
        Circle,
        Capsule,
        Box,
        Polygon,
    }

    /// <summary>One fitted 2D collider in the sprite's local space. Polygon shapes carry their path in <see cref="Path"/>.</summary>
    public struct Shape2D
    {
        public Shape2DType Type;
        public float2 Center;
        /// <summary>Rotation in degrees (Box / Capsule).</summary>
        public float Angle;
        /// <summary>Full size (Box: width/height; Capsule: width/height along its local X/Y, vertical = Y is the long axis).</summary>
        public float2 Size;
        public float Radius;
        public float2[] Path;
        /// <summary>Area of the covered outline / area of the shape (all fits enclose their input, so ≤ 1).</summary>
        public float Accuracy;

        public float Area => Type switch
        {
            Shape2DType.Circle => math.PI * Radius * Radius,
            Shape2DType.Box => Size.x * Size.y,
            Shape2DType.Capsule => Size.x * (Size.y - Size.x) + math.PI * Size.x * Size.x * 0.25f,
            _ => Path != null ? math.abs(Fit2D.SignedArea(Path)) : 0f,
        };
    }

    public struct Fit2DSettings
    {
        public bool AllowCircle, AllowCapsule, AllowBox, AllowPolygon;
        /// <summary>Primitive is accepted when area(outline) / area(shape) ≥ this.</summary>
        public float MinAccuracy;
        /// <summary>Accuracy traded per unit of cost when ranking (polygon cost grows with vertex count).</summary>
        public float CostWeight;

        public static Fit2DSettings Default => new Fit2DSettings
        {
            AllowCircle = true, AllowCapsule = true, AllowBox = true, AllowPolygon = true,
            MinAccuracy = 0.85f, CostWeight = 0.02f,
        };
    }

    /// <summary>
    /// 2D fitting and convex decomposition for sprite outlines. Inputs are tiny (tens to hundreds of vertices),
    /// so this is plain managed code on top of the Burst-compatible <see cref="MinEnclosing"/> kernels.
    /// Paths are closed (last vertex ≠ first), any winding.
    /// </summary>
    public static class Fit2D
    {
        public static float SignedArea(IReadOnlyList<float2> p)
        {
            float a = 0f;
            for (int i = 0, n = p.Count; i < n; i++)
            {
                float2 u = p[i], v = p[(i + 1) % n];
                a += u.x * v.y - v.x * u.y;
            }
            return a * 0.5f;
        }

        public static float2[] Hull(IEnumerable<float2> points)
        {
            var list = new List<float2>(points);
            var arr = new NativeArray<float2>(list.ToArray(), Allocator.Temp);
            var hull = new NativeList<float2>(Allocator.Temp);
            MinEnclosing.ConvexHull2D(arr, hull);
            var result = hull.AsArray().ToArray();
            arr.Dispose();
            hull.Dispose();
            return result;
        }

        public static Shape2D Circle(float2[] hull)
        {
            var arr = new NativeArray<float2>(hull, Allocator.Temp);
            MinEnclosing.Circle(arr, out var c, out var r);
            arr.Dispose();
            return new Shape2D { Type = Shape2DType.Circle, Center = c, Radius = math.max(r, 1e-5f) };
        }

        /// <summary>Minimum-area enclosing box; the axis-aligned box wins when within 1% of the minimum.</summary>
        public static Shape2D Box(float2[] hull)
        {
            var arr = new NativeArray<float2>(hull, Allocator.Temp);
            MinEnclosing.MinAreaRect(arr, out var c, out var axis, out var half);
            arr.Dispose();
            AxisBox(hull, out var ac, out var ahalf);
            if (ahalf.x * ahalf.y <= half.x * half.y * 1.01f || hull.Length < 3)
                return new Shape2D { Type = Shape2DType.Box, Center = ac, Size = math.max(ahalf * 2f, 1e-5f) };
            float angle = math.degrees(math.atan2(axis.y, axis.x));
            // Canonical angle in (-45, 45]: swap extents for quarter turns.
            while (angle > 45f) { angle -= 90f; half = half.yx; }
            while (angle <= -45f) { angle += 90f; half = half.yx; }
            return new Shape2D { Type = Shape2DType.Box, Center = c, Angle = angle, Size = math.max(half * 2f, 1e-5f) };
        }

        /// <summary>
        /// Minimum-area enclosing capsule over candidate axes (world X/Y, minimum box axes, every hull edge direction):
        /// radius = half the hull width across the axis, segment = tightest span keeping every point within the radius.
        /// </summary>
        public static Shape2D Capsule(float2[] hull)
        {
            var box = Box(hull);
            float rad = math.radians(box.Angle);
            float2 u = new float2(math.cos(rad), math.sin(rad));
            var axes = new List<float2> { new float2(1, 0), new float2(0, 1), u, new float2(-u.y, u.x) };
            for (int i = 0; i < hull.Length; i++)
            {
                float2 e = hull[(i + 1) % hull.Length] - hull[i];
                float len = math.length(e);
                if (len > 1e-9f) axes.Add(e / len);
            }

            Shape2D best = default;
            float bestArea = float.MaxValue;
            for (int k = 0; k < axes.Count; k++)
            {
                float2 axis = axes[k], side = new float2(-axis.y, axis.x);
                float smin = float.MaxValue, smax = float.MinValue;
                foreach (var p in hull)
                {
                    float s = math.dot(p, side);
                    smin = math.min(smin, s);
                    smax = math.max(smax, s);
                }
                float r = math.max((smax - smin) * 0.5f, 1e-5f), sc = (smax + smin) * 0.5f;
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var p in hull)
                {
                    float t = math.dot(p, axis), s = math.dot(p, side) - sc;
                    float reach = math.sqrt(math.max(0f, r * r - s * s));
                    lo = math.min(lo, t + reach);
                    hi = math.max(hi, t - reach);
                }
                if (hi < lo) { float m = (lo + hi) * 0.5f; lo = hi = m; }
                float area = 2f * r * (hi - lo) + math.PI * r * r;
                // World axes win ties (within 1%): cleaner, unrotated colliders.
                bool better = k < 2 ? area < bestArea : area < bestArea * 0.99f;
                if (!better) continue;
                bestArea = area;
                float angle = math.degrees(math.atan2(axis.y, axis.x)) - 90f; // capsule long axis = local Y
                while (angle > 90f) angle -= 180f;
                while (angle <= -90f) angle += 180f;
                best = new Shape2D
                {
                    Type = Shape2DType.Capsule,
                    Center = axis * ((lo + hi) * 0.5f) + side * sc,
                    Angle = angle,
                    Size = new float2(2f * r, hi - lo + 2f * r),
                };
            }
            return best;
        }

        static void AxisBox(float2[] pts, out float2 center, out float2 half)
        {
            float2 mn = new float2(float.MaxValue), mx = new float2(float.MinValue);
            foreach (var p in pts) { mn = math.min(mn, p); mx = math.max(mx, p); }
            center = (mn + mx) * 0.5f;
            half = (mx - mn) * 0.5f;
        }

        public static float CostOf(Shape2DType t, int vertices = 0) => t switch
        {
            Shape2DType.Circle => 1f,
            Shape2DType.Capsule => 1.5f,
            Shape2DType.Box => 1.5f,
            _ => 2f + math.max(0, vertices - 4) * 0.25f,
        };

        /// <summary>Best collider for one convex piece (or a whole outline when <paramref name="polygon"/> is concave).</summary>
        /// <param name="coveredArea">Area the shape must cover (default: the polygon area), e.g. the sum of several islands.</param>
        public static Shape2D Best(float2[] polygon, in Fit2DSettings s, float coveredArea = -1f)
        {
            float area = coveredArea >= 0f ? coveredArea : math.abs(SignedArea(polygon));
            var hull = Hull(polygon);
            var candidates = new List<Shape2D>(4);
            if (s.AllowCircle) candidates.Add(Circle(hull));
            if (s.AllowCapsule) candidates.Add(Capsule(hull));
            if (s.AllowBox) candidates.Add(Box(hull));

            Shape2D best = default;
            float bestScore = float.MinValue;
            bool found = false;
            foreach (var c0 in candidates)
            {
                var c = c0;
                c.Accuracy = c.Area > 1e-12f ? math.saturate(area / c.Area) : 0f;
                if (c.Accuracy < s.MinAccuracy) continue;
                float score = c.Accuracy - s.CostWeight * CostOf(c.Type);
                if (score > bestScore) { bestScore = score; best = c; found = true; }
            }
            if (found) return best;
            if (s.AllowPolygon || candidates.Count == 0)
                return new Shape2D { Type = Shape2DType.Polygon, Path = (float2[])polygon.Clone(), Accuracy = 1f, Center = Centroid(polygon) };
            // Nothing passes and polygons are not allowed: most accurate primitive.
            foreach (var c0 in candidates)
            {
                var c = c0;
                c.Accuracy = c.Area > 1e-12f ? math.saturate(area / c.Area) : 0f;
                if (!found || c.Accuracy > best.Accuracy) { best = c; found = true; }
            }
            return best;
        }

        public static float2 Centroid(IReadOnlyList<float2> p)
        {
            float2 s = float2.zero;
            for (int i = 0; i < p.Count; i++) s += p[i];
            return p.Count > 0 ? s / p.Count : s;
        }

        // ---------- simplification ----------

        /// <summary>Douglas–Peucker on a closed path; keeps at least 3 vertices.</summary>
        public static float2[] Simplify(float2[] path, float tolerance)
        {
            int n = path.Length;
            if (n <= 3 || tolerance <= 0f) return (float2[])path.Clone();
            // Split at the two mutually farthest-ish vertices (0 and the farthest from it).
            int far = 0;
            float best = -1f;
            for (int i = 1; i < n; i++)
            {
                float d = math.distancesq(path[0], path[i]);
                if (d > best) { best = d; far = i; }
            }
            var keep = new bool[n];
            keep[0] = keep[far] = true;
            Dp(path, 0, far, tolerance, keep);
            Dp(path, far, n, tolerance, keep);
            var result = new List<float2>();
            for (int i = 0; i < n; i++) if (keep[i]) result.Add(path[i]);
            return result.Count >= 3 ? result.ToArray() : (float2[])path.Clone();
        }

        static void Dp(float2[] p, int a, int b, float tol, bool[] keep)
        {
            int n = p.Length;
            float2 pa = p[a % n], pb = p[b % n];
            int idx = -1;
            float maxD = tol;
            for (int i = a + 1; i < b; i++)
            {
                float d = SegmentDistance(p[i % n], pa, pb);
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (idx < 0) return;
            keep[idx % n] = true;
            Dp(p, a, idx, tol, keep);
            Dp(p, idx, b, tol, keep);
        }

        static float SegmentDistance(float2 p, float2 a, float2 b)
        {
            float2 ab = b - a;
            float len2 = math.lengthsq(ab);
            float t = len2 > 1e-12f ? math.saturate(math.dot(p - a, ab) / len2) : 0f;
            return math.distance(p, a + ab * t);
        }

        // ---------- convex decomposition ----------

        /// <summary>
        /// Convex decomposition: recursively cut at reflex vertices along the extension of one of their edges
        /// (shortest cut first) — rectilinear and "blocky" outlines split into clean rectangles. Falls back to
        /// ear clipping + Hertel–Mehlhorn when a cut can't be made robustly. Pieces are CCW.
        /// </summary>
        public static List<float2[]> ConvexDecompose(float2[] path)
        {
            var poly = new List<float2>(path);
            if (SignedArea(poly) < 0f) poly.Reverse();
            RemoveDegenerate(poly);
            var result = new List<float2[]>();
            if (poly.Count < 3) return result;
            var stack = new Stack<List<float2>>();
            stack.Push(poly);
            int guard = 0;
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                if (++guard > 512 || !TryCut(p, out var a, out var b))
                {
                    if (IsConvex(p)) result.Add(p.ToArray());
                    else result.AddRange(ConvexDecomposeTriangulated(p.ToArray()));
                    continue;
                }
                stack.Push(a);
                stack.Push(b);
            }
            return result;
        }

        static bool IsConvex(List<float2> p)
        {
            for (int i = 0; i < p.Count; i++)
                if (Cross(p[(i + p.Count - 1) % p.Count], p[i], p[(i + 1) % p.Count]) < -1e-7f * math.max(1f, math.distancesq(p[i], p[(i + 1) % p.Count])))
                    return false;
            return true;
        }

        /// <summary>Splits a CCW polygon along the shortest edge-extension cut from a reflex vertex. False when convex or no valid cut.</summary>
        static bool TryCut(List<float2> p, out List<float2> a, out List<float2> b)
        {
            a = b = null;
            int n = p.Count;
            float area = SignedArea(p);
            float scale = 0f;
            foreach (var v in p) scale = math.max(scale, math.cmax(math.abs(v)));
            float eps = math.max(scale, 1f) * 1e-6f;
            float bestLen = float.MaxValue;

            for (int i = 0; i < n; i++)
            {
                float2 prev = p[(i + n - 1) % n], cur = p[i], next = p[(i + 1) % n];
                if (Cross(prev, cur, next) >= -eps * eps) continue; // convex or flat
                foreach (var dir0 in new[] { cur - prev, cur - next })
                {
                    float len = math.length(dir0);
                    if (len < eps) continue;
                    float2 d = dir0 / len;
                    // Nearest boundary hit along the ray, skipping the two edges at the vertex.
                    float bestT = float.MaxValue;
                    int hitEdge = -1;
                    float hitS = 0f;
                    for (int j = 0; j < n; j++)
                    {
                        if (j == i || (j + 1) % n == i) continue;
                        float2 e0 = p[j], e1 = p[(j + 1) % n], e = e1 - e0;
                        float den = d.x * e.y - d.y * e.x;
                        if (math.abs(den) < 1e-12f) continue;
                        float2 w = e0 - cur;
                        float t = (w.x * e.y - w.y * e.x) / den;
                        float s = (w.x * d.y - w.y * d.x) / den;
                        if (t > eps && s >= -1e-6f && s <= 1f + 1e-6f && t < bestT) { bestT = t; hitEdge = j; hitS = s; }
                    }
                    if (hitEdge < 0 || bestT >= bestLen) continue;

                    // Build both sides: cur → … → e0 → q  and  q → e1 → … → cur.
                    float2 q = cur + d * bestT;
                    int j0 = hitEdge, j1 = (hitEdge + 1) % n;
                    bool atStart = math.distance(q, p[j0]) < eps * 10f, atEnd = math.distance(q, p[j1]) < eps * 10f;
                    var sideA = new List<float2>();
                    for (int k = i; ; k = (k + 1) % n)
                    {
                        sideA.Add(p[k]);
                        if (k == j0) break;
                    }
                    if (!atStart && !atEnd) sideA.Add(q);
                    if (atEnd) sideA.Add(p[j1]);
                    var sideB = new List<float2>();
                    if (!atStart && !atEnd) sideB.Add(q);
                    int startB = atEnd ? j1 : (atStart ? j0 : j1);
                    for (int k = startB; ; k = (k + 1) % n)
                    {
                        sideB.Add(p[k]);
                        if (k == i) break;
                    }
                    RemoveDegenerate(sideA);
                    RemoveDegenerate(sideB);
                    if (sideA.Count < 3 || sideB.Count < 3) continue;
                    float aa = SignedArea(sideA), ab = SignedArea(sideB);
                    if (aa <= eps * eps || ab <= eps * eps || math.abs(aa + ab - area) > math.max(area, 1e-9f) * 1e-4f) continue;
                    bestLen = bestT;
                    a = sideA;
                    b = sideB;
                }
            }
            return a != null;
        }

        /// <summary>
        /// Ear-clipping triangulation followed by Hertel–Mehlhorn (drop diagonals while both sides stay convex).
        /// Result: at most 4× the optimal number of convex pieces, CCW.
        /// </summary>
        public static List<float2[]> ConvexDecomposeTriangulated(float2[] path)
        {
            var poly = new List<float2>(path);
            if (SignedArea(poly) < 0f) poly.Reverse();
            RemoveDegenerate(poly);
            var pieces = new List<List<int>>();
            if (poly.Count < 3) return new List<float2[]>();

            // Ear clipping (indices into poly).
            var idx = new List<int>();
            for (int i = 0; i < poly.Count; i++) idx.Add(i);
            int guard = 0;
            while (idx.Count > 3 && guard++ < 100000)
            {
                bool clipped = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int i0 = idx[(i + idx.Count - 1) % idx.Count], i1 = idx[i], i2 = idx[(i + 1) % idx.Count];
                    float2 a = poly[i0], b = poly[i1], c = poly[i2];
                    if (Cross(a, b, c) <= 1e-12f) continue; // reflex or flat
                    bool contains = false;
                    foreach (int k in idx)
                    {
                        if (k == i0 || k == i1 || k == i2) continue;
                        if (InTriangle(poly[k], a, b, c)) { contains = true; break; }
                    }
                    if (contains) continue;
                    pieces.Add(new List<int> { i0, i1, i2 });
                    idx.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped)
                {
                    // Numerically stuck (self touching input): drop the flattest vertex.
                    int worst = 0;
                    float wa = float.MaxValue;
                    for (int i = 0; i < idx.Count; i++)
                    {
                        float ar = math.abs(Cross(poly[idx[(i + idx.Count - 1) % idx.Count]], poly[idx[i]], poly[idx[(i + 1) % idx.Count]]));
                        if (ar < wa) { wa = ar; worst = i; }
                    }
                    idx.RemoveAt(worst);
                }
            }
            if (idx.Count == 3 && Cross(poly[idx[0]], poly[idx[1]], poly[idx[2]]) > 1e-12f) pieces.Add(new List<int>(idx));

            // Hertel–Mehlhorn: merge pieces sharing a diagonal while the union stays convex.
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < pieces.Count && !merged; i++)
                for (int j = i + 1; j < pieces.Count && !merged; j++)
                {
                    var u = TryMerge(pieces[i], pieces[j], poly);
                    if (u == null) continue;
                    pieces[i] = u;
                    pieces.RemoveAt(j);
                    merged = true;
                }
            }

            var result = new List<float2[]>(pieces.Count);
            foreach (var p in pieces)
            {
                var arr = new float2[p.Count];
                for (int k = 0; k < p.Count; k++) arr[k] = poly[p[k]];
                result.Add(arr);
            }
            return result;
        }

        static List<int> TryMerge(List<int> a, List<int> b, List<float2> poly)
        {
            for (int i = 0; i < a.Count; i++)
            {
                int a0 = a[i], a1 = a[(i + 1) % a.Count];
                for (int j = 0; j < b.Count; j++)
                {
                    if (b[j] != a1 || b[(j + 1) % b.Count] != a0) continue;
                    // Shared edge a0→a1 (a) == a1→a0 (b). Union: a from a1 around to a0, then b from a0 around to a1 (exclusive).
                    var u = new List<int>();
                    for (int k = 0; k < a.Count; k++) u.Add(a[(i + 1 + k) % a.Count]); // a1 ... a0
                    for (int k = 2; k < b.Count; k++) u.Add(b[(j + k) % b.Count]);     // after a0 in b, up to before a1
                    return IsConvex(u, poly) ? u : null;
                }
            }
            return null;
        }

        static bool IsConvex(List<int> p, List<float2> poly)
        {
            for (int i = 0; i < p.Count; i++)
                if (Cross(poly[p[(i + p.Count - 1) % p.Count]], poly[p[i]], poly[p[(i + 1) % p.Count]]) < -1e-9f) return false;
            return true;
        }

        static void RemoveDegenerate(List<float2> poly)
        {
            for (int i = poly.Count - 1; i >= 0 && poly.Count > 3; i--)
            {
                int n = poly.Count;
                float2 a = poly[(i + n - 1) % n], b = poly[i], c = poly[(i + 1) % n];
                if (math.distancesq(a, b) < 1e-14f || math.abs(Cross(a, b, c)) < 1e-12f * math.max(1f, math.distancesq(a, c)))
                    poly.RemoveAt(i);
            }
        }

        static float Cross(float2 o, float2 a, float2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        static bool InTriangle(float2 p, float2 a, float2 b, float2 c) =>
            Cross(a, b, p) >= 0f && Cross(b, c, p) >= 0f && Cross(c, a, p) >= 0f;

        /// <summary>
        /// Greedy merge of convex pieces: repeatedly merge the touching pair whose combined hull wastes the least area,
        /// while waste/area ≤ <paramref name="tolerance"/>, or while there are more than <paramref name="maxPieces"/>.
        /// Merged pieces are replaced by their convex hull.
        /// </summary>
        public static List<float2[]> MergePieces(List<float2[]> pieces, float tolerance, int maxPieces)
        {
            var list = new List<float2[]>(pieces);
            var areas = new List<float>();
            foreach (var p in list) areas.Add(math.abs(SignedArea(p)));
            float diag = 0f;
            {
                float2 mn = new float2(float.MaxValue), mx = new float2(float.MinValue);
                foreach (var p in list) foreach (var v in p) { mn = math.min(mn, v); mx = math.max(mx, v); }
                if (list.Count > 0) diag = math.distance(mn, mx);
            }
            float touch = diag * 1e-3f + 1e-6f;

            while (list.Count > 1)
            {
                bool forced = list.Count > math.max(1, maxPieces);
                int bi = -1, bj = -1;
                float bestRatio = float.MaxValue;
                float2[] bestHull = null;
                for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (!forced && !Touch(list[i], list[j], touch)) continue;
                    var all = new List<float2>(list[i]);
                    all.AddRange(list[j]);
                    var h = Hull(all);
                    float sum = areas[i] + areas[j];
                    float ratio = (math.abs(SignedArea(h)) - sum) / math.max(sum, 1e-12f);
                    if (!Touch(list[i], list[j], touch)) ratio += 1f; // prefer neighbours even when forced
                    if (ratio < bestRatio) { bestRatio = ratio; bi = i; bj = j; bestHull = h; }
                }
                if (bi < 0 || (!forced && bestRatio > tolerance)) break;
                list[bi] = bestHull;
                areas[bi] = areas[bi] + areas[bj];
                list.RemoveAt(bj);
                areas.RemoveAt(bj);
            }
            return list;
        }

        static bool Touch(float2[] a, float2[] b, float eps)
        {
            // Convex pieces touch when some vertex of one is within eps of an edge of the other.
            for (int i = 0; i < a.Length; i++)
            for (int k = 0; k < b.Length; k++)
                if (SegmentDistance(a[i], b[k], b[(k + 1) % b.Length]) <= eps || SegmentDistance(b[k], a[i], a[(i + 1) % a.Length]) <= eps)
                    return true;
            return false;
        }
    }
}
