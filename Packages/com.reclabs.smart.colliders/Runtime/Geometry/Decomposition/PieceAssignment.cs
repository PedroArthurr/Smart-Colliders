using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace RecLabs.SmartColliders.Geometry.Decomposition
{
    /// <summary>
    /// Assigns each source triangle to the convex piece whose hull best contains its centroid
    /// (minimum signed distance to the hull planes). Used for engines that only return hulls.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct PieceAssignmentJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Vertices;
        [ReadOnly] public NativeArray<int> Indices;
        /// <summary>All hull planes back to back.</summary>
        [ReadOnly] public NativeArray<float4> Planes;
        /// <summary>Per piece: (first plane, plane count).</summary>
        [ReadOnly] public NativeArray<int2> PieceRanges;
        public NativeArray<int> TrianglePiece;

        public void Execute()
        {
            int triCount = Indices.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                float3 c = (Vertices[Indices[t * 3]] + Vertices[Indices[t * 3 + 1]] + Vertices[Indices[t * 3 + 2]]) / 3f;
                int best = 0;
                float bestD = float.MaxValue;
                for (int p = 0; p < PieceRanges.Length; p++)
                {
                    int2 r = PieceRanges[p];
                    float d = float.MinValue;
                    for (int k = 0; k < r.y; k++)
                    {
                        float4 pl = Planes[r.x + k];
                        d = math.max(d, math.dot(pl.xyz, c) - pl.w);
                        if (d > bestD) break;
                    }
                    if (d < bestD)
                    {
                        bestD = d;
                        best = p;
                    }
                }
                TrianglePiece[t] = best;
            }
        }
    }
}
