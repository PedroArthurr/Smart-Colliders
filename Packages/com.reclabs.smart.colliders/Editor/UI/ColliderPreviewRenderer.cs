using System.Collections.Generic;
using RecLabs.SmartColliders.Geometry;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>
    /// Scene view preview of parts for every selected authoring object: translucent fresnel fill
    /// (with dimmer x-ray pass for occluded parts) plus crisp wireframe. Selected parts = violet,
    /// hovered = brighter, others dim while something is selected.
    /// </summary>
    [InitializeOnLoad]
    internal static class ColliderPreviewRenderer
    {
        const string ShaderPath = "Packages/com.reclabs.smart.colliders/Editor/Shaders/SmartCollidersPreview.shader";
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int OccludedId = Shader.PropertyToID("_OccludedAlpha");
        static Material s_Material;

        static SmartColliderAuthoring s_HoverOwner;
        static int s_HoverPart = -1;

        static ColliderPreviewRenderer()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            PartSelection.Changed += SceneView.RepaintAll;
            PaintSession.Changed += SceneView.RepaintAll;
            Undo.undoRedoPerformed += SceneView.RepaintAll;
        }

        public static void SetHover(SmartColliderAuthoring owner, int part)
        {
            if (owner == s_HoverOwner && part == s_HoverPart) return;
            s_HoverOwner = owner;
            s_HoverPart = part;
            SceneView.RepaintAll();
        }

        public static void ClearHover() => SetHover(null, -1);

        static Material Material
        {
            get
            {
                if (s_Material != null) return s_Material;
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
                if (shader == null) return null;
                s_Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                return s_Material;
            }
        }

        static readonly List<SmartColliderAuthoring> s_Targets = new List<SmartColliderAuthoring>();

        static void OnSceneGUI(SceneView sceneView)
        {
            if (Event.current.type != EventType.Repaint || !PreviewPrefs.Enabled) return;

            s_Targets.Clear();
            foreach (var t in Selection.transforms)
            {
                var a = t.GetComponentInParent<SmartColliderAuthoring>(true);
                if (a != null && a.isActiveAndEnabled && !s_Targets.Contains(a)) s_Targets.Add(a);
            }
            if (s_Targets.Count == 0) return;

            PreviewMeshes.BeginFrame();
            var mat = Material;
            Vector3 camPos = sceneView.camera.transform.position;
            foreach (var a in s_Targets) Draw(a, mat, camPos);
        }

        static void Draw(SmartColliderAuthoring a, Material mat, Vector3 camPos)
        {
            var parts = a.Parts;
            if (parts.Count == 0) return;
            bool anySelected = PartSelection.Any(a);
            Matrix4x4 root = a.transform.localToWorldMatrix;

            // Pass 1: fills, unselected first so selected ones blend on top.
            if (mat != null)
            {
                mat.SetFloat(OccludedId, PreviewPrefs.ShowOccluded ? 0.3f : 0f);
                for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < parts.Count; i++)
                {
                    bool selected = PartSelection.Contains(a, i);
                    if (selected != (pass == 1) || !parts[i].HasFit) continue;
                    Color c = parts[i].Color;
                    c.a = Mathf.Clamp01(FillAlpha(a, i, selected, anySelected) * SmartCollidersPreferences.FillOpacity);
                    mat.SetColor(ColorId, c);
                    DrawFill(parts[i], root * parts[i].PoseDelta(a.transform), mat);
                }
            }

            // Pass 2: wires.
            var zTest = Handles.zTest;
            for (int i = 0; i < parts.Count; i++)
            {
                if (!parts[i].HasFit) continue;
                bool selected = PartSelection.Contains(a, i);
                bool hover = a == s_HoverOwner && i == s_HoverPart;
                Color wire = selected ? SmartCollidersTheme.Selection
                    : hover ? Color.Lerp(parts[i].Color, Color.white, 0.35f)
                    : parts[i].Color;
                if (anySelected && !selected && !hover) wire.a = 0.45f;
                float width = selected ? SmartCollidersTheme.WireWidthSelected
                    : hover ? SmartCollidersTheme.WireWidthHover
                    : SmartCollidersTheme.WireWidth;
                width *= SmartCollidersPreferences.WireWidth;

                if (PreviewPrefs.ShowOccluded)
                {
                    Handles.zTest = CompareFunction.Greater;
                    var occluded = wire;
                    occluded.a *= SmartCollidersTheme.OccludedWireAlpha;
                    DrawWire(parts[i], root * parts[i].PoseDelta(a.transform), occluded, 1f, camPos);
                }
                Handles.zTest = CompareFunction.LessEqual;
                DrawWire(parts[i], root * parts[i].PoseDelta(a.transform), wire, width, camPos);
            }
            Handles.zTest = zTest;
        }

        static float FillAlpha(SmartColliderAuthoring a, int i, bool selected, bool anySelected)
        {
            if (selected) return SmartCollidersTheme.FillAlphaSelected;
            if (a == s_HoverOwner && i == s_HoverPart) return SmartCollidersTheme.FillAlphaHover;
            return anySelected ? SmartCollidersTheme.FillAlphaDimmed : SmartCollidersTheme.FillAlpha;
        }

        // ---------- fill ----------

        static void DrawFill(SmartColliderPart part, Matrix4x4 root, Material mat)
        {
            var s = part.Shape;
            switch (part.Type)
            {
                case ShapeType.Box:
                    DrawMesh(PreviewMeshes.Cube, root * Matrix4x4.TRS(s.Center, s.Rotation, s.HalfExtents), mat);
                    break;
                case ShapeType.Sphere:
                    DrawMesh(PreviewMeshes.Sphere, root * Matrix4x4.TRS(s.Center, s.Rotation, Vector3.one * s.Radius), mat);
                    break;
                case ShapeType.Capsule:
                {
                    Matrix4x4 m = root * Matrix4x4.TRS(s.Center, s.Rotation, Vector3.one);
                    if (s.HalfHeight > 1e-5f)
                        DrawMesh(PreviewMeshes.Cylinder, m * Matrix4x4.Scale(new Vector3(s.Radius, s.HalfHeight, s.Radius)), mat);
                    DrawMesh(PreviewMeshes.Hemisphere, m * Matrix4x4.TRS(Vector3.up * s.HalfHeight, Quaternion.identity, Vector3.one * s.Radius), mat);
                    DrawMesh(PreviewMeshes.Hemisphere, m * Matrix4x4.TRS(Vector3.down * s.HalfHeight, Quaternion.Euler(180f, 0f, 0f), Vector3.one * s.Radius), mat);
                    break;
                }
                case ShapeType.ConvexHull:
                    if (part.HullTriangles.Length >= 12)
                        DrawMesh(PreviewMeshes.Hull(part, out _), root, mat);
                    break;
            }
        }

        /// <summary>Draws an arbitrary mesh with the preview material (paint overlay, decomposition preview). Repaint only.</summary>
        public static void DrawOverlay(Mesh mesh, Matrix4x4 matrix, Color color)
        {
            var mat = Material;
            if (mat == null || mesh == null) return;
            mat.SetColor(ColorId, color);
            mat.SetFloat(OccludedId, PreviewPrefs.ShowOccluded ? 0.3f : 0f);
            DrawMesh(mesh, matrix, mat);
        }

        static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material mat)
        {
            mat.SetPass(0);
            Graphics.DrawMeshNow(mesh, matrix);
            if (!PreviewPrefs.ShowOccluded) return;
            mat.SetPass(1);
            Graphics.DrawMeshNow(mesh, matrix);
        }

        // ---------- wire ----------

        static readonly Vector3[] s_Corners = new Vector3[8];

        static void DrawWire(SmartColliderPart part, Matrix4x4 root, Color color, float width, Vector3 camPosWorld)
        {
            var s = part.Shape;
            Matrix4x4 m = part.Type == ShapeType.ConvexHull ? root : root * Matrix4x4.TRS(s.Center, s.Rotation, Vector3.one);
            Vector3 camLocal = m.inverse.MultiplyPoint3x4(camPosWorld);

            using (new Handles.DrawingScope(color, m))
            {
                switch (part.Type)
                {
                    case ShapeType.Box:
                    {
                        Vector3 he = s.HalfExtents;
                        for (int k = 0; k < 8; k++)
                            s_Corners[k] = new Vector3((k & 1) == 0 ? -he.x : he.x, (k & 2) == 0 ? -he.y : he.y, (k & 4) == 0 ? -he.z : he.z);
                        for (int k = 0; k < 8; k++)
                        for (int bit = 1; bit < 8; bit <<= 1)
                            if ((k & bit) == 0) Handles.DrawLine(s_Corners[k], s_Corners[k | bit], width);
                        break;
                    }
                    case ShapeType.Sphere:
                    {
                        Handles.DrawWireDisc(Vector3.zero, camLocal.sqrMagnitude > 1e-8f ? camLocal.normalized : Vector3.forward, s.Radius, width);
                        Handles.DrawWireDisc(Vector3.zero, Vector3.up, s.Radius, width * 0.6f);
                        break;
                    }
                    case ShapeType.Capsule:
                        DrawCapsuleWire(s.Radius, s.HalfHeight, camLocal, width);
                        break;
                    case ShapeType.ConvexHull:
                    {
                        if (part.HullTriangles.Length < 12) break;
                        PreviewMeshes.Hull(part, out var edges);
                        for (int k = 0; k + 1 < edges.Length; k += 2)
                            Handles.DrawLine(edges[k], edges[k + 1], width);
                        break;
                    }
                }
            }
        }

        const int ArcSegments = 20;

        static void DrawCapsuleWire(float r, float h, Vector3 camLocal, float width)
        {
            Vector3 top = Vector3.up * h, bottom = Vector3.down * h;
            Handles.DrawWireDisc(top, Vector3.up, r, width * 0.6f);
            Handles.DrawWireDisc(bottom, Vector3.up, r, width * 0.6f);

            // Silhouette: side vector perpendicular to the axis and the view direction.
            Vector3 view = new Vector3(camLocal.x, 0f, camLocal.z);
            Vector3 side = view.sqrMagnitude > 1e-8f ? Vector3.Cross(Vector3.up, view).normalized : Vector3.right;
            Handles.DrawLine(top + side * r, bottom + side * r, width);
            Handles.DrawLine(top - side * r, bottom - side * r, width);
            DrawHalfCircle(top, side, Vector3.up, r, width);
            DrawHalfCircle(bottom, side, Vector3.down, r, width);
        }

        static void DrawHalfCircle(Vector3 c, Vector3 a, Vector3 b, float r, float width)
        {
            Vector3 prev = c + a * r;
            for (int i = 1; i <= ArcSegments; i++)
            {
                float t = Mathf.PI * i / ArcSegments;
                Vector3 next = c + (a * Mathf.Cos(t) + b * Mathf.Sin(t)) * r;
                Handles.DrawLine(prev, next, width);
                prev = next;
            }
        }
    }
}
