using System;
using RecLabs.SmartColliders.Geometry;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace RecLabs.SmartColliders.EditorTools
{
    /// <summary>Undoable authoring operations: generate, refit, convert. Does not touch real colliders (see <see cref="SmartColliderBaker"/>).</summary>
    public static class SmartColliderOperations
    {
        /// <summary>Generates parts with the authoring's own mode (<see cref="SmartColliderAuthoring.Mode"/>). Synchronous.</summary>
        public static bool Generate(SmartColliderAuthoring authoring) => Generate(authoring, authoring.generateMode);

        /// <summary>Replaces all parts. Synchronous (Decompose blocks; use <see cref="DecompositionRunner"/> for async).</summary>
        public static bool Generate(SmartColliderAuthoring authoring, GenerateMode mode)
        {
            using var geo = SourceGeometry.Gather(authoring);
            if (geo.IsEmpty)
            {
                Debug.LogWarning($"[Smart Colliders] '{authoring.name}' has no triangle meshes to fit.", authoring);
                return false;
            }

            System.Collections.Generic.List<SmartColliderPart> parts;
            switch (mode)
            {
                case GenerateMode.Decompose:
                    parts = DecompositionRunner.RunSync(authoring, geo);
                    break;
                case GenerateMode.PerBone:
                    parts = SkinnedPartition.Build(authoring, geo) ?? SplitPerMesh(authoring, geo);
                    break;
                case GenerateMode.Single:
                    parts = new System.Collections.Generic.List<SmartColliderPart> { CreatePart(0, authoring.name, TriangleRanges.All(geo.TriangleCount)) };
                    break;
                default:
                    parts = SplitPerMesh(authoring, geo);
                    break;
            }
            ApplyParts(authoring, geo, parts, "Generate Smart Colliders");
            return true;
        }

        static System.Collections.Generic.List<SmartColliderPart> SplitPerMesh(SmartColliderAuthoring authoring, SourceGeometry geo)
        {
            var parts = new System.Collections.Generic.List<SmartColliderPart>();
            if (geo.Sources.Count == 1)
            {
                parts.Add(CreatePart(0, authoring.name, TriangleRanges.All(geo.TriangleCount)));
                return parts;
            }
            foreach (var src in geo.Sources)
            {
                if (src.TriangleCount == 0) continue;
                parts.Add(CreatePart(parts.Count, src.Name, TriangleRanges.Range(src.TriangleStart, src.TriangleCount)));
            }
            return parts;
        }

        /// <summary>Replaces the parts (one Undo step), fits them and stores the source signature.</summary>
        internal static void ApplyParts(SmartColliderAuthoring authoring, SourceGeometry geo,
            System.Collections.Generic.List<SmartColliderPart> parts, string undoName)
        {
            Undo.RecordObject(authoring, undoName);
            authoring.parts.Clear();
            authoring.parts.AddRange(parts);
            foreach (var part in authoring.parts)
                Fit(part, geo, authoring.fitSettings);
            authoring.parts.RemoveAll(p => !p.HasFit);
            authoring.sourceVertexCount = geo.Vertices.Length;
            authoring.sourceTriangleCount = geo.TriangleCount;
            authoring.sourceVolume = ClosedVolume(geo);
        }

        /// <summary>
        /// Enclosed volume of the source meshes (divergence theorem), or 0 when the surface is not closed / consistently
        /// wound (vertices welded by position, every edge must be shared by exactly two opposite half-edges).
        /// </summary>
        internal static float ClosedVolume(SourceGeometry geo)
        {
            if (geo.IsEmpty) return 0f;
            var v = geo.Vertices;
            var idx = geo.Indices;
            float3 mn = v[0], mx = v[0];
            for (int i = 1; i < v.Length; i++) { mn = math.min(mn, v[i]); mx = math.max(mx, v[i]); }
            float cell = math.max(math.cmax(mx - mn), 1e-6f) * 1e-5f;
            var weld = new System.Collections.Generic.Dictionary<int3, int>();
            var id = new int[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                var key = (int3)math.round((v[i] - mn) / cell);
                if (!weld.TryGetValue(key, out int w)) weld[key] = w = weld.Count;
                id[i] = w;
            }
            var half = new System.Collections.Generic.Dictionary<long, int>();
            double vol = 0;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                vol += math.dot(v[a], math.cross(v[b], v[c])) / 6.0;
                int ia = id[a], ib = id[b], ic = id[c];
                if (ia == ib || ib == ic || ia == ic) continue;
                foreach (var (p, q) in new[] { (ia, ib), (ib, ic), (ic, ia) })
                {
                    long key = ((long)p << 32) | (uint)q;
                    half[key] = half.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            }
            foreach (var kv in half)
            {
                long rev = ((kv.Key & 0xFFFFFFFFL) << 32) | (kv.Key >> 32);
                if (kv.Value != 1 || !half.TryGetValue(rev, out int r) || r != 1) return 0f;
            }
            return (float)math.abs(vol);
        }

        /// <summary>Decomposition pieces → parts (explicit fit points + centroid-assigned triangles).</summary>
        internal static System.Collections.Generic.List<SmartColliderPart> PartsFromPieces(SmartColliderAuthoring authoring,
            System.Collections.Generic.IReadOnlyList<Geometry.Decomposition.DecomposedPiece> pieces)
        {
            var parts = new System.Collections.Generic.List<SmartColliderPart>(pieces.Count);
            for (int i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i];
                if (piece.Points == null || piece.Points.Length == 0) continue;
                piece.Triangles.Sort();
                var part = CreatePart(parts.Count, $"{authoring.name} {parts.Count + 1}", TriangleRanges.FromSorted(piece.Triangles));
                part.fitPoints = new Vector3[piece.Points.Length];
                for (int k = 0; k < piece.Points.Length; k++) part.fitPoints[k] = piece.Points[k];
                parts.Add(part);
            }
            return parts;
        }

        public static void RefitAll(SmartColliderAuthoring authoring)
        {
            using var geo = SourceGeometry.Gather(authoring);
            Undo.RecordObject(authoring, "Refit Smart Colliders");
            foreach (var part in authoring.parts)
                if (TriangleRanges.IsValidFor(part.triangleRanges, geo.TriangleCount))
                    Fit(part, geo, authoring.fitSettings);
        }

        public static void Refit(SmartColliderAuthoring authoring, int partIndex)
        {
            using var geo = SourceGeometry.Gather(authoring);
            var part = authoring.parts[partIndex];
            if (!TriangleRanges.IsValidFor(part.triangleRanges, geo.TriangleCount))
            {
                Debug.LogWarning($"[Smart Colliders] Part '{part.name}' no longer matches the source meshes. Regenerate.", authoring);
                return;
            }
            Undo.RecordObject(authoring, "Refit Smart Collider");
            Fit(part, geo, authoring.fitSettings);
        }

        /// <summary>Forces a collider type (instant: all candidates are cached).</summary>
        public static void SetType(SmartColliderAuthoring authoring, int partIndex, ShapeType type)
        {
            var part = authoring.parts[partIndex];
            if (!part.reports[(int)type].Valid) return;
            Undo.RecordObject(authoring, $"Convert to {type}");
            part.autoType = false;
            part.type = type;
        }

        /// <summary>Returns the part to fitter-selected type.</summary>
        public static void SetAuto(SmartColliderAuthoring authoring, int partIndex)
        {
            var part = authoring.parts[partIndex];
            var reports = new NativeArray<FitReport>(part.reports, Allocator.Temp);
            int selected = ShapeFitter.Select(reports, authoring.fitSettings);
            reports.Dispose();
            if (selected < 0) return;
            Undo.RecordObject(authoring, "Auto Collider Type");
            part.autoType = true;
            part.type = (ShapeType)selected;
        }

        /// <summary>Removes parts (their triangles get no collider). Re-bake to apply to real colliders.</summary>
        public static void RemoveParts(SmartColliderAuthoring authoring, System.Collections.Generic.IEnumerable<int> partIndices)
        {
            var sorted = new System.Collections.Generic.List<int>(partIndices);
            sorted.Sort();
            if (sorted.Count == 0) return;
            Undo.RecordObject(authoring, sorted.Count == 1 ? "Remove Collider" : $"Remove {sorted.Count} Colliders");
            for (int k = sorted.Count - 1; k >= 0; k--)
            {
                int i = sorted[k];
                if (i >= 0 && i < authoring.parts.Count && (k == sorted.Count - 1 || sorted[k + 1] != i))
                    authoring.parts.RemoveAt(i);
            }
        }

        /// <summary>
        /// Moves source triangles into one part (paint/split). <paramref name="targetPart"/> &lt; 0 creates a new part.
        /// Triangles are removed from every other part; parts left empty are deleted; touched parts are re-fitted.
        /// </summary>
        /// <returns>Index of the receiving part after removals, or -1 if nothing changed.</returns>
        public static int AssignTriangles(SmartColliderAuthoring authoring, System.Collections.Generic.ICollection<int> triangles, int targetPart = -1)
        {
            if (triangles == null || triangles.Count == 0) return -1;
            using var geo = SourceGeometry.Gather(authoring);
            var moved = new System.Collections.Generic.HashSet<int>();
            foreach (int t in triangles)
                if (t >= 0 && t < geo.TriangleCount) moved.Add(t);
            if (moved.Count == 0) return -1;
            if (targetPart >= authoring.parts.Count) targetPart = -1;

            Undo.RecordObject(authoring, targetPart < 0 ? "New Collider from Paint" : "Paint into Collider");

            SmartColliderPart target;
            if (targetPart < 0)
            {
                target = CreatePart(NextColorIndex(authoring), UniqueName(authoring, "Painted"), Array.Empty<int>());
                authoring.parts.Add(target);
                targetPart = authoring.parts.Count - 1;
            }
            else target = authoring.parts[targetPart];

            var touched = new System.Collections.Generic.List<SmartColliderPart> { target };
            var emptied = new System.Collections.Generic.HashSet<SmartColliderPart>();
            foreach (var part in authoring.parts)
            {
                if (part == target) continue;
                var kept = new System.Collections.Generic.List<int>();
                bool changed = false;
                foreach (int t in TriangleRanges.Enumerate(part.triangleRanges))
                {
                    if (moved.Contains(t)) changed = true;
                    else kept.Add(t);
                }
                if (!changed) continue;
                kept.Sort();
                part.triangleRanges = TriangleRanges.FromSorted(kept);
                part.fitPoints = Array.Empty<Vector3>(); // painted parts become triangle based
                touched.Add(part);
                if (kept.Count == 0) emptied.Add(part);
            }

            var all = new System.Collections.Generic.List<int>(moved);
            all.AddRange(TriangleRanges.Enumerate(target.triangleRanges));
            all.Sort();
            all = DistinctSorted(all);
            target.triangleRanges = TriangleRanges.FromSorted(all);
            target.fitPoints = Array.Empty<Vector3>();

            authoring.parts.RemoveAll(emptied.Contains);
            foreach (var part in touched)
                if (part.TriangleCount > 0 && authoring.parts.Contains(part))
                    Fit(part, geo, authoring.fitSettings);
            return authoring.parts.IndexOf(target);
        }

        /// <summary>Merges parts into the first (lowest index) one: union of triangles, re-fitted, type back to auto.</summary>
        /// <returns>Index of the merged part, or -1.</returns>
        public static int MergeParts(SmartColliderAuthoring authoring, System.Collections.Generic.IEnumerable<int> partIndices)
        {
            var sorted = new System.Collections.Generic.List<int>();
            foreach (int i in partIndices)
                if (i >= 0 && i < authoring.parts.Count && !sorted.Contains(i)) sorted.Add(i);
            sorted.Sort();
            if (sorted.Count < 2) return sorted.Count == 1 ? sorted[0] : -1;

            using var geo = SourceGeometry.Gather(authoring);
            Undo.RecordObject(authoring, $"Merge {sorted.Count} Colliders");
            var target = authoring.parts[sorted[0]];
            // Any decomposition part involved → the merged part keeps explicit points (union of every part's points).
            bool explicitPoints = sorted.Exists(i => authoring.parts[i].HasFitPoints);
            Vector3[] mergedPoints = null;
            if (explicitPoints)
            {
                var pts = new System.Collections.Generic.List<Vector3>();
                foreach (int i in sorted)
                {
                    using var p = geo.PartPoints(authoring.parts[i], Allocator.Temp);
                    foreach (var v in p) pts.Add(v);
                }
                mergedPoints = pts.ToArray();
            }
            var all = new System.Collections.Generic.List<int>();
            foreach (int i in sorted) all.AddRange(TriangleRanges.Enumerate(authoring.parts[i].triangleRanges));
            all.Sort();
            target.triangleRanges = TriangleRanges.FromSorted(DistinctSorted(all));
            target.autoType = true;
            if (mergedPoints != null) target.fitPoints = mergedPoints;
            for (int k = sorted.Count - 1; k >= 1; k--) authoring.parts.RemoveAt(sorted[k]);
            if (TriangleRanges.IsValidFor(target.triangleRanges, geo.TriangleCount))
                Fit(target, geo, authoring.fitSettings);
            return sorted[0];
        }

        /// <summary>Renames a part (undoable).</summary>
        public static void Rename(SmartColliderAuthoring authoring, int partIndex, string name)
        {
            if (partIndex < 0 || partIndex >= authoring.parts.Count || string.IsNullOrWhiteSpace(name)) return;
            Undo.RecordObject(authoring, "Rename Collider");
            authoring.parts[partIndex].name = name.Trim();
        }

        static System.Collections.Generic.List<int> DistinctSorted(System.Collections.Generic.List<int> sorted)
        {
            var result = new System.Collections.Generic.List<int>(sorted.Count);
            foreach (int v in sorted)
                if (result.Count == 0 || result[result.Count - 1] != v) result.Add(v);
            return result;
        }

        internal static int NextColorIndex(SmartColliderAuthoring a) => a.parts.Count;

        internal static string UniqueName(SmartColliderAuthoring a, string baseName)
        {
            for (int n = 1; ; n++)
            {
                string candidate = $"{baseName} {n}";
                if (!a.parts.Exists(p => p.name == candidate)) return candidate;
            }
        }

        /// <summary>True when the source meshes changed since generation (parts may be wrong).</summary>
        public static bool IsStale(SmartColliderAuthoring authoring)
        {
            if (authoring.parts.Count == 0) return false;
            var (v, t) = SourceGeometry.Signature(authoring);
            return v != authoring.sourceVertexCount || t != authoring.sourceTriangleCount;
        }

        static SmartColliderPart CreatePart(int index, string name, int[] ranges) => new SmartColliderPart
        {
            name = name,
            color = SmartColliderPart.PaletteColor(index),
            triangleRanges = ranges,
        };

        internal static void Fit(SmartColliderPart part, SourceGeometry geo, in ShapeFitSettings settings)
        {
            var points = geo.PartPoints(part, Allocator.TempJob);
            var hullV = new NativeList<float3>(Allocator.TempJob);
            var hullT = new NativeList<int>(Allocator.TempJob);
            var candidates = new NativeArray<ColliderShape>(ShapeFitter.CandidateCount, Allocator.TempJob);
            var reports = new NativeArray<FitReport>(ShapeFitter.CandidateCount, Allocator.TempJob);
            var selected = new NativeReference<int>(Allocator.TempJob);
            try
            {
                new ShapeFitJob
                {
                    Points = points,
                    Settings = settings,
                    HullVertices = hullV,
                    HullTriangles = hullT,
                    Candidates = candidates,
                    Reports = reports,
                    Selected = selected,
                }.Run();

                part.candidates = candidates.ToArray();
                part.reports = reports.ToArray();
                part.hullVertices = hullV.AsArray().Reinterpret<Vector3>().ToArray();
                part.hullTriangles = hullT.AsArray().ToArray();
                if (part.bone != null) part.bonePose = geo.WorldToLocal * part.bone.localToWorldMatrix;

                int sel = selected.Value;
                if (sel < 0) return;
                if (part.autoType || !part.reports[(int)part.type].Valid)
                    part.type = (ShapeType)sel;
            }
            finally
            {
                points.Dispose();
                hullV.Dispose();
                hullT.Dispose();
                candidates.Dispose();
                reports.Dispose();
                selected.Dispose();
            }
        }
    }
}
