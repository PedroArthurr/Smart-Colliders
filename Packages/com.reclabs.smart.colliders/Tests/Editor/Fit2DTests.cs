using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RecLabs.SmartColliders.EditorTools;
using RecLabs.SmartColliders.Geometry;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace RecLabs.SmartColliders.Tests
{
    /// <summary>Phase 8: 2D fitting, convex decomposition and Collider2D bake.</summary>
    public class Fit2DTests
    {
        readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created) if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        static float2[] Rect(float x0, float y0, float x1, float y1) =>
            new[] { new float2(x0, y0), new float2(x1, y0), new float2(x1, y1), new float2(x0, y1) };

        static readonly float2[] LShape =
        {
            new float2(0, 0), new float2(4, 0), new float2(4, 1), new float2(1, 1), new float2(1, 4), new float2(0, 4),
        };

        static float2[] Circle(int n, float r, float2 c)
        {
            var p = new float2[n];
            for (int i = 0; i < n; i++)
            {
                float t = i * math.PI * 2f / n;
                p[i] = c + new float2(math.cos(t), math.sin(t)) * r;
            }
            return p;
        }

        [Test]
        public void Best_Square_IsAxisAlignedBox()
        {
            var s = Fit2D.Best(Rect(-1, -2, 3, 2), Fit2DSettings.Default);
            Assert.AreEqual(Shape2DType.Box, s.Type);
            Assert.AreEqual(0f, s.Angle, 1e-3f);
            Assert.AreEqual(4f, s.Size.x, 1e-4f);
            Assert.AreEqual(4f, s.Size.y, 1e-4f);
            Assert.AreEqual(1f, s.Center.x, 1e-4f);
            Assert.Greater(s.Accuracy, 0.99f);
        }

        [Test]
        public void Best_Disc_IsCircle()
        {
            var s = Fit2D.Best(Circle(48, 1.5f, new float2(2, -1)), Fit2DSettings.Default);
            Assert.AreEqual(Shape2DType.Circle, s.Type);
            Assert.AreEqual(1.5f, s.Radius, 0.01f);
            Assert.AreEqual(2f, s.Center.x, 0.01f);
        }

        [Test]
        public void Box_RotatedRect_FindsAngle()
        {
            var q = quaternion.RotateZ(math.radians(30f));
            var pts = Rect(-2, -0.5f, 2, 0.5f).Select(p => math.mul(q, new float3(p, 0)).xy).ToArray();
            var s = Fit2D.Box(pts);
            Assert.AreEqual(30f, s.Angle, 0.1f);
            Assert.AreEqual(4f, s.Size.x, 1e-3f);
            Assert.AreEqual(1f, s.Size.y, 1e-3f);
        }

        [Test]
        public void Capsule_Stadium_EnclosesAndIsTight()
        {
            // Horizontal stadium: segment length 4, radius 1 → width 2, height 6 along X.
            var pts = new List<float2>();
            for (int i = 0; i <= 24; i++)
            {
                float t = -math.PI / 2 + math.PI * i / 24f;
                pts.Add(new float2(2 + math.cos(t), math.sin(t)));
                pts.Add(new float2(-2 - math.cos(t), math.sin(t)));
            }
            var s = Fit2D.Capsule(Fit2D.Hull(pts));
            Assert.AreEqual(2f, s.Size.x, 0.02f);
            Assert.AreEqual(6f, s.Size.y, 0.02f);
            Assert.AreEqual(90f, math.abs(s.Angle), 0.1f); // long axis along X ⇒ capsule (vertical) rotated 90°
        }

        [Test]
        public void ConvexDecompose_L_GivesTwoPieces_WithSameArea()
        {
            var pieces = Fit2D.ConvexDecompose(LShape);
            Assert.AreEqual(2, pieces.Count);
            float area = pieces.Sum(p => math.abs(Fit2D.SignedArea(p)));
            Assert.AreEqual(7f, area, 1e-4f);
        }

        [Test]
        public void MergePieces_RespectsMaxAndTolerance()
        {
            var pieces = Fit2D.ConvexDecompose(LShape);
            Assert.AreEqual(2, Fit2D.MergePieces(pieces, 0.05f, 10).Count, "L arms waste too much to merge");
            Assert.AreEqual(1, Fit2D.MergePieces(pieces, 0.05f, 1).Count, "forced by Max");
            var touching = new List<float2[]> { Rect(0, 0, 1, 1), Rect(1, 0, 2, 1) };
            Assert.AreEqual(1, Fit2D.MergePieces(touching, 0.05f, 10).Count, "coplanar halves merge");
        }

        [Test]
        public void Simplify_RemovesCollinearNoise()
        {
            var dense = new List<float2>();
            for (int i = 0; i < 10; i++) dense.Add(new float2(i * 0.1f, 0.001f * (i % 2)));
            for (int i = 0; i < 10; i++) dense.Add(new float2(1f, i * 0.1f));
            for (int i = 0; i < 10; i++) dense.Add(new float2(1f - i * 0.1f, 1f));
            for (int i = 0; i < 10; i++) dense.Add(new float2(0f, 1f - i * 0.1f));
            var s = Fit2D.Simplify(dense.ToArray(), 0.01f);
            Assert.AreEqual(4, s.Length);
        }

        static bool Inside(float2[] poly, float2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                    p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            return inside;
        }

        float2[] _shape;

        /// <summary>Exact test outline (the generated sprite physics shape of runtime sprites is a coarse approximation).</summary>
        List<float2[]> Exact() => new List<float2[]> { (float2[])_shape.Clone() };

        SmartCollider2DAuthoring SpriteObject(float2[] shape)
        {
            _shape = shape;
            // Runtime sprites can't override their geometry / physics shape: paint the shape into the alpha channel
            // (80×80 px, pivot at the centre, 16 px per unit) and let Unity generate the physics shape from it.
            const int size = 80;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _created.Add(tex);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var p = new float2((x + 0.5f - 40f) / 16f, (y + 0.5f - 40f) / 16f);
                pixels[y * size + x] = Inside(shape, p) ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 16f, 0, SpriteMeshType.Tight, Vector4.zero, true);
            _created.Add(sprite);
            var outline = new List<Vector2>();
            if (sprite.GetPhysicsShapeCount() > 0) sprite.GetPhysicsShape(0, outline);
            TestContext.WriteLine($"[Test] sprite outline {sprite.GetPhysicsShapeCount()}: {string.Join(" ", outline)}");
            var go = new GameObject("Sprite2D");
            _created.Add(go);
            go.AddComponent<SpriteRenderer>().sprite = sprite;
            return go.AddComponent<SmartCollider2DAuthoring>();
        }

        [Test]
        public void Bake_Decompose_L_CreatesTwoBoxes_AndUndo()
        {
            var a = SpriteObject(LShape.Select(p => p - 2f).ToArray());
            a.mode = Generate2DMode.Decompose;
            Undo.IncrementCurrentGroup();
            var r = SmartCollider2D.Bake(a, Exact());
            Assert.AreEqual(2, r.Shapes.Count);
            Assert.IsTrue(r.Shapes.All(s => s.Type == Shape2DType.Box));
            Assert.Greater(r.Accuracy, 0.99f);
            Assert.AreEqual(2, a.GetComponents<BoxCollider2D>().Length);

            SmartCollider2D.Bake(a, Exact()); // re-bake replaces
            Assert.AreEqual(2, a.GetComponents<BoxCollider2D>().Length);

            Undo.PerformUndo();
            Undo.PerformUndo();
            Assert.AreEqual(0, a.GetComponents<Collider2D>().Length);
        }

        [Test]
        public void Bake_Auto_ConcaveOutline_IsPolygon_AndFlipApplies()
        {
            var a = SpriteObject(LShape.Select(p => p - 2f).ToArray());
            var outline = Exact();
            SmartCollider2D.Flip(outline, flipX: true, flipY: false);
            SmartCollider2D.Bake(a, outline);
            var poly = a.GetComponent<PolygonCollider2D>();
            Assert.IsNotNull(poly);
            Assert.AreEqual(1, poly.pathCount);
            Assert.IsTrue(poly.GetPath(0).All(p => p.x <= 2.01f));
            // flipped: the long arm (x from -2..2 at y -2..-1) mirrors; the vertical arm moves to +x.
            Assert.IsTrue(poly.GetPath(0).Any(p => Mathf.Abs(p.x - 2f) < 1e-3f && Mathf.Abs(p.y - 2f) < 1e-3f));
        }

        [Test]
        public void Bake_RotatedCapsule_UsesChild()
        {
            var q = quaternion.RotateZ(math.radians(40f));
            var stadium = new List<float2>();
            for (int i = 0; i <= 16; i++)
            {
                float t = -math.PI / 2 + math.PI * i / 16f;
                stadium.Add(new float2(1 + math.cos(t) * 0.5f, math.sin(t) * 0.5f));
            }
            for (int i = 0; i <= 16; i++)
            {
                float t = math.PI / 2 + math.PI * i / 16f;
                stadium.Add(new float2(-1 + math.cos(t) * 0.5f, math.sin(t) * 0.5f));
            }
            var a = SpriteObject(stadium.Select(p => math.mul(q, new float3(p, 0)).xy).ToArray());
            a.mode = Generate2DMode.Capsule;
            SmartCollider2D.Bake(a, Exact());
            Assert.AreEqual(1, a.bakedObjects.Count);
            var cap = a.bakedObjects[0].GetComponent<CapsuleCollider2D>();
            Assert.IsNotNull(cap);
            Assert.AreEqual(3f, cap.size.y, 0.05f);
            Assert.AreEqual(1f, cap.size.x, 0.05f);
            SmartCollider2D.Clear(a);
            Assert.AreEqual(0, a.transform.childCount);
        }

        [Test]
        public void SpriteOutline_FromGeneratedPhysicsShape_Bakes()
        {
            var a = SpriteObject(LShape.Select(p => p - 2f).ToArray());
            var paths = SmartCollider2D.Outline(a.GetComponent<SpriteRenderer>());
            Assert.GreaterOrEqual(paths.Count, 1);
            Assert.IsTrue(paths.All(p => p.Length >= 3));
            var r = SmartCollider2D.Bake(a);
            Assert.GreaterOrEqual(r.Shapes.Count, 1);
            Assert.Greater(r.Accuracy, 0.5f);
            Assert.IsTrue(a.IsBaked);

            a.GetComponent<SpriteRenderer>().flipX = true;
            var flipped = SmartCollider2D.Outline(a.GetComponent<SpriteRenderer>());
            Assert.AreEqual(-paths[0].Max(p => p.x), flipped[0].Min(p => p.x), 1e-4f);
        }
    }
}
