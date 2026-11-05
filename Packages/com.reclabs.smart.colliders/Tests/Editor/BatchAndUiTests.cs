using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RecLabs.SmartColliders.EditorTools;
using RecLabs.SmartColliders.Geometry;
using RecLabs.SmartColliders.UI;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RecLabs.SmartColliders.Tests
{
    /// <summary>Phase 4/7: batch processing (prefabs, folders, scene objects, presets) and UI helpers.</summary>
    public class BatchAndUiTests
    {
        const string TestFolder = "Assets/__SmartCollidersBatchTests";
        const string GeneratedFolder = TestFolder + "/Generated";
        string _previousFolder;
        readonly List<Object> _created = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            _previousFolder = SmartCollidersProjectSettings.instance.generatedFolder;
            SmartCollidersProjectSettings.instance.generatedFolder = GeneratedFolder;
            if (!AssetDatabase.IsValidFolder(TestFolder)) AssetDatabase.CreateFolder("Assets", "__SmartCollidersBatchTests");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created) if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
            SmartCollidersProjectSettings.instance.generatedFolder = _previousFolder;
            AssetDatabase.DeleteAsset(TestFolder);
        }

        static GameObject TwoCubes(string name)
        {
            var root = new GameObject(name);
            for (int i = 0; i < 2; i++)
            {
                var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.DestroyImmediate(c.GetComponent<Collider>());
                c.transform.SetParent(root.transform, false);
                c.transform.localPosition = new Vector3(i * 3f, 0, 0);
            }
            return root;
        }

        GameObject Prefab(string name)
        {
            var go = TwoCubes(name);
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, $"{TestFolder}/{name}.prefab");
            Object.DestroyImmediate(go);
            return prefab;
        }

        SmartColliderPreset Preset(BakeTarget target)
        {
            var p = ScriptableObject.CreateInstance<SmartColliderPreset>();
            p.mode = GenerateMode.PerMesh;
            p.bakeTarget = target;
            p.isTrigger = true;
            _created.Add(p);
            return p;
        }

        [Test]
        public void Batch_Folder_BakesPrefabs_AndRemovesAuthoring()
        {
            Prefab("BatchA");
            Prefab("BatchB");
            var folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(TestFolder);

            var results = BatchProcessor.Run(new Object[] { folder }, Preset(BakeTarget.SourceObjects), bake: true, keepAuthoring: false);

            Assert.AreEqual(2, results.Count);
            Assert.IsTrue(results.All(r => r.Ok), string.Join("; ", results.Select(r => r.Error)));
            Assert.IsTrue(results.All(r => r.Colliders == 2));
            foreach (var name in new[] { "BatchA", "BatchB" })
            {
                var root = PrefabUtility.LoadPrefabContents($"{TestFolder}/{name}.prefab");
                try
                {
                    Assert.IsNull(root.GetComponent<SmartColliderAuthoring>(), "authoring removed");
                    var colliders = root.GetComponentsInChildren<Collider>(true);
                    Assert.AreEqual(2, colliders.Length);
                    Assert.IsTrue(colliders.All(c => c.isTrigger), "preset applied");
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
        }

        [Test]
        public void Batch_KeepAuthoring_And_SkipExisting()
        {
            var prefab = Prefab("BatchKeep");
            var first = BatchProcessor.Run(new Object[] { prefab }, Preset(BakeTarget.ChildContainer), bake: true, keepAuthoring: true);
            Assert.IsTrue(first[0].Ok);

            var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(prefab));
            try
            {
                var a = root.GetComponent<SmartColliderAuthoring>();
                Assert.IsNotNull(a);
                Assert.AreEqual(2, a.Parts.Count);
                Assert.IsTrue(a.IsBaked);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }

            var second = BatchProcessor.Run(new Object[] { prefab }, null, bake: true, keepAuthoring: true, skipExisting: true);
            Assert.IsTrue(second[0].Skipped);
        }

        [Test]
        public void Batch_SceneObject_IsUndoable()
        {
            var go = TwoCubes("SceneBatch");
            _created.Add(go);
            Undo.IncrementCurrentGroup();
            var results = BatchProcessor.Run(new Object[] { go }, null, bake: false, keepAuthoring: true);
            Assert.IsTrue(results[0].Ok);
            Assert.IsNotNull(go.GetComponent<SmartColliderAuthoring>());
            Undo.PerformUndo();
            Assert.IsNull(go.GetComponent<SmartColliderAuthoring>());
        }

        [Test]
        public void Expand_DedupesAndSkipsNonGameObjects()
        {
            var prefab = Prefab("BatchDup");
            var folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(TestFolder);
            var list = BatchProcessor.Expand(new Object[] { prefab, folder, prefab, null, ScriptableObject.CreateInstance<SmartColliderPreset>() });
            Assert.AreEqual(1, list.Count);
        }

        [Test]
        public void Preset_ApplyTo_CopiesEverything()
        {
            var go = new GameObject("PresetTarget");
            _created.Add(go);
            var a = go.AddComponent<SmartColliderAuthoring>();
            var p = Preset(BakeTarget.ChildContainer);
            p.mode = GenerateMode.Decompose;
            p.decomposition.MaxColliders = 7;
            p.fit.MaxHullVertices = 32;
            p.containerName = "Phys";
            p.ApplyTo(a);
            Assert.AreEqual(GenerateMode.Decompose, a.generateMode);
            Assert.AreEqual(7, a.decomposition.MaxColliders);
            Assert.AreEqual(32, a.fitSettings.MaxHullVertices);
            Assert.AreEqual(BakeTarget.ChildContainer, a.bakeTarget);
            Assert.AreEqual("Phys", a.containerName);
            Assert.IsTrue(a.isTrigger);
        }

        [Test]
        public void MetricsSummary_AggregatesSelectionOrAll()
        {
            var go = TwoCubes("Metrics");
            _created.Add(go);
            var a = go.AddComponent<SmartColliderAuthoring>();
            SmartColliderOperations.Generate(a, GenerateMode.PerMesh);
            var all = MetricsSummary.Of(a, null);
            Assert.AreEqual(2, all.Parts);
            Assert.AreEqual(2, all.Box);
            Assert.Greater(all.Accuracy, 0.95f);
            Assert.Greater(a.sourceVolume, 1.99f, "two closed unit cubes");
            Assert.Less(a.sourceVolume, 2.01f);
            Assert.AreEqual(1f, all.Fill, 0.01f, "boxes fill exactly");
            var one = MetricsSummary.Of(a, new[] { 1 });
            Assert.AreEqual(1, one.Parts);
            Assert.AreEqual("100%", one.AccuracyText);
            Assert.AreEqual(0f, one.Fill, "fill only for the whole object");
        }

        [Test]
        public void IconRaster_RendersEveryIcon()
        {
            foreach (HudIcon icon in Enum.GetValues(typeof(HudIcon)))
            {
                var r = IconRaster.Draw(icon, 32, Color.white);
                float coverage = r.Pixels.Sum(p => p.a);
                Assert.Greater(coverage, 8f, $"{icon} drew nothing");
                Assert.Less(coverage, 32 * 32 * 0.8f, $"{icon} filled the whole tile");
            }
        }
    }
}
