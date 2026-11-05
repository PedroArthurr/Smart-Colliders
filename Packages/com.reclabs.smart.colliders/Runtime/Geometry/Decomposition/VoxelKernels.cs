using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace RecLabs.SmartColliders.Geometry.Decomposition
{
    /// <summary>Axis-aligned box of voxels, [Min, Max) in voxel coordinates.</summary>
    public struct VoxelBox
    {
        public int3 Min;
        public int3 Max;

        public VoxelBox(int3 min, int3 max) { Min = min; Max = max; }
        public int3 Size => Max - Min;
        public bool IsEmpty => math.any(Max <= Min);
    }

    /// <summary>
    /// Solid voxel grid with O(1) range queries: summed-volume table (count in box) and, per axis,
    /// next/previous solid voxel along each grid line (extremes of a box along a line in O(1)).
    /// Cell states: 0 = outside, 1 = surface, 2 = interior.
    /// </summary>
    public struct VoxelGrid : System.IDisposable
    {
        public int3 Dims;
        public float3 Origin;
        public float Cell;
        public NativeArray<byte> State;
        public NativeArray<int> Prefix;           // (nx+1)(ny+1)(nz+1)
        public NativeArray<int> NextX, PrevX, NextY, PrevY, NextZ, PrevZ;
        public int SolidCount;

        public int Index(int x, int y, int z) => x + Dims.x * (y + Dims.y * z);
        public int Index(int3 c) => c.x + Dims.x * (c.y + Dims.y * c.z);
        public float3 ToLocal(float3 voxelCoord) => Origin + voxelCoord * Cell;

        public bool IsSolid(int3 c) =>
            math.all(c >= 0) && math.all(c < Dims) && State[Index(c)] != 0;

        public bool IsInterior(int3 c) =>
            math.all(c >= 0) && math.all(c < Dims) && State[Index(c)] == 2;

        int P(int x, int y, int z) => x + (Dims.x + 1) * (y + (Dims.y + 1) * z);

        public int Count(VoxelBox b)
        {
            if (b.IsEmpty) return 0;
            int3 a = b.Min, c = b.Max;
            return Prefix[P(c.x, c.y, c.z)] - Prefix[P(a.x, c.y, c.z)] - Prefix[P(c.x, a.y, c.z)] - Prefix[P(c.x, c.y, a.z)]
                 + Prefix[P(a.x, a.y, c.z)] + Prefix[P(a.x, c.y, a.z)] + Prefix[P(c.x, a.y, a.z)] - Prefix[P(a.x, a.y, a.z)];
        }

        /// <summary>Shrinks a box to the tight bounds of its solid voxels (empty result if none).</summary>
        public VoxelBox Tighten(VoxelBox b)
        {
            if (Count(b) == 0) return new VoxelBox(int3.zero, int3.zero);
            for (int axis = 0; axis < 3; axis++)
            {
                // Smallest lo such that slab [Min, lo] has solid; largest hi such that [hi, Max) has solid.
                int lo = b.Min[axis], hi = b.Max[axis] - 1;
                int l = b.Min[axis], r = b.Max[axis] - 1;
                while (l < r)
                {
                    int m = (l + r) >> 1;
                    var s = b; s.Max[axis] = m + 1;
                    if (Count(s) > 0) r = m; else l = m + 1;
                }
                lo = l;
                l = b.Min[axis]; r = b.Max[axis] - 1;
                while (l < r)
                {
                    int m = (l + r + 1) >> 1;
                    var s = b; s.Min[axis] = m;
                    if (Count(s) > 0) l = m; else r = m - 1;
                }
                hi = l;
                b.Min[axis] = lo;
                b.Max[axis] = hi + 1;
            }
            return b;
        }

        /// <summary>
        /// Appends the exact corner set whose convex hull equals the hull of the box's solid voxels:
        /// for every grid line along <paramref name="lineAxis"/>, the outer 4 corners of its first and last solid voxel.
        /// </summary>
        public void AppendHullPoints(VoxelBox b, int lineAxis, NativeList<float3> points)
        {
            int u = (lineAxis + 1) % 3, v = (lineAxis + 2) % 3;
            NativeArray<int> next = lineAxis == 0 ? NextX : lineAxis == 1 ? NextY : NextZ;
            NativeArray<int> prev = lineAxis == 0 ? PrevX : lineAxis == 1 ? PrevY : PrevZ;
            int3 c = default;
            for (int a = b.Min[u]; a < b.Max[u]; a++)
            for (int d = b.Min[v]; d < b.Max[v]; d++)
            {
                c[u] = a; c[v] = d;
                c[lineAxis] = b.Min[lineAxis];
                int lo = next[Index(c)];
                if (lo >= b.Max[lineAxis]) continue;
                c[lineAxis] = b.Max[lineAxis] - 1;
                int hi = prev[Index(c)] + 1;
                for (int k = 0; k < 4; k++)
                {
                    float3 p = default;
                    p[u] = a + (k & 1);
                    p[v] = d + (k >> 1);
                    p[lineAxis] = lo;
                    points.Add(p);
                    p[lineAxis] = hi;
                    points.Add(p);
                }
            }
        }

        /// <summary>Line axis that minimizes the number of lines for this box (its longest axis).</summary>
        public static int LineAxis(VoxelBox b)
        {
            int3 s = b.Size;
            return s.x >= s.y && s.x >= s.z ? 0 : s.y >= s.z ? 1 : 2;
        }

        public void Dispose()
        {
            if (State.IsCreated) State.Dispose();
            if (Prefix.IsCreated) Prefix.Dispose();
            if (NextX.IsCreated) NextX.Dispose();
            if (PrevX.IsCreated) PrevX.Dispose();
            if (NextY.IsCreated) NextY.Dispose();
            if (PrevY.IsCreated) PrevY.Dispose();
            if (NextZ.IsCreated) NextZ.Dispose();
            if (PrevZ.IsCreated) PrevZ.Dispose();
        }
    }

    public static class VoxelKernels
    {
        /// <summary>Conservative surface voxelization + exterior flood fill + query tables.</summary>
        public static void Build(ref VoxelGrid g, NativeArray<float3> vertices, NativeArray<int> indices)
        {
            int3 d = g.Dims;
            int n = d.x * d.y * d.z;
            var state = g.State;
            float h = g.Cell;
            float3 half = h * 0.5f * 1.0001f;

            // 1. Surface.
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                float3 a = (vertices[indices[t]] - g.Origin) / h;
                float3 b = (vertices[indices[t + 1]] - g.Origin) / h;
                float3 c = (vertices[indices[t + 2]] - g.Origin) / h;
                // Widen by a hair so faces lying exactly on a voxel boundary mark both neighbouring voxels.
                int3 lo = math.clamp((int3)math.floor(math.min(a, math.min(b, c)) - 1e-3f), 0, d - 1);
                int3 hi = math.clamp((int3)math.floor(math.max(a, math.max(b, c)) + 1e-3f), 0, d - 1);
                float3 ta = a * h, tb = b * h, tc = c * h;
                for (int z = lo.z; z <= hi.z; z++)
                for (int y = lo.y; y <= hi.y; y++)
                for (int x = lo.x; x <= hi.x; x++)
                {
                    int i = x + d.x * (y + d.y * z);
                    if (state[i] == 1) continue;
                    float3 center = (new float3(x, y, z) + 0.5f) * h;
                    if (TriBoxOverlap(center, half, ta, tb, tc)) state[i] = 1;
                }
            }

            // 2. Exterior flood fill from the padded corner (6-connected), marks outside = 3.
            var queue = new NativeList<int>(n / 8 + 64, Allocator.Temp);
            // Seed from every empty border cell (robust when a surface touches the padding).
            for (int z = 0; z < d.z; z++)
            for (int y = 0; y < d.y; y++)
            for (int x = 0; x < d.x; x++)
            {
                if (x != 0 && y != 0 && z != 0 && x != d.x - 1 && y != d.y - 1 && z != d.z - 1) continue;
                int i = x + d.x * (y + d.y * z);
                if (state[i] == 0) { state[i] = 3; queue.Add(i); }
            }
            int head = 0;
            while (head < queue.Length)
            {
                int i = queue[head++];
                int x = i % d.x, y = (i / d.x) % d.y, z = i / (d.x * d.y);
                if (x > 0 && state[i - 1] == 0) { state[i - 1] = 3; queue.Add(i - 1); }
                if (x < d.x - 1 && state[i + 1] == 0) { state[i + 1] = 3; queue.Add(i + 1); }
                if (y > 0 && state[i - d.x] == 0) { state[i - d.x] = 3; queue.Add(i - d.x); }
                if (y < d.y - 1 && state[i + d.x] == 0) { state[i + d.x] = 3; queue.Add(i + d.x); }
                int s = d.x * d.y;
                if (z > 0 && state[i - s] == 0) { state[i - s] = 3; queue.Add(i - s); }
                if (z < d.z - 1 && state[i + s] == 0) { state[i + s] = 3; queue.Add(i + s); }
                // Reclaim queue memory periodically on huge grids.
                if (head > 1 << 20 && head * 2 > queue.Length)
                {
                    queue.RemoveRange(0, head);
                    head = 0;
                }
            }
            queue.Dispose();

            int solid = 0;
            for (int i = 0; i < n; i++)
            {
                byte s = state[i];
                state[i] = s == 3 ? (byte)0 : s == 0 ? (byte)2 : (byte)1;
                if (state[i] != 0) solid++;
            }
            g.SolidCount = solid;

            // 3. Summed volume table.
            int px = d.x + 1, py = d.y + 1;
            var pre = g.Prefix;
            for (int z = 1; z <= d.z; z++)
            for (int y = 1; y <= d.y; y++)
            for (int x = 1; x <= d.x; x++)
            {
                int v = state[(x - 1) + d.x * ((y - 1) + d.y * (z - 1))] != 0 ? 1 : 0;
                pre[x + px * (y + py * z)] = v
                    + pre[(x - 1) + px * (y + py * z)] + pre[x + px * ((y - 1) + py * z)] + pre[x + px * (y + py * (z - 1))]
                    - pre[(x - 1) + px * ((y - 1) + py * z)] - pre[(x - 1) + px * (y + py * (z - 1))] - pre[x + px * ((y - 1) + py * (z - 1))]
                    + pre[(x - 1) + px * ((y - 1) + py * (z - 1))];
            }

            // 4. Next / previous solid along each axis.
            for (int axis = 0; axis < 3; axis++)
            {
                var next = axis == 0 ? g.NextX : axis == 1 ? g.NextY : g.NextZ;
                var prev = axis == 0 ? g.PrevX : axis == 1 ? g.PrevY : g.PrevZ;
                int u = (axis + 1) % 3, v = (axis + 2) % 3;
                int len = d[axis];
                int3 c = default;
                for (int a = 0; a < d[u]; a++)
                for (int b = 0; b < d[v]; b++)
                {
                    c[u] = a; c[v] = b;
                    int last = -1;
                    for (int k = 0; k < len; k++)
                    {
                        c[axis] = k;
                        int i = c.x + d.x * (c.y + d.y * c.z);
                        if (state[i] != 0) last = k;
                        prev[i] = last;
                    }
                    int nxt = len;
                    for (int k = len - 1; k >= 0; k--)
                    {
                        c[axis] = k;
                        int i = c.x + d.x * (c.y + d.y * c.z);
                        if (state[i] != 0) nxt = k;
                        next[i] = nxt;
                    }
                }
            }
        }

        /// <summary>Convex hull volume of the solid voxels of a box (voxel units³). 0 if empty.</summary>
        public static float HullVolume(in VoxelGrid g, VoxelBox b, NativeList<float3> scratch, NativeList<float3> hv, NativeList<int> ht)
        {
            scratch.Clear();
            g.AppendHullPoints(b, VoxelGrid.LineAxis(b), scratch);
            if (scratch.Length < 4) return 0f;
            if (!ConvexHull3D.Build(scratch.AsArray(), 0, hv, ht)) return 0f;
            return ConvexHull3D.Volume(hv.AsArray(), ht.AsArray());
        }

        // Akenine-Möller triangle/box overlap (box centered at c with half size h).
        static bool TriBoxOverlap(float3 c, float3 h, float3 a, float3 b, float3 d)
        {
            float3 v0 = a - c, v1 = b - c, v2 = d - c;
            float3 e0 = v1 - v0, e1 = v2 - v1, e2 = v0 - v2;

            if (!AxisTest(new float3(0, -e0.z, e0.y), v0, v1, v2, h)) return false;
            if (!AxisTest(new float3(0, -e1.z, e1.y), v0, v1, v2, h)) return false;
            if (!AxisTest(new float3(0, -e2.z, e2.y), v0, v1, v2, h)) return false;
            if (!AxisTest(new float3(e0.z, 0, -e0.x), v0, v1, v2, h)) return false;
            if (!AxisTest(new float3(e1.z, 0, -e1.x), v0, v1, v2, h)) return false;
            if (!AxisTest(new float3(e2.z, 0, -e2.x), v0, v1, v2, h)) return false;
            if (!AxisTest(new float3(-e0.y, e0.x, 0), v0, v1, v2, h)) return false;
            if (!AxisTest(new float3(-e1.y, e1.x, 0), v0, v1, v2, h)) return false;
            if (!AxisTest(new float3(-e2.y, e2.x, 0), v0, v1, v2, h)) return false;

            float3 mn = math.min(v0, math.min(v1, v2)), mx = math.max(v0, math.max(v1, v2));
            if (math.any(mn > h) || math.any(mx < -h)) return false;

            float3 n = math.cross(e0, e1);
            float r = math.dot(h, math.abs(n));
            float s = math.dot(n, v0);
            return math.abs(s) <= r;
        }

        static bool AxisTest(float3 axis, float3 v0, float3 v1, float3 v2, float3 h)
        {
            if (math.lengthsq(axis) < 1e-20f) return true;
            float p0 = math.dot(axis, v0), p1 = math.dot(axis, v1), p2 = math.dot(axis, v2);
            float r = math.dot(h, math.abs(axis));
            return !(math.min(p0, math.min(p1, p2)) > r || math.max(p0, math.max(p1, p2)) < -r);
        }

        /// <summary>
        /// Sutherland–Hodgman clip of every triangle against a box shrunk by <paramref name="eps"/>; appends the clipped
        /// polygon vertices. Shrinking keeps geometry lying exactly on a cut plane (e.g. the top face of the lower arm of an L)
        /// out of the neighbouring region. Outer region bounds always keep a voxel of margin (conservative voxelization).
        /// </summary>
        public static void ClipToBox(NativeArray<float3> vertices, NativeArray<int> indices, float3 boxMin, float3 boxMax, float eps, NativeList<float3> output)
        {
            float3 bmin = boxMin + eps, bmax = boxMax - eps;
            var poly = new NativeList<float3>(12, Allocator.Temp);
            var tmp = new NativeList<float3>(12, Allocator.Temp);
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                float3 a = vertices[indices[t]], b = vertices[indices[t + 1]], c = vertices[indices[t + 2]];
                float3 tmin = math.min(a, math.min(b, c)), tmax = math.max(a, math.max(b, c));
                if (math.any(tmax < bmin) || math.any(tmin > bmax)) continue;
                if (math.all(tmin >= bmin) && math.all(tmax <= bmax))
                {
                    output.Add(a); output.Add(b); output.Add(c);
                    continue;
                }
                poly.Clear();
                poly.Add(a); poly.Add(b); poly.Add(c);
                for (int axis = 0; axis < 3 && poly.Length > 0; axis++)
                {
                    ClipPlane(poly, tmp, axis, bmin[axis], +1f);
                    ClipPlane(tmp, poly, axis, bmax[axis], -1f);
                }
                for (int i = 0; i < poly.Length; i++) output.Add(poly[i]);
            }
            poly.Dispose();
            tmp.Dispose();
        }

        // Keeps the side where sign * (p[axis] - value) >= 0.
        static void ClipPlane(NativeList<float3> input, NativeList<float3> output, int axis, float value, float sign)
        {
            output.Clear();
            int n = input.Length;
            for (int i = 0; i < n; i++)
            {
                float3 p = input[i], q = input[(i + 1) % n];
                float dp = sign * (p[axis] - value), dq = sign * (q[axis] - value);
                if (dp >= 0f) output.Add(p);
                if ((dp >= 0f) != (dq >= 0f))
                {
                    float t = dp / (dp - dq);
                    float3 x = p + (q - p) * t;
                    x[axis] = value;
                    output.Add(x);
                }
            }
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct VoxelizeJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Vertices;
        [ReadOnly] public NativeArray<int> Indices;
        public VoxelGrid Grid;
        public NativeReference<int> SolidCount;

        public void Execute()
        {
            VoxelKernels.Build(ref Grid, Vertices, Indices);
            SolidCount.Value = Grid.SolidCount;
        }
    }

    /// <summary>Evaluates candidate split planes of one box: cost = hull volume(left) + hull volume(right).</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct SplitEvalJob : IJob
    {
        [ReadOnly] public VoxelGrid Grid;
        public VoxelBox Box;
        /// <summary>x = axis, y = split coordinate (left = [Min, y), right = [y, Max)).</summary>
        [ReadOnly] public NativeArray<int2> Candidates;
        public NativeArray<float> Costs;

        public void Execute()
        {
            var scratch = new NativeList<float3>(1024, Allocator.Temp);
            var hv = new NativeList<float3>(256, Allocator.Temp);
            var ht = new NativeList<int>(512, Allocator.Temp);
            for (int i = 0; i < Candidates.Length; i++)
            {
                int axis = Candidates[i].x, at = Candidates[i].y;
                var l = Box; l.Max[axis] = at;
                var r = Box; r.Min[axis] = at;
                if (Grid.Count(l) == 0 || Grid.Count(r) == 0) { Costs[i] = float.MaxValue; continue; }
                l = Grid.Tighten(l);
                r = Grid.Tighten(r);
                Costs[i] = VoxelKernels.HullVolume(Grid, l, scratch, hv, ht) + VoxelKernels.HullVolume(Grid, r, scratch, hv, ht);
            }
            scratch.Dispose();
            hv.Dispose();
            ht.Dispose();
        }
    }

    /// <summary>Exact hull (vertices + volume) of the solid voxels of a set of boxes.</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct BoxesHullJob : IJob
    {
        [ReadOnly] public VoxelGrid Grid;
        [ReadOnly] public NativeArray<VoxelBox> Boxes;
        /// <summary>Extra points (e.g. hull vertices of other pieces) merged into the hull.</summary>
        [ReadOnly] public NativeArray<float3> ExtraPoints;
        public NativeList<float3> HullVertices;
        public NativeReference<float> Volume;

        public void Execute()
        {
            var pts = new NativeList<float3>(1024, Allocator.Temp);
            for (int i = 0; i < Boxes.Length; i++)
                if (!Boxes[i].IsEmpty) Grid.AppendHullPoints(Boxes[i], VoxelGrid.LineAxis(Boxes[i]), pts);
            if (ExtraPoints.IsCreated) pts.AddRange(ExtraPoints);
            var ht = new NativeList<int>(512, Allocator.Temp);
            HullVertices.Clear();
            Volume.Value = 0f;
            if (pts.Length >= 4 && ConvexHull3D.Build(pts.AsArray(), 0, HullVertices, ht))
                Volume.Value = ConvexHull3D.Volume(HullVertices.AsArray(), ht.AsArray());
            pts.Dispose();
            ht.Dispose();
        }
    }

    /// <summary>Hull volume of the union of two vertex sets (merge cost).</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct PairHullVolumeJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Points;
        /// <summary>Per pair: (start, count) of A then (start, count) of B within <see cref="Points"/>.</summary>
        [ReadOnly] public NativeArray<int4> Pairs;
        public NativeArray<float> Volumes;

        public void Execute()
        {
            var pts = new NativeList<float3>(512, Allocator.Temp);
            var hv = new NativeList<float3>(256, Allocator.Temp);
            var ht = new NativeList<int>(512, Allocator.Temp);
            for (int i = 0; i < Pairs.Length; i++)
            {
                int4 p = Pairs[i];
                pts.Clear();
                pts.AddRange(Points.GetSubArray(p.x, p.y));
                pts.AddRange(Points.GetSubArray(p.z, p.w));
                Volumes[i] = pts.Length >= 4 && ConvexHull3D.Build(pts.AsArray(), 0, hv, ht)
                    ? ConvexHull3D.Volume(hv.AsArray(), ht.AsArray())
                    : 0f;
            }
            pts.Dispose();
            hv.Dispose();
            ht.Dispose();
        }
    }

    /// <summary>Final piece geometry: the real mesh clipped to the piece's boxes (+ interior box corners) → hull vertices.</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct PieceGeometryJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Vertices;
        [ReadOnly] public NativeArray<int> Indices;
        /// <summary>Local-space boxes: (min, max) pairs.</summary>
        [ReadOnly] public NativeArray<float3> Boxes;
        public float Epsilon;
        [ReadOnly] public NativeArray<float3> ExtraPoints;
        public NativeList<float3> HullVertices;

        public void Execute()
        {
            var pts = new NativeList<float3>(1024, Allocator.Temp);
            for (int i = 0; i + 1 < Boxes.Length; i += 2)
                VoxelKernels.ClipToBox(Vertices, Indices, Boxes[i], Boxes[i + 1], Epsilon, pts);
            pts.AddRange(ExtraPoints);
            var ht = new NativeList<int>(512, Allocator.Temp);
            HullVertices.Clear();
            if (pts.Length >= 4 && !ConvexHull3D.Build(pts.AsArray(), 0, HullVertices, ht))
            {
                // Flat piece: keep the raw points (the fitter turns them into a thin box).
                HullVertices.Clear();
                HullVertices.AddRange(pts.AsArray());
            }
            else if (pts.Length < 4)
            {
                HullVertices.AddRange(pts.AsArray());
            }
            pts.Dispose();
            ht.Dispose();
        }
    }
}
