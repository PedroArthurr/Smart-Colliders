using System;
using System.Collections.Generic;
using RecLabs.SmartColliders.EditorTools;
using RecLabs.SmartColliders.Geometry;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>
    /// Triangle painting for split/paint-into-part. Holds a cached copy of the source geometry (authoring-local),
    /// the painted triangle set per authoring (session only) and the overlay mesh.
    /// </summary>
    internal static class PaintSession
    {
        const string BrushPref = "RecLabs.SmartColliders.Paint.BrushPixels";
        public const float MinBrush = 4f, MaxBrush = 160f;

        public static event Action Changed;

        public static float BrushPixels
        {
            get => EditorPrefs.GetFloat(BrushPref, 28f);
            set
            {
                EditorPrefs.SetFloat(BrushPref, Mathf.Clamp(value, MinBrush, MaxBrush));
                Changed?.Invoke();
            }
        }

        sealed class Cache : IDisposable
        {
            public SmartColliderAuthoring Owner;
            public int VertexCount, TriangleCount;
            public NativeArray<float3> Vertices;
            public NativeArray<int> Indices;
            public int[] Welded;               // weld id per vertex (lazy)
            public List<int>[] WeldToTris;     // weld id → triangles (lazy)
            public readonly HashSet<int> Painted = new HashSet<int>();
            public Mesh Overlay;
            public bool OverlayDirty = true;

            public void Dispose()
            {
                if (Vertices.IsCreated) Vertices.Dispose();
                if (Indices.IsCreated) Indices.Dispose();
                if (Overlay != null) UnityEngine.Object.DestroyImmediate(Overlay);
            }
        }

        static Cache s_Cache;

        static PaintSession()
        {
            Undo.undoRedoPerformed += Invalidate;
            AssemblyReloadEvents.beforeAssemblyReload += () => { s_Cache?.Dispose(); s_Cache = null; };
        }

        static Cache Get(SmartColliderAuthoring a)
        {
            if (a == null) return null;
            if (s_Cache != null && s_Cache.Owner == a && s_Cache.Vertices.IsCreated)
            {
                var (v, t) = SourceGeometry.Signature(a);
                if (v == s_Cache.VertexCount && t == s_Cache.TriangleCount) return s_Cache;
            }

            var painted = s_Cache != null && s_Cache.Owner == a ? new HashSet<int>(s_Cache.Painted) : null;
            s_Cache?.Dispose();
            using var geo = SourceGeometry.Gather(a);
            s_Cache = new Cache
            {
                Owner = a,
                VertexCount = geo.Vertices.Length,
                TriangleCount = geo.TriangleCount,
                Vertices = new NativeArray<float3>(geo.Vertices, Allocator.Persistent),
                Indices = new NativeArray<int>(geo.Indices, Allocator.Persistent),
            };
            if (painted != null)
                foreach (int t in painted) if (t < s_Cache.TriangleCount) s_Cache.Painted.Add(t);
            return s_Cache;
        }

        /// <summary>Undo may have changed parts/geometry: rebuild the overlay (geometry is re-validated by signature on next use).</summary>
        public static void Invalidate()
        {
            if (s_Cache == null) return;
            s_Cache.OverlayDirty = true;
        }

        public static int Count(SmartColliderAuthoring a) =>
            s_Cache != null && s_Cache.Owner == a ? s_Cache.Painted.Count : 0;

        public static ICollection<int> Painted(SmartColliderAuthoring a) =>
            s_Cache != null && s_Cache.Owner == a ? (ICollection<int>)new List<int>(s_Cache.Painted) : Array.Empty<int>();

        public static void Clear(SmartColliderAuthoring a)
        {
            if (s_Cache == null || s_Cache.Owner != a || s_Cache.Painted.Count == 0) return;
            s_Cache.Painted.Clear();
            s_Cache.OverlayDirty = true;
            Changed?.Invoke();
        }

        /// <summary>Paints (or erases) every triangle under a screen-space brush centered on the mouse.</summary>
        /// <returns>True if the brush hit the mesh.</returns>
        public static bool Stroke(SmartColliderAuthoring a, Vector2 guiMouse, bool erase, out Vector3 worldHit, out Vector3 worldNormal, out float worldRadius)
        {
            worldHit = worldNormal = default;
            worldRadius = 0f;
            if (!Raycast(a, guiMouse, out var c, out int tri, out float3 localHit)) return false;

            float3 n = TriangleNormal(c, tri);
            worldHit = a.transform.TransformPoint(localHit);
            worldNormal = a.transform.TransformDirection(n).normalized;
            worldRadius = BrushPixels * HandleUtility.GetHandleSize(worldHit) / 80f;
            float localRadius = worldRadius / Mathf.Max(MaxAbs(a.transform.lossyScale), 1e-6f);

            var result = new NativeList<int>(256, Allocator.TempJob);
            try
            {
                new MeshGatherJob
                {
                    Vertices = c.Vertices,
                    Indices = c.Indices,
                    Center = localHit,
                    Radius = localRadius,
                    Result = result,
                }.Run();

                bool changed = false;
                for (int i = 0; i < result.Length; i++)
                {
                    int t = result[i];
                    // Skip back faces relative to the hit (brush shouldn't bleed through thin walls).
                    if (math.dot(TriangleNormal(c, t), n) < -0.2f) continue;
                    changed |= erase ? c.Painted.Remove(t) : c.Painted.Add(t);
                }
                changed |= erase ? c.Painted.Remove(tri) : c.Painted.Add(tri);
                if (changed)
                {
                    c.OverlayDirty = true;
                    Changed?.Invoke();
                }
            }
            finally
            {
                result.Dispose();
            }
            return true;
        }

        /// <summary>Brush preview position without painting.</summary>
        public static bool Probe(SmartColliderAuthoring a, Vector2 guiMouse, out Vector3 worldHit, out Vector3 worldNormal, out float worldRadius)
        {
            worldHit = worldNormal = default;
            worldRadius = 0f;
            if (!Raycast(a, guiMouse, out var c, out int tri, out float3 localHit)) return false;
            worldHit = a.transform.TransformPoint(localHit);
            worldNormal = a.transform.TransformDirection(TriangleNormal(c, tri)).normalized;
            worldRadius = BrushPixels * HandleUtility.GetHandleSize(worldHit) / 80f;
            return true;
        }

        /// <summary>Adds one ring of neighbouring triangles (shared welded vertex).</summary>
        public static void Grow(SmartColliderAuthoring a)
        {
            var c = Get(a);
            if (c == null || c.Painted.Count == 0) return;
            EnsureAdjacency(c);
            var add = new List<int>();
            foreach (int t in c.Painted)
                for (int k = 0; k < 3; k++)
                    foreach (int n in c.WeldToTris[c.Welded[c.Indices[t * 3 + k]]])
                        if (!c.Painted.Contains(n)) add.Add(n);
            if (add.Count == 0) return;
            foreach (int t in add) c.Painted.Add(t);
            c.OverlayDirty = true;
            Changed?.Invoke();
        }

        /// <summary>Extends the paint to every triangle connected (through welded vertices) to a painted one.</summary>
        public static void FillConnected(SmartColliderAuthoring a)
        {
            var c = Get(a);
            if (c == null || c.Painted.Count == 0) return;
            EnsureAdjacency(c);
            var stack = new Stack<int>(c.Painted);
            int before = c.Painted.Count;
            while (stack.Count > 0)
            {
                int t = stack.Pop();
                for (int k = 0; k < 3; k++)
                    foreach (int n in c.WeldToTris[c.Welded[c.Indices[t * 3 + k]]])
                        if (c.Painted.Add(n)) stack.Push(n);
            }
            if (c.Painted.Count == before) return;
            c.OverlayDirty = true;
            Changed?.Invoke();
        }

        /// <summary>Paints every triangle of the given parts (start a split from an existing part).</summary>
        public static void PaintParts(SmartColliderAuthoring a, IEnumerable<int> parts)
        {
            var c = Get(a);
            if (c == null) return;
            foreach (int p in parts)
            {
                if (p < 0 || p >= a.Parts.Count) continue;
                foreach (int t in TriangleRanges.Enumerate(a.Parts[p].triangleRanges))
                    if (t < c.TriangleCount) c.Painted.Add(t);
            }
            c.OverlayDirty = true;
            Changed?.Invoke();
        }

        public static void DrawOverlay(SmartColliderAuthoring a)
        {
            if (s_Cache == null || s_Cache.Owner != a || s_Cache.Painted.Count == 0) return;
            var c = s_Cache;
            if (c.OverlayDirty || c.Overlay == null)
            {
                if (c.Overlay == null) c.Overlay = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                var verts = new List<Vector3>(c.Painted.Count * 3);
                var tris = new List<int>(c.Painted.Count * 3);
                foreach (int t in c.Painted)
                {
                    if (t >= c.TriangleCount) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        tris.Add(verts.Count);
                        verts.Add(c.Vertices[c.Indices[t * 3 + k]]);
                    }
                }
                c.Overlay.Clear();
                c.Overlay.SetVertices(verts);
                c.Overlay.SetTriangles(tris, 0);
                c.Overlay.RecalculateNormals();
                c.Overlay.RecalculateBounds();
                c.OverlayDirty = false;
            }
            var color = SmartCollidersTheme.Selection;
            color.a = 0.7f;
            ColliderPreviewRenderer.DrawOverlay(c.Overlay, a.transform.localToWorldMatrix, color);
        }

        // ---------- helpers ----------

        static bool Raycast(SmartColliderAuthoring a, Vector2 guiMouse, out Cache c, out int tri, out float3 localHit)
        {
            tri = -1;
            localHit = default;
            c = Get(a);
            if (c == null || c.TriangleCount == 0) return false;
            Ray ray = HandleUtility.GUIPointToWorldRay(guiMouse);
            Matrix4x4 toLocal = a.transform.worldToLocalMatrix;
            float3 o = toLocal.MultiplyPoint3x4(ray.origin);
            float3 d = toLocal.MultiplyVector(ray.direction);

            var triRef = new NativeReference<int>(Allocator.TempJob);
            var dist = new NativeReference<float>(Allocator.TempJob);
            try
            {
                new MeshRaycastJob
                {
                    Vertices = c.Vertices,
                    Indices = c.Indices,
                    Origin = o,
                    Direction = d,
                    Triangle = triRef,
                    Distance = dist,
                }.Run();
                tri = triRef.Value;
                if (tri < 0) return false;
                localHit = o + d * dist.Value;
                return true;
            }
            finally
            {
                triRef.Dispose();
                dist.Dispose();
            }
        }

        static float3 TriangleNormal(Cache c, int t)
        {
            float3 a = c.Vertices[c.Indices[t * 3]], b = c.Vertices[c.Indices[t * 3 + 1]], d = c.Vertices[c.Indices[t * 3 + 2]];
            return math.normalizesafe(math.cross(b - a, d - a));
        }

        static void EnsureAdjacency(Cache c)
        {
            if (c.Welded != null) return;
            float3 min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < c.Vertices.Length; i++) { min = math.min(min, c.Vertices[i]); max = math.max(max, c.Vertices[i]); }
            float cell = math.max(math.cmax(max - min) * 1e-5f, 1e-7f);
            var weld = new NativeArray<int>(c.Vertices.Length, Allocator.Temp);
            MeshQueries.Weld(c.Vertices, cell, weld);
            c.Welded = weld.ToArray();
            weld.Dispose();

            int ids = 0;
            foreach (int w in c.Welded) ids = Mathf.Max(ids, w + 1);
            c.WeldToTris = new List<int>[ids];
            for (int i = 0; i < ids; i++) c.WeldToTris[i] = new List<int>(6);
            for (int t = 0; t < c.TriangleCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    var list = c.WeldToTris[c.Welded[c.Indices[t * 3 + k]]];
                    if (list.Count == 0 || list[list.Count - 1] != t) list.Add(t);
                }
        }

        static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));
    }
}
