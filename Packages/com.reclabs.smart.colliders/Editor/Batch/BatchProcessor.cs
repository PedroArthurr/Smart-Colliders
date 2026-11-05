using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RecLabs.SmartColliders.EditorTools
{
    /// <summary>One batch input: a scene GameObject, a prefab asset or a folder (expanded to its prefabs).</summary>
    [Serializable]
    public sealed class BatchTarget
    {
        public Object Object;
        public string Label => Object == null ? "(missing)" : Object.name;
        public bool IsFolder => Object is DefaultAsset && AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(Object));
        public bool IsPrefab => Object is GameObject go && EditorUtility.IsPersistent(go);
    }

    public sealed class BatchResult
    {
        public string Name;
        public string Path;
        public int Colliders;
        public float Accuracy;
        public double Seconds;
        public string Error;
        public bool Skipped;
        public bool Ok => Error == null && !Skipped;
    }

    /// <summary>
    /// Generates (and optionally bakes) colliders for many objects with one preset, one object per step so the editor
    /// stays responsive and the run can be cancelled. Prefabs are edited through LoadPrefabContents / SaveAsPrefabAsset.
    /// Never runs automatically (no AssetPostprocessor): only on explicit user request.
    /// </summary>
    public sealed class BatchProcessor
    {
        public static BatchProcessor Current { get; private set; }
        public static event Action Changed;

        public readonly List<BatchResult> Results = new List<BatchResult>();
        public int Total => _queue.Count;
        public int Done { get; private set; }
        public bool IsRunning => Current == this;
        public string CurrentName { get; private set; } = "";
        public double ElapsedSeconds => _watch.Elapsed.TotalSeconds;

        readonly List<Object> _queue;
        readonly SmartColliderPreset _preset;
        readonly bool _bake, _keepAuthoring, _skipExisting;
        readonly Stopwatch _watch = new Stopwatch();
        bool _ownsPreset;

        BatchProcessor(List<Object> queue, SmartColliderPreset preset, bool bake, bool keepAuthoring, bool skipExisting)
        {
            _queue = queue;
            _preset = preset;
            _bake = bake;
            _keepAuthoring = keepAuthoring;
            _skipExisting = skipExisting;
        }

        /// <summary>Expands folders into their prefabs; drops duplicates and model/non-GameObject assets.</summary>
        public static List<Object> Expand(IEnumerable<Object> inputs)
        {
            var result = new List<Object>();
            var seen = new HashSet<Object>();
            foreach (var o in inputs)
            {
                if (o == null) continue;
                string path = AssetDatabase.GetAssetPath(o);
                if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
                {
                    foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { path }))
                    {
                        var go = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                        if (go != null && seen.Add(go)) result.Add(go);
                    }
                    continue;
                }
                if (!(o is GameObject g)) continue;
                if (EditorUtility.IsPersistent(g))
                {
                    var type = PrefabUtility.GetPrefabAssetType(g);
                    if (type == PrefabAssetType.Model || type == PrefabAssetType.NotAPrefab) continue; // models are read-only
                }
                if (seen.Add(g)) result.Add(g);
            }
            return result;
        }

        /// <summary>Starts a time-sliced run. <paramref name="preset"/> null = default settings.</summary>
        public static BatchProcessor Start(IEnumerable<Object> inputs, SmartColliderPreset preset, bool bake, bool keepAuthoring, bool skipExisting)
        {
            Current?.Cancel();
            var queue = Expand(inputs);
            var p = new BatchProcessor(queue, preset != null ? preset : SmartColliderPreset.CreateDefault(), bake, keepAuthoring, skipExisting)
            {
                _ownsPreset = preset == null,
            };
            Current = p;
            p._watch.Start();
            EditorApplication.update += p.Tick;
            Changed?.Invoke();
            return p;
        }

        /// <summary>Synchronous run (tests, scripts).</summary>
        public static List<BatchResult> Run(IEnumerable<Object> inputs, SmartColliderPreset preset, bool bake, bool keepAuthoring, bool skipExisting = false)
        {
            var p = new BatchProcessor(Expand(inputs), preset != null ? preset : SmartColliderPreset.CreateDefault(), bake, keepAuthoring, skipExisting)
            {
                _ownsPreset = preset == null,
            };
            p._watch.Start();
            while (p.Done < p._queue.Count) p.ProcessNext();
            p.Cleanup();
            return p.Results;
        }

        public void Cancel()
        {
            if (Current != this) return;
            Finish();
        }

        void Tick()
        {
            // One object per editor update keeps the UI alive; slice by time for tiny objects.
            var sw = Stopwatch.StartNew();
            while (Done < _queue.Count && sw.ElapsedMilliseconds < 50) ProcessNext();
            Changed?.Invoke();
            if (Done >= _queue.Count) Finish();
        }

        void Finish()
        {
            EditorApplication.update -= Tick;
            _watch.Stop();
            Cleanup();
            if (Current == this) Current = null;
            AssetDatabase.SaveAssets();
            Changed?.Invoke();
        }

        void Cleanup()
        {
            if (_ownsPreset && _preset != null) Object.DestroyImmediate(_preset);
            _ownsPreset = false;
        }

        void ProcessNext()
        {
            var o = _queue[Done++];
            var result = new BatchResult { Name = o != null ? o.name : "(missing)", Path = o != null ? AssetDatabase.GetAssetPath(o) : "" };
            CurrentName = result.Name;
            var sw = Stopwatch.StartNew();
            try
            {
                if (o is GameObject go)
                {
                    if (EditorUtility.IsPersistent(go)) ProcessPrefab(go, result);
                    else ProcessSceneObject(go, result);
                }
                else result.Error = "Not a GameObject";
            }
            catch (Exception e)
            {
                result.Error = e.Message;
                UnityEngine.Debug.LogException(e);
            }
            result.Seconds = sw.Elapsed.TotalSeconds;
            Results.Add(result);
        }

        void ProcessSceneObject(GameObject go, BatchResult result)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName($"Smart Colliders Batch: {go.name}");
            Apply(go, result, undo: true);
        }

        void ProcessPrefab(GameObject asset, BatchResult result)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                SmartColliderBaker.BankOwnerOverride = Path.GetFileNameWithoutExtension(path);
                if (Apply(root, result, undo: false))
                    PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                SmartColliderBaker.BankOwnerOverride = null;
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        bool Apply2D(GameObject go, BatchResult result, bool undo)
        {
            if (go.GetComponent<SpriteRenderer>() == null) { result.Skipped = true; result.Error = "No meshes or sprite"; return false; }
            var a = go.GetComponent<SmartCollider2DAuthoring>();
            if (a != null && _skipExisting) { result.Skipped = true; return false; }
            if (a == null) a = undo ? Undo.AddComponent<SmartCollider2DAuthoring>(go) : go.AddComponent<SmartCollider2DAuthoring>();
            a.isTrigger = _preset.isTrigger;
            var r = SmartCollider2D.Compute(a);
            result.Colliders = r.Shapes.Count;
            result.Accuracy = r.Accuracy;
            if (_bake)
            {
                if (_keepAuthoring) SmartCollider2D.Bake(a);
                else SmartCollider2D.BakeAndRemoveAuthoring(a);
            }
            return true;
        }

        /// <returns>True if the object was modified.</returns>
        bool Apply(GameObject go, BatchResult result, bool undo)
        {
            bool hasMeshes = go.GetComponentInChildren<MeshFilter>(true) != null || go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
            if (!hasMeshes) return Apply2D(go, result, undo);

            var a = go.GetComponent<SmartColliderAuthoring>();
            if (a != null && _skipExisting) { result.Skipped = true; return false; }
            if (a == null) a = undo ? Undo.AddComponent<SmartColliderAuthoring>(go) : go.AddComponent<SmartColliderAuthoring>();
            else if (undo) Undo.RecordObject(a, "Apply Preset");

            _preset.ApplyTo(a);
            if (!SmartColliderOperations.Generate(a)) { result.Error = "Generate failed"; return false; }

            var m = UI.MetricsSummary.Of(a, null);
            result.Colliders = a.Parts.Count;
            result.Accuracy = m.Accuracy;

            if (_bake)
            {
                if (_keepAuthoring) SmartColliderBaker.Bake(a);
                else SmartColliderBaker.BakeAndRemoveAuthoring(a);
            }
            return true;
        }
    }
}
