using Unity.Collections;
using Unity.Mathematics;

namespace RecLabs.SmartColliders.Geometry
{
    /// <summary>Small Burst-compatible math helpers shared by the fitters.</summary>
    public static class GeometryUtils
    {
        /// <summary>Scale-relative tolerance for plane/side tests (quickhull style).</summary>
        public static float ComputeEpsilon(NativeArray<float3> points)
        {
            float3 m = float3.zero;
            for (int i = 0; i < points.Length; i++) m = math.max(m, math.abs(points[i]));
            return math.max((m.x + m.y + m.z) * 1e-6f, 1e-7f);
        }

        /// <summary>Branchless orthonormal basis around unit vector n (Duff et al. 2017).</summary>
        public static void OrthonormalBasis(float3 n, out float3 b1, out float3 b2)
        {
            float sign = n.z >= 0f ? 1f : -1f;
            float a = -1f / (sign + n.z);
            float b = n.x * n.y * a;
            b1 = new float3(1f + sign * n.x * n.x * a, sign * b, -sign * n.x);
            b2 = new float3(b, sign + n.y * n.y * a, -n.y);
        }

        public static float3 Centroid(NativeArray<float3> points)
        {
            float3 c = float3.zero;
            for (int i = 0; i < points.Length; i++) c += points[i];
            return points.Length > 0 ? c / points.Length : c;
        }

        /// <summary>Eigen-decomposition of the point covariance. Columns of result = axes sorted by descending variance.</summary>
        public static float3x3 PrincipalAxes(NativeArray<float3> points, out float3 mean)
        {
            mean = Centroid(points);
            float3x3 cov = float3x3.zero;
            for (int i = 0; i < points.Length; i++)
            {
                float3 d = points[i] - mean;
                cov.c0 += d * d.x;
                cov.c1 += d * d.y;
                cov.c2 += d * d.z;
            }
            SymmetricEigen(cov, out float3 values, out float3x3 vectors);

            // Sort descending by eigenvalue.
            for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2 - i; j++)
            {
                if (values[j] < values[j + 1])
                {
                    float tv = values[j]; values[j] = values[j + 1]; values[j + 1] = tv;
                    float3 tc = vectors[j]; vectors[j] = vectors[j + 1]; vectors[j + 1] = tc;
                }
            }
            vectors.c2 = math.cross(vectors.c0, vectors.c1); // force right-handed
            return vectors;
        }

        /// <summary>Cyclic Jacobi eigen solver for symmetric 3x3 (column-major: m[col][row]).</summary>
        public static void SymmetricEigen(float3x3 m, out float3 values, out float3x3 vectors)
        {
            float3x3 v = float3x3.identity;
            for (int sweep = 0; sweep < 24; sweep++)
            {
                float off = m[1][0] * m[1][0] + m[2][0] * m[2][0] + m[2][1] * m[2][1];
                if (off < 1e-14f) break;

                for (int pair = 0; pair < 3; pair++)
                {
                    int p = pair == 2 ? 1 : 0;
                    int q = pair == 0 ? 1 : 2;
                    float apq = m[q][p];
                    if (math.abs(apq) < 1e-14f) continue;

                    float theta = (m[q][q] - m[p][p]) / (2f * apq);
                    float t = math.sign(theta) / (math.abs(theta) + math.sqrt(theta * theta + 1f));
                    if (theta == 0f) t = 1f;
                    float c = 1f / math.sqrt(t * t + 1f);
                    float s = t * c;

                    for (int k = 0; k < 3; k++) // m = m * J
                    {
                        float mkp = m[p][k], mkq = m[q][k];
                        m[p][k] = c * mkp - s * mkq;
                        m[q][k] = s * mkp + c * mkq;
                    }
                    for (int k = 0; k < 3; k++) // m = J^T * m
                    {
                        float mpk = m[k][p], mqk = m[k][q];
                        m[k][p] = c * mpk - s * mqk;
                        m[k][q] = s * mpk + c * mqk;
                    }
                    for (int k = 0; k < 3; k++) // v = v * J
                    {
                        float vkp = v[p][k], vkq = v[q][k];
                        v[p][k] = c * vkp - s * vkq;
                        v[q][k] = s * vkp + c * vkq;
                    }
                }
            }
            values = new float3(m[0][0], m[1][1], m[2][2]);
            vectors = v;
        }

        /// <summary>
        /// Re-orders/flips box axes so the rotation is as close to identity as possible
        /// (keeps generated colliders readable: 0° rotations whenever the mesh allows).
        /// </summary>
        public static void CanonicalizeAxes(ref float3x3 axes, ref float3 halfExtents)
        {
            int ix = 0;
            float best = -1f;
            for (int i = 0; i < 3; i++)
            {
                float d = math.abs(axes[i].x);
                if (d > best) { best = d; ix = i; }
            }
            float3 x = axes[ix] * math.sign(axes[ix].x + 1e-30f);
            float ex = halfExtents[ix];

            int iy = -1;
            best = -1f;
            for (int i = 0; i < 3; i++)
            {
                if (i == ix) continue;
                float d = math.abs(axes[i].y);
                if (d > best) { best = d; iy = i; }
            }
            float3 y = axes[iy] * math.sign(axes[iy].y + 1e-30f);
            float ey = halfExtents[iy];
            int iz = 3 - ix - iy;

            axes = new float3x3(x, y, math.cross(x, y));
            halfExtents = new float3(ex, ey, halfExtents[iz]);
        }

        /// <summary>Rotation whose local X/Y/Z map to the given orthonormal columns.</summary>
        public static quaternion RotationFromAxes(float3x3 axes)
        {
            float3 x = math.normalize(axes.c0);
            float3 z = math.normalize(math.cross(x, axes.c1));
            float3 y = math.cross(z, x);
            return new quaternion(new float3x3(x, y, z));
        }
    }
}
