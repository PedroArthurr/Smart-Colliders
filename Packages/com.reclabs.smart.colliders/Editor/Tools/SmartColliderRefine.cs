using System;
using System.Collections.Generic;
using RecLabs.SmartColliders.Geometry;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace RecLabs.SmartColliders.EditorTools
{
    /// <summary>
    /// Refine operations on existing parts (Scene view editing). Public ops record their own Undo;
    /// the low-level Translate/Rotate/SetShape are called from handles that already recorded Undo.
    /// Any manual change turns the part's type to manual (autoType = false).
    /// </summary>
    public static class SmartColliderRefine
    {
        // ---------- one-shot actions ----------

        /// <summary>Re-tightens the current shape around its triangles, keeping the user's orientation.</summary>
        public static void FitToMesh(SmartColliderAuthoring a, IReadOnlyList<int> parts) =>
            ForEachReference(a, parts, "Fit Collider to Mesh", (part, r) =>
            {
                var shape = part.Shape;
                switch (part.type)
                {
                    case ShapeType.Box:
                        shape = PrimitiveFitter.FitBoxOriented(r.Vertices, shape.Rotation, a.fitSettings.MinThickness);
                        break;
                    case ShapeType.Capsule:
                        shape = PrimitiveFitter.FitCapsuleOriented(r.Vertices, shape.Rotation);
                        break;
                    case ShapeType.Sphere:
                        shape = PrimitiveFitter.FitSphere(r.Vertices);
                        break;
                    default:
                        RebuildHull(part, r, a.fitSettings.MaxHullVertices);
                        return;
                }
                part.candidates[(int)part.type] = shape;
                part.reports[(int)part.type] = r.EvaluatePrimitive(shape);
            });

        /// <summary>Snaps box/capsule orientation to the authoring object's local axes, then re-fits.</summary>
        public static void AlignToAxes(SmartColliderAuthoring a, IReadOnlyList<int> parts) =>
            ForEachReference(a, parts, "Align Collider to Axes", (part, r) =>
            {
                ColliderShape shape;
                switch (part.type)
                {
                    case ShapeType.Box:
                        shape = PrimitiveFitter.FitBoxOriented(r.Vertices, quaternion.identity, a.fitSettings.MinThickness);
                        break;
                    case ShapeType.Capsule:
                        shape = default;
                        float best = float.MaxValue;
                        foreach (var rot in AxisRotations())
                        {
                            var c = PrimitiveFitter.FitCapsuleOriented(r.Vertices, rot);
                            if (c.Volume() < best) { best = c.Volume(); shape = c; }
                        }
                        break;
                    case ShapeType.ConvexHull:
                        RebuildHull(part, r, a.fitSettings.MaxHullVertices);
                        return;
                    default:
                        return;
                }
                part.candidates[(int)part.type] = shape;
                part.reports[(int)part.type] = r.EvaluatePrimitive(shape);
            });

        /// <summary>
        /// Rotates each part around its own center by <paramref name="degrees"/> about an axis (0=X,1=Y,2=Z).
        /// Local = the part's own axis; otherwise the world axis. Fit metrics are re-evaluated.
        /// </summary>
        public static void RotateParts(SmartColliderAuthoring a, IReadOnlyList<int> parts, int axis, float degrees, bool localSpace)
        {
            if (parts.Count == 0) return;
            Undo.RecordObject(a, $"Rotate Collider {"XYZ"[axis]} {degrees:+0;-0}°");
            float3 unit = axis == 0 ? new float3(1, 0, 0) : axis == 1 ? new float3(0, 1, 0) : new float3(0, 0, 1);
            foreach (int i in parts)
            {
                if (i < 0 || i >= a.parts.Count) continue;
                var part = a.parts[i];
                var s = part.Shape;
                float3 dir = localSpace
                    ? math.rotate(s.Rotation, unit)
                    : math.normalizesafe((float3)a.transform.InverseTransformDirection(unit), unit);
                Rotate(part, s.Center, quaternion.AxisAngle(dir, math.radians(degrees)));
            }
            Reevaluate(a, parts, recordUndo: false);
        }

        /// <summary>Sets the absolute rotation (Euler degrees, authoring-local) of each part, around its own center.</summary>
        public static void SetRotation(SmartColliderAuthoring a, IReadOnlyList<int> parts, Vector3 euler)
        {
            if (parts.Count == 0) return;
            Undo.RecordObject(a, "Set Collider Rotation");
            quaternion target = Quaternion.Euler(euler);
            foreach (int i in parts)
            {
                if (i < 0 || i >= a.parts.Count) continue;
                var part = a.parts[i];
                var s = part.Shape;
                quaternion delta = math.mul(target, math.conjugate(s.Rotation));
                Rotate(part, s.Center, delta);
                // Exact target (avoid drift from the delta round-trip).
                part.candidates[(int)part.type].Rotation = target;
            }
            Reevaluate(a, parts, recordUndo: false);
        }

        /// <summary>Discards manual edits: full re-fit and back to automatic type.</summary>
        public static void ResetParts(SmartColliderAuthoring a, IReadOnlyList<int> parts)
        {
            using var geo = SourceGeometry.Gather(a);
            Undo.RecordObject(a, "Reset Collider");
            foreach (int i in parts)
            {
                var part = a.parts[i];
                if (!TriangleRanges.IsValidFor(part.triangleRanges, geo.TriangleCount)) continue;
                part.autoType = true;
                SmartColliderOperations.Fit(part, geo, a.fitSettings);
            }
        }

        /// <summary>Recomputes reports of edited parts (grid IoU, edits may not enclose the mesh).</summary>
        /// <param name="recordUndo">False when folding into an edit that already recorded Undo (handle drags).</param>
        public static void Reevaluate(SmartColliderAuthoring a, IReadOnlyList<int> parts, bool recordUndo = true)
        {
            using var geo = SourceGeometry.Gather(a);
            if (recordUndo) Undo.RecordObject(a, "Evaluate Collider");
            else EditorUtility.SetDirty(a);
            foreach (int i in parts)
            {
                if (i < 0 || i >= a.parts.Count) continue;
                var part = a.parts[i];
                if (!TriangleRanges.IsValidFor(part.triangleRanges, geo.TriangleCount)) continue;
                using var r = PartReference.Build(geo, part);
                if (!r.Valid) continue;

                if (part.type == ShapeType.ConvexHull)
                {
                    var hv = new NativeArray<Vector3>(part.hullVertices, Allocator.Temp).Reinterpret<float3>();
                    var ht = new NativeArray<int>(part.hullTriangles, Allocator.Temp);
                    part.reports[(int)ShapeType.ConvexHull] = FitMetrics.EvaluateEdited(part.Shape, hv, ht, r.Vertices, r.Planes.AsArray(), r.Volume);
                    hv.Dispose();
                    ht.Dispose();
                }
                else
                {
                    part.reports[(int)part.type] = FitMetrics.EvaluateEdited(part.Shape, default, default, r.Vertices, r.Planes.AsArray(), r.Volume);
                }
            }
        }

        // ---------- low-level (caller records Undo) ----------

        internal static void Translate(SmartColliderPart part, float3 delta)
        {
            ref var s = ref part.candidates[(int)part.type];
            s.Center += delta;
            if (part.type == ShapeType.ConvexHull)
                for (int v = 0; v < part.hullVertices.Length; v++)
                    part.hullVertices[v] += (Vector3)delta;
            part.autoType = false;
        }

        internal static void Rotate(SmartColliderPart part, float3 pivot, quaternion delta)
        {
            ref var s = ref part.candidates[(int)part.type];
            s.Center = pivot + math.rotate(delta, s.Center - pivot);
            s.Rotation = math.normalize(math.mul(delta, s.Rotation));
            if (part.type == ShapeType.ConvexHull)
                for (int v = 0; v < part.hullVertices.Length; v++)
                    part.hullVertices[v] = pivot + math.rotate(delta, (float3)part.hullVertices[v] - pivot);
            part.autoType = false;
        }

        internal static void SetShape(SmartColliderPart part, in ColliderShape shape)
        {
            part.candidates[(int)part.type] = shape;
            part.autoType = false;
        }

        /// <summary>Scales a hull snapshot in its own frame around its center.</summary>
        internal static void ScaleHull(SmartColliderPart part, Vector3[] snapshot, float3 scale)
        {
            var s = part.candidates[(int)ShapeType.ConvexHull];
            var inv = math.conjugate(s.Rotation);
            for (int v = 0; v < snapshot.Length && v < part.hullVertices.Length; v++)
            {
                float3 local = math.rotate(inv, (float3)snapshot[v] - s.Center) * scale;
                part.hullVertices[v] = s.Center + math.rotate(s.Rotation, local);
            }
            part.autoType = false;
        }

        // ---------- helpers ----------

        static IEnumerable<quaternion> AxisRotations()
        {
            yield return quaternion.identity;                       // Y up
            yield return quaternion.RotateZ(math.PI * 0.5f);        // Y → -X
            yield return quaternion.RotateX(math.PI * 0.5f);        // Y → Z
        }

        static void RebuildHull(SmartColliderPart part, PartReference r, int maxVertices)
        {
            var v = new NativeList<float3>(Allocator.Temp);
            var t = new NativeList<int>(Allocator.Temp);
            int cap = math.clamp(maxVertices, 4, ConvexHull3D.MaxConvexColliderVertices);
            if (!ConvexHull3D.Build(r.Vertices, cap, v, t))
            {
                v.Dispose();
                t.Dispose();
                return;
            }
            part.hullVertices = v.AsArray().Reinterpret<Vector3>().ToArray();
            part.hullTriangles = t.AsArray().ToArray();
            part.candidates[(int)ShapeType.ConvexHull] = new ColliderShape
            {
                Type = ShapeType.ConvexHull,
                Center = GeometryUtils.Centroid(v.AsArray()),
                Rotation = quaternion.identity,
            };
            part.reports[(int)ShapeType.ConvexHull] = FitMetrics.EvaluateHull(v.AsArray(), t.AsArray(), r.Vertices, r.Volume);
            v.Dispose();
            t.Dispose();
        }

        static void ForEachReference(SmartColliderAuthoring a, IReadOnlyList<int> parts, string undoName,
            Action<SmartColliderPart, PartReference> action)
        {
            using var geo = SourceGeometry.Gather(a);
            Undo.RecordObject(a, undoName);
            foreach (int i in parts)
            {
                if (i < 0 || i >= a.parts.Count) continue;
                var part = a.parts[i];
                if (!part.HasFit || !TriangleRanges.IsValidFor(part.triangleRanges, geo.TriangleCount)) continue;
                using var r = PartReference.Build(geo, part);
                if (!r.Valid) continue;
                action(part, r);
                part.autoType = false;
            }
        }

        /// <summary>Full convex hull of a part's triangles: the reference every metric is measured against.</summary>
        internal sealed class PartReference : IDisposable
        {
            public NativeArray<float3> Vertices => _hullV.AsArray();
            public NativeList<float4> Planes;
            public float Volume;
            public bool Valid;
            NativeList<float3> _hullV;
            NativeList<int> _hullT;
            NativeList<float3> _scratch;

            public static PartReference Build(SourceGeometry geo, SmartColliderPart part)
            {
                var r = new PartReference
                {
                    _hullV = new NativeList<float3>(Allocator.TempJob),
                    _hullT = new NativeList<int>(Allocator.TempJob),
                    Planes = new NativeList<float4>(Allocator.TempJob),
                    _scratch = new NativeList<float3>(Allocator.TempJob),
                };
                using var points = geo.PartPoints(part, Allocator.TempJob);
                r.Valid = ConvexHull3D.Build(points, 0, r._hullV, r._hullT);
                if (r.Valid)
                {
                    r.Volume = ConvexHull3D.Volume(r._hullV.AsArray(), r._hullT.AsArray());
                    ConvexHull3D.ComputePlanes(r._hullV.AsArray(), r._hullT.AsArray(), r.Planes);
                }
                return r;
            }

            public FitReport EvaluatePrimitive(in ColliderShape shape) =>
                FitMetrics.EvaluatePrimitive(shape, Vertices, Planes.AsArray(), Volume, _scratch);

            public void Dispose()
            {
                _hullV.Dispose();
                _hullT.Dispose();
                Planes.Dispose();
                _scratch.Dispose();
            }
        }
    }
}
