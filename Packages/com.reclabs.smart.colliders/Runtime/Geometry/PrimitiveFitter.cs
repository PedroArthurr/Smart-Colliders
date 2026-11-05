using Unity.Collections;
using Unity.Mathematics;

namespace RecLabs.SmartColliders.Geometry
{
    /// <summary>Tight enclosing primitives. Feed hull vertices (not raw mesh vertices) for speed — result is identical.</summary>
    public static class PrimitiveFitter
    {
        public static ColliderShape FitSphere(NativeArray<float3> points)
        {
            MinEnclosing.Sphere(points, out float3 c, out float r);
            return ColliderShape.Sphere(c, r);
        }

        /// <summary>
        /// Near-minimal-volume OBB. Candidates: world axes, PCA, and every hull face normal combined with
        /// the minimum-area rectangle of the projection (exact for most real meshes, which have a box face
        /// flush with a hull face). World-aligned result is preferred when within 0.5% of the best volume.
        /// </summary>
        /// <param name="points">Points to enclose (full hull vertices).</param>
        /// <param name="normalSourceVertices">Hull used to generate candidate normals (may be a reduced hull).</param>
        public static ColliderShape FitBox(NativeArray<float3> points,
            NativeArray<float3> normalSourceVertices, NativeArray<int> normalSourceTriangles, float minThickness = 0f)
        {
            float bestVol = float.MaxValue;
            float3x3 bestAxes = float3x3.identity;
            float3 bestCenter = float3.zero, bestHe = float3.zero;

            // Candidate: world axes.
            EvaluateAxes(points, float3x3.identity, minThickness, ref bestVol, ref bestAxes, ref bestCenter, ref bestHe);
            float aabbVol = bestVol;
            float3 aabbCenter = bestCenter, aabbHe = bestHe;

            // Candidate: PCA.
            EvaluateAxes(points, GeometryUtils.PrincipalAxes(points, out _), minThickness,
                ref bestVol, ref bestAxes, ref bestCenter, ref bestHe);

            // Candidates: unique hull face normals + 2D min-area rectangle.
            var normals = new NativeList<float3>(64, Allocator.Temp);
            for (int t = 0; t + 2 < normalSourceTriangles.Length; t += 3)
            {
                float3 a = normalSourceVertices[normalSourceTriangles[t]];
                float3 n = math.cross(normalSourceVertices[normalSourceTriangles[t + 1]] - a,
                    normalSourceVertices[normalSourceTriangles[t + 2]] - a);
                float len = math.length(n);
                if (len < 1e-20f) continue;
                n /= len;
                bool dup = false;
                for (int k = 0; k < normals.Length && !dup; k++)
                    dup = math.abs(math.dot(normals[k], n)) > 0.9995f;
                if (!dup) normals.Add(n);
            }

            var proj = new NativeArray<float2>(points.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var hull2 = new NativeList<float2>(64, Allocator.Temp);
            for (int k = 0; k < normals.Length; k++)
            {
                float3 n = normals[k];
                GeometryUtils.OrthonormalBasis(n, out float3 u, out float3 v);
                for (int i = 0; i < points.Length; i++)
                    proj[i] = new float2(math.dot(points[i], u), math.dot(points[i], v));
                MinEnclosing.ConvexHull2D(proj, hull2);
                MinEnclosing.MinAreaRect(hull2.AsArray(), out _, out float2 axis2, out _);
                float3 a1 = math.normalize(u * axis2.x + v * axis2.y);
                float3 a2 = math.cross(n, a1);
                EvaluateAxes(points, new float3x3(a1, a2, n), minThickness, ref bestVol, ref bestAxes, ref bestCenter, ref bestHe);
            }
            normals.Dispose();
            proj.Dispose();
            hull2.Dispose();

            if (aabbVol <= bestVol * 1.005f)
                return ColliderShape.Box(aabbCenter, quaternion.identity, aabbHe);

            GeometryUtils.CanonicalizeAxes(ref bestAxes, ref bestHe);
            return ColliderShape.Box(bestCenter, GeometryUtils.RotationFromAxes(bestAxes), bestHe);
        }

        static void EvaluateAxes(NativeArray<float3> points, float3x3 axes, float minThickness,
            ref float bestVol, ref float3x3 bestAxes, ref float3 bestCenter, ref float3 bestHe)
        {
            float3 min = new float3(float.MaxValue), max = new float3(float.MinValue);
            for (int i = 0; i < points.Length; i++)
            {
                float3 p = points[i];
                float3 q = new float3(math.dot(p, axes.c0), math.dot(p, axes.c1), math.dot(p, axes.c2));
                min = math.min(min, q);
                max = math.max(max, q);
            }
            float3 he = math.max((max - min) * 0.5f, minThickness * 0.5f);
            float vol = he.x * he.y * he.z;
            if (vol >= bestVol) return;
            float3 mid = (min + max) * 0.5f;
            bestVol = vol;
            bestAxes = axes;
            bestHe = he;
            bestCenter = axes.c0 * mid.x + axes.c1 * mid.y + axes.c2 * mid.z;
        }

        /// <summary>Tight box with a fixed orientation (used by "Fit to Mesh" after manual rotation).</summary>
        public static ColliderShape FitBoxOriented(NativeArray<float3> points, quaternion rotation, float minThickness = 0f)
        {
            float bestVol = float.MaxValue;
            float3x3 axes = new float3x3(rotation), outAxes = axes;
            float3 center = float3.zero, he = float3.zero;
            EvaluateAxes(points, axes, minThickness, ref bestVol, ref outAxes, ref center, ref he);
            return ColliderShape.Box(center, rotation, he);
        }

        /// <summary>
        /// Minimal enclosing capsule over candidate axes (box axes + PCA major axis).
        /// Per axis: radius = min enclosing circle of the projection, caps pushed out just enough
        /// for every point to fall inside the hemispheres.
        /// </summary>
        public static ColliderShape FitCapsule(NativeArray<float3> points, in ColliderShape box)
        {
            var axes = new NativeList<float3>(4, Allocator.Temp);
            float3x3 boxAxes = new float3x3(box.Rotation);
            axes.Add(boxAxes.c0);
            axes.Add(boxAxes.c1);
            axes.Add(boxAxes.c2);
            float3 pca = GeometryUtils.PrincipalAxes(points, out _).c0;
            if (math.lengthsq(pca) > 0.5f) axes.Add(math.normalize(pca));

            ColliderShape best = default;
            float bestVol = float.MaxValue;
            for (int a = 0; a < axes.Length; a++)
            {
                float3 d = axes[a];
                GeometryUtils.OrthonormalBasis(d, out float3 u, out _);
                var shape = FitCapsuleOriented(points, GeometryUtils.RotationFromAxes(new float3x3(u, d, math.cross(u, d))));
                float vol = shape.Volume();
                if (vol < bestVol)
                {
                    bestVol = vol;
                    best = shape;
                }
            }
            axes.Dispose();
            return best;
        }

        /// <summary>Tightest capsule whose axis is the rotation's local Y (rotation preserved exactly).</summary>
        public static ColliderShape FitCapsuleOriented(NativeArray<float3> points, quaternion rotation)
        {
            float3x3 m = new float3x3(rotation);
            float3 u = m.c0, d = m.c1, v = m.c2;

            var proj = new NativeArray<float2>(points.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < points.Length; i++)
                proj[i] = new float2(math.dot(points[i], u), math.dot(points[i], v));
            MinEnclosing.Circle(proj, out float2 c2, out float r);
            r = math.max(r, 1e-6f);

            float tTop = float.MinValue, tBot = float.MaxValue;
            float r2 = r * r;
            for (int i = 0; i < points.Length; i++)
            {
                float t = math.dot(points[i], d);
                float s = math.sqrt(math.max(r2 - math.distancesq(proj[i], c2), 0f));
                tTop = math.max(tTop, t - s);
                tBot = math.min(tBot, t + s);
            }
            proj.Dispose();

            float halfHeight = math.max((tTop - tBot) * 0.5f, 0f);
            float tMid = (tTop + tBot) * 0.5f;
            return ColliderShape.Capsule(u * c2.x + v * c2.y + d * tMid, rotation, r, halfHeight);
        }
    }
}
