using RecLabs.SmartColliders.Geometry;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>Ray picking against the analytic part shapes (no physics, no baked colliders needed).</summary>
    internal static class PartPicker
    {
        /// <returns>Index of the nearest hit part, or -1.</returns>
        public static int Pick(SmartColliderAuthoring a, Ray worldRay)
        {
            Matrix4x4 toLocal = a.transform.worldToLocalMatrix;
            float3 o0 = toLocal.MultiplyPoint3x4(worldRay.origin);
            float3 d0 = toLocal.MultiplyVector(worldRay.direction); // same t across parts

            int best = -1;
            float bestT = float.MaxValue;
            var planes = new NativeList<float4>(64, Allocator.Temp);
            for (int i = 0; i < a.Parts.Count; i++)
            {
                var part = a.Parts[i];
                if (!part.HasFit) continue;
                bool hit;
                float t;
                float3 o = o0, d = d0;
                if (part.Bone != null)
                {
                    Matrix4x4 inv = part.PoseDelta(a.transform).inverse;
                    o = inv.MultiplyPoint3x4(o0);
                    d = inv.MultiplyVector(d0);
                }
                if (part.Type == ShapeType.ConvexHull)
                {
                    var v = new NativeArray<Vector3>(part.HullVertices, Allocator.Temp).Reinterpret<float3>();
                    var tri = new NativeArray<int>(part.HullTriangles, Allocator.Temp);
                    ConvexHull3D.ComputePlanes(v, tri, planes);
                    hit = ConvexHull3D.Raycast(planes.AsArray(), o, d, out t);
                    v.Dispose();
                    tri.Dispose();
                }
                else
                {
                    hit = part.Shape.Raycast(o, d, out t);
                }
                if (hit && t < bestT)
                {
                    bestT = t;
                    best = i;
                }
            }
            planes.Dispose();
            return best;
        }
    }
}
