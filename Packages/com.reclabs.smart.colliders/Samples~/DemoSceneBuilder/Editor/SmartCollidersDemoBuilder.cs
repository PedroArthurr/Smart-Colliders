using RecLabs.SmartColliders.EditorTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RecLabs.SmartColliders.Samples
{
    /// <summary>Builds a small demo scene: a rover (Per Mesh), an arch (Decompose) and a sprite (2D).</summary>
    public static class SmartCollidersDemoBuilder
    {
        [MenuItem("Tools/Smart Colliders/Samples/Create Demo Scene")]
        public static void Create()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(3, 1, 3);

            // Rover: several meshes → one collider per mesh, mostly primitives.
            var rover = new GameObject("Rover (Per Mesh)");
            rover.transform.position = new Vector3(-4, 0, 0);
            Part(rover, PrimitiveType.Cube, new Vector3(0, 0.6f, 0), new Vector3(2.2f, 0.6f, 3.4f), Vector3.zero);
            Part(rover, PrimitiveType.Cylinder, new Vector3(0, 1.15f, -0.2f), new Vector3(1.2f, 0.25f, 1.2f), Vector3.zero);
            Part(rover, PrimitiveType.Capsule, new Vector3(0, 1.2f, 1.2f), new Vector3(0.22f, 0.9f, 0.22f), new Vector3(90, 0, 0));
            for (int i = 0; i < 4; i++)
                Part(rover, PrimitiveType.Sphere, new Vector3(i < 2 ? -1.2f : 1.2f, 0.35f, i % 2 == 0 ? -1.1f : 1.1f), Vector3.one * 0.7f, Vector3.zero);
            var roverAuthoring = rover.AddComponent<SmartColliderAuthoring>();
            SmartColliderOperations.Generate(roverAuthoring, GenerateMode.PerMesh);

            // Arch: one concave mesh → decomposition.
            var arch = new GameObject("Arch (Decompose)");
            arch.transform.position = new Vector3(4, 0, 0);
            arch.AddComponent<MeshFilter>().sharedMesh = ArchMesh();
            arch.AddComponent<MeshRenderer>().sharedMaterial = ground.GetComponent<MeshRenderer>().sharedMaterial;
            var archAuthoring = arch.AddComponent<SmartColliderAuthoring>();
            var so = new SerializedObject(archAuthoring);
            so.FindProperty("generateMode").enumValueIndex = (int)GenerateMode.Decompose;
            so.ApplyModifiedPropertiesWithoutUndo();
            SmartColliderOperations.Generate(archAuthoring, GenerateMode.Decompose);

            // Sprite: 2D generation from the sprite outline.
            var sprite = new GameObject("Sprite (2D)");
            sprite.transform.position = new Vector3(0, 2, 4);
            sprite.AddComponent<SpriteRenderer>().sprite = DiamondSprite();
            sprite.AddComponent<SmartCollider2DAuthoring>();

            Selection.activeGameObject = arch;
            SceneView.lastActiveSceneView?.FrameSelected();
            Debug.Log("[Smart Colliders] Demo scene created. Select an object and open Window > Smart Colliders, or press Edit in Scene.");
        }

        static void Part(GameObject root, PrimitiveType type, Vector3 pos, Vector3 scale, Vector3 euler)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.transform.localEulerAngles = euler;
        }

        /// <summary>Half-ring arch (concave), 24 segments.</summary>
        static Mesh ArchMesh()
        {
            const int seg = 24;
            const float r0 = 1.4f, r1 = 2.2f, depth = 1f;
            var v = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();
            for (int i = 0; i <= seg; i++)
            {
                float a = Mathf.PI * i / seg;
                var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
                v.Add(dir * r0 + Vector3.back * depth * 0.5f);
                v.Add(dir * r1 + Vector3.back * depth * 0.5f);
                v.Add(dir * r0 + Vector3.forward * depth * 0.5f);
                v.Add(dir * r1 + Vector3.forward * depth * 0.5f);
            }
            for (int i = 0; i < seg; i++)
            {
                int a = i * 4, b = (i + 1) * 4;
                Quad(t, a + 0, b + 0, b + 1, a + 1); // front
                Quad(t, a + 3, b + 3, b + 2, a + 2); // back
                Quad(t, a + 1, b + 1, b + 3, a + 3); // outer
                Quad(t, a + 2, b + 2, b + 0, a + 0); // inner
            }
            int e = seg * 4;
            Quad(t, 0, 1, 3, 2);
            Quad(t, e + 2, e + 3, e + 1, e + 0);
            var mesh = new Mesh { name = "Arch" };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static void Quad(System.Collections.Generic.List<int> t, int a, int b, int c, int d)
        {
            t.Add(a); t.Add(b); t.Add(c);
            t.Add(a); t.Add(c); t.Add(d);
        }

        static Sprite DiamondSprite()
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Diamond" };
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool inside = Mathf.Abs(x - 31.5f) + Mathf.Abs(y - 31.5f) < 30f;
                tex.SetPixel(x, y, inside ? new Color(1f, 0.65f, 0.24f) : Color.clear);
            }
            tex.Apply();
            // Tight mesh + generated physics shape follow the alpha outline (the diamond).
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 32f, 0, SpriteMeshType.Tight, Vector4.zero, true);
            sprite.name = "Diamond";
            return sprite;
        }
    }
}
