using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>
    /// Unit meshes for primitive previews (scaled by matrix) and a frame-swept cache of flat-shaded hull meshes.
    /// Unit sizes: cube [-1,1]³, sphere r=1, hemisphere r=1 (y ≥ 0), open cylinder r=1 y∈[-1,1].
    /// </summary>
    internal static class PreviewMeshes
    {
        static Mesh s_Cube, s_Sphere, s_Hemisphere, s_Cylinder;

        public static Mesh Cube => s_Cube ? s_Cube : s_Cube = BuildCube();
        public static Mesh Sphere => s_Sphere ? s_Sphere : s_Sphere = BuildSphere(48, 24, false);
        public static Mesh Hemisphere => s_Hemisphere ? s_Hemisphere : s_Hemisphere = BuildSphere(48, 12, true);
        public static Mesh Cylinder => s_Cylinder ? s_Cylinder : s_Cylinder = BuildCylinder(48);

        // ---------- hull cache ----------

        sealed class HullEntry
        {
            public Mesh Mesh;
            public Vector3[] FeatureEdges; // segment pairs
            public int Hash;
            public int LastUsed;
        }

        static readonly Dictionary<SmartColliderPart, HullEntry> s_Hulls = new Dictionary<SmartColliderPart, HullEntry>();
        static int s_Frame;

        public static void BeginFrame()
        {
            s_Frame++;
            if (s_Frame % 120 != 0) return;
            var stale = new List<SmartColliderPart>();
            foreach (var kv in s_Hulls)
                if (s_Frame - kv.Value.LastUsed > 240) stale.Add(kv.Key);
            foreach (var part in stale)
            {
                Release(s_Hulls[part].Mesh);
                s_Hulls.Remove(part);
            }
        }

        public static Mesh Hull(SmartColliderPart part, out Vector3[] featureEdges)
        {
            int hash = HashHull(part.HullVertices, part.HullTriangles);
            if (!s_Hulls.TryGetValue(part, out var e))
                s_Hulls[part] = e = new HullEntry { Mesh = NewMesh("SC Hull Preview"), Hash = hash + 1 };

            if (e.Hash != hash)
            {
                e.Hash = hash;
                FillFlatShaded(e.Mesh, part.HullVertices, part.HullTriangles);
                e.FeatureEdges = FeatureEdges(part.HullVertices, part.HullTriangles);
            }
            e.LastUsed = s_Frame;
            featureEdges = e.FeatureEdges;
            return e.Mesh;
        }

        static int HashHull(Vector3[] v, int[] t)
        {
            unchecked
            {
                int h = v.Length * 397 ^ t.Length;
                for (int i = 0; i < v.Length; i++) h = h * 31 + v[i].GetHashCode();
                return h;
            }
        }

        static void FillFlatShaded(Mesh mesh, Vector3[] v, int[] t)
        {
            var verts = new Vector3[t.Length];
            var normals = new Vector3[t.Length];
            var idx = new int[t.Length];
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                Vector3 n = Vector3.Cross(b - a, c - a).normalized;
                verts[i] = a; verts[i + 1] = b; verts[i + 2] = c;
                normals[i] = normals[i + 1] = normals[i + 2] = n;
                idx[i] = i; idx[i + 1] = i + 1; idx[i + 2] = i + 2;
            }
            mesh.Clear();
            mesh.indexFormat = verts.Length > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.triangles = idx;
            mesh.RecalculateBounds();
        }

        /// <summary>Edges between non-coplanar faces (hides triangulation of flat hull facets).</summary>
        static Vector3[] FeatureEdges(Vector3[] v, int[] t)
        {
            var faceOfEdge = new Dictionary<long, int>();
            var segments = new List<Vector3>();
            int triCount = t.Length / 3;
            var normals = new Vector3[triCount];
            for (int f = 0; f < triCount; f++)
                normals[f] = Vector3.Cross(v[t[f * 3 + 1]] - v[t[f * 3]], v[t[f * 3 + 2]] - v[t[f * 3]]).normalized;

            for (int f = 0; f < triCount; f++)
            for (int e = 0; e < 3; e++)
            {
                int a = t[f * 3 + e], b = t[f * 3 + (e + 1) % 3];
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (faceOfEdge.TryGetValue(key, out int other))
                {
                    if (Vector3.Dot(normals[f], normals[other]) < 0.9995f)
                    {
                        segments.Add(v[a]);
                        segments.Add(v[b]);
                    }
                    faceOfEdge.Remove(key);
                }
                else faceOfEdge[key] = f;
            }
            foreach (var key in faceOfEdge.Keys) // open edges (should not happen on closed hulls)
            {
                segments.Add(v[(int)(key >> 32)]);
                segments.Add(v[(int)(key & 0xffffffff)]);
            }
            return segments.ToArray();
        }

        // ---------- unit meshes ----------

        static Mesh NewMesh(string name) => new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };

        static Mesh BuildCube()
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var tris = new List<int>();
            for (int axis = 0; axis < 3; axis++)
            for (int sign = -1; sign <= 1; sign += 2)
            {
                Vector3 n = Vector3.zero;
                n[axis] = sign;
                Vector3 u = Vector3.zero, w = Vector3.zero;
                u[(axis + 1) % 3] = 1;
                w[(axis + 2) % 3] = 1;
                if (sign < 0) (u, w) = (w, u); // keep outward winding
                int start = verts.Count;
                verts.Add(n - u - w); verts.Add(n - u + w); verts.Add(n + u + w); verts.Add(n + u - w);
                for (int k = 0; k < 4; k++) normals.Add(n);
                tris.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            }
            var m = NewMesh("SC Unit Cube");
            m.SetVertices(verts);
            m.SetNormals(normals);
            m.SetTriangles(tris, 0);
            FixWindingOutward(m);
            return m;
        }

        static Mesh BuildSphere(int lon, int lat, bool hemisphere)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            float maxTheta = hemisphere ? Mathf.PI * 0.5f : Mathf.PI;
            for (int y = 0; y <= lat; y++)
            {
                float theta = maxTheta * y / lat; // from +Y pole down
                for (int x = 0; x <= lon; x++)
                {
                    float phi = 2f * Mathf.PI * x / lon;
                    verts.Add(new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi)));
                }
            }
            for (int y = 0; y < lat; y++)
            for (int x = 0; x < lon; x++)
            {
                int a = y * (lon + 1) + x, b = a + lon + 1;
                tris.AddRange(new[] { a, a + 1, b, a + 1, b + 1, b });
            }
            var m = NewMesh(hemisphere ? "SC Unit Hemisphere" : "SC Unit Sphere");
            m.SetVertices(verts);
            m.SetNormals(verts); // unit sphere: normal == position
            m.SetTriangles(tris, 0);
            FixWindingOutward(m);
            return m;
        }

        static Mesh BuildCylinder(int segments)
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var tris = new List<int>();
            for (int i = 0; i <= segments; i++)
            {
                float phi = 2f * Mathf.PI * i / segments;
                var n = new Vector3(Mathf.Cos(phi), 0, Mathf.Sin(phi));
                verts.Add(n + Vector3.up); normals.Add(n);
                verts.Add(n + Vector3.down); normals.Add(n);
            }
            for (int i = 0; i < segments; i++)
            {
                int a = i * 2;
                tris.AddRange(new[] { a, a + 2, a + 1, a + 1, a + 2, a + 3 });
            }
            var m = NewMesh("SC Unit Cylinder");
            m.SetVertices(verts);
            m.SetNormals(normals);
            m.SetTriangles(tris, 0);
            FixWindingOutward(m);
            return m;
        }

        /// <summary>Flips any triangle whose geometric normal disagrees with its vertex normal (Unity: clockwise = front).</summary>
        static void FixWindingOutward(Mesh m)
        {
            var v = m.vertices;
            var n = m.normals;
            var t = m.triangles;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 g = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                Vector3 avg = n[t[i]] + n[t[i + 1]] + n[t[i + 2]];
                if (Vector3.Dot(g, avg) < 0f) (t[i + 1], t[i + 2]) = (t[i + 2], t[i + 1]);
            }
            m.triangles = t;
            m.RecalculateBounds();
        }

        [InitializeOnLoadMethod]
        static void RegisterCleanup()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                foreach (var e in s_Hulls.Values) Release(e.Mesh);
                s_Hulls.Clear();
                Release(s_Cube);
                Release(s_Sphere);
                Release(s_Hemisphere);
                Release(s_Cylinder);
            };
        }

        static void Release(Object o)
        {
            if (o != null) Object.DestroyImmediate(o);
        }
    }
}
