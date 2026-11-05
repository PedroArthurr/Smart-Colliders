using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace RecLabs.SmartColliders.Geometry
{
    /// <summary>Triangle-soup queries (brute force, Burst): ray hit, sphere gather, welded adjacency.</summary>
    public static class MeshQueries
    {
        /// <summary>Nearest ray/triangle hit (two-sided). Returns triangle index or -1.</summary>
        public static int Raycast(NativeArray<float3> vertices, NativeArray<int> indices, float3 origin, float3 dir, out float hitT)
        {
            hitT = float.MaxValue;
            int best = -1;
            int triCount = indices.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                float3 a = vertices[indices[t * 3]], b = vertices[indices[t * 3 + 1]], c = vertices[indices[t * 3 + 2]];
                float3 e1 = b - a, e2 = c - a;
                float3 p = math.cross(dir, e2);
                float det = math.dot(e1, p);
                if (math.abs(det) < 1e-12f) continue;
                float inv = 1f / det;
                float3 s = origin - a;
                float u = math.dot(s, p) * inv;
                if (u < 0f || u > 1f) continue;
                float3 q = math.cross(s, e1);
                float v = math.dot(dir, q) * inv;
                if (v < 0f || u + v > 1f) continue;
                float tt = math.dot(e2, q) * inv;
                if (tt > 0f && tt < hitT)
                {
                    hitT = tt;
                    best = t;
                }
            }
            return best;
        }

        /// <summary>Triangles with any vertex or the centroid inside the sphere.</summary>
        public static void Gather(NativeArray<float3> vertices, NativeArray<int> indices, float3 center, float radius, NativeList<int> result)
        {
            float r2 = radius * radius;
            int triCount = indices.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                float3 a = vertices[indices[t * 3]], b = vertices[indices[t * 3 + 1]], c = vertices[indices[t * 3 + 2]];
                if (math.distancesq(a, center) <= r2 || math.distancesq(b, center) <= r2 ||
                    math.distancesq(c, center) <= r2 || math.distancesq((a + b + c) / 3f, center) <= r2)
                    result.Add(t);
            }
        }

        /// <summary>
        /// Welds vertices by position (quantized to <paramref name="cell"/>) so UV/normal seams don't break connectivity.
        /// Output: weld id per vertex.
        /// </summary>
        public static void Weld(NativeArray<float3> vertices, float cell, NativeArray<int> weldIds)
        {
            var map = new NativeParallelHashMap<int3, int>(vertices.Length, Allocator.Temp);
            float inv = 1f / math.max(cell, 1e-9f);
            int next = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                int3 key = (int3)math.round(vertices[i] * inv);
                if (!map.TryGetValue(key, out int id))
                {
                    id = next++;
                    map.Add(key, id);
                }
                weldIds[i] = id;
            }
            map.Dispose();
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct MeshRaycastJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Vertices;
        [ReadOnly] public NativeArray<int> Indices;
        public float3 Origin;
        public float3 Direction;
        public NativeReference<int> Triangle;
        public NativeReference<float> Distance;

        public void Execute()
        {
            Triangle.Value = MeshQueries.Raycast(Vertices, Indices, Origin, Direction, out float t);
            Distance.Value = t;
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct MeshGatherJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Vertices;
        [ReadOnly] public NativeArray<int> Indices;
        public float3 Center;
        public float Radius;
        public NativeList<int> Result;

        public void Execute() => MeshQueries.Gather(Vertices, Indices, Center, Radius, Result);
    }
}
