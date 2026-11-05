using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace RecLabs.SmartColliders.Geometry
{
    [Serializable]
    public struct ShapeFitSettings
    {
        public bool AllowSphere;
        public bool AllowCapsule;
        public bool AllowBox;
        public bool AllowConvexHull;
        /// <summary>Vertex cap for the convex hull candidate (4..129, see <see cref="ConvexHull3D.MaxConvexColliderVertices"/>).</summary>
        public int MaxHullVertices;
        /// <summary>A candidate must reach this volume IoU (0..1) to be eligible.</summary>
        public float MinAccuracy;
        /// <summary>Accuracy traded per unit of physics cost when ranking eligible candidates.</summary>
        public float CostWeight;
        /// <summary>Minimum box thickness for flat inputs (quads, decals), in input units.</summary>
        public float MinThickness;

        public static ShapeFitSettings Default => new ShapeFitSettings
        {
            AllowSphere = true,
            AllowCapsule = true,
            AllowBox = true,
            AllowConvexHull = true,
            MaxHullVertices = 64,
            MinAccuracy = 0.8f,
            CostWeight = 0.02f,
            MinThickness = 0.01f,
        };
    }

    /// <summary>
    /// Fits every collider type to a point set, scores them, and picks the best:
    /// highest (Accuracy − CostWeight·Cost) among candidates with Accuracy ≥ MinAccuracy,
    /// falling back to the convex hull (or the most accurate allowed shape).
    /// </summary>
    public static class ShapeFitter
    {
        public const int CandidateCount = 4;

        /// <param name="candidates">Length <see cref="CandidateCount"/>, indexed by <see cref="ShapeType"/>.</param>
        /// <param name="reports">Length <see cref="CandidateCount"/>, indexed by <see cref="ShapeType"/>.</param>
        /// <returns>Selected <see cref="ShapeType"/> as int, or -1 if there are no points.</returns>
        public static int FitAll(NativeArray<float3> points, in ShapeFitSettings settings,
            NativeList<float3> hullVertices, NativeList<int> hullTriangles,
            NativeArray<ColliderShape> candidates, NativeArray<FitReport> reports)
        {
            hullVertices.Clear();
            hullTriangles.Clear();
            for (int i = 0; i < CandidateCount; i++)
            {
                candidates[i] = default;
                reports[i] = FitReport.Invalid;
            }
            if (points.Length == 0) return -1;

            var fullV = new NativeList<float3>(256, Allocator.Temp);
            var fullT = new NativeList<int>(512, Allocator.Temp);

            if (!ConvexHull3D.Build(points, 0, fullV, fullT))
            {
                // Flat/degenerate input → thin box (PCA candidate aligns with the plane).
                var box = PrimitiveFitter.FitBox(points, default, default, settings.MinThickness);
                candidates[(int)ShapeType.Box] = box;
                reports[(int)ShapeType.Box] = new FitReport
                {
                    Valid = true, Volume = box.Volume(), Accuracy = 1f, WorstFit = 0f,
                    Cost = FitMetrics.CostOf(ShapeType.Box),
                };
                fullV.Dispose();
                fullT.Dispose();
                return (int)ShapeType.Box;
            }

            NativeArray<float3> refV = fullV.AsArray();
            float refVolume = ConvexHull3D.Volume(refV, fullT.AsArray());
            var refPlanes = new NativeList<float4>(fullT.Length / 3, Allocator.Temp);
            ConvexHull3D.ComputePlanes(refV, fullT.AsArray(), refPlanes);

            // Reduced hull (also the normal source for OBB candidates).
            int cap = math.clamp(settings.MaxHullVertices, 4, ConvexHull3D.MaxConvexColliderVertices);
            if (refV.Length <= cap)
            {
                hullVertices.AddRange(refV);
                hullTriangles.AddRange(fullT.AsArray());
            }
            else if (!ConvexHull3D.Build(refV, cap, hullVertices, hullTriangles))
            {
                hullVertices.Clear();
                hullTriangles.Clear();
                hullVertices.AddRange(refV);
                hullTriangles.AddRange(fullT.AsArray());
            }

            var scratch = new NativeList<float3>(512, Allocator.Temp);

            var sphere = PrimitiveFitter.FitSphere(refV);
            var boxShape = PrimitiveFitter.FitBox(refV, hullVertices.AsArray(), hullTriangles.AsArray(), settings.MinThickness);
            var capsule = PrimitiveFitter.FitCapsule(refV, boxShape);

            candidates[(int)ShapeType.Sphere] = sphere;
            candidates[(int)ShapeType.Box] = boxShape;
            candidates[(int)ShapeType.Capsule] = capsule;
            candidates[(int)ShapeType.ConvexHull] = new ColliderShape
            {
                Type = ShapeType.ConvexHull,
                Center = GeometryUtils.Centroid(hullVertices.AsArray()),
                Rotation = quaternion.identity,
            };

            // All candidates are always evaluated (instant type conversion); Allow* only filters auto-selection.
            reports[(int)ShapeType.Sphere] = FitMetrics.EvaluatePrimitive(sphere, refV, refPlanes.AsArray(), refVolume, scratch);
            reports[(int)ShapeType.Capsule] = FitMetrics.EvaluatePrimitive(capsule, refV, refPlanes.AsArray(), refVolume, scratch);
            reports[(int)ShapeType.Box] = FitMetrics.EvaluatePrimitive(boxShape, refV, refPlanes.AsArray(), refVolume, scratch);
            reports[(int)ShapeType.ConvexHull] = FitMetrics.EvaluateHull(hullVertices.AsArray(), hullTriangles.AsArray(), refV, refVolume);

            scratch.Dispose();
            refPlanes.Dispose();
            fullV.Dispose();
            fullT.Dispose();

            return Select(reports, settings);
        }

        public static bool IsAllowed(in ShapeFitSettings s, int type)
        {
            switch ((ShapeType)type)
            {
                case ShapeType.Sphere: return s.AllowSphere;
                case ShapeType.Capsule: return s.AllowCapsule;
                case ShapeType.Box: return s.AllowBox;
                default: return s.AllowConvexHull;
            }
        }

        public static int Select(NativeArray<FitReport> reports, in ShapeFitSettings settings)
        {
            int best = -1;
            float bestScore = float.NegativeInfinity;
            for (int i = 0; i < CandidateCount; i++)
            {
                FitReport r = reports[i];
                if (!r.Valid || !IsAllowed(settings, i) || r.Accuracy < settings.MinAccuracy) continue;
                float score = r.Accuracy - settings.CostWeight * r.Cost;
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best >= 0) return best;

            int hull = (int)ShapeType.ConvexHull;
            if (reports[hull].Valid && settings.AllowConvexHull) return hull;

            // Nothing allowed passes: most accurate allowed shape, else most accurate overall.
            float bestAcc = -1f;
            for (int pass = 0; pass < 2 && best < 0; pass++)
            for (int i = 0; i < CandidateCount; i++)
            {
                if (!reports[i].Valid || (pass == 0 && !IsAllowed(settings, i))) continue;
                if (reports[i].Accuracy > bestAcc)
                {
                    bestAcc = reports[i].Accuracy;
                    best = i;
                }
            }
            return best;
        }
    }

    /// <summary>Burst entry point for <see cref="ShapeFitter.FitAll"/>. Run() for sync, Schedule() for async.</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct ShapeFitJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Points;
        public ShapeFitSettings Settings;
        public NativeList<float3> HullVertices;
        public NativeList<int> HullTriangles;
        public NativeArray<ColliderShape> Candidates;
        public NativeArray<FitReport> Reports;
        public NativeReference<int> Selected;

        public void Execute()
        {
            Selected.Value = ShapeFitter.FitAll(Points, Settings, HullVertices, HullTriangles, Candidates, Reports);
        }
    }
}
