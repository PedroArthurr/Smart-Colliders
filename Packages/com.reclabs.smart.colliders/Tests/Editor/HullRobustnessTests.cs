using System.Linq;
using NUnit.Framework;
using RecLabs.SmartColliders.Geometry;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace RecLabs.SmartColliders.Tests
{
    public class HullRobustnessTests
    {
        static float3[] BoxTriangleSoup(float3 size)
        {
            var c = new float3[8];
            for (int i = 0; i < 8; i++) c[i] = new float3((i & 1) * size.x, ((i >> 1) & 1) * size.y, ((i >> 2) & 1) * size.z);
            int[] q = { 0, 2, 3, 1, 4, 5, 7, 6, 0, 1, 5, 4, 2, 6, 7, 3, 0, 4, 6, 2, 1, 3, 7, 5 };
            var pts = new System.Collections.Generic.List<float3>();
            for (int f = 0; f < 6; f++)
            {
                pts.Add(c[q[f * 4]]); pts.Add(c[q[f * 4 + 1]]); pts.Add(c[q[f * 4 + 2]]);
                pts.Add(c[q[f * 4]]); pts.Add(c[q[f * 4 + 2]]); pts.Add(c[q[f * 4 + 3]]);
            }
            return pts.ToArray();
        }

        static void AssertBoxHull(float3[] input, float expectedVolume)
        {
            var pts = new NativeArray<float3>(input, Allocator.Temp);
            var v = new NativeList<float3>(Allocator.Temp);
            var t = new NativeList<int>(Allocator.Temp);
            Assert.IsTrue(ConvexHull3D.Build(pts, 0, v, t));
            string dump = string.Join(" ", v.AsArray().ToArray().Select(x => x.ToString()));
            Assert.AreEqual(8, v.Length, dump);
            Assert.AreEqual(expectedVolume, ConvexHull3D.Volume(v.AsArray(), t.AsArray()), expectedVolume * 1e-4f, dump);
            pts.Dispose(); v.Dispose(); t.Dispose();
        }

        [Unity.Burst.BurstCompile(CompileSynchronously = true)]
        struct HullJob : Unity.Jobs.IJob
        {
            public NativeArray<float3> Points;
            public NativeList<float3> V;
            public NativeList<int> T;
            public void Execute() => ConvexHull3D.Build(Points, 0, V, T);
        }

        [Test]
        public void Hull_BoxTriangleSoup_Burst()
        {
            var pts = new NativeArray<float3>(BoxTriangleSoup(new float3(1, 2, 3)), Allocator.TempJob);
            var v = new NativeList<float3>(Allocator.TempJob);
            var t = new NativeList<int>(Allocator.TempJob);
            Unity.Jobs.IJobExtensions.Run(new HullJob { Points = pts, V = v, T = t });
            string dump = string.Join(" ", v.AsArray().ToArray().Select(x => x.ToString()));
            int n = v.Length;
            float vol = ConvexHull3D.Volume(v.AsArray(), t.AsArray());
            pts.Dispose(); v.Dispose(); t.Dispose();
            Assert.AreEqual(8, n, dump);
            Assert.AreEqual(6f, vol, 1e-3f, dump);
        }

        [Test]
        public void Hull_BoxTriangleSoup_WithDuplicates() => AssertBoxHull(BoxTriangleSoup(new float3(1, 2, 3)), 6f);

        [Test]
        public void Hull_UnitCubeTriangleSoup() => AssertBoxHull(BoxTriangleSoup(new float3(1, 1, 1)), 1f);

        [Test]
        public void Hull_BoxShuffledDuplicates()
        {
            var soup = BoxTriangleSoup(new float3(3, 1, 1)).ToList();
            var rng = new Unity.Mathematics.Random(7);
            for (int i = soup.Count - 1; i > 0; i--) { int j = rng.NextInt(i + 1); (soup[i], soup[j]) = (soup[j], soup[i]); }
            AssertBoxHull(soup.ToArray(), 3f);
        }

        [Test]
        public void Hull_LatticeBlock()
        {
            // Dense coplanar lattice (voxel corners): 5x4x3 grid points.
            var pts = new System.Collections.Generic.List<float3>();
            for (int x = 0; x <= 4; x++) for (int y = 0; y <= 3; y++) for (int z = 0; z <= 2; z++) pts.Add(new float3(x, y, z));
            AssertBoxHull(pts.ToArray(), 24f);
        }
    }
}
