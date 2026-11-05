using System;
using System.Collections.Generic;
using System.Diagnostics;
using RecLabs.SmartColliders.Geometry.Decomposition;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace RecLabs.SmartColliders.EditorTools
{
    /// <summary>A decomposition in progress (built-in or native), advanced in time slices.</summary>
    internal interface IDecompositionJob : IDisposable
    {
        /// <returns>True when finished (successfully or not).</returns>
        bool Step(double budgetMs);
        void SetPaused(bool paused);
        float Progress { get; }
        string Status { get; }
        int PieceCount { get; }
        IReadOnlyList<DecomposedPiece> Result { get; }
        bool Failed { get; }
    }

    internal sealed class BuiltInDecompositionJob : IDecompositionJob
    {
        readonly VoxelDecomposer _d;
        public BuiltInDecompositionJob(SourceGeometry geo, in DecompositionSettings s) =>
            _d = new VoxelDecomposer(geo.Vertices, geo.Indices, VoxelDecomposerParams.From(s));
        public bool Step(double budgetMs) => _d.Step(budgetMs);
        public void SetPaused(bool paused) { }
        public float Progress => _d.Progress;
        public string Status => _d.Status;
        public int PieceCount => _d.PieceCount;
        public IReadOnlyList<DecomposedPiece> Result => _d.Result;
        public bool Failed => false;
        public void Dispose() => _d.Dispose();
    }

    /// <summary>
    /// Runs a decomposition for one authoring object: synchronously (<see cref="RunSync"/>) or in the background
    /// (<see cref="Start"/>) with pause / resume / cancel and live progress. Results replace the parts in one Undo step.
    /// </summary>
    public sealed class DecompositionRunner
    {
        public static DecompositionRunner Current { get; private set; }
        public static event Action Changed;

        public SmartColliderAuthoring Target { get; }
        public bool IsPaused { get; private set; }
        public float Progress => _job?.Progress ?? 0f;
        public string Status => _job?.Status ?? "";
        public int PieceCount => _job?.PieceCount ?? 0;
        public string EngineName { get; }
        public double ElapsedSeconds => _watch.Elapsed.TotalSeconds;

        readonly IDecompositionJob _job;
        readonly Stopwatch _watch = new Stopwatch();
        readonly int _budgetMs;
        double _lastNotify;

        DecompositionRunner(SmartColliderAuthoring target, IDecompositionJob job, string engine, int budgetMs)
        {
            Target = target;
            _job = job;
            EngineName = engine;
            _budgetMs = Mathf.Clamp(budgetMs, 2, 500);
        }

        // ---------- engine selection ----------

        /// <summary>Engine actually used for these settings (falls back to Built-in when the native plugin lacks it).</summary>
        public static DecompositionEngine Resolve(in DecompositionSettings s)
        {
            var wanted = s.Engine;
            if (wanted == DecompositionEngine.Auto)
                wanted = s.Quality <= DecompositionQuality.Medium ? DecompositionEngine.VHacd : DecompositionEngine.CoAcd;
            if (wanted == DecompositionEngine.VHacd && !NativeDecomposer.IsAvailable(NativeDecomposer.EngineVHacd))
                return DecompositionEngine.BuiltIn;
            // No CoACD: the built-in engine at High/Ultra settings (concavity cuts) is the closer substitute.
            if (wanted == DecompositionEngine.CoAcd && !NativeDecomposer.IsAvailable(NativeDecomposer.EngineCoAcd))
                return DecompositionEngine.BuiltIn;
            return wanted;
        }

        public static string DisplayName(DecompositionEngine e) => e switch
        {
            DecompositionEngine.VHacd => "V-HACD",
            DecompositionEngine.CoAcd => "CoACD",
            DecompositionEngine.BuiltIn => "Built-in",
            _ => "Auto",
        };

        static IDecompositionJob CreateJob(SmartColliderAuthoring a, SourceGeometry geo, out DecompositionEngine engine)
        {
            engine = Resolve(a.decomposition);
            if (engine == DecompositionEngine.VHacd || engine == DecompositionEngine.CoAcd)
            {
                var native = new NativeDecomposer(engine == DecompositionEngine.CoAcd ? NativeDecomposer.EngineCoAcd : NativeDecomposer.EngineVHacd,
                    geo.Vertices, geo.Indices, a.decomposition, a.fitSettings);
                if (!native.Failed) return native;
                native.Dispose();
                engine = DecompositionEngine.BuiltIn;
            }
            return new BuiltInDecompositionJob(geo, a.decomposition);
        }

        // ---------- sync ----------

        /// <summary>Blocking decomposition → parts (not yet applied).</summary>
        internal static List<SmartColliderPart> RunSync(SmartColliderAuthoring a, SourceGeometry geo)
        {
            using var job = CreateJob(a, geo, out _);
            while (!job.Step(1000)) { }
            return SmartColliderOperations.PartsFromPieces(a, job.Result ?? Array.Empty<DecomposedPiece>());
        }

        // ---------- async ----------

        /// <summary>Starts a background decomposition (cancels any running one). Returns false if there is nothing to decompose.</summary>
        public static bool Start(SmartColliderAuthoring a)
        {
            Current?.Cancel();
            var geo = SourceGeometry.Gather(a);
            try
            {
                if (geo.IsEmpty)
                {
                    Debug.LogWarning($"[Smart Colliders] '{a.name}' has no triangle meshes to decompose.", a);
                    return false;
                }
                var job = CreateJob(a, geo, out var engine);
                Current = new DecompositionRunner(a, job, DisplayName(engine), a.decomposition.YieldMilliseconds);
            }
            finally
            {
                geo.Dispose();
            }
            Current._watch.Start();
            EditorApplication.update += Current.Tick;
            Changed?.Invoke();
            return true;
        }

        public void Pause()
        {
            if (IsPaused || Current != this) return;
            IsPaused = true;
            _watch.Stop();
            _job.SetPaused(true);
            Changed?.Invoke();
        }

        public void Resume()
        {
            if (!IsPaused || Current != this) return;
            IsPaused = false;
            _watch.Start();
            _job.SetPaused(false);
            Changed?.Invoke();
        }

        public void Cancel()
        {
            if (Current != this) return;
            Finish(apply: false);
        }

        void Tick()
        {
            if (Target == null) { Finish(false); return; }
            if (IsPaused) return;
            bool done;
            try
            {
                done = _job.Step(_budgetMs);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Finish(false);
                return;
            }
            if (done)
            {
                Finish(apply: !_job.Failed);
                return;
            }
            if (_watch.Elapsed.TotalSeconds - _lastNotify > 0.1)
            {
                _lastNotify = _watch.Elapsed.TotalSeconds;
                Changed?.Invoke();
            }
        }

        bool _finished;

        void Finish(bool apply)
        {
            EditorApplication.update -= Tick;
            if (_finished) return;
            _finished = true;
            _watch.Stop();
            try
            {
                if (apply && Target != null && _job.Result != null && _job.Result.Count > 0)
                {
                    using var geo = SourceGeometry.Gather(Target);
                    var parts = SmartColliderOperations.PartsFromPieces(Target, _job.Result);
                    Undo.IncrementCurrentGroup();
                    SmartColliderOperations.ApplyParts(Target, geo, parts, "Decompose Colliders");
                    Debug.Log($"[Smart Colliders] {EngineName}: {Target.parts.Count} colliders in {ElapsedSeconds:F1}s.", Target);
                }
                else if (apply && _job.Failed)
                {
                    Debug.LogWarning($"[Smart Colliders] Decomposition failed ({EngineName}).", Target);
                }
            }
            finally
            {
                _job.Dispose();
                if (Current == this) Current = null;
                Changed?.Invoke();
                SceneView.RepaintAll();
            }
        }

        [InitializeOnLoadMethod]
        static void CancelOnReload() =>
            AssemblyReloadEvents.beforeAssemblyReload += () => Current?.Cancel();
    }
}
