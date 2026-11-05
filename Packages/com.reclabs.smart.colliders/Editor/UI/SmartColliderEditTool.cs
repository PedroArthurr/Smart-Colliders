using System;
using System.Collections.Generic;
using System.Linq;
using RecLabs.SmartColliders.Geometry;
using RecLabs.SmartColliders.EditorTools;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>
    /// Scene view part editing. Click = select part (Shift/Ctrl = multi), Esc = clear.
    /// Modes: Select, Move, Rotate (around selection pivot), Resize (bounds handles; hulls scale in own frame).
    /// Fit metrics of edited parts are recomputed when the drag ends.
    /// </summary>
    [EditorTool("Edit Smart Colliders", typeof(SmartColliderAuthoring))]
    internal sealed class SmartColliderEditTool : EditorTool
    {
        public enum Mode { Select, Move, Rotate, Resize, Paint }

        const string ModePref = "RecLabs.SmartColliders.EditTool.Mode";
        public static event Action ModeChanged;

        public static Mode CurrentMode
        {
            get => (Mode)EditorPrefs.GetInt(ModePref, (int)Mode.Resize);
            set
            {
                if (value == CurrentMode) return;
                EditorPrefs.SetInt(ModePref, (int)value);
                ModeChanged?.Invoke();
                SceneView.RepaintAll();
            }
        }

        const string LocalPref = "RecLabs.SmartColliders.EditTool.Local";

        /// <summary>Handle space: true = the part's own axes, false = world axes.</summary>
        public static bool LocalSpace
        {
            get => EditorPrefs.GetBool(LocalPref, true);
            set
            {
                if (value == LocalSpace) return;
                EditorPrefs.SetBool(LocalPref, value);
                ModeChanged?.Invoke();
                SceneView.RepaintAll();
            }
        }

        public static bool IsActive => ToolManager.activeToolType == typeof(SmartColliderEditTool);

        /// <summary>
        /// Activates the tool. Component tools only become available after the inspector tracker sees the
        /// (possibly just added) component, so rebuild it and retry for a few editor ticks instead of throwing.
        /// </summary>
        public static void Activate() => TryActivate(8);

        static void TryActivate(int attempts)
        {
            if (IsActive) return;
            if (!Selection.gameObjects.Any(go => go != null && go.GetComponent<SmartColliderAuthoring>() != null)) return;
            ActiveEditorTracker.sharedTracker.ForceRebuild();
            try { ToolManager.SetActiveTool<SmartColliderEditTool>(); }
            catch (InvalidOperationException)
            {
                if (attempts > 0) EditorApplication.delayCall += () => TryActivate(attempts - 1);
            }
        }
        public static void Deactivate() { if (IsActive) ToolManager.RestorePreviousPersistentTool(); }

        static GUIContent s_Icon;
        public override GUIContent toolbarIcon =>
            s_Icon ??= new GUIContent(SmartCollidersIcons.Tool, "Edit Smart Colliders");

        readonly BoxBoundsHandle _box = new BoxBoundsHandle();
        readonly SphereBoundsHandle _sphere = new SphereBoundsHandle();
        readonly CapsuleBoundsHandle _capsule = new CapsuleBoundsHandle { heightAxis = CapsuleBoundsHandle.HeightAxis.Y };

        SmartColliderAuthoring _dirtyOwner;
        readonly List<int> _dirty = new List<int>();
        Vector3[] _hullSnapshot;
        Vector3 _hullScale = Vector3.one;
        Quaternion _globalHandle = Quaternion.identity;
        bool _painting;

        public override void OnActivated()
        {
            foreach (var h in new PrimitiveBoundsHandle[] { _box, _sphere, _capsule })
            {
                h.handleColor = SmartCollidersTheme.Accent;
                h.wireframeColor = Color.clear; // preview renderer draws the wire
            }
            SceneView.RepaintAll();
        }

        public override void OnWillBeDeactivated()
        {
            FlushEvaluation();
            ColliderPreviewRenderer.ClearHover();
        }

        public override void OnToolGUI(EditorWindow window)
        {
            if (!(window is SceneView sceneView)) return;
            var a = target as SmartColliderAuthoring;
            if (a == null) return;
            var mode = CurrentMode;
            if (a.Parts.Count == 0 && mode != Mode.Paint) return;

            Event e = Event.current;
            int pickId = GUIUtility.GetControlID(FocusType.Passive);
            var selected = PartSelection.Get(a);

            if (mode == Mode.Paint)
            {
                DoPaint(sceneView, a, pickId, e);
            }
            else if (selected.Count > 0)
            {
                switch (mode)
                {
                    case Mode.Move: DoMove(a, selected); break;
                    case Mode.Rotate: DoRotate(a, selected); break;
                    case Mode.Resize: if (selected.Count == 1) DoResize(a, selected[0]); break;
                }
            }

            switch (e.GetTypeForControl(pickId))
            {
                case EventType.Layout:
                    HandleUtility.AddDefaultControl(pickId);
                    break;

                case EventType.MouseMove:
                    if (mode == Mode.Paint) { sceneView.Repaint(); break; }
                    ColliderPreviewRenderer.SetHover(a, PartPicker.Pick(a, HandleUtility.GUIPointToWorldRay(e.mousePosition)));
                    break;

                case EventType.MouseDown:
                    if (mode == Mode.Paint || e.button != 0 || e.alt || HandleUtility.nearestControl != pickId) break;
                    int hit = PartPicker.Pick(a, HandleUtility.GUIPointToWorldRay(e.mousePosition));
                    if (e.shift || EditorGUI.actionKey) { if (hit >= 0) PartSelection.Toggle(a, hit); }
                    else if (hit >= 0) PartSelection.Set(a, hit);
                    else PartSelection.Clear(a);
                    e.Use();
                    break;

                case EventType.KeyDown:
                    if (e.keyCode == KeyCode.Escape && mode == Mode.Paint && PaintSession.Count(a) > 0)
                    {
                        PaintSession.Clear(a);
                        e.Use();
                    }
                    else if (e.keyCode == KeyCode.Escape && selected.Count > 0)
                    {
                        PartSelection.Clear(a);
                        e.Use();
                    }
                    else if (!e.control && !e.command && !e.alt && e.keyCode >= KeyCode.Alpha1 && e.keyCode <= KeyCode.Alpha5)
                    {
                        CurrentMode = (Mode)(e.keyCode - KeyCode.Alpha1);
                        e.Use();
                    }
                    else if (!e.control && !e.command && !e.alt && !e.shift && e.keyCode == KeyCode.G)
                    {
                        SceneHud.GenerateOpen = !SceneHud.GenerateOpen;
                        e.Use();
                        SceneView.RepaintAll();
                    }
                    else if (!e.control && !e.command && !e.alt && e.keyCode == KeyCode.X && mode != Mode.Paint)
                    {
                        LocalSpace = !LocalSpace;
                        e.Use();
                    }
                    else if (!e.control && !e.command && !e.alt && e.keyCode == KeyCode.M && mode != Mode.Paint && selected.Count > 1)
                    {
                        int merged = SmartColliderOperations.MergeParts(a, selected.ToList());
                        if (merged >= 0) PartSelection.Set(a, merged);
                        e.Use();
                    }
                    else if (mode == Mode.Paint && (e.keyCode == KeyCode.LeftBracket || e.keyCode == KeyCode.RightBracket))
                    {
                        PaintSession.BrushPixels *= e.keyCode == KeyCode.LeftBracket ? 1f / 1.2f : 1.2f;
                        sceneView.Repaint();
                        e.Use();
                    }
                    else if (mode == Mode.Paint && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && PaintSession.Count(a) > 0)
                    {
                        SplitPaint(a);
                        e.Use();
                    }
                    break;

                // Unity routes Delete / Ctrl+A / F as commands: claim them while parts are the focus,
                // so Delete removes the collider part instead of the whole GameObject.
                case EventType.ValidateCommand:
                    if (IsPartCommand(e.commandName, selected.Count)) e.Use();
                    break;

                case EventType.ExecuteCommand:
                    if (!IsPartCommand(e.commandName, selected.Count)) break;
                    switch (e.commandName)
                    {
                        case "SoftDelete":
                        case "Delete":
                            SmartColliderOperations.RemoveParts(a, selected.ToList());
                            PartSelection.Clear(a);
                            break;
                        case "SelectAll":
                            PartSelection.SelectAll(a);
                            break;
                        case "FrameSelected":
                            FrameParts(sceneView, a, selected);
                            break;
                    }
                    e.Use();
                    break;
            }

            if (_dirty.Count > 0 && GUIUtility.hotControl == 0 && e.type != EventType.Layout)
                FlushEvaluation();
            if (GUIUtility.hotControl == 0)
            {
                _hullSnapshot = null;
                _hullScale = Vector3.one;
                _globalHandle = Quaternion.identity;
            }
        }

        /// <summary>Painted triangles → new part, or into the selected part when <paramref name="intoSelected"/> and exactly one is selected.</summary>
        internal static void SplitPaint(SmartColliderAuthoring a, bool intoSelected = false)
        {
            var sel = PartSelection.Get(a);
            int target = intoSelected && sel.Count == 1 ? sel[0] : -1;
            int index = SmartColliderOperations.AssignTriangles(a, PaintSession.Painted(a), target);
            PaintSession.Clear(a);
            if (index >= 0) PartSelection.Set(a, index);
        }

        void DoPaint(SceneView sceneView, SmartColliderAuthoring a, int pickId, Event e)
        {
            bool erase = EditorGUI.actionKey;
            switch (e.GetTypeForControl(pickId))
            {
                case EventType.MouseDown:
                    if (e.button != 0 || e.alt || HandleUtility.nearestControl != pickId) return;
                    GUIUtility.hotControl = pickId;
                    _painting = true;
                    PaintSession.Stroke(a, e.mousePosition, erase, out _, out _, out _);
                    e.Use();
                    return;

                case EventType.MouseDrag:
                    if (!_painting || GUIUtility.hotControl != pickId) return;
                    PaintSession.Stroke(a, e.mousePosition, erase, out _, out _, out _);
                    e.Use();
                    return;

                case EventType.MouseUp:
                    if (!_painting || GUIUtility.hotControl != pickId) return;
                    GUIUtility.hotControl = 0;
                    _painting = false;
                    e.Use();
                    return;

                case EventType.ScrollWheel:
                    // Shift+wheel resizes the brush (plain wheel keeps zooming).
                    if (!e.shift) return;
                    float delta = Mathf.Abs(e.delta.y) > Mathf.Abs(e.delta.x) ? e.delta.y : e.delta.x;
                    PaintSession.BrushPixels *= delta > 0 ? 1f / 1.12f : 1.12f;
                    e.Use();
                    sceneView.Repaint();
                    return;

                case EventType.Repaint:
                    PaintSession.DrawOverlay(a);
                    if (PaintSession.Probe(a, e.mousePosition, out var hit, out var normal, out float radius))
                    {
                        var c = erase ? new Color(1f, 0.42f, 0.42f) : SmartCollidersTheme.Selection;
                        using (new Handles.DrawingScope(c))
                        {
                            var fill = c;
                            fill.a = 0.12f;
                            Handles.color = fill;
                            Handles.DrawSolidDisc(hit, normal, radius);
                            Handles.color = c;
                            Handles.DrawWireDisc(hit, normal, radius, 2f);
                        }
                    }
                    return;
            }
        }

        static bool IsPartCommand(string command, int selectedCount)
        {
            switch (command)
            {
                case "SoftDelete":
                case "Delete":
                case "FrameSelected":
                    return selectedCount > 0;
                case "SelectAll":
                    return true;
                default:
                    return false;
            }
        }

        static void FrameParts(SceneView sceneView, SmartColliderAuthoring a, IReadOnlyList<int> parts)
        {
            var bounds = new Bounds();
            bool init = false;
            Matrix4x4 m = a.transform.localToWorldMatrix;
            foreach (int i in parts)
            {
                var part = a.Parts[i];
                Vector3 min, max;
                if (part.Type == ShapeType.ConvexHull && part.HullVertices.Length > 0)
                {
                    min = max = part.HullVertices[0];
                    foreach (var v in part.HullVertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
                }
                else
                {
                    part.Shape.Bounds(out var mn, out var mx);
                    min = mn; max = mx;
                }
                for (int k = 0; k < 8; k++)
                {
                    var p = m.MultiplyPoint3x4(new Vector3((k & 1) == 0 ? min.x : max.x, (k & 2) == 0 ? min.y : max.y, (k & 4) == 0 ? min.z : max.z));
                    if (!init) { bounds = new Bounds(p, Vector3.zero); init = true; }
                    else bounds.Encapsulate(p);
                }
            }
            if (init) sceneView.Frame(bounds, false);
        }

        // ---------- handles ----------

        static Vector3 WorldPivot(SmartColliderAuthoring a, IReadOnlyList<int> parts)
        {
            Vector3 sum = Vector3.zero;
            foreach (int i in parts) sum += a.transform.TransformPoint(a.Parts[i].Shape.Center);
            return sum / parts.Count;
        }

        static Quaternion WorldRotation(SmartColliderAuthoring a, int part) =>
            a.transform.rotation * (Quaternion)a.Parts[part].Shape.Rotation;

        void DoMove(SmartColliderAuthoring a, IReadOnlyList<int> parts)
        {
            Vector3 pivot = WorldPivot(a, parts);
            Quaternion rot = LocalSpace ? WorldRotation(a, parts[0]) : Quaternion.identity;

            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(pivot, rot);
            if (!EditorGUI.EndChangeCheck()) return;

            Undo.RecordObject(a, "Move Collider");
            float3 delta = a.transform.InverseTransformVector(moved - pivot);
            foreach (int i in parts) SmartColliderRefine.Translate(a.parts[i], delta);
            MarkDirty(a, parts);
        }

        void DoRotate(SmartColliderAuthoring a, IReadOnlyList<int> parts)
        {
            Vector3 pivot = WorldPivot(a, parts);
            // Local: handle follows the part. Global: world-aligned handle; its rotation accumulates during the drag.
            Quaternion current = LocalSpace ? WorldRotation(a, parts[0]) : _globalHandle;

            EditorGUI.BeginChangeCheck();
            Quaternion rotated = Handles.RotationHandle(current, pivot);
            if (!EditorGUI.EndChangeCheck()) return;
            if (!LocalSpace) _globalHandle = rotated;

            Undo.RecordObject(a, "Rotate Collider");
            Quaternion worldDelta = rotated * Quaternion.Inverse(current);
            Quaternion root = a.transform.rotation;
            quaternion localDelta = Quaternion.Inverse(root) * worldDelta * root;
            float3 localPivot = a.transform.InverseTransformPoint(pivot);
            foreach (int i in parts) SmartColliderRefine.Rotate(a.parts[i], localPivot, localDelta);
            MarkDirty(a, parts);
        }

        void DoResize(SmartColliderAuthoring a, int index)
        {
            var part = a.parts[index];
            var s = part.Shape;
            Matrix4x4 m = a.transform.localToWorldMatrix * Matrix4x4.TRS(s.Center, s.Rotation, Vector3.one);

            using (new Handles.DrawingScope(SmartCollidersTheme.Accent, m))
            {
                EditorGUI.BeginChangeCheck();
                switch (part.Type)
                {
                    case ShapeType.Box:
                        _box.center = Vector3.zero;
                        _box.size = (Vector3)s.HalfExtents * 2f;
                        _box.DrawHandle();
                        if (!EditorGUI.EndChangeCheck()) return;
                        Undo.RecordObject(a, "Resize Collider");
                        s.Center += math.rotate(s.Rotation, (float3)_box.center);
                        s.HalfExtents = (float3)_box.size * 0.5f;
                        break;

                    case ShapeType.Sphere:
                        _sphere.center = Vector3.zero;
                        _sphere.radius = s.Radius;
                        _sphere.DrawHandle();
                        if (!EditorGUI.EndChangeCheck()) return;
                        Undo.RecordObject(a, "Resize Collider");
                        s.Center += math.rotate(s.Rotation, (float3)_sphere.center);
                        s.Radius = _sphere.radius;
                        break;

                    case ShapeType.Capsule:
                        _capsule.center = Vector3.zero;
                        _capsule.radius = s.Radius;
                        _capsule.height = 2f * (s.HalfHeight + s.Radius);
                        _capsule.DrawHandle();
                        if (!EditorGUI.EndChangeCheck()) return;
                        Undo.RecordObject(a, "Resize Collider");
                        s.Center += math.rotate(s.Rotation, (float3)_capsule.center);
                        s.Radius = _capsule.radius;
                        s.HalfHeight = math.max(_capsule.height * 0.5f - _capsule.radius, 0f);
                        break;

                    case ShapeType.ConvexHull:
                    {
                        float size = HandleUtility.GetHandleSize(Vector3.zero);
                        Vector3 scale = Handles.ScaleHandle(_hullScale, Vector3.zero, Quaternion.identity, size);
                        if (!EditorGUI.EndChangeCheck()) return;
                        Undo.RecordObject(a, "Scale Collider");
                        _hullSnapshot ??= (Vector3[])part.hullVertices.Clone();
                        _hullScale = Vector3.Max(scale, Vector3.one * 0.01f);
                        SmartColliderRefine.ScaleHull(part, _hullSnapshot, _hullScale);
                        MarkDirty(a, new[] { index });
                        return;
                    }
                    default:
                        EditorGUI.EndChangeCheck();
                        return;
                }
            }

            SmartColliderRefine.SetShape(part, s);
            MarkDirty(a, new[] { index });
        }

        // ---------- deferred evaluation ----------

        void MarkDirty(SmartColliderAuthoring a, IEnumerable<int> parts)
        {
            if (_dirtyOwner != a) FlushEvaluation();
            _dirtyOwner = a;
            foreach (int i in parts)
                if (!_dirty.Contains(i)) _dirty.Add(i);
        }

        void FlushEvaluation()
        {
            if (_dirtyOwner != null && _dirty.Count > 0)
                SmartColliderRefine.Reevaluate(_dirtyOwner, _dirty.ToList(), recordUndo: false);
            _dirtyOwner = null;
            _dirty.Clear();
        }
    }
}
