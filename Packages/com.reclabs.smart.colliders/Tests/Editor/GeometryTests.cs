using NUnit.Framework;
using RecLabs.SmartColliders.Geometry;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace RecLabs.SmartColliders.Tests
{
    public class GeometryTests
    {
        static readonly quaternion OddRotation = quaternion.Euler(0.37f, 1.1f, -0.52f);
        static readonly float3 Offset = new float3(3.5f, -2f, 7.25f);

        // ---------- point generators ----------

        static NativeArray<float3> Grid(int res, float3 halfExtents, quaternion rot, float3 offset)
        {
            var list = new NativeList<float3>(Allocator.Temp);
            for (int x = 0; x < res; x++)
            for (int y = 0; y < res; y++)
            for (int z = 0; z < res; z++)
            {
                float3 t = new float3(x, y, z) / (res - 1) * 2f - 1f;
                list.Add(offset + math.rotate(rot, t * halfExtents));
            }
            var arr = new NativeArray<float3>(list.AsArray(), Allocator.TempJob);
            list.Dispose();
            return arr;
        }

        static NativeArray<float3> RandomBall(int count, float radius, uint seed)
        {
            var rng = new Random(seed);
            var arr = new NativeArray<float3>(count, Allocator.TempJob);
            for (int i = 0; i < count; i++)
                arr[i] = rng.NextFloat3Direction() * radius * math.pow(rng.NextFloat(), 1f / 3f);
            return arr;
        }

        static NativeArray<float3> ShapeSurface(ColliderShape shape, int density)
        {
            var list = new NativeList<float3>(Allocator.Temp);
            shape.SampleSurface(list, density);
            var arr = new NativeArray<float3>(list.AsArray(), Allocator.TempJob);
            list.Dispose();
            return arr;
        }

        static NativeArray<float3> Octahedron(float r)
        {
            var arr = new NativeArray<float3>(6, Allocator.TempJob);
            arr[0] = new float3(r, 0, 0); arr[1] = new float3(-r, 0, 0);
            arr[2] = new float3(0, r, 0); arr[3] = new float3(0, -r, 0);
            arr[4] = new float3(0, 0, r); arr[5] = new float3(0, 0, -r);
            return arr;
        }

        // ---------- convex hull ----------

        [Test]
        public void Hull_GridCube_IgnoresCoplanarPoints()
        {
            var pts = Grid(5, new float3(0.5f), quaternion.identity, float3.zero);
            var v = new NativeList<float3>(Allocator.Temp);
            var t = new NativeList<int>(Allocator.Temp);

            Assert.IsTrue(ConvexHull3D.Build(pts, 0, v, t));
            Assert.AreEqual(8, v.Length);
            Assert.AreEqual(36, t.Length);
            Assert.AreEqual(1f, ConvexHull3D.Volume(v.AsArray(), t.AsArray()), 1e-4f);

            pts.Dispose(); v.Dispose(); t.Dispose();
        }

        [Test]
        public void Hull_RandomBall_ContainsAllPoints_AndIsClosed()
        {
            var pts = RandomBall(3000, 2f, 1234u);
            var v = new NativeList<float3>(Allocator.Temp);
            var t = new NativeList<int>(Allocator.Temp);
            var planes = new NativeList<float4>(Allocator.Temp);

            Assert.IsTrue(ConvexHull3D.Build(pts, 0, v, t));
            Assert.AreEqual(2 * v.Length - 4, t.Length / 3, "Euler: F = 2V - 4 for a closed triangulated hull");

            ConvexHull3D.ComputePlanes(v.AsArray(), t.AsArray(), planes);
            for (int i = 0; i < pts.Length; i++)
                Assert.LessOrEqual(ConvexHull3D.SignedDistance(planes.AsArray(), pts[i]), 1e-4f);

            pts.Dispose(); v.Dispose(); t.Dispose(); planes.Dispose();
        }

        [Test]
        public void Hull_VertexCap_IsRespected_AndBakeable()
        {
            var pts = ShapeSurface(ColliderShape.Sphere(Offset, 3f), 30); // 3600 points
            var v = new NativeList<float3>(Allocator.Temp);
            var t = new NativeList<int>(Allocator.Temp);

            Assert.IsTrue(ConvexHull3D.Build(pts, ConvexHull3D.MaxConvexColliderVertices, v, t));
            Assert.LessOrEqual(v.Length, ConvexHull3D.MaxConvexColliderVertices);
            Assert.LessOrEqual(t.Length / 3, 255, "PhysX convex limit");

            float vol = ConvexHull3D.Volume(v.AsArray(), t.AsArray());
            float sphereVol = 4f / 3f * math.PI * 27f;
            Assert.Greater(vol / sphereVol, 0.9f, "progressive hull keeps most of the volume");

            pts.Dispose(); v.Dispose(); t.Dispose();
        }

        [Test]
        public void Hull_Coplanar_ReturnsFalse()
        {
            var pts = new NativeArray<float3>(5, Allocator.TempJob);
            pts[0] = new float3(0, 0, 0); pts[1] = new float3(1, 0, 0); pts[2] = new float3(0, 0, 1);
            pts[3] = new float3(1, 0, 1); pts[4] = new float3(0.5f, 0, 0.5f);
            var v = new NativeList<float3>(Allocator.Temp);
            var t = new NativeList<int>(Allocator.Temp);

            Assert.IsFalse(ConvexHull3D.Build(pts, 0, v, t));

            pts.Dispose(); v.Dispose(); t.Dispose();
        }

        [Test]
        public void Hull_TrianglesWoundOutward()
        {
            var pts = RandomBall(500, 1f, 99u);
            var v = new NativeList<float3>(Allocator.Temp);
            var t = new NativeList<int>(Allocator.Temp);
            ConvexHull3D.Build(pts, 0, v, t);
            float3 c = GeometryUtils.Centroid(v.AsArray());

            for (int i = 0; i < t.Length; i += 3)
            {
                float3 a = v[t[i]], b = v[t[i + 1]], d = v[t[i + 2]];
                Assert.Greater(math.dot(math.cross(b - a, d - a), a - c), 0f);
            }
            pts.Dispose(); v.Dispose(); t.Dispose();
        }

        // ---------- primitives ----------

        [Test]
        public void Sphere_RecoversRadiusAndCenter()
        {
            var pts = ShapeSurface(ColliderShape.Sphere(Offset, 2f), 12);
            var s = PrimitiveFitter.FitSphere(pts);
            Assert.AreEqual(2f, s.Radius, 2e-3f);
            Assert.Less(math.distance(Offset, s.Center), 2e-3f);
            pts.Dispose();
        }

        [Test]
        public void Box_RotatedGrid_RecoversExtents()
        {
            var he = new float3(1f, 2f, 3f);
            var pts = Grid(4, he, OddRotation, Offset);
            var v = new NativeList<float3>(Allocator.Temp);
            var t = new NativeList<int>(Allocator.Temp);
            ConvexHull3D.Build(pts, 0, v, t);

            var box = PrimitiveFitter.FitBox(v.AsArray(), v.AsArray(), t.AsArray());
            Assert.AreEqual(48f, box.Volume(), 1e-2f);
            Assert.Less(math.distance(Offset, box.Center), 1e-3f);
            for (int i = 0; i < pts.Length; i++)
                Assert.LessOrEqual(box.SignedDistance(pts[i]), 1e-4f);

            pts.Dispose(); v.Dispose(); t.Dispose();
        }

        [Test]
        public void Box_AxisAligned_KeepsIdentityRotation()
        {
            var pts = Grid(3, new float3(2f, 0.5f, 1f), quaternion.identity, Offset);
            var v = new NativeList<float3>(Allocator.Temp);
            var t = new NativeList<int>(Allocator.Temp);
            ConvexHull3D.Build(pts, 0, v, t);

            var box = PrimitiveFitter.FitBox(v.AsArray(), v.AsArray(), t.AsArray());
            Assert.AreEqual(quaternion.identity.value, box.Rotation.value);
            Assert.Less(math.distance(new float3(2f, 0.5f, 1f), box.HalfExtents), 1e-4f);

            pts.Dispose(); v.Dispose(); t.Dispose();
        }

        [Test]
        public void Capsule_RecoversRadiusAndLength()
        {
            var source = ColliderShape.Capsule(Offset, OddRotation, 0.5f, 1f);
            var pts = ShapeSurface(source, 12);
            var v = new NativeList<float3>(Allocator.Temp);
            var t = new NativeList<int>(Allocator.Temp);
            ConvexHull3D.Build(pts, 0, v, t);

            var box = PrimitiveFitter.FitBox(v.AsArray(), v.AsArray(), t.AsArray());
            var cap = PrimitiveFitter.FitCapsule(v.AsArray(), box);
            Assert.AreEqual(0.5f, cap.Radius, 5e-3f);
            Assert.AreEqual(1f, cap.HalfHeight, 1e-2f);
            Assert.Less(math.distance(Offset, cap.Center), 1e-2f);

            pts.Dispose(); v.Dispose(); t.Dispose();
        }

        // ---------- selection ----------

        static ShapeType FitAndSelect(NativeArray<float3> pts, out NativeArray<FitReport> reports)
        {
            var settings = ShapeFitSettings.Default;
            var v = new NativeList<float3>(Allocator.TempJob);
            var t = new NativeList<int>(Allocator.TempJob);
            var c = new NativeArray<ColliderShape>(ShapeFitter.CandidateCount, Allocator.TempJob);
            reports = new NativeArray<FitReport>(ShapeFitter.CandidateCount, Allocator.TempJob);
            var selected = new NativeReference<int>(Allocator.TempJob);

            new ShapeFitJob
            {
                Points = pts, Settings = settings, HullVertices = v, HullTriangles = t,
                Candidates = c, Reports = reports, Selected = selected,
            }.Run();

            var result = (ShapeType)selected.Value;
            v.Dispose(); t.Dispose(); c.Dispose(); selected.Dispose();
            return result;
        }

        [Test]
        public void Select_BoxPoints_PicksBox()
        {
            var pts = Grid(3, new float3(1f, 0.25f, 2f), OddRotation, Offset);
            Assert.AreEqual(ShapeType.Box, FitAndSelect(pts, out var reports));
            Assert.Greater(reports[(int)ShapeType.Box].Accuracy, 0.999f);
            pts.Dispose(); reports.Dispose();
        }

        [Test]
        public void Select_SpherePoints_PicksSphere()
        {
            var pts = ShapeSurface(ColliderShape.Sphere(Offset, 1.5f), 12);
            Assert.AreEqual(ShapeType.Sphere, FitAndSelect(pts, out var reports));
            pts.Dispose(); reports.Dispose();
        }

        [Test]
        public void Select_CapsulePoints_PicksCapsule()
        {
            var pts = ShapeSurface(ColliderShape.Capsule(Offset, OddRotation, 0.4f, 1.2f), 12);
            Assert.AreEqual(ShapeType.Capsule, FitAndSelect(pts, out var reports));
            pts.Dispose(); reports.Dispose();
        }

        [Test]
        public void Select_Octahedron_PicksConvexHull()
        {
            var pts = Octahedron(1f);
            Assert.AreEqual(ShapeType.ConvexHull, FitAndSelect(pts, out var reports));
            Assert.AreEqual(1f, reports[(int)ShapeType.ConvexHull].Accuracy, 1e-4f);
            pts.Dispose(); reports.Dispose();
        }

        [Test]
        public void Select_FlatQuad_PicksThinBox()
        {
            var pts = new NativeArray<float3>(4, Allocator.TempJob);
            pts[0] = new float3(-1, 0, -1); pts[1] = new float3(1, 0, -1);
            pts[2] = new float3(1, 0, 1); pts[3] = new float3(-1, 0, 1);
            Assert.AreEqual(ShapeType.Box, FitAndSelect(pts, out var reports));
            Assert.AreEqual(2f * 2f * 0.01f, reports[(int)ShapeType.Box].Volume, 1e-4f);
            pts.Dispose(); reports.Dispose();
        }

        [Test]
        public void Burst_LargeCloud_FitsFast()
        {
            var pts = RandomBall(100_000, 1f, 7u);
            FitAndSelect(pts, out var warm).ToString(); // warm-up (Burst compile)
            warm.Dispose();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            FitAndSelect(pts, out var reports);
            sw.Stop();
            UnityEngine.Debug.Log($"[SmartColliders] FitAll 100k points: {sw.Elapsed.TotalMilliseconds:F1} ms (Burst enabled: {Unity.Burst.BurstCompiler.IsEnabled})");
            Assert.Less(sw.Elapsed.TotalMilliseconds, 2000);

            pts.Dispose(); reports.Dispose();
        }

        [Test]
        public void CostTiers_AreMonotonic()
        {
            Assert.AreEqual(PhysicsCostTier.Light, FitMetrics.TierOf(FitMetrics.CostOf(ShapeType.Box) * 7));
            Assert.AreEqual(PhysicsCostTier.HeavyPlus, FitMetrics.TierOf(FitMetrics.CostOf(ShapeType.ConvexHull, 64) * 196));
        }
    }
}
