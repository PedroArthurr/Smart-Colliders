using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RecLabs.SmartColliders.Geometry;
using RecLabs.SmartColliders.EditorTools;
using UnityEditor;
using UnityEngine;

namespace RecLabs.SmartColliders.Tests
{
    public class AuthoringTests
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

        GameObject Primitive(PrimitiveType type, Transform parent = null)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            if (parent != null) go.transform.SetParent(parent, false);
            else _created.Add(go);
            return go;
        }

        GameObject Root(string name = "Root")
        {
            var go = new GameObject(name);
            _created.Add(go);
            return go;
        }

        static Mesh OctahedronMesh()
        {
            var m = new Mesh { name = "Octa" };
            m.vertices = new[]
            {
                Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back,
            };
            m.triangles = new[] { 0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4, 2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5 };
            return m;
        }

        static SmartColliderAuthoring Author(GameObject go, BakeTarget target = BakeTarget.ChildContainer)
        {
            var a = go.AddComponent<SmartColliderAuthoring>();
            a.fitSettings = ShapeFitSettings.Default;
            a.bakeTarget = target;
            return a;
        }

        static IEnumerable<Vector3> BoxCorners(BoxCollider b)
        {
            for (int i = 0; i < 8; i++)
            {
                var s = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f);
                yield return b.transform.TransformPoint(b.center + Vector3.Scale(s, b.size));
            }
        }

        // ---------- generate ----------

        [TestCase(PrimitiveType.Cube, ShapeType.Box)]
        [TestCase(PrimitiveType.Sphere, ShapeType.Sphere)]
        [TestCase(PrimitiveType.Capsule, ShapeType.Capsule)]
        public void Generate_Primitive_PicksMatchingType(PrimitiveType primitive, ShapeType expected)
        {
            var a = Author(Primitive(primitive));
            Assert.IsTrue(SmartColliderOperations.Generate(a, GenerateMode.Single));
            Assert.AreEqual(1, a.Parts.Count);
            Assert.AreEqual(expected, a.Parts[0].Type);
            Assert.IsTrue(a.Parts[0].AutoType);
            Assert.Greater(a.Parts[0].Report.Accuracy, 0.9f);
        }

        [Test]
        public void Generate_PerMesh_OnePartPerSource()
        {
            var root = Root();
            Primitive(PrimitiveType.Cube, root.transform).transform.localPosition = new Vector3(-2, 0, 0);
            Primitive(PrimitiveType.Sphere, root.transform).transform.localPosition = new Vector3(2, 0, 0);
            var a = Author(root);

            SmartColliderOperations.Generate(a, GenerateMode.PerMesh);

            Assert.AreEqual(2, a.Parts.Count);
            Assert.AreEqual(ShapeType.Box, a.Parts[0].Type);
            Assert.AreEqual(ShapeType.Sphere, a.Parts[1].Type);
            Assert.AreEqual(-2f, a.Parts[0].Shape.Center.x, 1e-3f);
            Assert.AreNotEqual(a.Parts[0].Color, a.Parts[1].Color);
        }

        [Test]
        public void Generate_NoMeshes_ReturnsFalse()
        {
            var a = Author(Root());
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("no triangle meshes"));
            Assert.IsFalse(SmartColliderOperations.Generate(a, GenerateMode.Single));
        }

        [Test]
        public void SetType_IsInstant_AndSetAutoRestores()
        {
            var a = Author(Primitive(PrimitiveType.Cube));
            SmartColliderOperations.Generate(a, GenerateMode.Single);

            SmartColliderOperations.SetType(a, 0, ShapeType.Sphere);
            Assert.AreEqual(ShapeType.Sphere, a.Parts[0].Type);
            Assert.IsFalse(a.Parts[0].AutoType);
            Assert.AreEqual(Mathf.Sqrt(0.75f), a.Parts[0].Shape.Radius, 1e-3f);

            SmartColliderOperations.SetAuto(a, 0);
            Assert.AreEqual(ShapeType.Box, a.Parts[0].Type);
        }

        [Test]
        public void IsStale_DetectsSourceChange()
        {
            var root = Root();
            Primitive(PrimitiveType.Cube, root.transform);
            var a = Author(root);
            SmartColliderOperations.Generate(a, GenerateMode.Single);
            Assert.IsFalse(SmartColliderOperations.IsStale(a));

            Primitive(PrimitiveType.Sphere, root.transform);
            Assert.IsTrue(SmartColliderOperations.IsStale(a));
        }

        // ---------- bake ----------

        [Test]
        public void Bake_SameObject_AxisAligned_PutsColliderOnObject()
        {
            var go = Primitive(PrimitiveType.Cube);
            var a = Author(go, BakeTarget.SameObject);
            SmartColliderOperations.Generate(a, GenerateMode.Single);

            Assert.AreEqual(1, SmartColliderBaker.Bake(a));
            var box = go.GetComponent<BoxCollider>();
            Assert.IsNotNull(box);
            Assert.AreEqual(Vector3.one, box.size);
            Assert.AreEqual(0, go.transform.childCount);
        }

        [Test]
        public void Bake_Twice_ReplacesInsteadOfDuplicating()
        {
            var go = Primitive(PrimitiveType.Cube);
            var a = Author(go);
            SmartColliderOperations.Generate(a, GenerateMode.Single);

            SmartColliderBaker.Bake(a);
            SmartColliderBaker.Bake(a);

            Assert.AreEqual(1, go.GetComponentsInChildren<Collider>().Length);
            Assert.AreEqual(1, go.transform.childCount);
        }

        [Test]
        public void Bake_RotatedScaledChild_MatchesMeshCorners()
        {
            var root = Root();
            var cube = Primitive(PrimitiveType.Cube, root.transform);
            cube.transform.localPosition = new Vector3(1, 2, 3);
            cube.transform.localRotation = Quaternion.Euler(10, 35, -20);
            cube.transform.localScale = new Vector3(2, 0.5f, 1);
            var a = Author(root, BakeTarget.SameObject);
            SmartColliderOperations.Generate(a, GenerateMode.Single);
            SmartColliderBaker.Bake(a);

            var box = root.GetComponentInChildren<BoxCollider>();
            Assert.IsNotNull(box);
            Assert.AreNotSame(root, box.gameObject, "rotated box needs its own child");

            var meshCorners = cube.GetComponent<MeshFilter>().sharedMesh.vertices
                .Select(v => cube.transform.TransformPoint(v)).ToArray();
            foreach (var c in BoxCorners(box))
                Assert.Less(meshCorners.Min(m => Vector3.Distance(m, c)), 1e-3f);
        }

        [Test]
        public void Bake_Hull_CreatesConvexMeshColliderAsset()
        {
            var go = Root("Octa");
            go.AddComponent<MeshFilter>().sharedMesh = OctahedronMesh();
            _created.Add(go.GetComponent<MeshFilter>().sharedMesh);
            var a = Author(go, BakeTarget.SameObject);
            SmartColliderOperations.Generate(a, GenerateMode.Single);
            Assert.AreEqual(ShapeType.ConvexHull, a.Parts[0].Type);

            SmartColliderBaker.Bake(a);

            var mc = go.GetComponent<MeshCollider>();
            Assert.IsNotNull(mc);
            Assert.IsTrue(mc.convex);
            Assert.AreEqual(6, mc.sharedMesh.vertexCount);
            StringAssert.StartsWith(TestFolder, AssetDatabase.GetAssetPath(mc.sharedMesh));

            // Re-bake updates the same mesh asset in place.
            var mesh = mc.sharedMesh;
            SmartColliderBaker.Bake(a);
            Assert.AreSame(mesh, go.GetComponent<MeshCollider>().sharedMesh);
        }

        [Test]
        public void Bake_AppliesMaterialTriggerAndLayer()
        {
            var go = Primitive(PrimitiveType.Sphere);
            go.layer = 4;
            var a = Author(go);
            var mat = new PhysicsMaterial("Bouncy");
            _created.Add(mat);
            a.material = mat;
            a.isTrigger = true;
            SmartColliderOperations.Generate(a, GenerateMode.Single);
            SmartColliderBaker.Bake(a);

            var col = go.GetComponentInChildren<SphereCollider>();
            Assert.AreSame(mat, col.sharedMaterial);
            Assert.IsTrue(col.isTrigger);
            Assert.AreEqual(4, col.gameObject.layer);
        }

        [Test]
        public void ClearBaked_RemovesEverythingBakeCreated()
        {
            var go = Primitive(PrimitiveType.Capsule);
            var a = Author(go);
            SmartColliderOperations.Generate(a, GenerateMode.Single);
            SmartColliderBaker.Bake(a);
            Assert.IsTrue(a.IsBaked);

            SmartColliderBaker.ClearBaked(a);

            Assert.IsFalse(a.IsBaked);
            Assert.AreEqual(0, go.GetComponentsInChildren<Collider>().Length);
            Assert.AreEqual(0, go.transform.childCount);
        }

        [Test]
        public void Undo_Bake_RemovesColliders()
        {
            var go = Primitive(PrimitiveType.Cube);
            var a = Author(go);
            SmartColliderOperations.Generate(a, GenerateMode.Single);
            Undo.IncrementCurrentGroup();

            SmartColliderBaker.Bake(a);
            Assert.AreEqual(1, go.GetComponentsInChildren<Collider>().Length);

            Undo.PerformUndo();
            Assert.AreEqual(0, go.GetComponentsInChildren<Collider>().Length);
            Assert.AreEqual(0, go.transform.childCount);
        }

        [Test]
        public void Bake_SourceObjects_PutsColliderOnMeshObject_InItsLocalSpace()
        {
            var root = Root();
            var cube = Primitive(PrimitiveType.Cube, root.transform);
            cube.transform.localPosition = new Vector3(1, 2, 3);
            cube.transform.localRotation = Quaternion.Euler(10, 35, -20);
            cube.transform.localScale = new Vector3(2, 0.5f, 1); // non-uniform: must still be a plain BoxCollider
            var sphere = Primitive(PrimitiveType.Sphere, root.transform);
            sphere.transform.localPosition = new Vector3(-3, 0, 0);
            var a = Author(root, BakeTarget.SourceObjects);
            SmartColliderOperations.Generate(a, GenerateMode.PerMesh);

            SmartColliderBaker.Bake(a);

            Assert.IsNull(root.GetComponent<Collider>());
            var box = cube.GetComponent<BoxCollider>();
            Assert.IsNotNull(box, "collider must live on the mesh object so it follows it");
            Assert.Less(Vector3.Distance(Vector3.one, box.size), 1e-3f);
            Assert.Less(box.center.magnitude, 1e-3f);
            Assert.AreEqual(0, cube.transform.childCount);
            var sc = sphere.GetComponent<SphereCollider>();
            Assert.IsNotNull(sc);
            Assert.AreEqual(0.5f, sc.radius, 1e-3f);
        }

        [Test]
        public void Bake_SourceObjects_HullUsesMeshLocalVertices()
        {
            var root = Root();
            var octa = new GameObject("Octa");
            octa.transform.SetParent(root.transform, false);
            octa.transform.localPosition = new Vector3(0, 0, 5);
            octa.transform.localRotation = Quaternion.Euler(0, 45, 0);
            var mesh = OctahedronMesh();
            _created.Add(mesh);
            octa.AddComponent<MeshFilter>().sharedMesh = mesh;
            var a = Author(root, BakeTarget.SourceObjects);
            SmartColliderOperations.Generate(a, GenerateMode.PerMesh);

            SmartColliderBaker.Bake(a);

            var mc = octa.GetComponent<MeshCollider>();
            Assert.IsNotNull(mc);
            foreach (var v in mc.sharedMesh.vertices)
                Assert.Less(mesh.vertices.Min(m => Vector3.Distance(m, v)), 1e-4f);
        }

        [Test]
        public void Bake_SourceObjects_MultiMeshPart_FallsBackToAuthoring()
        {
            var root = Root();
            Primitive(PrimitiveType.Cube, root.transform).transform.localPosition = new Vector3(-1, 0, 0);
            Primitive(PrimitiveType.Cube, root.transform).transform.localPosition = new Vector3(1, 0, 0);
            var a = Author(root, BakeTarget.SourceObjects);
            SmartColliderOperations.Generate(a, GenerateMode.Single);

            SmartColliderBaker.Bake(a);

            var box = root.GetComponent<BoxCollider>();
            Assert.IsNotNull(box);
            Assert.Less(Vector3.Distance(new Vector3(3, 1, 1), box.size), 1e-3f);
        }

        [Test]
        public void RemoveParts_RemovesOnlyChosen_AndIsUndoable()
        {
            var root = Root();
            for (int i = 0; i < 3; i++)
                Primitive(PrimitiveType.Cube, root.transform).transform.localPosition = new Vector3(i * 2, 0, 0);
            var a = Author(root);
            SmartColliderOperations.Generate(a, GenerateMode.PerMesh);
            Undo.IncrementCurrentGroup();

            SmartColliderOperations.RemoveParts(a, new[] { 2, 0, 2 }); // unordered + duplicate

            Assert.AreEqual(1, a.Parts.Count);
            Assert.AreEqual(2f, a.Parts[0].Shape.Center.x, 1e-3f, "middle part kept");

            Undo.PerformUndo();
            Assert.AreEqual(3, a.Parts.Count);
        }

        // ---------- data ----------

        [Test]
        public void TriangleRanges_RoundTrip()
        {
            var tris = new List<int> { 0, 1, 2, 5, 6, 9 };
            var ranges = TriangleRanges.FromSorted(tris);
            CollectionAssert.AreEqual(new[] { 0, 3, 5, 2, 9, 1 }, ranges);
            CollectionAssert.AreEqual(tris, TriangleRanges.Enumerate(ranges).ToList());
            Assert.AreEqual(6, TriangleRanges.Count(ranges));
            Assert.IsFalse(TriangleRanges.IsValidFor(ranges, 9));
        }
    }
}
