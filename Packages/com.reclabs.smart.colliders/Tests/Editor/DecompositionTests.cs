using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RecLabs.SmartColliders.EditorTools;
using RecLabs.SmartColliders.Geometry;
using RecLabs.SmartColliders.Geometry.Decomposition;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace RecLabs.SmartColliders.Tests
{
    public class DecompositionTests
    {
        const string TestFolder = "Assets/__SmartCollidersTests";
        string _previousFolder;
        readonly List<Object> _created = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            _previousFolder = SmartCollidersProjectSettings.instance.generatedFolder;
            SmartCollidersProjectSettings.instance.generatedFolder = TestFolder;
        }

        [TearDown]
        public void TearDown()
        {
            DecompositionRunner.Current?.Cancel();
            foreach (var o in _created) if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
            SmartCollidersProjectSettings.instance.generatedFolder = _previousFolder;
            AssetDatabase.DeleteAsset(TestFolder);
        }

        /// <summary>Closed box meshes (outward winding) merged into one mesh.</summary>
        static Mesh Boxes(params Bounds[] boxes)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            foreach (var b in boxes)
            {
                int o = v.Count;
                for (int i = 0; i < 8; i++)
                    v.Add(new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z));
                int[] q =
                {
                    0, 2, 3, 1, // -z
                    4, 5, 7, 6, // +z
                    0, 1, 5, 4, // -y
                    2, 6, 7, 3, // +y
                    0, 4, 6, 2, // -x
                    1, 3, 7, 5, // +x
                };
                for (int f = 0; f < 6; f++)
                {
                    t.Add(o + q[f * 4]); t.Add(o + q[f * 4 + 1]); t.Add(o + q[f * 4 + 2]);
                    t.Add(o + q[f * 4]); t.Add(o + q[f * 4 + 2]); t.Add(o + q[f * 4 + 3]);
                }
            }
            var m = new Mesh();
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        static Bounds B(Vector3 min, Vector3 max) { var b = new Bounds(); b.SetMinMax(min, max); return b; }

        SmartColliderAuthoring MeshObject(Mesh mesh, DecompositionQuality quality = DecompositionQuality.Medium, int max = 10)
        {
            var go = new GameObject("Decomp");
            _created.Add(go);
            _created.Add(mesh);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var a = go.AddComponent<SmartColliderAuthoring>();
            a.fitSettings = ShapeFitSettings.Default;
            a.decomposition = DecompositionSettings.Default;
            a.decomposition.Quality = quality;
            a.decomposition.Engine = DecompositionEngine.BuiltIn;
            a.decomposition.MaxColliders = max;
            a.decomposition.Async = false;
            return a;
        }

        static Mesh LShape() => Boxes(B(new Vector3(0, 0, 0), new Vector3(3, 1, 1)), B(new Vector3(0, 1, 0), new Vector3(1, 3, 1)));

        [Test]
        public void Voxelizer_SolidBox_FillsInterior()
        {
            var mesh = Boxes(B(Vector3.zero, new Vector3(2, 1, 1)));
            var tr = new System.Text.StringBuilder();
            using var d = new VoxelDecomposer(
                new NativeArray<float3>(mesh.vertices.Select(x => (float3)x).ToArray(), Allocator.Temp),
                new NativeArray<int>(mesh.triangles, Allocator.Temp),
                new VoxelDecomposerParams { Resolution = 20, Candidates = 8, Concavity = 0.02f, MaxPieces = 8 }) { Trace = tr };
            d.RunToCompletion();
            Assert.AreEqual(1, d.Result.Count, "a convex box needs no cut");
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void Decompose_LShape_TwoBoxes()
        {
            var a = MeshObject(LShape());
            Assert.IsTrue(SmartColliderOperations.Generate(a, GenerateMode.Decompose));
            Assert.AreEqual(2, a.Parts.Count, string.Join(", ", a.Parts.Select(p => $"{p.Type} {p.Report.Accuracy:F2}")));
            foreach (var p in a.Parts)
            {
                Assert.AreEqual(ShapeType.Box, p.Type);
                Assert.Greater(p.Report.Accuracy, 0.9f);
                Assert.IsTrue(p.HasFitPoints);
            }
            // Pieces cover the L: total box volume ≈ 3 + 2 = 5 (within voxel error).
            float vol = a.Parts.Sum(p => p.Shape.Volume());
            Assert.AreEqual(5f, vol, 0.35f);
            // Every source triangle is assigned to exactly one part.
            Assert.AreEqual(24, a.Parts.Sum(p => p.TriangleCount));
        }

        [Test]
        public void Decompose_ConvexInput_SinglePart()
        {
            var a = MeshObject(Boxes(B(Vector3.zero, new Vector3(1, 2, 3))));
            SmartColliderOperations.Generate(a, GenerateMode.Decompose);
            Assert.AreEqual(1, a.Parts.Count);
            var fp = a.Parts[0].fitPoints;
            TestContext.WriteLine($"[Test] convex fitpoints {fp.Length} min {fp.Aggregate(Vector3.positiveInfinity, Vector3.Min)} max {fp.Aggregate(Vector3.negativeInfinity, Vector3.Max)} box {a.Parts[0].GetReport(ShapeType.Box).Accuracy} hull {a.Parts[0].GetReport(ShapeType.ConvexHull).Accuracy}");
            Assert.AreEqual(ShapeType.Box, a.Parts[0].Type);
        }

        [Test]
        public void Decompose_RespectsMaxColliders()
        {
            // A comb: 5 teeth on a spine.
            var boxes = new List<Bounds> { B(new Vector3(0, 0, 0), new Vector3(9, 1, 1)) };
            for (int i = 0; i < 5; i++) boxes.Add(B(new Vector3(i * 2, 1, 0), new Vector3(i * 2 + 1, 4, 1)));
            var combMesh = Boxes(boxes.ToArray());
            var trace = new System.Text.StringBuilder();
            using (var d = new VoxelDecomposer(
                new NativeArray<float3>(combMesh.vertices.Select(x => (float3)x).ToArray(), Allocator.Temp),
                new NativeArray<int>(combMesh.triangles, Allocator.Temp),
                VoxelDecomposerParams.From(new DecompositionSettings { Quality = DecompositionQuality.High, MaxColliders = 25, AutoMerge = true, MergeTolerance = 0.05f })) { Trace = trace })
            {
                d.RunToCompletion();
            }
            Object.DestroyImmediate(combMesh);
            var a = MeshObject(Boxes(boxes.ToArray()), DecompositionQuality.Medium, 3);
            SmartColliderOperations.Generate(a, GenerateMode.Decompose);
            Assert.LessOrEqual(a.Parts.Count, 3);
            Assert.GreaterOrEqual(a.Parts.Count, 2);
        }

        [Test]
        public void Decompose_Comb_SeparatesTeeth_AtHighQuality()
        {
            var boxes = new List<Bounds> { B(new Vector3(0, 0, 0), new Vector3(9, 1, 1)) };
            for (int i = 0; i < 5; i++) boxes.Add(B(new Vector3(i * 2, 1, 0), new Vector3(i * 2 + 1, 4, 1)));
            var a = MeshObject(Boxes(boxes.ToArray()), DecompositionQuality.High, 25);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            SmartColliderOperations.Generate(a, GenerateMode.Decompose);
            TestContext.WriteLine($"[Test] comb high: {a.Parts.Count} parts in {sw.ElapsedMilliseconds} ms");
            Assert.GreaterOrEqual(a.Parts.Count, 6);
            float vol = a.Parts.Sum(p => p.Type == ShapeType.Box ? p.Shape.Volume() : p.Report.Volume);
            Assert.AreEqual(9f + 15f, vol, 2.5f, "pieces should hug the comb (no hull over the gaps)");
        }

        [Test]
        public void Decompose_Sphere_Performance()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            _created.Add(go);
            var a = go.AddComponent<SmartColliderAuthoring>();
            a.decomposition = DecompositionSettings.Default;
            a.decomposition.Engine = DecompositionEngine.BuiltIn;
            a.decomposition.Quality = DecompositionQuality.Ultra;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            SmartColliderOperations.Generate(a, GenerateMode.Decompose);
            TestContext.WriteLine($"[Test] sphere ultra: {a.Parts.Count} parts in {sw.ElapsedMilliseconds} ms");
            Assert.LessOrEqual(a.Parts.Count, 3, "a sphere is convex");
            Assert.Less(sw.ElapsedMilliseconds, 20000);
        }

        [Test]
        public void MergeParts_KeepsExplicitPoints()
        {
            var a = MeshObject(LShape());
            SmartColliderOperations.Generate(a, GenerateMode.Decompose);
            Assume.That(a.Parts.Count, Is.EqualTo(2));
            SmartColliderOperations.MergeParts(a, new[] { 0, 1 });
            Assert.AreEqual(1, a.Parts.Count);
            Assert.IsTrue(a.Parts[0].HasFitPoints);
            Assert.AreEqual(24, a.Parts[0].TriangleCount);
        }

        [UnityTest]
        public IEnumerator Runner_Async_PauseResumeApply()
        {
            var a = MeshObject(LShape(), DecompositionQuality.High);
            a.decomposition.YieldMilliseconds = 2;
            SmartColliderOperations.Generate(a, GenerateMode.Single);
            Assert.AreEqual(1, a.Parts.Count);
            Undo.IncrementCurrentGroup();

            Assert.IsTrue(DecompositionRunner.Start(a));
            var runner = DecompositionRunner.Current;
            runner.Pause();
            Assert.IsTrue(runner.IsPaused);
            float p = runner.Progress;
            for (int i = 0; i < 5; i++) yield return null;
            Assert.AreEqual(p, runner.Progress, "no progress while paused");
            Assert.AreEqual(1, a.Parts.Count);

            runner.Resume();
            for (int i = 0; i < 600 && DecompositionRunner.Current != null; i++) yield return null;
            Assert.IsNull(DecompositionRunner.Current);
            Assert.AreEqual(2, a.Parts.Count);

            Undo.PerformUndo();
            Assert.AreEqual(1, a.Parts.Count, "decomposition is one undo step");
        }

        [UnityTest]
        public IEnumerator Runner_Cancel_KeepsParts()
        {
            var a = MeshObject(LShape());
            SmartColliderOperations.Generate(a, GenerateMode.Single);
            DecompositionRunner.Start(a);
            DecompositionRunner.Current.Cancel();
            yield return null;
            Assert.IsNull(DecompositionRunner.Current);
            Assert.AreEqual(1, a.Parts.Count);
        }

        [Test]
        public void Native_VHacd_Decomposes_LShape()
        {
            if (!NativeDecomposer.IsAvailable(NativeDecomposer.EngineVHacd)) Assert.Ignore("native plugin not available on this platform");
            var a = MeshObject(LShape());
            a.decomposition.Engine = DecompositionEngine.VHacd;
            Assert.AreEqual(DecompositionEngine.VHacd, DecompositionRunner.Resolve(a.decomposition));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.IsTrue(SmartColliderOperations.Generate(a, GenerateMode.Decompose));
            TestContext.WriteLine($"[Test] vhacd L: {a.Parts.Count} parts in {sw.ElapsedMilliseconds} ms: {string.Join(", ", a.Parts.Select(p => $"{p.Type} {p.Report.Accuracy:F2} tris {p.TriangleCount}"))}");
            Assert.GreaterOrEqual(a.Parts.Count, 2);
            Assert.AreEqual(24, a.Parts.Sum(p => p.TriangleCount));
            Assert.IsTrue(a.Parts.All(p => p.HasFit));
        }

        [UnityTest]
        public IEnumerator Native_VHacd_Async_Cancel()
        {
            if (!NativeDecomposer.IsAvailable(NativeDecomposer.EngineVHacd)) { Assert.Ignore("native plugin not available"); yield break; }
            var a = MeshObject(LShape(), DecompositionQuality.Medium);
            a.decomposition.Engine = DecompositionEngine.VHacd;
            SmartColliderOperations.Generate(a, GenerateMode.Single);
            Assert.IsTrue(DecompositionRunner.Start(a));
            Assert.AreEqual("V-HACD", DecompositionRunner.Current.EngineName);
            DecompositionRunner.Current.Pause();
            yield return null;
            DecompositionRunner.Current.Cancel();
            Assert.IsNull(DecompositionRunner.Current);
            Assert.AreEqual(1, a.Parts.Count);
        }

        // ---------- skinned ----------

        SmartColliderAuthoring SkinnedTwoBones(out Transform b0, out Transform b1)
        {
            var root = new GameObject("Rig");
            _created.Add(root);
            b0 = new GameObject("Upper").transform;
            b0.SetParent(root.transform, false);
            b1 = new GameObject("Lower").transform;
            b1.SetParent(b0, false);
            b1.localPosition = new Vector3(0, 2, 0);

            var mesh = Boxes(B(new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 2, 0.5f)), B(new Vector3(-0.5f, 2, -0.5f), new Vector3(0.5f, 4, 0.5f)));
            _created.Add(mesh);
            var weights = new BoneWeight[16];
            for (int i = 0; i < 16; i++) weights[i] = new BoneWeight { boneIndex0 = i < 8 ? 0 : 1, weight0 = 1f };
            mesh.boneWeights = weights;
            mesh.bindposes = new[] { b0.worldToLocalMatrix * root.transform.localToWorldMatrix, b1.worldToLocalMatrix * root.transform.localToWorldMatrix };

            var skin = new GameObject("Skin");
            skin.transform.SetParent(root.transform, false);
            var smr = skin.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = new[] { b0, b1 };
            smr.rootBone = b0;

            var a = root.AddComponent<SmartColliderAuthoring>();
            a.bakeTarget = BakeTarget.SourceObjects;
            return a;
        }

        [Test]
        public void PerBone_OnePartPerBone_WithBone()
        {
            var a = SkinnedTwoBones(out var b0, out var b1);
            Assert.IsTrue(SmartColliderOperations.Generate(a, GenerateMode.PerBone));
            Assert.AreEqual(2, a.Parts.Count);
            Assert.AreSame(b0, a.Parts[0].Bone);
            Assert.AreSame(b1, a.Parts[1].Bone);
            Assert.AreEqual(ShapeType.Box, a.Parts[1].Type);
            Assert.AreEqual(3f, a.Parts[1].Shape.Center.y, 1e-3f);
        }

        [Test]
        public void PerBone_Bake_CollidersUnderBones_FollowPose()
        {
            var a = SkinnedTwoBones(out var b0, out var b1);
            SmartColliderOperations.Generate(a, GenerateMode.PerBone);

            // Bend the lower bone 90° after generation: preview + bake must follow.
            b1.localRotation = Quaternion.Euler(0, 0, 90);
            var delta = a.Parts[1].PoseDelta(a.transform);
            Vector3 movedCenter = delta.MultiplyPoint3x4(a.Parts[1].Shape.Center);
            Assert.AreEqual(-1f, movedCenter.x, 1e-3f);
            Assert.AreEqual(2f, movedCenter.y, 1e-3f);

            SmartColliderBaker.Bake(a);
            var lower = b1.GetComponents<Collider>();
            Assert.AreEqual(1, lower.Length);
            var box = lower[0] as BoxCollider;
            Assert.IsNotNull(box);
            Assert.Less((box.center - new Vector3(0, 1, 0)).magnitude, 1e-4f);
            Assert.Less((box.bounds.center - new Vector3(-1, 2, 0)).magnitude, 1e-3f);
            Assert.AreEqual(1, b0.GetComponents<Collider>().Length);
        }
    }
}
