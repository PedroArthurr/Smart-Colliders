using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RecLabs.SmartColliders.EditorTools
{
    /// <summary>
    /// All source triangles of an authoring object, merged into its local space.
    /// Triangle indices into this combined list are what parts store (<see cref="TriangleRanges"/>).
    /// </summary>
    internal sealed class SourceGeometry : IDisposable
    {
        public struct Source
        {
            public Component Component;
            public string Name;
            public int TriangleStart;
            public int VertexStart;
            public int VertexCount;
            public int TriangleCount;
        }

        public NativeArray<float3> Vertices;
        public NativeArray<int> Indices;
        public readonly List<Source> Sources = new List<Source>();
        /// <summary>World → authoring-local at gather time.</summary>
        public Matrix4x4 WorldToLocal = Matrix4x4.identity;

        public int TriangleCount => Indices.IsCreated ? Indices.Length / 3 : 0;
        public bool IsEmpty => TriangleCount == 0;

        public static SourceGeometry Gather(SmartColliderAuthoring authoring)
        {
            var verts = new List<Vector3>(4096);
            var indices = new List<int>(8192);
            var geo = new SourceGeometry();
            Matrix4x4 toLocal = authoring.transform.worldToLocalMatrix;
            geo.WorldToLocal = toLocal;
            var scratchV = new List<Vector3>();
            var scratchI = new List<int>();

            foreach (var mf in GetSources<MeshFilter>(authoring))
            {
                if (mf.sharedMesh == null) continue;
                Append(geo, mf, mf.sharedMesh, toLocal * mf.transform.localToWorldMatrix, verts, indices, scratchV, scratchI);
            }

            foreach (var smr in GetSources<SkinnedMeshRenderer>(authoring))
            {
                if (smr.sharedMesh == null) continue;
                var baked = new Mesh();
                try
                {
                    smr.BakeMesh(baked, true); // scale baked in → use unscaled TRS
                    var t = smr.transform;
                    Append(geo, smr, baked, toLocal * Matrix4x4.TRS(t.position, t.rotation, Vector3.one), verts, indices, scratchV, scratchI);
                }
                finally
                {
                    Object.DestroyImmediate(baked);
                }
            }

            geo.Vertices = new NativeArray<float3>(verts.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < verts.Count; i++) geo.Vertices[i] = verts[i];
            geo.Indices = new NativeArray<int>(indices.ToArray(), Allocator.Persistent);
            return geo;
        }

        /// <summary>Cheap (no vertex copy) signature used to detect stale parts.</summary>
        public static (int vertices, int triangles) Signature(SmartColliderAuthoring authoring)
        {
            int v = 0, t = 0;
            foreach (var mf in GetSources<MeshFilter>(authoring))
                Accumulate(mf.sharedMesh, ref v, ref t);
            foreach (var smr in GetSources<SkinnedMeshRenderer>(authoring))
                Accumulate(smr.sharedMesh, ref v, ref t);
            return (v, t);
        }

        static void Accumulate(Mesh mesh, ref int v, ref int t)
        {
            if (mesh == null) return;
            v += mesh.vertexCount;
            for (int s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles)
                    t += (int)mesh.GetIndexCount(s) / 3;
        }

        static T[] GetSources<T>(SmartColliderAuthoring a) where T : Component =>
            a.includeChildren ? a.GetComponentsInChildren<T>(false) : a.GetComponents<T>();

        static void Append(SourceGeometry geo, Component component, Mesh mesh, Matrix4x4 matrix,
            List<Vector3> verts, List<int> indices, List<Vector3> scratchV, List<int> scratchI)
        {
            int baseVertex = verts.Count;
            int triStart = indices.Count / 3;

            mesh.GetVertices(scratchV);
            for (int i = 0; i < scratchV.Count; i++) verts.Add(matrix.MultiplyPoint3x4(scratchV[i]));

            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                mesh.GetTriangles(scratchI, s, true);
                for (int i = 0; i < scratchI.Count; i++) indices.Add(scratchI[i] + baseVertex);
            }

            geo.Sources.Add(new Source
            {
                Component = component,
                Name = component.gameObject.name,
                TriangleStart = triStart,
                VertexStart = baseVertex,
                VertexCount = verts.Count - baseVertex,
                TriangleCount = indices.Count / 3 - triStart,
            });
        }

        /// <summary>Unique vertices referenced by the given triangle ranges.</summary>
        public NativeArray<float3> CollectPoints(int[] triangleRanges, Allocator allocator)
        {
            var used = new bool[Vertices.Length];
            var points = new List<float3>();
            foreach (int tri in TriangleRanges.Enumerate(triangleRanges))
            {
                if (tri < 0 || tri >= TriangleCount) continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = Indices[tri * 3 + k];
                    if (used[v]) continue;
                    used[v] = true;
                    points.Add(Vertices[v]);
                }
            }
            var result = new NativeArray<float3>(points.Count, allocator, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < points.Count; i++) result[i] = points[i];
            return result;
        }

        /// <summary>Points a part is fitted to: its explicit fit points (decomposition) or its triangles' vertices.</summary>
        public NativeArray<float3> PartPoints(SmartColliderPart part, Allocator allocator)
        {
            if (!part.HasFitPoints) return CollectPoints(part.triangleRanges, allocator);
            var result = new NativeArray<float3>(part.fitPoints.Length, allocator, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < result.Length; i++) result[i] = part.fitPoints[i];
            return result;
        }

        /// <summary>Index of the single source containing every triangle of the ranges, or -1 (empty / spans several).</summary>
        public int SourceIndexOf(int[] triangleRanges)
        {
            if (triangleRanges == null || triangleRanges.Length < 2) return -1;
            int found = -1;
            for (int r = 0; r + 1 < triangleRanges.Length; r += 2)
            {
                int start = triangleRanges[r], end = start + triangleRanges[r + 1];
                if (triangleRanges[r + 1] <= 0) continue;
                int owner = -1;
                for (int s = 0; s < Sources.Count; s++)
                {
                    var src = Sources[s];
                    if (start >= src.TriangleStart && end <= src.TriangleStart + src.TriangleCount) { owner = s; break; }
                }
                if (owner < 0 || (found >= 0 && owner != found)) return -1;
                found = owner;
            }
            return found;
        }

        public void Dispose()
        {
            if (Vertices.IsCreated) Vertices.Dispose();
            if (Indices.IsCreated) Indices.Dispose();
        }
    }
}
