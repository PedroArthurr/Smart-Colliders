using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RecLabs.SmartColliders.EditorTools;
using RecLabs.SmartColliders.Geometry;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace RecLabs.SmartColliders.Tests
{
    /// <summary>Phase 3 refine operations: assign/split, merge, axis rotation, container re-use, mesh cleanup, mesh queries.</summary>
    public class EditOperationsTests
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
            foreach (var o in _created) if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
            SmartCollidersProjectSettings.instance.generatedFolder = _previousFolder;
            AssetDatabase.DeleteAsset(TestFolder);
        }

        GameObject Primitive(PrimitiveType type, Transform parent, Vector3 position)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            return go;
        }

        SmartColliderAuthoring TwoCubes(BakeTarget target = BakeTarget.ChildContainer)
        {
            var root = new GameObject("Root");
            _created.Add(root);
            Primitive(PrimitiveType.Cube, root.transform, new Vector3(-2, 0, 0));
            Primitive(PrimitiveType.Cube, root.transform, new Vector3(2, 0, 0));
            var a = root.AddComponent<SmartColliderAuthoring>();
            a.fitSettings = ShapeFitSettings.Default;
            a.bakeTarget = target;
            SmartColliderOperations.Generate(a, GenerateMode.PerMesh);
            return a;
        }

        [Test]
        public void MergeParts_UnionsTriangles_AndRefits()
        {
            var a = TwoCubes();
            Assert.AreEqual(2, a.Parts.Count);
            int total = a.Parts.Sum(p => p.TriangleCount);

            Undo.IncrementCurrentGroup();
            int merged = SmartColliderOperations.MergeParts(a, new[] { 1, 0 });

            Assert.AreEqual(0, merged);
            Assert.AreEqual(1, a.Parts.Count);
            Assert.AreEqual(total, a.Parts[0].TriangleCount);
            // One box spanning both cubes: x from -2.5 to 2.5.
            Assert.AreEqual(ShapeType.Box, a.Parts[0].Type);
            Assert.AreEqual(0f, a.Parts[0].Shape.Center.x, 1e-3f);
            Assert.AreEqual(2.5f, a.Parts[0].Shape.HalfExtents.x, 1e-3f);

            Undo.PerformUndo();
            Assert.AreEqual(2, a.Parts.Count);
        }

        [Test]
        public void AssignTriangles_NewPart_RemovesFromOwner_AndDropsEmpty()
        {
            var a = TwoCubes();
            // Move all of cube 1 plus half of cube 0 into a new part.
            var cube1 = TriangleRanges.Enumerate(a.Parts[1].triangleRanges).ToList();
            var half0 = TriangleRanges.Enumerate(a.Parts[0].triangleRanges).Take(6).ToList();
            int before0 = a.Parts[0].TriangleCount;

            int index = SmartColliderOperations.AssignTriangles(a, cube1.Concat(half0).ToList());

            Assert.AreEqual(2, a.Parts.Count, "cube 1 part became empty and is removed; new part added");
            Assert.AreEqual(1, index);
            Assert.AreEqual(before0 - 6, a.Parts[0].TriangleCount);
            Assert.AreEqual(cube1.Count + 6, a.Parts[1].TriangleCount);
            Assert.IsTrue(a.Parts[1].HasFit);
            Assert.IsTrue(a.Parts.All(p => p.HasFit));
        }

        [Test]
        public void AssignTriangles_IntoExistingPart()
        {
            var a = TwoCubes();
            var some0 = TriangleRanges.Enumerate(a.Parts[0].triangleRanges).Take(4).ToList();
            int target = SmartColliderOperations.AssignTriangles(a, some0, 1);
            Assert.AreEqual(1, target);
            Assert.AreEqual(2, a.Parts.Count);
            Assert.AreEqual(16, a.Parts[1].TriangleCount);
            Assert.AreEqual(8, a.Parts[0].TriangleCount);
            // Part 1 now spans into cube 0 → its fit grows toward -x.
            Assert.Less(a.Parts[1].HullVertices.Min(v => v.x), 0f);
        }

        [Test]
        public void RotateParts_Local90_KeepsCenter_AndTurnsManual()
        {
            var a = TwoCubes();
            SmartColliderOperations.SetType(a, 0, ShapeType.Box);
            var before = a.Parts[0].Shape;
            SmartColliderRefine.RotateParts(a, new[] { 0 }, 1, 90f, localSpace: true);
            var after = a.Parts[0].Shape;

            Assert.AreEqual((Vector3)before.Center, (Vector3)after.Center);
            float angle = Quaternion.Angle(before.Rotation, after.Rotation);
            Assert.AreEqual(90f, angle, 0.01f);
            Assert.IsFalse(a.Parts[0].AutoType);
            // Cube rotated 90° about Y still encloses it perfectly.
            Assert.Greater(a.Parts[0].Report.Accuracy, 0.97f);
        }

        [Test]
        public void SetRotation_Absolute()
        {
            var a = TwoCubes();
            SmartColliderOperations.SetType(a, 0, ShapeType.Box);
            SmartColliderRefine.SetRotation(a, new[] { 0 }, new Vector3(0, 45, 0));
            Assert.AreEqual(0f, Quaternion.Angle(Quaternion.Euler(0, 45, 0), a.Parts[0].Shape.Rotation), 0.01f);
            Assert.Less(a.Parts[0].Report.Accuracy, 0.97f, "a 45° box no longer matches the cube");
        }

        [Test]
        public void RotateParts_Hull_RotatesVertices()
        {
            var a = TwoCubes();
            SmartColliderOperations.SetType(a, 1, ShapeType.ConvexHull);
            var centroid = a.Parts[1].Shape.Center;
            var v0 = a.Parts[1].HullVertices[0];
            SmartColliderRefine.RotateParts(a, new[] { 1 }, 2, 90f, localSpace: false);
            var v1 = a.Parts[1].HullVertices[0];
            Vector3 expected = (Vector3)centroid + Quaternion.Euler(0, 0, 90) * (v0 - (Vector3)centroid);
            Assert.Less((v1 - expected).magnitude, 1e-4f);
        }

        [Test]
        public void Rebake_Container_IsReused()
        {
            var a = TwoCubes(BakeTarget.ChildContainer);
            SmartColliderBaker.Bake(a);
            var container = a.transform.Find("Colliders");
            Assert.IsNotNull(container);
            int id = container.gameObject.GetInstanceID();
            Assert.AreEqual(2, container.childCount);

            SmartColliderBaker.Bake(a);
            var again = a.transform.Find("Colliders");
            Assert.AreEqual(id, again.gameObject.GetInstanceID());
            Assert.AreEqual(2, again.childCount);
            Assert.AreEqual(2, a.bakedColliders.Count);

            SmartColliderBaker.ClearBaked(a);
            Assert.IsNull(a.transform.Find("Colliders"));
        }

        [Test]
        public void CleanUnusedMeshes_RemovesOnlyUnreferenced()
        {
            var a = TwoCubes(BakeTarget.ChildContainer);
            SmartColliderOperations.SetType(a, 0, ShapeType.ConvexHull);
            SmartColliderOperations.SetType(a, 1, ShapeType.ConvexHull);
            SmartColliderBaker.Bake(a);
            Assert.AreEqual(2, a.meshBank.meshes.Count);

            SmartColliderOperations.SetType(a, 1, ShapeType.Box);
            SmartColliderBaker.Bake(a);
            Assert.AreEqual(1, SmartColliderBaker.CountUnusedMeshes(a));
            Assert.AreEqual(1, SmartColliderBaker.CleanUnusedMeshes(a));
            Assert.AreEqual(1, a.meshBank.meshes.Count);
            var mc = a.bakedColliders.OfType<MeshCollider>().Single();
            Assert.AreSame(a.meshBank.meshes[0], mc.sharedMesh);
        }

        [Test]
        public void MeshQueries_RaycastAndGather()
        {
            var verts = new NativeArray<float3>(new[] { new float3(-1, 0, -1), new float3(1, 0, -1), new float3(0, 0, 1), new float3(5, 0, 5), new float3(6, 0, 5), new float3(5, 0, 6) }, Allocator.Temp);
            var idx = new NativeArray<int>(new[] { 0, 2, 1, 3, 5, 4 }, Allocator.Temp);
            int tri = MeshQueries.Raycast(verts, idx, new float3(0, 5, 0), new float3(0, -1, 0), out float t);
            Assert.AreEqual(0, tri);
            Assert.AreEqual(5f, t, 1e-5f);
            Assert.AreEqual(-1, MeshQueries.Raycast(verts, idx, new float3(3, 5, 3), new float3(0, -1, 0), out _));

            var result = new NativeList<int>(Allocator.Temp);
            MeshQueries.Gather(verts, idx, new float3(5.2f, 0, 5.2f), 0.5f, result);
            Assert.AreEqual(1, result.Length);
            Assert.AreEqual(1, result[0]);

            var weld = new NativeArray<int>(4, Allocator.Temp);
            var dup = new NativeArray<float3>(new[] { new float3(0, 0, 0), new float3(1, 0, 0), new float3(0, 0, 0), new float3(1, 0, 1e-9f) }, Allocator.Temp);
            MeshQueries.Weld(dup, 1e-5f, weld);
            Assert.AreEqual(weld[0], weld[2]);
            Assert.AreEqual(weld[1], weld[3]);
            Assert.AreNotEqual(weld[0], weld[1]);
            verts.Dispose(); idx.Dispose(); result.Dispose(); weld.Dispose(); dup.Dispose();
        }
    }
}
