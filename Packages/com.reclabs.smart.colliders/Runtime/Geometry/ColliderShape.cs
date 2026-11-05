using System;
using Unity.Collections;
using Unity.Mathematics;

namespace RecLabs.SmartColliders.Geometry
{
    /// <summary>Order = ascending physics cost.</summary>
    public enum ShapeType : byte
    {
        Sphere = 0,
        Capsule = 1,
        Box = 2,
        ConvexHull = 3,
    }

    /// <summary>
    /// Engine-agnostic collider description in the space of the input points.
    /// Capsule axis = local Y; <see cref="HalfHeight"/> is half the inner segment (Unity height = 2 * (HalfHeight + Radius)).
    /// ConvexHull geometry lives in separate vertex/triangle buffers; Center = hull centroid.
    /// </summary>
    [Serializable]
    public struct ColliderShape
    {
        public ShapeType Type;
        public float3 Center;
        public quaternion Rotation;
        public float3 HalfExtents;
        public float Radius;
        public float HalfHeight;

        public static ColliderShape Box(float3 center, quaternion rotation, float3 halfExtents) => new ColliderShape
            { Type = ShapeType.Box, Center = center, Rotation = rotation, HalfExtents = halfExtents };

        public static ColliderShape Sphere(float3 center, float radius) => new ColliderShape
            { Type = ShapeType.Sphere, Center = center, Rotation = quaternion.identity, Radius = radius };

        public static ColliderShape Capsule(float3 center, quaternion rotation, float radius, float halfHeight) => new ColliderShape
            { Type = ShapeType.Capsule, Center = center, Rotation = rotation, Radius = radius, HalfHeight = halfHeight };

        public float3 ToLocal(float3 p) => math.rotate(math.conjugate(Rotation), p - Center);
        public float3 ToWorld(float3 p) => Center + math.rotate(Rotation, p);

        /// <summary>Analytic volume. ConvexHull returns 0 (use <see cref="ConvexHull3D.Volume"/>).</summary>
        public float Volume()
        {
            switch (Type)
            {
                case ShapeType.Box: return 8f * HalfExtents.x * HalfExtents.y * HalfExtents.z;
                case ShapeType.Sphere: return 4f / 3f * math.PI * Radius * Radius * Radius;
                case ShapeType.Capsule: return math.PI * Radius * Radius * (2f * HalfHeight + 4f / 3f * Radius);
                default: return 0f;
            }
        }

        /// <summary>Exact signed distance for primitives (negative inside). ConvexHull returns 0.</summary>
        public float SignedDistance(float3 p)
        {
            float3 q = ToLocal(p);
            switch (Type)
            {
                case ShapeType.Box:
                {
                    float3 d = math.abs(q) - HalfExtents;
                    return math.length(math.max(d, 0f)) + math.min(math.cmax(d), 0f);
                }
                case ShapeType.Sphere:
                    return math.length(q) - Radius;
                case ShapeType.Capsule:
                {
                    q.y -= math.clamp(q.y, -HalfHeight, HalfHeight);
                    return math.length(q) - Radius;
                }
                default:
                    return 0f;
            }
        }

        /// <summary>Ray hit for primitives (direction need not be normalized; t is in direction units). Origin inside ⇒ t = 0.</summary>
        public bool Raycast(float3 origin, float3 direction, out float t)
        {
            t = 0f;
            float3 o = ToLocal(origin);
            float3 d = math.rotate(math.conjugate(Rotation), direction);
            switch (Type)
            {
                case ShapeType.Box:
                {
                    float3 inv = 1f / d;
                    float3 t0 = (-HalfExtents - o) * inv, t1 = (HalfExtents - o) * inv;
                    float tNear = math.cmax(math.min(t0, t1)), tFar = math.cmin(math.max(t0, t1));
                    if (tNear > tFar || tFar < 0f) return false;
                    t = math.max(tNear, 0f);
                    return true;
                }
                case ShapeType.Sphere:
                    return RaySphere(o, d, float3.zero, Radius, out t);
                case ShapeType.Capsule:
                {
                    // Sphere tracing on the exact SDF.
                    float len = math.length(d);
                    if (len < 1e-12f) return false;
                    float3 dir = d / len;
                    float scale = Radius + HalfHeight;
                    float s = 0f;
                    for (int i = 0; i < 128; i++)
                    {
                        float3 q = o + dir * s;
                        q.y -= math.clamp(q.y, -HalfHeight, HalfHeight);
                        float dist = math.length(q) - Radius;
                        if (dist < scale * 1e-4f) { t = s / len; return true; }
                        s += dist;
                        if (s > 1e4f * scale + math.length(o)) break;
                    }
                    return false;
                }
                default:
                    return false;
            }
        }

        static bool RaySphere(float3 o, float3 d, float3 c, float r, out float t)
        {
            t = 0f;
            float3 oc = o - c;
            float a = math.dot(d, d);
            float b = math.dot(oc, d);
            float cc = math.dot(oc, oc) - r * r;
            if (cc <= 0f) return true; // inside
            float disc = b * b - a * cc;
            if (disc < 0f || b > 0f) return false;
            t = (-b - math.sqrt(disc)) / a;
            return true;
        }

        /// <summary>World-space AABB of the primitive (conservative for rotated capsules/boxes).</summary>
        public void Bounds(out float3 min, out float3 max)
        {
            float3x3 m = new float3x3(Rotation);
            float3x3 abs = new float3x3(math.abs(m.c0), math.abs(m.c1), math.abs(m.c2));
            float3 ext;
            switch (Type)
            {
                case ShapeType.Box: ext = math.mul(abs, HalfExtents); break;
                case ShapeType.Sphere: ext = new float3(Radius); break;
                case ShapeType.Capsule: ext = abs.c1 * HalfHeight + Radius; break;
                default: ext = float3.zero; break;
            }
            min = Center - ext;
            max = Center + ext;
        }

        /// <summary>Appends evenly distributed points on the primitive's surface (used for fit metrics).</summary>
        public void SampleSurface(NativeList<float3> output, int density = 8)
        {
            density = math.max(density, 2);
            switch (Type)
            {
                case ShapeType.Box:
                {
                    for (int axis = 0; axis < 3; axis++)
                    for (int side = -1; side <= 1; side += 2)
                    for (int i = 0; i < density; i++)
                    for (int j = 0; j < density; j++)
                    {
                        float u = math.lerp(-1f, 1f, i / (float)(density - 1));
                        float v = math.lerp(-1f, 1f, j / (float)(density - 1));
                        float3 l = axis == 0 ? new float3(side, u, v) : axis == 1 ? new float3(u, side, v) : new float3(u, v, side);
                        output.Add(ToWorld(l * HalfExtents));
                    }
                    break;
                }
                case ShapeType.Sphere:
                {
                    int count = density * density * 4;
                    for (int i = 0; i < count; i++)
                        output.Add(Center + Fibonacci(i, count) * Radius);
                    break;
                }
                case ShapeType.Capsule:
                {
                    int cap = density * density * 4;
                    for (int i = 0; i < cap; i++)
                    {
                        float3 d = Fibonacci(i, cap);
                        float3 l = d * Radius;
                        l.y += d.y >= 0f ? HalfHeight : -HalfHeight;
                        output.Add(ToWorld(l));
                    }
                    int ring = density * 4;
                    for (int s = 0; s < density; s++)
                    {
                        float y = math.lerp(-HalfHeight, HalfHeight, s / (float)(density - 1));
                        for (int k = 0; k < ring; k++)
                        {
                            math.sincos(2f * math.PI * k / ring, out float sn, out float cs);
                            output.Add(ToWorld(new float3(cs * Radius, y, sn * Radius)));
                        }
                    }
                    break;
                }
            }
        }

        static float3 Fibonacci(int i, int count)
        {
            float y = 1f - 2f * (i + 0.5f) / count;
            float r = math.sqrt(math.max(0f, 1f - y * y));
            float phi = i * 2.39996323f; // golden angle
            math.sincos(phi, out float s, out float c);
            return new float3(c * r, y, s * r);
        }
    }
}
