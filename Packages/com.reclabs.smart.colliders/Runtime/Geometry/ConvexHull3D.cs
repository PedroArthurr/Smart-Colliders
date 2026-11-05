using Unity.Collections;
using Unity.Mathematics;

namespace RecLabs.SmartColliders.Geometry
{
    /// <summary>
    /// Quickhull 3D with conflict lists, Burst-compatible. Always expands the globally furthest
    /// point first, so a vertex cap yields a progressive (greedy farthest-point) inner hull.
    /// Output triangles are wound so cross(b-a, c-a) points outward (Unity front faces outward).
    /// </summary>
    public static class ConvexHull3D
    {
        /// <summary>A triangulated hull has F = 2V - 4 faces: 129 vertices → 254 tris, under PhysX's 255 limit.</summary>
        public const int MaxConvexColliderVertices = 129;

        struct Face
        {
            public int A, B, C;
            public float3 Normal;
            public float Offset;
            public int OutsideHead;
            public int Furthest;
            public float FurthestDist;
            public int Stamp;
            public bool Visible;
            public bool Alive;
        }

        static long Key(int a, int b) => ((long)a << 32) | (uint)b;

        struct Builder
        {
            public NativeArray<float3> P;
            public NativeList<Face> Faces;
            public NativeHashMap<long, int> Edges;
            public NativeArray<int> Next;

            public float Dist(in Face f, float3 p) => math.dot(f.Normal, p) - f.Offset;

            public int AddFace(int a, int b, int c, float3 fallbackNormal)
            {
                float3 pa = P[a];
                float3 n = math.cross(P[b] - pa, P[c] - pa);
                float len = math.length(n);
                n = len > 1e-20f ? n / len : fallbackNormal;

                int index = Faces.Length;
                Faces.Add(new Face
                {
                    A = a, B = b, C = c,
                    Normal = n,
                    Offset = math.dot(n, pa),
                    OutsideHead = -1,
                    Furthest = -1,
                    FurthestDist = 0f,
                    Alive = true,
                });
                Edges[Key(a, b)] = index;
                Edges[Key(b, c)] = index;
                Edges[Key(c, a)] = index;
                return index;
            }

            public void AddOriented(int a, int b, int c, float3 interior)
            {
                float3 n = math.cross(P[b] - P[a], P[c] - P[a]);
                if (math.dot(n, interior - P[a]) > 0f)
                {
                    int t = b;
                    b = c;
                    c = t;
                }
                AddFace(a, b, c, float3.zero);
            }

            public void Assign(int point, int face, float dist)
            {
                ref Face f = ref Faces.ElementAt(face);
                Next[point] = f.OutsideHead;
                f.OutsideHead = point;
                if (dist > f.FurthestDist)
                {
                    f.FurthestDist = dist;
                    f.Furthest = point;
                }
            }
        }

        /// <summary>
        /// Builds the convex hull of <paramref name="points"/>.
        /// </summary>
        /// <param name="maxVertices">0 = unlimited. Otherwise stops after reaching this many vertices (inner approximation).</param>
        /// <returns>False if the input is degenerate (fewer than 4 points, collinear or coplanar).</returns>
        public static bool Build(NativeArray<float3> points, int maxVertices,
            NativeList<float3> outVertices, NativeList<int> outTriangles)
        {
            outVertices.Clear();
            outTriangles.Clear();
            int n = points.Length;
            if (n < 4) return false;
            float eps = GeometryUtils.ComputeEpsilon(points);

            if (!FindInitialSimplex(points, eps, out int i0, out int i1, out int i2, out int i3))
                return false;

            var b = new Builder
            {
                P = points,
                Faces = new NativeList<Face>(64, Allocator.Temp),
                Edges = new NativeHashMap<long, int>(256, Allocator.Temp),
                Next = new NativeArray<int>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory),
            };

            float3 interior = (points[i0] + points[i1] + points[i2] + points[i3]) * 0.25f;
            b.AddOriented(i0, i1, i2, interior);
            b.AddOriented(i0, i1, i3, interior);
            b.AddOriented(i0, i2, i3, interior);
            b.AddOriented(i1, i2, i3, interior);

            for (int i = 0; i < n; i++)
            {
                if (i == i0 || i == i1 || i == i2 || i == i3) continue;
                int best = -1;
                float bestD = eps;
                for (int f = 0; f < 4; f++)
                {
                    float d = b.Dist(b.Faces[f], points[i]);
                    if (d > bestD) { bestD = d; best = f; }
                }
                if (best >= 0) b.Assign(i, best, bestD);
            }

            var stack = new NativeList<int>(32, Allocator.Temp);
            var visible = new NativeList<int>(32, Allocator.Temp);
            var horizon = new NativeList<int2>(32, Allocator.Temp);
            var orphans = new NativeList<int>(64, Allocator.Temp);
            var newFaces = new NativeList<int>(32, Allocator.Temp);

            int vertexCount = 4;
            int stamp = 0;
            int guard = n * 4 + 16;

            while (guard-- > 0)
            {
                if (maxVertices > 0 && vertexCount >= maxVertices) break;

                // Globally furthest conflict point → progressive refinement.
                int fi = -1;
                float bestDist = 0f;
                for (int f = 0; f < b.Faces.Length; f++)
                {
                    Face face = b.Faces[f];
                    if (face.Alive && face.Furthest >= 0 && face.FurthestDist > bestDist)
                    {
                        bestDist = face.FurthestDist;
                        fi = f;
                    }
                }
                if (fi < 0) break;

                int pi = b.Faces[fi].Furthest;
                float3 p = points[pi];
                stamp++;

                stack.Clear(); visible.Clear(); horizon.Clear(); orphans.Clear(); newFaces.Clear();

                {
                    ref Face f0 = ref b.Faces.ElementAt(fi);
                    f0.Stamp = stamp;
                    f0.Visible = true;
                }
                stack.Add(fi);

                while (stack.Length > 0)
                {
                    int f = stack[stack.Length - 1];
                    stack.RemoveAt(stack.Length - 1);
                    visible.Add(f);
                    Face face = b.Faces[f];

                    for (int e = 0; e < 3; e++)
                    {
                        int ea = e == 0 ? face.A : e == 1 ? face.B : face.C;
                        int eb = e == 0 ? face.B : e == 1 ? face.C : face.A;
                        if (!b.Edges.TryGetValue(Key(eb, ea), out int nb))
                        {
                            horizon.Add(new int2(ea, eb));
                            continue;
                        }
                        ref Face nf = ref b.Faces.ElementAt(nb);
                        if (nf.Stamp != stamp)
                        {
                            nf.Stamp = stamp;
                            nf.Visible = b.Dist(nf, p) > eps;
                            if (nf.Visible) stack.Add(nb);
                        }
                        if (!nf.Visible) horizon.Add(new int2(ea, eb));
                    }
                }

                float3 fallbackNormal = b.Faces[fi].Normal;

                for (int v = 0; v < visible.Length; v++)
                {
                    ref Face face = ref b.Faces.ElementAt(visible[v]);
                    for (int q = face.OutsideHead; q >= 0; q = b.Next[q])
                        if (q != pi) orphans.Add(q);
                    b.Edges.Remove(Key(face.A, face.B));
                    b.Edges.Remove(Key(face.B, face.C));
                    b.Edges.Remove(Key(face.C, face.A));
                    face.Alive = false;
                    face.OutsideHead = -1;
                    face.Furthest = -1;
                }

                for (int h = 0; h < horizon.Length; h++)
                    newFaces.Add(b.AddFace(horizon[h].x, horizon[h].y, pi, fallbackNormal));

                for (int o = 0; o < orphans.Length; o++)
                {
                    int q = orphans[o];
                    int best = -1;
                    float bestD = eps;
                    for (int k = 0; k < newFaces.Length; k++)
                    {
                        float d = b.Dist(b.Faces[newFaces[k]], points[q]);
                        if (d > bestD) { bestD = d; best = newFaces[k]; }
                    }
                    if (best >= 0) b.Assign(q, best, bestD);
                }

                vertexCount++;
            }

            var remap = new NativeArray<int>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < n; i++) remap[i] = -1;

            for (int f = 0; f < b.Faces.Length; f++)
            {
                Face face = b.Faces[f];
                if (!face.Alive) continue;
                outTriangles.Add(Remap(face.A, points, remap, outVertices));
                outTriangles.Add(Remap(face.B, points, remap, outVertices));
                outTriangles.Add(Remap(face.C, points, remap, outVertices));
            }

            b.Faces.Dispose(); b.Edges.Dispose(); b.Next.Dispose();
            stack.Dispose(); visible.Dispose(); horizon.Dispose(); orphans.Dispose(); newFaces.Dispose();
            remap.Dispose();
            return outTriangles.Length >= 12;
        }

        static int Remap(int index, NativeArray<float3> points, NativeArray<int> remap, NativeList<float3> outVertices)
        {
            if (remap[index] < 0)
            {
                remap[index] = outVertices.Length;
                outVertices.Add(points[index]);
            }
            return remap[index];
        }

        static bool FindInitialSimplex(NativeArray<float3> p, float eps, out int i0, out int i1, out int i2, out int i3)
        {
            i0 = i1 = i2 = i3 = -1;
            int n = p.Length;

            // Axis extremes → farthest pair.
            var ext = new NativeArray<int>(6, Allocator.Temp);
            for (int i = 1; i < n; i++)
            {
                float3 v = p[i];
                if (v.x < p[ext[0]].x) ext[0] = i;
                if (v.x > p[ext[1]].x) ext[1] = i;
                if (v.y < p[ext[2]].y) ext[2] = i;
                if (v.y > p[ext[3]].y) ext[3] = i;
                if (v.z < p[ext[4]].z) ext[4] = i;
                if (v.z > p[ext[5]].z) ext[5] = i;
            }
            float best = -1f;
            for (int a = 0; a < 6; a++)
            for (int c = a + 1; c < 6; c++)
            {
                float d = math.distancesq(p[ext[a]], p[ext[c]]);
                if (d > best) { best = d; i0 = ext[a]; i1 = ext[c]; }
            }
            ext.Dispose();
            if (best <= eps * eps) return false;

            float3 a0 = p[i0];
            float3 dir = math.normalize(p[i1] - a0);
            best = eps;
            for (int i = 0; i < n; i++)
            {
                float d = math.length(math.cross(p[i] - a0, dir));
                if (d > best) { best = d; i2 = i; }
            }
            if (i2 < 0) return false;

            float3 nrm = math.normalize(math.cross(p[i1] - a0, p[i2] - a0));
            best = eps;
            for (int i = 0; i < n; i++)
            {
                float d = math.abs(math.dot(nrm, p[i] - a0));
                if (d > best) { best = d; i3 = i; }
            }
            return i3 >= 0;
        }

        /// <summary>Signed volume of a closed, outward-wound triangle mesh.</summary>
        public static float Volume(NativeArray<float3> vertices, NativeArray<int> triangles)
        {
            if (vertices.Length == 0) return 0f;
            float3 o = GeometryUtils.Centroid(vertices);
            float v = 0f;
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                float3 a = vertices[triangles[t]] - o;
                float3 b = vertices[triangles[t + 1]] - o;
                float3 c = vertices[triangles[t + 2]] - o;
                v += math.dot(a, math.cross(b, c));
            }
            return v / 6f;
        }

        /// <summary>Outward planes (xyz = unit normal, w = offset) of a hull's triangles.</summary>
        public static void ComputePlanes(NativeArray<float3> vertices, NativeArray<int> triangles, NativeList<float4> planes)
        {
            planes.Clear();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                float3 a = vertices[triangles[t]];
                float3 nrm = math.cross(vertices[triangles[t + 1]] - a, vertices[triangles[t + 2]] - a);
                float len = math.length(nrm);
                if (len < 1e-20f) continue;
                nrm /= len;
                planes.Add(new float4(nrm, math.dot(nrm, a)));
            }
        }

        /// <summary>
        /// Signed distance to a convex hull given its planes. Exact inside (negative);
        /// outside it is a lower bound of the true distance (exact when the closest feature is a face).
        /// </summary>
        public static float SignedDistance(NativeArray<float4> planes, float3 p)
        {
            float d = float.MinValue;
            for (int i = 0; i < planes.Length; i++)
                d = math.max(d, math.dot(planes[i].xyz, p) - planes[i].w);
            return d;
        }

        /// <summary>Ray vs convex polytope (plane clipping). Origin inside ⇒ t = 0.</summary>
        public static bool Raycast(NativeArray<float4> planes, float3 origin, float3 direction, out float t)
        {
            t = 0f;
            if (planes.Length == 0) return false;
            float tEnter = float.MinValue, tExit = float.MaxValue;
            for (int i = 0; i < planes.Length; i++)
            {
                float3 n = planes[i].xyz;
                float denom = math.dot(n, direction);
                float dist = math.dot(n, origin) - planes[i].w;
                if (math.abs(denom) < 1e-12f)
                {
                    if (dist > 0f) return false;
                    continue;
                }
                float ti = -dist / denom;
                if (denom < 0f) tEnter = math.max(tEnter, ti);
                else tExit = math.min(tExit, ti);
                if (tEnter > tExit) return false;
            }
            if (tExit < 0f) return false;
            t = math.max(tEnter, 0f);
            return true;
        }
    }
}
