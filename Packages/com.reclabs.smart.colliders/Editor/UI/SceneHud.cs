using System;
using System.Collections.Generic;
using System.Linq;
using RecLabs.SmartColliders.EditorTools;
using RecLabs.SmartColliders.Geometry;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;
using UnityEngine.UIElements;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>
    /// Floating Scene view HUD (UI Toolkit) shown while the edit tool is active: metrics card + icon toolbar,
    /// anchored under the selected parts (or the whole object) and kept inside the view.
    /// </summary>
    internal sealed class SceneHud
    {
        const string StylePath = "Packages/com.reclabs.smart.colliders/Editor/UI/SmartCollidersHud.uss";
        static readonly Color IconColor = new Color(0.86f, 0.87f, 0.9f);
        static readonly Color Good = new Color32(0x6E, 0xE7, 0xA0, 0xFF);
        static readonly Color Bad = new Color32(0xFF, 0x6B, 0x6B, 0xFF);

        readonly VisualElement _root;
        readonly Label _title, _accuracy, _worst, _cost, _hint;
        readonly VisualElement[] _countItems = new VisualElement[4];
        readonly Label[] _countLabels = new Label[4];
        readonly Dictionary<SmartColliderEditTool.Mode, (VisualElement btn, HudIconElement icon)> _modes =
            new Dictionary<SmartColliderEditTool.Mode, (VisualElement, HudIconElement)>();
        readonly (VisualElement btn, HudIconElement icon)[] _types = new (VisualElement, HudIconElement)[4];
        readonly List<VisualElement> _needsSelection = new List<VisualElement>();
        readonly List<VisualElement> _needsMulti = new List<VisualElement>();
        readonly (VisualElement btn, HudIconElement icon) _space;
        readonly VisualElement _rotatePanel, _paintPanel;
        readonly GeneratePopover _generate;
        readonly (VisualElement btn, HudIconElement icon) _generateButton;

        /// <summary>Generate popover visibility (HUD button or G).</summary>
        public static bool GenerateOpen;
        readonly FloatField[] _euler = new FloatField[3];
        Label _paintCount;
        Slider _brush;
        VisualElement _paintInto, _paintSplit;
        string _lastState;

        static readonly Color[] AxisColors =
        {
            new Color32(0xF2, 0x6D, 0x6D, 0xFF), new Color32(0x8B, 0xD9, 0x6A, 0xFF), new Color32(0x6A, 0xA8, 0xF2, 0xFF),
        };

        public SceneHud(SceneView sceneView)
        {
            _root = new VisualElement { name = "smart-colliders-hud", pickingMode = PickingMode.Ignore };
            _root.AddToClassList("sc-hud");
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
            if (sheet != null) _root.styleSheets.Add(sheet);

            // Card
            var card = new VisualElement();
            card.AddToClassList("sc-panel");
            card.AddToClassList("sc-card");
            _title = new Label();
            _title.AddToClassList("sc-card__title");
            card.Add(_title);

            var metrics = new VisualElement();
            metrics.AddToClassList("sc-metrics");
            _accuracy = Metric(metrics, "Accuracy", "Volume overlap between collider and mesh hull");
            _worst = Metric(metrics, "Worst Fit", "Largest gap between collider surface and mesh (scene units)");
            _cost = Metric(metrics, "Physics Cost", "Estimated narrow-phase cost of the colliders");
            card.Add(metrics);

            var counts = new VisualElement();
            counts.AddToClassList("sc-counts");
            var countIcons = new[] { HudIcon.Sphere, HudIcon.Capsule, HudIcon.Box, HudIcon.Hull };
            for (int i = 0; i < 4; i++)
            {
                var item = new VisualElement { tooltip = ((ShapeType)i).ToString() };
                item.AddToClassList("sc-count");
                item.Add(new HudIconElement(countIcons[i]) { Color = IconColor });
                _countLabels[i] = new Label();
                _countLabels[i].AddToClassList("sc-count__label");
                item.Add(_countLabels[i]);
                _countItems[i] = item;
                counts.Add(item);
            }
            card.Add(counts);
            _root.Add(card);

            // Toolbar
            var bar = new VisualElement();
            bar.AddToClassList("sc-panel");
            bar.AddToClassList("sc-toolbar");

            AddMode(bar, SmartColliderEditTool.Mode.Select, HudIcon.Select, "Select parts  (1)\nClick, Shift/Ctrl+Click to add");
            AddMode(bar, SmartColliderEditTool.Mode.Move, HudIcon.Move, "Move  (2)");
            AddMode(bar, SmartColliderEditTool.Mode.Rotate, HudIcon.Rotate, "Rotate  (3)");
            AddMode(bar, SmartColliderEditTool.Mode.Resize, HudIcon.Resize, "Resize  (4)\nHulls scale in their own frame");
            AddMode(bar, SmartColliderEditTool.Mode.Paint, HudIcon.Paint,
                "Paint  (5)\nPaint triangles to split them into a new collider\nCtrl = erase · [ ] or Shift+Wheel = brush · Enter = split");
            _space = Button(bar, HudIcon.Local, "", _ => SmartColliderEditTool.LocalSpace = !SmartColliderEditTool.LocalSpace);
            _space.btn.AddToClassList("sc-btn--compact");
            Divider(bar);

            _needsSelection.Add(Button(bar, HudIcon.FitToMesh, "Fit to Mesh\nRe-tighten around the mesh, keeping orientation",
                a => SmartColliderRefine.FitToMesh(a, Selected(a))).btn);
            _needsSelection.Add(Button(bar, HudIcon.AlignAxes, "Align to Axes\nSnap rotation to the object's local axes and re-fit",
                a => SmartColliderRefine.AlignToAxes(a, Selected(a))).btn);
            _needsSelection.Add(Button(bar, HudIcon.Reset, "Reset\nDiscard manual edits, automatic type",
                a => SmartColliderRefine.ResetParts(a, Selected(a))).btn);
            _needsMulti.Add(Button(bar, HudIcon.Merge, "Merge  (M)\nCombine the selected colliders into one and re-fit",
                a =>
                {
                    int merged = SmartColliderOperations.MergeParts(a, Selected(a));
                    if (merged >= 0) PartSelection.Set(a, merged);
                }).btn);
            var delete = Button(bar, HudIcon.Delete, "Remove collider  (Delete)\nThe part's triangles get no collider",
                a =>
                {
                    SmartColliderOperations.RemoveParts(a, Selected(a));
                    PartSelection.Clear(a);
                }).btn;
            delete.AddToClassList("sc-btn--danger");
            _needsSelection.Add(delete);
            Divider(bar);

            var typeIcons = new[] { HudIcon.Sphere, HudIcon.Capsule, HudIcon.Box, HudIcon.Hull };
            var typeNames = new[] { "Sphere", "Capsule", "Box", "Convex Hull" };
            for (int i = 0; i < 4; i++)
            {
                var type = (ShapeType)i;
                _types[i] = Button(bar, typeIcons[i], $"Convert to {typeNames[i]}", a =>
                {
                    foreach (int p in Selected(a)) SmartColliderOperations.SetType(a, p, type);
                });
                _needsSelection.Add(_types[i].btn);
            }
            Divider(bar);

            _generateButton = Button(bar, HudIcon.Generate, "Generate…  (G)\nMode, decomposition and hull options",
                _ => GenerateOpen = !GenerateOpen);

            var bake = new VisualElement { tooltip = "Create the real Unity colliders" };
            bake.AddToClassList("sc-primary");
            var bakeLabel = new Label("Bake");
            bakeLabel.AddToClassList("sc-primary__label");
            bake.Add(bakeLabel);
            bake.AddManipulator(new Clickable(() => Run(a => SmartColliderBaker.Bake(a))));
            bar.Add(bake);
            _root.Add(bar);

            _generate = new GeneratePopover(() => Current, () => { _lastState = null; SceneView.RepaintAll(); });
            _root.Add(_generate);
            _rotatePanel = BuildRotatePanel();
            _root.Add(_rotatePanel);
            _paintPanel = BuildPaintPanel();
            _root.Add(_paintPanel);

            _hint = new Label();
            _hint.AddToClassList("sc-hint");
            _root.Add(_hint);

            sceneView.rootVisualElement.Add(_root);
        }

        public void Dispose() => _root.RemoveFromHierarchy();

        public void SetVisible(bool visible) =>
            _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        static SmartColliderAuthoring Current =>
            Selection.activeGameObject != null ? Selection.activeGameObject.GetComponent<SmartColliderAuthoring>() : null;

        static List<int> Selected(SmartColliderAuthoring a) => PartSelection.Get(a).ToList();

        // ---------- sub panels ----------

        VisualElement BuildRotatePanel()
        {
            var panel = new VisualElement();
            panel.AddToClassList("sc-panel");
            panel.AddToClassList("sc-subbar");
            for (int axis = 0; axis < 3; axis++)
            {
                int ax = axis;
                var group = new VisualElement();
                group.AddToClassList("sc-axis");
                var label = new Label("XYZ"[axis].ToString());
                label.AddToClassList("sc-axis__label");
                label.style.color = AxisColors[axis];
                group.Add(label);
                group.Add(Chip("−90", $"Rotate −90° around {"XYZ"[axis]} (Local/Global per toggle)",
                    a => SmartColliderRefine.RotateParts(a, Selected(a), ax, -90f, SmartColliderEditTool.LocalSpace)));
                var field = new FloatField { isDelayed = true, tooltip = $"{"XYZ"[axis]} rotation (degrees, relative to the object)" };
                field.AddToClassList("sc-field");
                field.RegisterValueChangedCallback(_ => Run(a =>
                {
                    var e = new Vector3(_euler[0].value, _euler[1].value, _euler[2].value);
                    SmartColliderRefine.SetRotation(a, Selected(a), e);
                }));
                _euler[axis] = field;
                group.Add(field);
                group.Add(Chip("+90", $"Rotate +90° around {"XYZ"[axis]}",
                    a => SmartColliderRefine.RotateParts(a, Selected(a), ax, 90f, SmartColliderEditTool.LocalSpace)));
                panel.Add(group);
            }
            return panel;
        }

        VisualElement BuildPaintPanel()
        {
            var panel = new VisualElement();
            panel.AddToClassList("sc-panel");
            panel.AddToClassList("sc-subbar");

            var brushIcon = new HudIconElement(HudIcon.Paint) { Color = IconColor };
            brushIcon.AddToClassList("sc-icon--small");
            panel.Add(brushIcon);
            _brush = new Slider(PaintSession.MinBrush, PaintSession.MaxBrush) { tooltip = "Brush size ( [ ] or Shift+Wheel )" };
            _brush.AddToClassList("sc-slider");
            _brush.RegisterValueChangedCallback(e => PaintSession.BrushPixels = e.newValue);
            panel.Add(_brush);

            _paintCount = new Label();
            _paintCount.AddToClassList("sc-subbar__count");
            panel.Add(_paintCount);

            SmallButton(panel, HudIcon.Hull, "Paint the selected colliders' triangles\n(start a split from an existing collider)",
                a => PaintSession.PaintParts(a, Selected(a)));
            SmallButton(panel, HudIcon.Grow, "Grow\nAdd one ring of neighbouring triangles", PaintSession.Grow);
            SmallButton(panel, HudIcon.Connected, "Fill Connected\nExtend to every connected triangle (whole mesh island)", PaintSession.FillConnected);
            SmallButton(panel, HudIcon.Clear, "Clear paint  (Esc)", PaintSession.Clear);

            _paintInto = Chip("Add to Part", "Move the painted triangles into the selected collider", a => SmartColliderEditTool.SplitPaint(a, intoSelected: true));
            _paintInto.AddToClassList("sc-chip--wide");
            panel.Add(_paintInto);

            _paintSplit = new VisualElement { tooltip = "Create a new collider from the painted triangles  (Enter)" };
            _paintSplit.AddToClassList("sc-primary");
            _paintSplit.AddToClassList("sc-primary--small");
            var l = new Label("Split");
            l.AddToClassList("sc-primary__label");
            _paintSplit.Add(l);
            _paintSplit.AddManipulator(new Clickable(() => Run(a => SmartColliderEditTool.SplitPaint(a))));
            panel.Add(_paintSplit);
            return panel;
        }

        VisualElement Chip(string text, string tooltip, Action<SmartColliderAuthoring> action)
        {
            var chip = new Label(text) { tooltip = tooltip };
            chip.AddToClassList("sc-chip");
            chip.AddManipulator(new Clickable(() => Run(action)));
            return chip;
        }

        void SmallButton(VisualElement parent, HudIcon icon, string tooltip, Action<SmartColliderAuthoring> action)
        {
            var b = Button(parent, icon, tooltip, action);
            b.btn.AddToClassList("sc-btn--small");
        }

        // ---------- build helpers ----------

        static Label Metric(VisualElement parent, string caption, string tooltip)
        {
            var col = new VisualElement { tooltip = tooltip };
            col.AddToClassList("sc-metric");
            var value = new Label("–");
            value.AddToClassList("sc-metric__value");
            var cap = new Label(caption);
            cap.AddToClassList("sc-metric__caption");
            col.Add(value);
            col.Add(cap);
            parent.Add(col);
            return value;
        }

        static void Divider(VisualElement bar)
        {
            var d = new VisualElement();
            d.AddToClassList("sc-divider");
            bar.Add(d);
        }

        void AddMode(VisualElement bar, SmartColliderEditTool.Mode mode, HudIcon icon, string tooltip)
        {
            var b = Button(bar, icon, tooltip, _ => SmartColliderEditTool.CurrentMode = mode);
            _modes[mode] = b;
        }

        (VisualElement btn, HudIconElement icon) Button(VisualElement bar, HudIcon icon, string tooltip, Action<SmartColliderAuthoring> action)
        {
            var b = new VisualElement { tooltip = tooltip };
            b.AddToClassList("sc-btn");
            var i = new HudIconElement(icon) { Color = IconColor };
            b.Add(i);
            b.AddManipulator(new Clickable(() => Run(action)));
            bar.Add(b);
            return (b, i);
        }

        void Run(Action<SmartColliderAuthoring> action)
        {
            var a = Current;
            if (a == null) return;
            action(a);
            _lastState = null;
            SceneView.RepaintAll();
        }

        // ---------- per-frame update ----------

        public void Update(SceneView sceneView, SmartColliderAuthoring a)
        {
            Refresh(a);
            Position(sceneView, a);
        }

        void Refresh(SmartColliderAuthoring a)
        {
            var selected = PartSelection.Get(a);
            var mode = SmartColliderEditTool.CurrentMode;
            bool local = SmartColliderEditTool.LocalSpace;
            int painted = PaintSession.Count(a);
            string rot = selected.Count > 0 ? a.Parts[selected[0]].Shape.Rotation.GetHashCode().ToString() : "";
            string state = $"{a.GetHashCode()}|{mode}|{GenerateOpen}|{(int)a.generateMode}|{a.decomposition.GetHashCode()}|{a.fitSettings.MaxHullVertices}|{local}|{painted}|{PaintSession.BrushPixels:F1}|{rot}|{string.Join(",", selected)}|{Signature(a)}";
            if (state == _lastState) return;
            _lastState = state;

            IEnumerable<int> scope = selected.Count > 0 ? selected : Enumerable.Range(0, a.Parts.Count);
            var parts = scope.Select(i => a.Parts[i]).Where(p => p.HasFit).ToList();

            _title.text = selected.Count > 0
                ? $"{selected.Count} of {a.Parts.Count} parts selected"
                : $"{a.Name()} · {a.Parts.Count} parts";

            if (parts.Count > 0)
            {
                var m = MetricsSummary.Of(a, selected);
                _accuracy.text = m.AccuracyText;
                _accuracy.style.color = m.AccuracyColor;
                _worst.text = m.WorstText;
                _cost.text = m.CostText;
                _cost.style.color = m.CostColor;
            }

            for (int t = 0; t < 4; t++)
            {
                int n = parts.Count(p => (int)p.Type == t);
                _countLabels[t].text = $"×{n}";
                _countItems[t].EnableInClassList("sc-count--zero", n == 0);
            }

            foreach (var kv in _modes)
            {
                bool on = kv.Key == mode;
                kv.Value.btn.EnableInClassList("sc-btn--on", on);
                kv.Value.icon.Color = on ? SmartCollidersTheme.Accent : IconColor;
            }

            bool any = selected.Count > 0;
            foreach (var b in _needsSelection) b.SetEnabled(any);
            foreach (var b in _needsMulti) b.SetEnabled(selected.Count > 1);

            _space.icon.Icon = local ? HudIcon.Local : HudIcon.Global;
            _space.btn.tooltip = local
                ? "Handle space: Local  (X)\nMove/rotate along the collider's own axes"
                : "Handle space: Global  (X)\nMove/rotate along world axes";

            bool rotating = mode == SmartColliderEditTool.Mode.Rotate && any;
            _generate.style.display = GenerateOpen ? DisplayStyle.Flex : DisplayStyle.None;
            _generateButton.btn.EnableInClassList("sc-btn--on", GenerateOpen);
            if (GenerateOpen) _generate.Refresh(a);
            _rotatePanel.style.display = rotating ? DisplayStyle.Flex : DisplayStyle.None;
            if (rotating)
            {
                Vector3 e = ((Quaternion)a.Parts[selected[0]].Shape.Rotation).eulerAngles;
                for (int k = 0; k < 3; k++)
                {
                    float v = Mathf.Round(Mathf.DeltaAngle(0f, e[k]) * 10f) / 10f;
                    if (_euler[k].panel?.focusController?.focusedElement != _euler[k]) _euler[k].SetValueWithoutNotify(v);
                }
            }

            bool paintMode = mode == SmartColliderEditTool.Mode.Paint;
            _paintPanel.style.display = paintMode ? DisplayStyle.Flex : DisplayStyle.None;
            if (paintMode)
            {
                _brush.SetValueWithoutNotify(PaintSession.BrushPixels);
                _paintCount.text = painted == 1 ? "1 tri" : $"{painted:N0} tris";
                _paintSplit.SetEnabled(painted > 0);
                _paintInto.SetEnabled(painted > 0 && selected.Count == 1);
            }

            // Highlight the type shared by the whole selection.
            int sharedType = any && parts.Select(p => p.Type).Distinct().Count() == 1 ? (int)parts[0].Type : -1;
            for (int t = 0; t < 4; t++)
            {
                _types[t].btn.EnableInClassList("sc-btn--current-type", t == sharedType);
                _types[t].icon.Color = t == sharedType ? SmartCollidersTheme.Selection : IconColor;
            }

            _hint.style.display = SmartCollidersPreferences.ShowHints ? DisplayStyle.Flex : DisplayStyle.None;
            _hint.text = paintMode
                ? (painted > 0 ? "Enter split · Ctrl erase · [ ] brush · Esc clear" : "Drag on the mesh to paint triangles · Ctrl erases")
                : any
                ? (mode == SmartColliderEditTool.Mode.Resize && selected.Count > 1 ? "Resize works on one part at a time" : "Del remove · M merge · F frame · X local/global · Esc deselect")
                : "Click a collider to select it";
        }

        static string Signature(SmartColliderAuthoring a)
        {
            unchecked
            {
                int h = a.Parts.Count;
                foreach (var p in a.Parts) h = h * 31 + ((int)p.Type * 7919) ^ p.Report.Accuracy.GetHashCode() ^ p.Report.WorstFit.GetHashCode();
                return h.ToString();
            }
        }

        void Position(SceneView sceneView, SmartColliderAuthoring a)
        {
            var selected = PartSelection.Get(a);
            IEnumerable<int> scope = selected.Count > 0 ? selected : Enumerable.Range(0, a.Parts.Count);

            // Screen-space bounds of the parts.
            Rect gui = default;
            bool init = false;
            Matrix4x4 m = a.transform.localToWorldMatrix;
            Vector3 camForward = sceneView.camera.transform.forward;
            Vector3 camPos = sceneView.camera.transform.position;
            foreach (int i in scope)
            {
                var part = a.Parts[i];
                if (!part.HasFit) continue;
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
                    var local = new Vector3((k & 1) == 0 ? min.x : max.x, (k & 2) == 0 ? min.y : max.y, (k & 4) == 0 ? min.z : max.z);
                    var world = m.MultiplyPoint3x4(local);
                    if (Vector3.Dot(world - camPos, camForward) <= 0.01f) continue; // behind camera
                    var g = HandleUtility.WorldToGUIPoint(world);
                    if (!init) { gui = new Rect(g, Vector2.zero); init = true; }
                    else gui = Rect.MinMaxRect(Mathf.Min(gui.xMin, g.x), Mathf.Min(gui.yMin, g.y), Mathf.Max(gui.xMax, g.x), Mathf.Max(gui.yMax, g.y));
                }
            }

            var root = sceneView.rootVisualElement;
            float viewW = root.layout.width, viewH = root.layout.height;
            float w = _root.layout.width, h = _root.layout.height;
            if (float.IsNaN(w) || float.IsNaN(viewW) || w <= 0f) return;

            // GUI point → rootVisualElement local (robust to toolbar/overlay offsets).
            Vector2 ToLocal(Vector2 guiPoint)
            {
                Vector2 screen = GUIUtility.GUIToScreenPoint(guiPoint);
                return root.WorldToLocal(screen - sceneView.position.position);
            }

            const float gap = 18f, margin = 10f;
            Vector2 anchor;
            if (!init)
            {
                anchor = new Vector2(viewW * 0.5f, viewH - h - margin);
            }
            else
            {
                Vector2 below = ToLocal(new Vector2(gui.center.x, gui.yMax));
                Vector2 above = ToLocal(new Vector2(gui.center.x, gui.yMin));
                anchor = below.y + gap + h <= viewH - margin
                    ? new Vector2(below.x, below.y + gap)
                    : above.y - gap - h >= margin ? new Vector2(above.x, above.y - gap - h)
                    : new Vector2(below.x, viewH - h - margin); // object fills the view → dock bottom
            }

            float left = Mathf.Clamp(anchor.x - w * 0.5f, margin, Mathf.Max(margin, viewW - w - margin));
            float top = Mathf.Clamp(anchor.y, margin, Mathf.Max(margin, viewH - h - margin));
            _root.style.left = Mathf.Round(left);
            _root.style.top = Mathf.Round(top);
        }
    }

    internal static class AuthoringNameExtensions
    {
        public static string Name(this SmartColliderAuthoring a) => a.gameObject.name;
    }

    /// <summary>Creates/updates one HUD per Scene view.</summary>
    [InitializeOnLoad]
    internal static class SceneHudManager
    {
        static readonly Dictionary<SceneView, SceneHud> s_Huds = new Dictionary<SceneView, SceneHud>();

        static SceneHudManager()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            ToolManager.activeToolChanged += SceneView.RepaintAll;
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                foreach (var h in s_Huds.Values) h.Dispose();
                s_Huds.Clear();
            };
        }

        static void OnSceneGUI(SceneView sceneView)
        {
            var a = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponent<SmartColliderAuthoring>() : null;
            bool show = SmartColliderEditTool.IsActive && a != null && SmartCollidersPreferences.ShowHud
                && (a.Parts.Count > 0 || SmartColliderEditTool.CurrentMode == SmartColliderEditTool.Mode.Paint);

            if (!s_Huds.TryGetValue(sceneView, out var hud))
            {
                if (!show) return;
                foreach (var dead in s_Huds.Keys.Where(k => k == null).ToList()) s_Huds.Remove(dead);
                s_Huds[sceneView] = hud = new SceneHud(sceneView);
            }

            hud.SetVisible(show);
            if (show && Event.current.type == EventType.Repaint) hud.Update(sceneView, a);
        }
    }
}
