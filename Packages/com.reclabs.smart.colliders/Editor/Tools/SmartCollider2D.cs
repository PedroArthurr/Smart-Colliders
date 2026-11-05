using System.Collections.Generic;
using System.Linq;
using RecLabs.SmartColliders.Geometry;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace RecLabs.SmartColliders.EditorTools
{
    /// <summary>
    /// 2D pipeline: sprite outline → fitted shapes (single primitive, polygon or convex decomposition) → Collider2D bake.
    /// Everything is in the SpriteRenderer's local space. Every mutation is one Undo group.
    /// </summary>
    public static class SmartCollider2D
    {
        public const string ChildPrefix = "Collider2D";

        /// <summary>Closed outline paths of the sprite (physics shape, else mesh boundary), flip applied.</summary>
        public static List<float2[]> Outline(SpriteRenderer sr)
        {
            var paths = new List<float2[]>();
            var sprite = sr != null ? sr.sprite : null;
            if (sprite == null) return paths;

            int count = sprite.GetPhysicsShapeCount();
            var tmp = new List<Vector2>();
            for (int i = 0; i < count; i++)
            {
                tmp.Clear();
                sprite.GetPhysicsShape(i, tmp);
                if (tmp.Count >= 3) paths.Add(tmp.Select(v => (float2)v).ToArray());
            }
            if (paths.Count == 0) paths.AddRange(MeshBoundary(sprite));
            if (paths.Count == 0)
            {
                var b = sprite.bounds;
                paths.Add(new[]
                {
                    new float2(b.min.x, b.min.y), new float2(b.max.x, b.min.y), new float2(b.max.x, b.max.y), new float2(b.min.x, b.max.y),
                });
            }

            Flip(paths, sr.flipX, sr.flipY);
            return paths;
        }

        /// <summary>Mirrors paths in place like SpriteRenderer.flipX / flipY (keeps winding).</summary>
        internal static void Flip(List<float2[]> paths, bool flipX, bool flipY)
        {
            if (!flipX && !flipY) return;
            float2 flip = new float2(flipX ? -1f : 1f, flipY ? -1f : 1f);
            foreach (var path in paths)
            {
                for (int i = 0; i < path.Length; i++) path[i] *= flip;
                if (flipX ^ flipY) System.Array.Reverse(path);
            }
        }

        /// <summary>Boundary loops of the sprite mesh (edges used by one triangle).</summary>
        static IEnumerable<float2[]> MeshBoundary(Sprite sprite)
        {
            var v = sprite.vertices;
            var t = sprite.triangles;
            var edges = new Dictionary<(int, int), int>();
            for (int i = 0; i < t.Length; i += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = t[i + k], b = t[i + (k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                edges[key] = edges.TryGetValue(key, out int n) ? n + 1 : 1;
            }
            var next = new Dictionary<int, int>();
            for (int i = 0; i < t.Length; i += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = t[i + k], b = t[i + (k + 1) % 3];
                if (edges[a < b ? (a, b) : (b, a)] == 1) next[a] = b;
            }
            var used = new HashSet<int>();
            foreach (int start in next.Keys)
            {
                if (used.Contains(start)) continue;
                var loop = new List<float2>();
                int cur = start, guard = 0;
                while (used.Add(cur) && next.TryGetValue(cur, out int nx) && guard++ < 100000)
                {
                    loop.Add(v[cur]);
                    cur = nx;
                }
                if (loop.Count >= 3) yield return loop.ToArray();
            }
        }

        public struct Result
        {
            public List<Shape2D> Shapes;
            public float OutlineArea;
            public float ShapeArea;
            /// <summary>Outline area / collider area, clamped (colliders enclose the outline).</summary>
            public float Accuracy => ShapeArea > 1e-12f ? Mathf.Clamp01(OutlineArea / ShapeArea) : 0f;
            public int Count(Shape2DType t) => Shapes.Count(s => s.Type == t);
        }

        public static Result Compute(SmartCollider2DAuthoring a) => Compute(a, null);

        /// <param name="outline">Closed paths in local space; null = the sprite outline (<see cref="Outline"/>).</param>
        internal static Result Compute(SmartCollider2DAuthoring a, List<float2[]> outline)
        {
            var sr = a.GetComponent<SpriteRenderer>();
            outline ??= Outline(sr);
            float ppu = sr != null && sr.sprite != null ? sr.sprite.pixelsPerUnit : 100f;
            float tol = a.simplifyPixels / Mathf.Max(ppu, 1e-3f);
            var paths = outline.Select(p => Fit2D.Simplify(p, tol)).Where(p => p.Length >= 3).ToList();
            var s = a.FitSettings;
            var shapes = new List<Shape2D>();
            float outlineArea = outline.Where(p => p.Length >= 3).Sum(p => Mathf.Abs(Fit2D.SignedArea(p)));

            switch (a.mode)
            {
                case Generate2DMode.Decompose:
                {
                    var pieces = new List<float2[]>();
                    foreach (var p in paths) pieces.AddRange(Fit2D.ConvexDecompose(p));
                    pieces = Fit2D.MergePieces(pieces, a.mergeTolerance, Mathf.Max(1, a.maxColliders));
                    foreach (var piece in pieces) shapes.Add(Fit2D.Best(piece, s));
                    break;
                }
                case Generate2DMode.Polygon:
                    foreach (var p in paths)
                        shapes.Add(new Shape2D { Type = Shape2DType.Polygon, Path = p, Accuracy = 1f, Center = Fit2D.Centroid(p) });
                    break;
                default:
                {
                    // Primitives fit the raw outline (simplification would cut cap tips and skew the fit).
                    var all = outline.Where(p => p.Length >= 3).SelectMany(p => p).ToArray();
                    if (all.Length < 3) break;
                    var hull = Fit2D.Hull(all);
                    if (a.mode == Generate2DMode.Auto)
                    {
                        // No primitive accurate enough: the (simplified) outline polygons keep the exact shape.
                        var fit = Fit2D.Best(hull, s, outlineArea);
                        if (fit.Type == Shape2DType.Polygon)
                            foreach (var p in paths)
                                shapes.Add(new Shape2D { Type = Shape2DType.Polygon, Path = p, Accuracy = 1f, Center = Fit2D.Centroid(p) });
                        else shapes.Add(fit);
                    }
                    else
                    {
                        var shape = a.mode switch
                        {
                            Generate2DMode.Circle => Fit2D.Circle(hull),
                            Generate2DMode.Capsule => Fit2D.Capsule(hull),
                            _ => Fit2D.Box(hull),
                        };
                        shape.Accuracy = shape.Area > 0f ? Mathf.Clamp01(outlineArea / shape.Area) : 0f;
                        shapes.Add(shape);
                    }
                    break;
                }
            }

            return new Result
            {
                Shapes = shapes,
                OutlineArea = outlineArea,
                ShapeArea = shapes.Sum(x => x.Type == Shape2DType.Polygon ? Mathf.Abs(Fit2D.SignedArea(x.Path)) : x.Area),
            };
        }

        /// <summary>Creates the Collider2D components (rotated boxes / capsules get a rotated child). One Undo group.</summary>
        public static Result Bake(SmartCollider2DAuthoring a) => Bake(a, null);

        internal static Result Bake(SmartCollider2DAuthoring a, List<float2[]> outline)
        {
            var result = Compute(a, outline);
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Bake 2D Colliders");
            int group = Undo.GetCurrentGroup();
            ClearInternal(a);
            Undo.RecordObject(a, "Bake 2D Colliders");

            var go = a.gameObject;
            PolygonCollider2D polygon = null;
            int childIndex = 0;
            foreach (var s in result.Shapes)
            {
                Collider2D c;
                switch (s.Type)
                {
                    case Shape2DType.Circle:
                    {
                        var cc = Undo.AddComponent<CircleCollider2D>(go);
                        cc.offset = s.Center;
                        cc.radius = s.Radius;
                        c = cc;
                        break;
                    }
                    case Shape2DType.Polygon:
                    {
                        if (polygon == null)
                        {
                            polygon = Undo.AddComponent<PolygonCollider2D>(go);
                            polygon.pathCount = 0;
                            c = polygon;
                        }
                        else c = null;
                        polygon.pathCount += 1;
                        polygon.SetPath(polygon.pathCount - 1, s.Path.Select(p => (Vector2)p).ToArray());
                        break;
                    }
                    default:
                    {
                        // Box / Capsule: rotation needs its own transform.
                        bool rotated = Mathf.Abs(s.Angle) > 1e-3f;
                        var host = go;
                        Vector2 offset = s.Center;
                        if (rotated)
                        {
                            host = new GameObject($"{ChildPrefix} {++childIndex} ({s.Type})");
                            Undo.RegisterCreatedObjectUndo(host, "Bake 2D Colliders");
                            host.layer = go.layer;
                            GameObjectUtility.SetStaticEditorFlags(host, GameObjectUtility.GetStaticEditorFlags(go));
                            host.transform.SetParent(go.transform, false);
                            host.transform.localPosition = new Vector3(s.Center.x, s.Center.y, 0f);
                            host.transform.localRotation = Quaternion.Euler(0, 0, s.Angle);
                            offset = Vector2.zero;
                            a.bakedObjects.Add(host);
                        }
                        if (s.Type == Shape2DType.Box)
                        {
                            var bc = Undo.AddComponent<BoxCollider2D>(host);
                            bc.offset = offset;
                            bc.size = s.Size;
                            c = bc;
                        }
                        else
                        {
                            var cap = Undo.AddComponent<CapsuleCollider2D>(host);
                            cap.direction = CapsuleDirection2D.Vertical;
                            cap.offset = offset;
                            cap.size = s.Size;
                            c = cap;
                        }
                        break;
                    }
                }
                if (c == null) continue;
                c.isTrigger = a.isTrigger;
                c.sharedMaterial = a.material;
                a.bakedColliders.Add(c);
            }
            EditorUtility.SetDirty(a);
            Undo.CollapseUndoOperations(group);
            return result;
        }

        public static void Clear(SmartCollider2DAuthoring a)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Clear 2D Colliders");
            int group = Undo.GetCurrentGroup();
            ClearInternal(a);
            Undo.CollapseUndoOperations(group);
        }

        static void ClearInternal(SmartCollider2DAuthoring a)
        {
            Undo.RecordObject(a, "Clear 2D Colliders");
            foreach (var c in a.bakedColliders) if (c != null) Undo.DestroyObjectImmediate(c);
            foreach (var o in a.bakedObjects) if (o != null) Undo.DestroyObjectImmediate(o);
            a.bakedColliders.Clear();
            a.bakedObjects.Clear();
        }

        /// <summary>Bake, then remove the authoring component (one Undo group).</summary>
        public static void BakeAndRemoveAuthoring(SmartCollider2DAuthoring a)
        {
            Bake(a);
            int group = Undo.GetCurrentGroup();
            Undo.DestroyObjectImmediate(a);
            Undo.CollapseUndoOperations(group);
        }
    }
}
