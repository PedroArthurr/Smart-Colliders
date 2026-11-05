using System;
using System.Runtime.InteropServices;
using RecLabs.SmartColliders.Geometry;
using RecLabs.SmartColliders.Geometry.Decomposition;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace RecLabs.SmartColliders.EditorTools
{
    /// <summary>
    /// P/Invoke bridge to the optional native plugin <c>smartcolliders_native</c> (V-HACD v4 / CoACD).
    /// Missing plugin or missing engine ⇒ <see cref="IsAvailable"/> false and callers fall back to the built-in engine.
    /// The native job runs on its own thread; pause blocks it inside its progress callback.
    /// </summary>
    internal sealed class NativeDecomposer : IDecompositionJob
    {
        const string Lib = "smartcolliders_native";
        public const int EngineVHacd = 1, EngineCoAcd = 2;

        [StructLayout(LayoutKind.Sequential)]
        struct Params
        {
            public int engine;
            public int maxHulls;
            public int resolution;          // V-HACD voxel count
            public float minVolumeError;    // V-HACD percent
            public int maxVerticesPerHull;
            public int maxRecursion;        // V-HACD depth
            public float concavity;         // CoACD threshold
            public int mctsIterations;      // CoACD
            public int seed;
            public int shrinkWrap;
        }

        [DllImport(Lib)] static extern int sc_version();
        [DllImport(Lib)] static extern int sc_engine_available(int engine);
        [DllImport(Lib)] static extern IntPtr sc_begin(float[] vertices, int vertexCount, int[] triangles, int triangleCount, ref Params p);
        [DllImport(Lib)] static extern int sc_poll(IntPtr job, out float progress);
        [DllImport(Lib)] static extern void sc_pause(IntPtr job, int paused);
        [DllImport(Lib)] static extern void sc_cancel(IntPtr job);
        [DllImport(Lib)] static extern int sc_hull_count(IntPtr job);
        [DllImport(Lib)] static extern void sc_hull_sizes(IntPtr job, int hull, out int vertexCount, out int triangleCount);
        [DllImport(Lib)] static extern void sc_hull_data(IntPtr job, int hull, float[] vertices, int[] triangles);
        [DllImport(Lib)] static extern void sc_free(IntPtr job);

        static int s_Available = -1;      // -1 unknown, else bit mask of engines
        public static int Version { get; private set; }

        public static bool IsAvailable(int engine)
        {
            if (s_Available < 0)
            {
                s_Available = 0;
                try
                {
                    Version = sc_version();
                    if (sc_engine_available(EngineVHacd) != 0) s_Available |= 1 << EngineVHacd;
                    if (sc_engine_available(EngineCoAcd) != 0) s_Available |= 1 << EngineCoAcd;
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
                catch (BadImageFormatException) { }
            }
            return (s_Available & (1 << engine)) != 0;
        }

        IntPtr _job;
        NativeArray<float3> _vertices;
        NativeArray<int> _indices;
        float _progress;
        readonly DecompositionSettings _settings;
        bool _done;
        System.Collections.Generic.List<DecomposedPiece> _result;

        public float Progress => _progress;
        public string Status { get; private set; } = "Starting";
        public int PieceCount => _result?.Count ?? 0;
        public System.Collections.Generic.IReadOnlyList<DecomposedPiece> Result => _result;
        public bool Failed { get; private set; }

        public NativeDecomposer(int engine, NativeArray<float3> vertices, NativeArray<int> indices, in DecompositionSettings s, in ShapeFitSettings fit)
        {
            _vertices = new NativeArray<float3>(vertices, Allocator.Persistent);
            _indices = new NativeArray<int>(indices, Allocator.Persistent);
            _settings = s;
            var p = new Params
            {
                engine = engine,
                maxHulls = math.max(1, s.MaxColliders),
                maxVerticesPerHull = math.clamp(fit.MaxHullVertices, 8, ConvexHull3D.MaxConvexColliderVertices),
                seed = 1234,
                shrinkWrap = 1,
            };
            switch (s.Quality)
            {
                case DecompositionQuality.Low: p.resolution = 100_000; p.minVolumeError = 2f; p.maxRecursion = 8; p.concavity = 0.1f; p.mctsIterations = 80; break;
                case DecompositionQuality.Medium: p.resolution = 400_000; p.minVolumeError = 1f; p.maxRecursion = 10; p.concavity = 0.06f; p.mctsIterations = 120; break;
                case DecompositionQuality.High: p.resolution = 1_000_000; p.minVolumeError = 0.5f; p.maxRecursion = 12; p.concavity = 0.04f; p.mctsIterations = 150; break;
                default: p.resolution = 2_000_000; p.minVolumeError = 0.2f; p.maxRecursion = 14; p.concavity = 0.02f; p.mctsIterations = 250; break;
            }

            var flat = new float[vertices.Length * 3];
            for (int i = 0; i < vertices.Length; i++)
            {
                flat[i * 3] = vertices[i].x;
                flat[i * 3 + 1] = vertices[i].y;
                flat[i * 3 + 2] = vertices[i].z;
            }
            _job = sc_begin(flat, vertices.Length, indices.ToArray(), indices.Length / 3, ref p);
            Failed = _job == IntPtr.Zero;
            Status = engine == EngineCoAcd ? "CoACD" : "V-HACD";
        }

        public bool Step(double budgetMs)
        {
            if (_done) return true;
            if (_job == IntPtr.Zero) { _done = true; _result = new System.Collections.Generic.List<DecomposedPiece>(); return true; }
            int state = sc_poll(_job, out float progress);
            _progress = math.saturate(progress) * 0.95f;
            if (state == 0) return false;
            if (state < 0) Failed = true;
            else _result = ReadHulls();
            _result ??= new System.Collections.Generic.List<DecomposedPiece>();
            _progress = 1f;
            _done = true;
            return true;
        }

        public void SetPaused(bool paused)
        {
            if (_job != IntPtr.Zero) sc_pause(_job, paused ? 1 : 0);
        }

        System.Collections.Generic.List<DecomposedPiece> ReadHulls()
        {
            int rawCount = sc_hull_count(_job);
            var raw = new System.Collections.Generic.List<float3[]>(rawCount);
            for (int h = 0; h < rawCount; h++)
            {
                sc_hull_sizes(_job, h, out int vc, out int tc);
                var v = new float[vc * 3];
                var t = new int[tc * 3];
                sc_hull_data(_job, h, v, t);
                var pts = new float3[vc];
                for (int i = 0; i < vc; i++) pts[i] = new float3(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);
                if (vc >= 4) raw.Add(pts);
            }

            // V-HACD tends to over-split: apply the same Auto Merge / Max Colliders policy as the built-in engine.
            var groups = HullMerge.Merge(raw, _settings.AutoMerge, _settings.MergeTolerance, math.max(1, _settings.MaxColliders));

            var pieces = new System.Collections.Generic.List<DecomposedPiece>(groups.Count);
            var planes = new NativeList<float4>(Allocator.TempJob);
            var ranges = new NativeArray<int2>(groups.Count, Allocator.TempJob);
            for (int g = 0; g < groups.Count; g++)
            {
                var all = new System.Collections.Generic.List<float3>();
                foreach (int h in groups[g]) all.AddRange(raw[h]);
                var nv = new NativeArray<float3>(all.ToArray(), Allocator.Temp);
                var hv = new NativeList<float3>(Allocator.Temp);
                var ht = new NativeList<int>(Allocator.Temp);
                var hp = new NativeList<float4>(Allocator.Temp);
                if (ConvexHull3D.Build(nv, 0, hv, ht))
                {
                    pieces.Add(new DecomposedPiece { Points = hv.AsArray().ToArray() });
                    ConvexHull3D.ComputePlanes(hv.AsArray(), ht.AsArray(), hp);
                }
                else
                {
                    pieces.Add(new DecomposedPiece { Points = all.ToArray() });
                }
                ranges[g] = new int2(planes.Length, hp.Length);
                planes.AddRange(hp.AsArray());
                nv.Dispose(); hv.Dispose(); ht.Dispose(); hp.Dispose();
            }

            var owner = new NativeArray<int>(_indices.Length / 3, Allocator.TempJob);
            if (pieces.Count > 0)
            {
                new PieceAssignmentJob
                {
                    Vertices = _vertices, Indices = _indices, Planes = planes.AsArray(), PieceRanges = ranges, TrianglePiece = owner,
                }.Run();
                for (int t = 0; t < owner.Length; t++) pieces[owner[t]].Triangles.Add(t);
            }
            owner.Dispose();
            planes.Dispose();
            ranges.Dispose();
            return pieces;
        }

        public void Dispose()
        {
            if (_job != IntPtr.Zero)
            {
                if (!_done) sc_cancel(_job);
                sc_free(_job);
                _job = IntPtr.Zero;
            }
            // Non-readonly fields: Dispose must act on the field itself, not a defensive copy, so IsCreated turns false.
            if (_vertices.IsCreated) _vertices.Dispose();
            if (_indices.IsCreated) _indices.Dispose();
            _vertices = default;
            _indices = default;
        }
    }
}
