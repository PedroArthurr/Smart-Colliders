using System.Collections.Generic;
using NUnit.Framework;
using RecLabs.SmartColliders.Geometry;
using RecLabs.SmartColliders.EditorTools;
using RecLabs.SmartColliders.UI;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace RecLabs.SmartColliders.Tests
{
    public class RefineTests
    {
        readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created) if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        SmartColliderAuthoring CubeAuthoring()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            _created.Add(go);
            var a = go.AddComponent<SmartColliderAuthoring>();
            a.fitSettings = ShapeFitSettings.Default;
            SmartColliderOperations.Generate(a, GenerateMode.Single);
            return a;
        }

        static readonly int[] First = { 0 };

        // ---------- geometry ----------

        [Test]
        public void Raycast_Primitives_HitAndMiss()
        {
            var box = ColliderShape.Box(new float3(0, 0, 5), quaternion.RotateY(0.5f), new float3(1, 1, 1));
            Assert.IsTrue(box.Raycast(float3.zero, new float3(0, 0, 1), out float tb));
            Assert.Greater(tb, 3.5f);
            Assert.Less(tb, 4.1f);
            Assert.IsFalse(box.Raycast(float3.zero, new float3(0, 0, -1), out _));

            var sphere = ColliderShape.Sphere(new float3(0, 0, 5), 1f);
            Assert.IsTrue(sphere.Raycast(float3.zero, new float3(0, 0, 1), out float ts));
            Assert.AreEqual(4f, ts, 1e-4f);

            var capsule = ColliderShape.Capsule(new float3(0, 0, 5), quaternion.identity, 0.5f, 2f);
            Assert.IsTrue(capsule.Raycast(new float3(0, 2.2f, 0), new float3(0, 0, 1), out float tc));
            Assert.AreEqual(5f - math.sqrt(0.21f), tc, 1e-3f);
            Assert.IsFalse(capsule.Raycast(new float3(0, 3f, 0), new float3(0, 0, 1), out _));
        }

        [Test]
        public void Raycast_Hull_HitsFrontFace()
        {
            var pts = new NativeArray<float3>(8, Allocator.Temp);
            for (int i = 0; i < 8; i++)
                pts[i] = new float3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? 4 : 6);
            var v = new NativeList<float3>(Allocator.Temp);
            var t = new NativeList<int>(Allocator.Temp);
            var planes = new NativeList<float4>(Allocator.Temp);
            ConvexHull3D.Build(pts, 0, v, t);
            ConvexHull3D.ComputePlanes(v.AsArray(), t.AsArray(), planes);

            Assert.IsTrue(ConvexHull3D.Raycast(planes.AsArray(), float3.zero, new float3(0, 0, 1), out float hit));
            Assert.AreEqual(4f, hit, 1e-4f);
            Assert.IsFalse(ConvexHull3D.Raycast(planes.AsArray(), new float3(3, 0, 0), new float3(0, 0, 1), out _));

            pts.Dispose(); v.Dispose(); t.Dispose(); planes.Dispose();
        }

        [Test]
        public void FitOriented_KeepsRotation()
        {
            var rot = quaternion.Euler(0.2f, 0.9f, -0.4f);
            var source = ColliderShape.Box(new float3(1, 2, 3), rot, new float3(0.5f, 1f, 2f));
            var pts = new NativeList<float3>(Allocator.Temp);
            source.SampleSurface(pts, 4);

            var box = PrimitiveFitter.FitBoxOriented(pts.AsArray(), rot);
            Assert.AreEqual(rot.value, box.Rotation.value);
            Assert.Less(math.distance(source.HalfExtents, box.HalfExtents), 1e-4f);

            var capsule = PrimitiveFitter.FitCapsuleOriented(pts.AsArray(), rot);
            Assert.AreEqual(rot.value, capsule.Rotation.value);
            for (int i = 0; i < pts.Length; i++)
                Assert.LessOrEqual(capsule.SignedDistance(pts[i]), 1e-3f);

            pts.Dispose();
        }

        [Test]
        public void EvaluateEdited_GridIoU_TracksOverlap()
        {
            var refBox = ColliderShape.Box(float3.zero, quaternion.identity, new float3(1));
            var pts = new NativeList<float3>(Allocator.Temp);
            refBox.SampleSurface(pts, 2);
            var v = new NativeList<float3>(Allocator.Temp);
            var t = new NativeList<int>(Allocator.Temp);
            var planes = new NativeList<float4>(Allocator.Temp);
            ConvexHull3D.Build(pts.AsArray(), 0, v, t);
            ConvexHull3D.ComputePlanes(v.AsArray(), t.AsArray(), planes);

            var same = FitMetrics.EvaluateEdited(refBox, default, default, v.AsArray(), planes.AsArray(), 8f);
            Assert.AreEqual(1f, same.Accuracy, 1e-3f);
            Assert.AreEqual(0f, same.WorstFit, 1e-4f);

            var shifted = refBox;
            shifted.Center = new float3(1, 0, 0); // half overlap → IoU 1/3
            var r = FitMetrics.EvaluateEdited(shifted, default, default, v.AsArray(), planes.AsArray(), 8f);
            Assert.AreEqual(1f / 3f, r.Accuracy, 0.02f);
            Assert.AreEqual(1f, r.WorstFit, 1e-3f);

            pts.Dispose(); v.Dispose(); t.Dispose(); planes.Dispose();
        }

        // ---------- refine operations ----------

        [Test]
        public void Translate_ThenFitToMesh_RestoresTightFit()
        {
            var a = CubeAuthoring();
            var original = a.Parts[0].Shape;

            SmartColliderRefine.Translate(a.parts[0], new float3(0.3f, 0, 0));
            SmartColliderRefine.Reevaluate(a, First);
            Assert.Less(a.Parts[0].Report.Accuracy, 0.8f);
            Assert.IsFalse(a.Parts[0].AutoType);

            SmartColliderRefine.FitToMesh(a, First);
            Assert.Less(math.distance(original.Center, a.Parts[0].Shape.Center), 1e-4f);
            Assert.Greater(a.Parts[0].Report.Accuracy, 0.999f);
        }

        [Test]
        public void Rotate_ThenAlignToAxes_ReturnsIdentity()
        {
            var a = CubeAuthoring();
            SmartColliderRefine.Rotate(a.parts[0], float3.zero, quaternion.RotateY(0.6f));
            Assert.AreNotEqual(quaternion.identity.value, a.Parts[0].Shape.Rotation.value);

            SmartColliderRefine.AlignToAxes(a, First);
            Assert.AreEqual(quaternion.identity.value, a.Parts[0].Shape.Rotation.value);
            Assert.Less(math.distance(new float3(0.5f), a.Parts[0].Shape.HalfExtents), 1e-4f);
        }

        [Test]
        public void Translate_Hull_MovesVertices()
        {
            var a = CubeAuthoring();
            SmartColliderOperations.SetType(a, 0, ShapeType.ConvexHull);
            var before = (Vector3[])a.Parts[0].HullVertices.Clone();

            SmartColliderRefine.Translate(a.parts[0], new float3(0, 2, 0));

            for (int i = 0; i < before.Length; i++)
                Assert.AreEqual(before[i].y + 2f, a.Parts[0].HullVertices[i].y, 1e-5f);
            Assert.AreEqual(2f, a.Parts[0].Shape.Center.y, 1e-4f);
        }

        [Test]
        public void ResetParts_DiscardsEditsAndRestoresAuto()
        {
            var a = CubeAuthoring();
            SmartColliderOperations.SetType(a, 0, ShapeType.Sphere);
            SmartColliderRefine.Translate(a.parts[0], new float3(1, 0, 0));

            SmartColliderRefine.ResetParts(a, First);

            Assert.IsTrue(a.Parts[0].AutoType);
            Assert.AreEqual(ShapeType.Box, a.Parts[0].Type);
            Assert.Less(math.length(a.Parts[0].Shape.Center), 1e-4f);
        }

        [Test]
        public void FitToMesh_IsUndoable()
        {
            var a = CubeAuthoring();
            SmartColliderRefine.Translate(a.parts[0], new float3(0.5f, 0, 0));
            Undo.IncrementCurrentGroup();

            SmartColliderRefine.FitToMesh(a, First);
            Assert.AreEqual(0f, a.Parts[0].Shape.Center.x, 1e-4f);

            Undo.PerformUndo();
            Assert.AreEqual(0.5f, a.Parts[0].Shape.Center.x, 1e-4f);
        }

        [Test]
        public void Picker_FindsNearestPart()
        {
            var root = new GameObject("Root");
            _created.Add(root);
            for (int i = 0; i < 2; i++)
            {
                var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.DestroyImmediate(c.GetComponent<Collider>());
                c.transform.SetParent(root.transform, false);
                c.transform.localPosition = new Vector3(0, 0, 3 + i * 3);
            }
            var a = root.AddComponent<SmartColliderAuthoring>();
            a.fitSettings = ShapeFitSettings.Default;
            SmartColliderOperations.Generate(a, GenerateMode.PerMesh);

            Assert.AreEqual(0, PartPicker.Pick(a, new Ray(Vector3.zero, Vector3.forward)));
            Assert.AreEqual(1, PartPicker.Pick(a, new Ray(new Vector3(0, 0, 10), Vector3.back)));
            Assert.AreEqual(-1, PartPicker.Pick(a, new Ray(new Vector3(5, 0, 0), Vector3.forward)));
        }

        [Test]
        public void PartSelection_SanitizesAfterRegenerate()
        {
            var root = new GameObject("Root");
            _created.Add(root);
            for (int i = 0; i < 3; i++)
            {
                var c = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.DestroyImmediate(c.GetComponent<Collider>());
                c.transform.SetParent(root.transform, false);
                c.transform.localPosition = new Vector3(i * 2, 0, 0);
            }
            var a = root.AddComponent<SmartColliderAuthoring>();
            a.fitSettings = ShapeFitSettings.Default;
            SmartColliderOperations.Generate(a, GenerateMode.PerMesh);
            PartSelection.Set(a, 2);

            SmartColliderOperations.Generate(a, GenerateMode.Single);

            Assert.AreEqual(0, PartSelection.Get(a).Count);
        }
    }
}
