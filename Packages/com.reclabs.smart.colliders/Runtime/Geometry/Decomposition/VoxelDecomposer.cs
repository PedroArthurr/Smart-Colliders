using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace RecLabs.SmartColliders.Geometry.Decomposition
{
    /// <summary>One convex piece of a decomposition: its hull vertices and the source triangles assigned to it.</summary>
    public sealed class DecomposedPiece
    {
        public float3[] Points;
        public List<int> Triangles = new List<int>();
    }

    /// <summary>Parameters of the built-in decomposer, derived from <see cref="DecompositionSettings"/>.</summary>
    public struct VoxelDecomposerParams
    {
        public int Resolution;          // voxels along the longest axis
        public int Candidates;          // split positions sampled per axis
        public bool Refine;             // local refinement around the best plane
        public float Concavity;         // stop splitting below this empty-volume fraction (of total)
        public int MaxPieces;
        public bool AutoMerge;
        public float MergeTolerance;    // merge while added empty volume ≤ tolerance · total volume

        public static VoxelDecomposerParams From(in DecompositionSettings s)
        {
            var p = new VoxelDecomposerParams
            {
                MaxPieces = math.max(1, s.MaxColliders),
                AutoMerge = s.AutoMerge,
                MergeTolerance = math.max(0f, s.MergeTolerance),
            };
            switch (s.Quality)
            {
                case DecompositionQuality.Low: p.Resolution = 32; p.Candidates = 8; p.Refine = true; p.Concavity = 0.05f; break;
                case DecompositionQuality.Medium: p.Resolution = 48; p.Candidates = 12; p.Refine = true; p.Concavity = 0.025f; break;
                case DecompositionQuality.High: p.Resolution = 72; p.Candidates = 18; p.Refine = true; p.Concavity = 0.01f; break;
                default: p.Resolution = 100; p.Candidates = 28; p.Refine = true; p.Concavity = 0.004f; break;
            }
            return p;
        }
    }

    /// <summary>
    /// Built-in approximate convex decomposition (always available, no native code):
    /// 1) conservative voxelization + exterior flood fill, 2) best-first recursive axis-plane cuts minimizing the
    /// summed hull volume of both halves, 3) greedy merging by added empty volume (Auto Merge / Max Colliders),
    /// 4) per piece, the real mesh clipped to its region (+ interior corners) → convex hull.
    /// Runs as small steps (<see cref="Step"/>) so the editor can time-slice, pause and cancel it.
    /// </summary>
    public sealed class VoxelDecomposer : IDisposable
    {
        enum Stage { Voxelize, Split, Merge, Finalize, Done }

        sealed class Piece
        {
            public List<VoxelBox> Boxes = new List<VoxelBox>();
            public int Solid;
            public float HullVolume;
            public float3[] HullPoints;   // voxel units
            public bool Final;
            public float Empty => HullVolume - Solid;
        }

        readonly VoxelDecomposerParams _p;
        NativeArray<float3> _vertices;
        NativeArray<int> _indices;
        VoxelGrid _grid;
        Stage _stage;
        readonly List<Piece> _pieces = new List<Piece>();
        readonly List<DecomposedPiece> _result = new List<DecomposedPiece>();
        int _finalizeIndex;
        int _splitsDone;
        string _status = "Voxelizing";

        public bool IsDone => _stage == Stage.Done;
        public string Status => _status;
        public int PieceCount => _stage == Stage.Finalize || _stage == Stage.Done ? _result.Count + (_pieces.Count - _finalizeIndex) : _pieces.Count;
        public IReadOnlyList<DecomposedPiece> Result => _result;

        /// <summary>0..1 (coarse).</summary>
        public float Progress
        {
            get
            {
                switch (_stage)
                {
                    case Stage.Voxelize: return 0f;
                    case Stage.Split: return 0.05f + 0.6f * math.saturate(_splitsDone / (float)math.max(1, SplitBudget));
                    case Stage.Merge: return 0.7f;
                    case Stage.Finalize: return 0.75f + 0.25f * _finalizeIndex / math.max(1f, _pieces.Count);
                    default: return 1f;
                }
            }
        }

        int SplitBudget => _p.AutoMerge ? math.min(_p.MaxPieces * 2, 256) : _p.MaxPieces;

        /// <param name="vertices">Authoring-local positions (copied).</param>
        public VoxelDecomposer(NativeArray<float3> vertices, NativeArray<int> indices, in VoxelDecomposerParams p)
        {
            _p = p;
            _vertices = new NativeArray<float3>(vertices, Allocator.Persistent);
            _indices = new NativeArray<int>(indices, Allocator.Persistent);
        }

        /// <summary>Runs work until <paramref name="budgetMs"/> elapses. Returns true when finished.</summary>
        public bool Step(double budgetMs)
        {
            var sw = Stopwatch.StartNew();
            do
            {
                switch (_stage)
                {
                    case Stage.Voxelize: Voxelize(); break;
                    case Stage.Split: SplitOnce(); break;
                    case Stage.Merge: Merge(); break;
                    case Stage.Finalize: FinalizeOne(); break;
                    case Stage.Done: return true;
                }
            } while (sw.Elapsed.TotalMilliseconds < budgetMs);
            return _stage == Stage.Done;
        }

        /// <summary>Synchronous run.</summary>
        public void RunToCompletion()
        {
            while (!Step(1000)) { }
        }

        // ---------- 1. voxelize ----------

        void Voxelize()
        {
            float3 mn = float.MaxValue, mx = float.MinValue;
            for (int i = 0; i < _indices.Length; i++)
            {
                float3 v = _vertices[_indices[i]];
                mn = math.min(mn, v);
                mx = math.max(mx, v);
            }
            float3 ext = mx - mn;
            float maxExt = math.cmax(ext);
            if (_indices.Length < 3 || maxExt <= 0f)
            {
                _stage = Stage.Done;
                return;
            }
            float cell = maxExt / _p.Resolution;
            int3 dims = (int3)math.ceil(ext / cell) + 5; // 2 voxels padding each side + rounding
            int n = dims.x * dims.y * dims.z;
            _grid = new VoxelGrid
            {
                Dims = dims,
                Origin = mn - 2f * cell,
                Cell = cell,
                State = new NativeArray<byte>(n, Allocator.Persistent),
                Prefix = new NativeArray<int>((dims.x + 1) * (dims.y + 1) * (dims.z + 1), Allocator.Persistent),
                NextX = new NativeArray<int>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                PrevX = new NativeArray<int>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                NextY = new NativeArray<int>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                PrevY = new NativeArray<int>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                NextZ = new NativeArray<int>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                PrevZ = new NativeArray<int>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            };
            var solid = new NativeReference<int>(Allocator.TempJob);
            new VoxelizeJob { Vertices = _vertices, Indices = _indices, Grid = _grid, SolidCount = solid }.Run();
            _grid.SolidCount = solid.Value;
            solid.Dispose();

            if (_grid.SolidCount == 0)
            {
                _stage = Stage.Done;
                return;
            }
            var root = new Piece();
            root.Boxes.Add(_grid.Tighten(new VoxelBox(int3.zero, dims)));
            Evaluate(root);
            _pieces.Add(root);
            _stage = Stage.Split;
            _status = "Cutting";
        }

        void Evaluate(Piece piece)
        {
            piece.Solid = 0;
            foreach (var b in piece.Boxes) piece.Solid += _grid.Count(b);
            var boxes = new NativeArray<VoxelBox>(piece.Boxes.ToArray(), Allocator.TempJob);
            var hv = new NativeList<float3>(256, Allocator.TempJob);
            var vol = new NativeReference<float>(Allocator.TempJob);
            var none = new NativeArray<float3>(0, Allocator.TempJob);
            new BoxesHullJob { Grid = _grid, Boxes = boxes, ExtraPoints = none, HullVertices = hv, Volume = vol }.Run();
            piece.HullVolume = math.max(vol.Value, piece.Solid);
            piece.HullPoints = hv.AsArray().ToArray();
            none.Dispose();
            Trace?.AppendLine($"eval boxes {piece.Boxes.Count} [{piece.Boxes[0].Min}..{piece.Boxes[0].Max}] hull {piece.HullVolume:F0} (raw {vol.Value:F0}) solid {piece.Solid} pts {piece.HullPoints.Length}");
            boxes.Dispose();
            hv.Dispose();
            vol.Dispose();
        }

        // ---------- 2. split ----------

        void SplitOnce()
        {
            float threshold = _p.Concavity * _grid.SolidCount;
            Trace?.AppendLine($"round pieces {_pieces.Count} threshold {threshold:F0} empties {string.Join(",", _pieces.ConvertAll(x => x.Empty.ToString("F0") + (x.Final ? "F" : "")))}");
            Piece worst = null;
            foreach (var piece in _pieces)
                if (!piece.Final && piece.Empty > threshold && (worst == null || piece.Empty > worst.Empty))
                    worst = piece;

            if (worst == null || _pieces.Count >= SplitBudget)
            {
                _stage = Stage.Merge;
                _status = "Merging";
                return;
            }

            var box = worst.Boxes[0];
            int bestAxis = -1, bestAt = 0;
            float bestCost = float.MaxValue;
            var candidates = new List<int2>();
            for (int axis = 0; axis < 3; axis++)
            {
                int len = box.Size[axis];
                if (len < 2) continue;
                int count = math.min(_p.Candidates, len - 1);
                for (int k = 1; k <= count; k++)
                    candidates.Add(new int2(axis, box.Min[axis] + (int)math.round(k * len / (float)(count + 1))));
                AddFeatureCandidates(box, axis, candidates);
            }
            if (candidates.Count > 0) Evaluate(box, candidates, ref bestAxis, ref bestAt, ref bestCost);

            if (_p.Refine && bestAxis >= 0)
            {
                int len = box.Size[bestAxis];
                int step = math.max(1, len / (math.min(_p.Candidates, len - 1) + 1));
                candidates.Clear();
                for (int dlt = -step + 1; dlt < step; dlt++)
                {
                    int at = bestAt + dlt;
                    if (dlt != 0 && at > box.Min[bestAxis] && at < box.Max[bestAxis]) candidates.Add(new int2(bestAxis, at));
                }
                if (candidates.Count > 0) Evaluate(box, candidates, ref bestAxis, ref bestAt, ref bestCost);
            }

            Trace?.AppendLine($"split [{box.Min}..{box.Max}] hull {worst.HullVolume:F0} solid {worst.Solid} → axis {bestAxis} at {bestAt} cost {bestCost:F0}");
            // No improvement possible (or no valid plane) → keep as is.
            if (bestAxis < 0 || bestCost >= worst.HullVolume * 0.999f)
            {
                worst.Final = true;
                return;
            }

            var l = box; l.Max[bestAxis] = bestAt;
            var r = box; r.Min[bestAxis] = bestAt;
            _pieces.Remove(worst);
            foreach (var half in new[] { _grid.Tighten(l), _grid.Tighten(r) })
            {
                if (half.IsEmpty) continue;
                var p = new Piece();
                p.Boxes.Add(half);
                Evaluate(p);
                _pieces.Add(p);
            }
            _splitsDone++;
        }

        /// <summary>
        /// Concave features: positions where the solid cross-section area jumps (an arm leaving the body,
        /// a step, a gap). Cutting exactly there separates convex parts cleanly.
        /// </summary>
        void AddFeatureCandidates(VoxelBox box, int axis, List<int2> candidates)
        {
            int len = box.Size[axis];
            const int keep = 6;
            var best = new List<int2>(); // (score, at)
            int prev = -1;
            for (int k = 0; k < len; k++)
            {
                var slab = box;
                slab.Min[axis] = box.Min[axis] + k;
                slab.Max[axis] = box.Min[axis] + k + 1;
                int area = _grid.Count(slab);
                if (k > 0 && area != prev)
                {
                    best.Add(new int2(math.abs(area - prev), box.Min[axis] + k));
                    if (best.Count > keep * 4)
                    {
                        best.Sort((x, y) => y.x.CompareTo(x.x));
                        best.RemoveRange(keep, best.Count - keep);
                    }
                }
                prev = area;
            }
            best.Sort((x, y) => y.x.CompareTo(x.x));
            for (int i = 0; i < best.Count && i < keep; i++)
                if (!candidates.Contains(new int2(axis, best[i].y))) candidates.Add(new int2(axis, best[i].y));
        }

        /// <summary>Optional trace of decisions (set to a StringBuilder to record).</summary>
        public System.Text.StringBuilder Trace;

        void Evaluate(VoxelBox box, List<int2> candidates, ref int bestAxis, ref int bestAt, ref float bestCost)
        {
            var cands = new NativeArray<int2>(candidates.ToArray(), Allocator.TempJob);
            var costs = new NativeArray<float>(candidates.Count, Allocator.TempJob);
            new SplitEvalJob { Grid = _grid, Box = box, Candidates = cands, Costs = costs }.Run();
            for (int i = 0; i < costs.Length; i++)
            {
                // Tie-break toward balanced cuts (fewer slivers).
                float cost = costs[i];
                if (cost < bestCost * 0.9995f)
                {
                    bestCost = cost;
                    bestAxis = cands[i].x;
                    bestAt = cands[i].y;
                }
            }
            cands.Dispose();
            costs.Dispose();
        }

        // ---------- 3. merge ----------

        void Merge()
        {
            bool autoMerge = _p.AutoMerge;
            while (_pieces.Count > 1)
            {
                bool mustMerge = _pieces.Count > _p.MaxPieces;
                if (!mustMerge && !autoMerge) break;

                // Auto Merge: the merged hull may waste at most Tolerance × (combined solid volume).
                FindBestPair(adjacentOnly: true, out int a, out int b, out float ratio);
                if (a < 0 && mustMerge) FindBestPair(adjacentOnly: false, out a, out b, out ratio);
                if (a < 0) break;
                if (!mustMerge && ratio > _p.MergeTolerance) break;
                Trace?.AppendLine($"merge {a}+{b} ratio {ratio:F3} forced {mustMerge}");

                var pa = _pieces[a];
                var pb = _pieces[b];
                pa.Boxes.AddRange(pb.Boxes);
                pa.Solid += pb.Solid;
                _pieces.RemoveAt(b);
                // Recompute exact merged hull.
                MergeHull(pa, pb);
            }
            _stage = Stage.Finalize;
            _status = "Building hulls";
        }

        void MergeHull(Piece into, Piece other)
        {
            var extra = new NativeArray<float3>(Concat(into.HullPoints, other.HullPoints), Allocator.TempJob);
            var none = new NativeArray<VoxelBox>(0, Allocator.TempJob);
            var hv = new NativeList<float3>(256, Allocator.TempJob);
            var vol = new NativeReference<float>(Allocator.TempJob);
            new BoxesHullJob { Grid = _grid, Boxes = none, ExtraPoints = extra, HullVertices = hv, Volume = vol }.Run();
            into.HullVolume = math.max(vol.Value, into.Solid);
            into.HullPoints = hv.AsArray().ToArray();
            extra.Dispose();
            none.Dispose();
            hv.Dispose();
            vol.Dispose();
        }

        static float3[] Concat(float3[] a, float3[] b)
        {
            var r = new float3[a.Length + b.Length];
            Array.Copy(a, r, a.Length);
            Array.Copy(b, 0, r, a.Length, b.Length);
            return r;
        }

        /// <summary>Pair whose merged hull is the most convex (least empty volume relative to its solid volume). Adjacent = boxes touching (≤ 1 voxel gap).</summary>
        void FindBestPair(bool adjacentOnly, out int bestA, out int bestB, out float bestCost)
        {
            bestA = bestB = -1;
            bestCost = float.MaxValue;
            var pairs = new List<int2>();
            for (int i = 0; i < _pieces.Count; i++)
            for (int j = i + 1; j < _pieces.Count; j++)
                if (!adjacentOnly || Adjacent(_pieces[i], _pieces[j]))
                    pairs.Add(new int2(i, j));
            if (pairs.Count == 0) return;

            // Pack all hull points once; pairs reference ranges.
            var starts = new int[_pieces.Count];
            int total = 0;
            for (int i = 0; i < _pieces.Count; i++) { starts[i] = total; total += _pieces[i].HullPoints.Length; }
            var points = new NativeArray<float3>(total, Allocator.TempJob);
            for (int i = 0; i < _pieces.Count; i++)
                NativeArray<float3>.Copy(_pieces[i].HullPoints, 0, points, starts[i], _pieces[i].HullPoints.Length);
            var packed = new NativeArray<int4>(pairs.Count, Allocator.TempJob);
            for (int k = 0; k < pairs.Count; k++)
            {
                int i = pairs[k].x, j = pairs[k].y;
                packed[k] = new int4(starts[i], _pieces[i].HullPoints.Length, starts[j], _pieces[j].HullPoints.Length);
            }
            var volumes = new NativeArray<float>(pairs.Count, Allocator.TempJob);
            new PairHullVolumeJob { Points = points, Pairs = packed, Volumes = volumes }.Run();

            for (int k = 0; k < pairs.Count; k++)
            {
                var pa = _pieces[pairs[k].x];
                var pb = _pieces[pairs[k].y];
                // Concavity of the merged piece: empty volume of its hull relative to its solid volume.
                // (Differences of hull volumes go negative once hulls overlap, so they are not used.)
                float solid = pa.Solid + pb.Solid;
                float cost = (math.max(volumes[k], solid) - solid) / math.max(1f, solid);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestA = pairs[k].x;
                    bestB = pairs[k].y;
                }
            }
            points.Dispose();
            packed.Dispose();
            volumes.Dispose();
        }

        static bool Adjacent(Piece a, Piece b)
        {
            foreach (var x in a.Boxes)
            foreach (var y in b.Boxes)
                if (math.all(x.Min <= y.Max + 1) && math.all(y.Min <= x.Max + 1))
                    return true;
            return false;
        }

        // ---------- 4. finalize ----------

        void FinalizeOne()
        {
            if (_finalizeIndex >= _pieces.Count)
            {
                AssignTriangles();
                _stage = Stage.Done;
                _status = "Done";
                return;
            }

            var piece = _pieces[_finalizeIndex++];
            float h = _grid.Cell;
            float eps = h * 1e-3f;
            var boxes = new NativeArray<float3>(piece.Boxes.Count * 2, Allocator.TempJob);
            var extra = new List<float3>();
            for (int i = 0; i < piece.Boxes.Count; i++)
            {
                var b = piece.Boxes[i];
                boxes[i * 2] = _grid.ToLocal(b.Min);
                boxes[i * 2 + 1] = _grid.ToLocal(b.Max);
                // Box corners deep inside the solid are extreme points the surface clip cannot provide.
                for (int k = 0; k < 8; k++)
                {
                    int3 corner = new int3((k & 1) == 0 ? b.Min.x : b.Max.x, (k & 2) == 0 ? b.Min.y : b.Max.y, (k & 4) == 0 ? b.Min.z : b.Max.z);
                    if (CornerInside(corner)) extra.Add(_grid.ToLocal(corner));
                }
            }
            var extraArr = new NativeArray<float3>(extra.ToArray(), Allocator.TempJob);
            var hv = new NativeList<float3>(256, Allocator.TempJob);
            new PieceGeometryJob { Vertices = _vertices, Indices = _indices, Boxes = boxes, Epsilon = eps, ExtraPoints = extraArr, HullVertices = hv }.Run();

            float3[] points = hv.Length >= 4 ? hv.AsArray().ToArray() : null;
            Trace?.AppendLine($"final boxes {string.Join(";", piece.Boxes.ConvertAll(x => $"{_grid.ToLocal(x.Min)}..{_grid.ToLocal(x.Max)}"))} extra {extra.Count} hull {hv.Length}: {string.Join(" ", System.Linq.Enumerable.Select(hv.AsArray().ToArray(), q => q.ToString()))}");
            if (points == null)
            {
                // Interior-only region: its voxel hull is exact.
                points = new float3[piece.HullPoints.Length];
                for (int i = 0; i < points.Length; i++) points[i] = _grid.ToLocal(piece.HullPoints[i]);
            }
            _result.Add(new DecomposedPiece { Points = points });
            boxes.Dispose();
            extraArr.Dispose();
            hv.Dispose();
        }

        /// <summary>All 8 voxels around a grid corner are interior (not surface) → the corner lies inside the solid.</summary>
        bool CornerInside(int3 corner)
        {
            for (int k = 0; k < 8; k++)
                if (!_grid.IsInterior(corner - 1 + new int3(k & 1, (k >> 1) & 1, (k >> 2) & 1)))
                    return false;
            return true;
        }

        void AssignTriangles()
        {
            int triCount = _indices.Length / 3;
            var boxes = new List<(int piece, float3 min, float3 max)>();
            for (int p = 0; p < _pieces.Count; p++)
                foreach (var b in _pieces[p].Boxes)
                    boxes.Add((p, _grid.ToLocal(b.Min), _grid.ToLocal(b.Max)));

            for (int t = 0; t < triCount; t++)
            {
                float3 c = (_vertices[_indices[t * 3]] + _vertices[_indices[t * 3 + 1]] + _vertices[_indices[t * 3 + 2]]) / 3f;
                int best = 0;
                float bestD = float.MaxValue;
                foreach (var (piece, min, max) in boxes)
                {
                    float3 d = math.max(math.max(min - c, c - max), 0f);
                    float dist = math.lengthsq(d);
                    if (dist < bestD)
                    {
                        bestD = dist;
                        best = piece;
                        if (dist == 0f) break;
                    }
                }
                if (best < _result.Count) _result[best].Triangles.Add(t);
            }
        }

        public void Dispose()
        {
            if (_vertices.IsCreated) _vertices.Dispose();
            if (_indices.IsCreated) _indices.Dispose();
            _grid.Dispose();
        }
    }
}
