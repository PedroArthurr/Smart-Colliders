using Unity.Collections;
using Unity.Mathematics;

namespace RecLabs.SmartColliders.Geometry
{
    public enum PhysicsCostTier : byte
    {
        Light,
        Medium,
        Heavy,
        HeavyPlus,
    }

    /// <summary>Quality of one collider against the reference geometry (the part's full convex hull).</summary>
    [System.Serializable]
    public struct FitReport
    {
        public bool Valid;
        public float Volume;
        /// <summary>Volume IoU in 0..1 (shapes are nested, so min(V)/max(V)).</summary>
        public float Accuracy;
        /// <summary>Max surface deviation (Hausdorff approximation) in input units.</summary>
        public float WorstFit;
        public float Cost;

        public static FitReport Invalid => new FitReport { Accuracy = -1f };
    }

    /// <summary>
    /// Heuristic physics cost. Relative narrow-phase price: sphere &lt; capsule &lt; box &lt; convex hull,
    /// hull cost grows with vertex count (GJK/SAT support-map work).
    /// </summary>
    public static class FitMetrics
    {
        public static float CostOf(ShapeType type, int hullVertexCount = 0)
        {
            switch (type)
            {
                case ShapeType.Sphere: return 1f;
                case ShapeType.Capsule: return 1.25f;
                case ShapeType.Box: return 1.5f;
                default: return 3f + hullVertexCount / 32f;
            }
        }

        public static PhysicsCostTier TierOf(float totalCost)
        {
            if (totalCost <= 24f) return PhysicsCostTier.Light;
            if (totalCost <= 72f) return PhysicsCostTier.Medium;
            if (totalCost <= 240f) return PhysicsCostTier.Heavy;
            return PhysicsCostTier.HeavyPlus;
        }

        /// <summary>Report for an enclosing primitive against the reference hull.</summary>
        public static FitReport EvaluatePrimitive(in ColliderShape shape, NativeArray<float3> refVertices,
            NativeArray<float4> refPlanes, float refVolume, NativeList<float3> scratch)
        {
            float vol = shape.Volume();

            // Over-coverage: collider surface far outside the hull.
            scratch.Clear();
            shape.SampleSurface(scratch);
            float worst = 0f;
            for (int i = 0; i < scratch.Length; i++)
                worst = math.max(worst, ConvexHull3D.SignedDistance(refPlanes, scratch[i]));
            // Under-coverage: hull vertices outside the collider (≈0 for enclosing fits).
            for (int i = 0; i < refVertices.Length; i++)
                worst = math.max(worst, shape.SignedDistance(refVertices[i]));

            return new FitReport
            {
                Valid = true,
                Volume = vol,
                Accuracy = NestedIoU(vol, refVolume),
                WorstFit = worst,
                Cost = CostOf(shape.Type),
            };
        }

        /// <summary>Report for a (possibly vertex-capped, hence inscribed) convex hull against the full hull.</summary>
        public static FitReport EvaluateHull(NativeArray<float3> hullVertices, NativeArray<int> hullTriangles,
            NativeArray<float3> refVertices, float refVolume)
        {
            float vol = ConvexHull3D.Volume(hullVertices, hullTriangles);
            var planes = new NativeList<float4>(hullTriangles.Length / 3, Allocator.Temp);
            ConvexHull3D.ComputePlanes(hullVertices, hullTriangles, planes);
            float worst = 0f;
            for (int i = 0; i < refVertices.Length; i++)
                worst = math.max(worst, ConvexHull3D.SignedDistance(planes.AsArray(), refVertices[i]));
            planes.Dispose();

            return new FitReport
            {
                Valid = true,
                Volume = vol,
                Accuracy = NestedIoU(vol, refVolume),
                WorstFit = worst,
                Cost = CostOf(ShapeType.ConvexHull, hullVertices.Length),
            };
        }

        static float NestedIoU(float a, float b)
        {
            float hi = math.max(a, b);
            return hi > 1e-12f ? math.saturate(math.min(a, b) / hi) : 1f;
        }

        /// <summary>
        /// Report for a manually edited collider, which may no longer enclose (or be enclosed by) the
        /// reference hull: IoU is estimated on a regular grid instead of the nested-volume shortcut.
        /// For <see cref="ShapeType.ConvexHull"/>, pass the edited hull buffers; <paramref name="shape"/> is ignored.
        /// </summary>
        public static FitReport EvaluateEdited(in ColliderShape shape,
            NativeArray<float3> hullVertices, NativeArray<int> hullTriangles,
            NativeArray<float3> refVertices, NativeArray<float4> refPlanes, float refVolume, int gridResolution = 32)
        {
            bool isHull = shape.Type == ShapeType.ConvexHull;
            var shapePlanes = new NativeList<float4>(64, Allocator.Temp);
            var samples = new NativeList<float3>(512, Allocator.Temp);
            float volume;

            if (isHull)
            {
                ConvexHull3D.ComputePlanes(hullVertices, hullTriangles, shapePlanes);
                volume = ConvexHull3D.Volume(hullVertices, hullTriangles);
                samples.AddRange(hullVertices);
            }
            else
            {
                volume = shape.Volume();
                shape.SampleSurface(samples);
            }
            // Two-sided deviation.
            float worst = 0f;
            for (int i = 0; i < samples.Length; i++)
                worst = math.max(worst, ConvexHull3D.SignedDistance(refPlanes, samples[i]));
            for (int i = 0; i < refVertices.Length; i++)
            {
                float d = isHull
                    ? ConvexHull3D.SignedDistance(shapePlanes.AsArray(), refVertices[i])
                    : shape.SignedDistance(refVertices[i]);
                worst = math.max(worst, d);
            }

            // IoU = I / (Vs + Vr − I) with exact volumes; only the intersection fraction is sampled,
            // on a grid aligned to the shape's own frame (stratified inside the shape).

            int res = math.max(gridResolution, 4);
            SamplingBounds(shape, hullVertices, out float3 lmin, out float3 lmax);
            float3 step = (lmax - lmin) / res;
            int inside = 0, both = 0;
            for (int x = 0; x < res; x++)
            for (int y = 0; y < res; y++)
            for (int z = 0; z < res; z++)
            {
                float3 l = lmin + (new float3(x, y, z) + 0.5f) * step;
                float3 p = isHull ? l : shape.ToWorld(l);
                bool inShape = isHull
                    ? ConvexHull3D.SignedDistance(shapePlanes.AsArray(), p) <= 0f
                    : shape.SignedDistance(p) <= 0f;
                if (!inShape) continue;
                inside++;
                if (ConvexHull3D.SignedDistance(refPlanes, p) <= 0f) both++;
            }
            float intersection = inside > 0 ? volume * both / inside : 0f;
            float unionVolume = volume + refVolume - intersection;

            shapePlanes.Dispose();
            samples.Dispose();

            return new FitReport
            {
                Valid = true,
                Volume = volume,
                Accuracy = unionVolume > 1e-12f ? math.saturate(intersection / unionVolume) : 0f,
                WorstFit = worst,
                Cost = CostOf(shape.Type, hullVertices.IsCreated ? hullVertices.Length : 0),
            };
        }

        /// <summary>Sampling domain: primitive's local AABB, or the hull's own world AABB.</summary>
        static void SamplingBounds(in ColliderShape shape, NativeArray<float3> hullVertices, out float3 min, out float3 max)
        {
            switch (shape.Type)
            {
                case ShapeType.Box:
                    max = shape.HalfExtents;
                    break;
                case ShapeType.Sphere:
                    max = new float3(shape.Radius);
                    break;
                case ShapeType.Capsule:
                    max = new float3(shape.Radius, shape.HalfHeight + shape.Radius, shape.Radius);
                    break;
                default:
                    min = new float3(float.MaxValue);
                    max = new float3(float.MinValue);
                    for (int i = 0; i < hullVertices.Length; i++)
                    {
                        min = math.min(min, hullVertices[i]);
                        max = math.max(max, hullVertices[i]);
                    }
                    return;
            }
            min = -max;
        }
    }
}
