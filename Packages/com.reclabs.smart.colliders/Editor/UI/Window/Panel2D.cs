using System;
using System.Collections.Generic;
using RecLabs.SmartColliders.EditorTools;
using RecLabs.SmartColliders.Geometry;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>2D editing UI for a SpriteRenderer: mode, accuracy, decomposition, live metrics, Bake / Clear.</summary>
    internal sealed class Panel2D : VisualElement
    {
        static readonly string[] ModeLabels = { "Auto", "Circle", "Box", "Capsule", "Polygon", "Decompose" };
        static readonly string[] ModeTips =
        {
            "Cheapest primitive that meets the accuracy, else the outline polygon",
            "One CircleCollider2D", "One BoxCollider2D (rotated boxes get a child)", "One CapsuleCollider2D (rotated capsules get a child)",
            "PolygonCollider2D from the sprite outline",
            "Convex pieces, each fitted with its best primitive or a polygon path",
        };

        readonly SmartCollider2DAuthoring _a;
        readonly SerializedObject _so;
        readonly Segmented _mode;
        readonly VisualElement _decompose;
        readonly Label _accuracy, _count, _types, _status;
        readonly ChipRow _maxChips;
        string _lastSig;

        public Panel2D(SmartCollider2DAuthoring a)
        {
            _a = a;
            _so = new SerializedObject(a);
            UIKit.ApplyStyle(this);
            AddToClassList("sc-panel-root");

            var header = new VisualElement().Class("sc-header");
            var titles = new VisualElement().Class("sc-header__titles");
            titles.Add(UIKit.Text(a.name, "sc-header__title"));
            _status = UIKit.Text("", "sc-header__subtitle");
            titles.Add(_status);
            header.Add(UIKit.Icon(HudIcon.Sprite, SmartCollidersTheme.Accent, "sc-brand__icon"));
            header.Add(titles);
            Add(header);

            var card = new VisualElement().Class("sc-card", "sc-metrics-card");
            var row = new VisualElement().Class("sc-metrics");
            _accuracy = Metric(row, "Accuracy", "Sprite outline area / collider area");
            _count = Metric(row, "Colliders", "Number of Collider2D components / polygon paths");
            _types = Metric(row, "Types", "Circle · Capsule · Box · Polygon");
            card.Add(row);
            Add(card);

            UIKit.Section(this, HudIcon.Generate, "Generate 2D", out var body, "preview in Scene view");
            _mode = new Segmented(ModeLabels, ModeTips);
            _mode.Changed += v => Set("mode", p => p.enumValueIndex = v);
            body.Add(_mode);

            AddSlider(body, "Min Accuracy", "Primitives are used when outline area / collider area ≥ this", "minAccuracy", 0.5f, 1f);
            AddSlider(body, "Simplify (px)", "Outline simplification tolerance in sprite pixels", "simplifyPixels", 0f, 8f);

            _decompose = new VisualElement().Class("sc-suboptions");
            body.Add(_decompose);
            var m = UIKit.Row(_decompose, "Max Colliders", "Upper bound on the number of colliders");
            _maxChips = new ChipRow(new float[] { 2, 5, 10, 25, 50 }, x => x.ToString("0"));
            _maxChips.Picked += v => Set("maxColliders", p => p.intValue = (int)v);
            m.Add(_maxChips);
            var tol = UIKit.Row(_decompose, "Merge", "Merge neighbouring pieces whose combined hull wastes at most this fraction of their area");
            var tolSlider = new Slider(0f, 0.5f) { showInputField = true }.Class("sc-slider");
            tolSlider.BindProperty(_so.FindProperty("mergeTolerance"));
            tol.Add(tolSlider);

            var allowed = UIKit.Row(body, "Allowed", "Shapes the automatic selection may use");
            var icons = new[] { HudIcon.Sphere, HudIcon.Capsule, HudIcon.Box, HudIcon.Hull };
            var props = new[] { "allowCircle", "allowCapsule", "allowBox", "allowPolygon" };
            var names = new[] { "Circle", "Capsule", "Box", "Polygon" };
            for (int i = 0; i < 4; i++)
            {
                string prop = props[i];
                var b = UIKit.IconButton(icons[i], () => Set(prop, p => p.boolValue = !p.boolValue), $"Allow {names[i]}", out var icon);
                b.AddToClassList("sc-icon-toggle");
                b.userData = (prop, icon);
                allowed.Add(b);
            }

            UIKit.Section(this, HudIcon.Bake, "Bake", out var bake, "Collider2D components");
            var mat = UIKit.Row(bake, "Material", "PhysicsMaterial2D for every baked collider");
            var matField = new ObjectField { objectType = typeof(PhysicsMaterial2D), allowSceneObjects = false }.Class("sc-object");
            matField.BindProperty(_so.FindProperty("material"));
            mat.Add(matField);
            var trig = UIKit.Row(bake, "Is Trigger");
            var trigToggle = new Toggle().Class("sc-toggle");
            trigToggle.BindProperty(_so.FindProperty("isTrigger"));
            trig.Add(trigToggle);
            var actions = new VisualElement().Class("sc-actions", "sc-actions--bake");
            actions.Add(UIKit.Primary("Bake", () => { SmartCollider2D.Bake(_a); _lastSig = null; Refresh(); }, "Create / replace the Collider2D components"));
            actions.Add(UIKit.Button("Clear", () => { SmartCollider2D.Clear(_a); _lastSig = null; Refresh(); }, "Remove the colliders created by the last bake"));
            actions.Add(UIKit.Button("Bake & Finish", () =>
            {
                if (EditorUtility.DisplayDialog("Bake & Finish", "Bake and remove the Smart Colliders 2D component (Undo restores it)?", "Bake & Finish", "Cancel"))
                    SmartCollider2D.BakeAndRemoveAuthoring(_a);
            }, "Bake, then remove the editing component"));
            bake.Add(actions);

            schedule.Execute(Refresh).Every(300);
            Refresh();
        }

        static Label Metric(VisualElement parent, string caption, string tooltip)
        {
            var col = new VisualElement { tooltip = tooltip }.Class("sc-metric");
            var value = UIKit.Text("–", "sc-metric__value");
            col.Add(value);
            col.Add(UIKit.Text(caption, "sc-metric__caption"));
            parent.Add(col);
            return value;
        }

        void AddSlider(VisualElement body, string label, string tooltip, string path, float min, float max)
        {
            var row = UIKit.Row(body, label, tooltip);
            var s = new Slider(min, max) { showInputField = true }.Class("sc-slider", "sc-slider--wide");
            s.BindProperty(_so.FindProperty(path));
            row.Add(s);
        }

        void Set(string path, Action<SerializedProperty> set)
        {
            if (_a == null) return;
            _so.Update();
            var p = _so.FindProperty(path);
            set(p);
            _so.ApplyModifiedProperties();
            _lastSig = null;
            Refresh();
            SceneView.RepaintAll();
        }

        void Refresh()
        {
            if (_a == null) return;
            var sr = _a.GetComponent<SpriteRenderer>();
            string sig = $"{EditorJsonUtility.ToJson(_a)}|{(sr != null && sr.sprite != null ? sr.sprite.GetInstanceID() : 0)}|{sr?.flipX}|{sr?.flipY}";
            if (sig == _lastSig) return;
            _lastSig = sig;
            _so.Update();

            _mode.SetValueWithoutNotify((int)_a.mode);
            _decompose.style.display = _a.mode == Generate2DMode.Decompose ? DisplayStyle.Flex : DisplayStyle.None;
            _maxChips.Highlight(_a.maxColliders);
            this.Query<VisualElement>(className: "sc-icon-toggle").ForEach(b =>
            {
                if (!(b.userData is ValueTuple<string, HudIconElement> data)) return;
                bool on = _so.FindProperty(data.Item1).boolValue;
                b.EnableInClassList("sc-icon-toggle--on", on);
                data.Item2.Color = on ? SmartCollidersTheme.Accent : UIKit.IconMuted;
            });

            var r = Preview2D.Get(_a);
            _accuracy.text = r.Shapes.Count > 0 ? $"{r.Accuracy * 100f:0}%" : "–";
            _accuracy.style.color = r.Accuracy >= 0.9f ? MetricsSummary.Good : r.Accuracy >= 0.75f ? SmartCollidersTheme.Accent : MetricsSummary.Bad;
            _count.text = r.Shapes.Count.ToString();
            _types.text = $"{r.Count(Shape2DType.Circle)}·{r.Count(Shape2DType.Capsule)}·{r.Count(Shape2DType.Box)}·{r.Count(Shape2DType.Polygon)}";
            _status.text = _a.IsBaked ? $"{_a.bakedColliders.Count} collider(s) baked" : "Not baked — preview only";
        }
    }

    [CustomEditor(typeof(SmartCollider2DAuthoring))]
    internal sealed class SmartCollider2DAuthoringEditor : Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            UIKit.ApplyStyle(root);
            root.AddToClassList("sc-inspector");
            root.Add(new Panel2D((SmartCollider2DAuthoring)target));
            return root;
        }
    }

    /// <summary>Scene view preview of the 2D result of every selected SmartCollider2DAuthoring (cached per settings).</summary>
    [InitializeOnLoad]
    internal static class Preview2D
    {
        static readonly Dictionary<int, (string sig, SmartCollider2D.Result result)> s_Cache = new Dictionary<int, (string, SmartCollider2D.Result)>();

        static Preview2D()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed += s_Cache.Clear;
        }

        public static SmartCollider2D.Result Get(SmartCollider2DAuthoring a)
        {
            var sr = a.GetComponent<SpriteRenderer>();
            string sig = $"{EditorJsonUtility.ToJson(a)}|{(sr != null && sr.sprite != null ? sr.sprite.GetInstanceID() : 0)}|{sr?.flipX}|{sr?.flipY}";
            int id = a.GetInstanceID();
            if (s_Cache.TryGetValue(id, out var c) && c.sig == sig) return c.result;
            var r = SmartCollider2D.Compute(a);
            s_Cache[id] = (sig, r);
            return r;
        }

        static void OnSceneGUI(SceneView sv)
        {
            if (Event.current.type != EventType.Repaint || !PreviewPrefs.Enabled) return;
            foreach (var go in Selection.gameObjects)
            {
                var a = go.GetComponent<SmartCollider2DAuthoring>();
                if (a == null || !a.isActiveAndEnabled) continue;
                var r = Get(a);
                var m = a.transform.localToWorldMatrix;
                var palette = new[] { SmartCollidersTheme.Accent, SmartCollidersTheme.Selection, new Color(0.43f, 0.9f, 0.63f), new Color(0.42f, 0.66f, 0.95f) };
                for (int i = 0; i < r.Shapes.Count; i++)
                {
                    var color = palette[i % palette.Length];
                    var pts = Outline(r.Shapes[i]);
                    var world = new Vector3[pts.Count + 1];
                    for (int k = 0; k <= pts.Count; k++) world[k] = m.MultiplyPoint3x4(pts[k % pts.Count]);
                    if (r.Shapes[i].Type != Shape2DType.Polygon)
                        using (new Handles.DrawingScope(new Color(color.r, color.g, color.b, Mathf.Clamp01(0.16f * SmartCollidersPreferences.FillOpacity))))
                            Handles.DrawAAConvexPolygon(world);
                    using (new Handles.DrawingScope(color))
                        Handles.DrawAAPolyLine(2.4f * SmartCollidersPreferences.WireWidth, world);
                }
            }
        }

        /// <summary>Local-space outline of a shape (closed, no repeated last vertex).</summary>
        public static List<Vector3> Outline(in Shape2D s)
        {
            var pts = new List<Vector3>();
            switch (s.Type)
            {
                case Shape2DType.Polygon:
                    foreach (var p in s.Path) pts.Add(new Vector3(p.x, p.y, 0));
                    break;
                case Shape2DType.Circle:
                    for (int i = 0; i < 48; i++)
                    {
                        float t = i / 48f * Mathf.PI * 2f;
                        pts.Add(new Vector3(s.Center.x + Mathf.Cos(t) * s.Radius, s.Center.y + Mathf.Sin(t) * s.Radius, 0));
                    }
                    break;
                case Shape2DType.Box:
                {
                    var q = Quaternion.Euler(0, 0, s.Angle);
                    var c = new Vector3(s.Center.x, s.Center.y, 0);
                    float hx = s.Size.x * 0.5f, hy = s.Size.y * 0.5f;
                    foreach (var v in new[] { new Vector3(-hx, -hy), new Vector3(hx, -hy), new Vector3(hx, hy), new Vector3(-hx, hy) })
                        pts.Add(c + q * v);
                    break;
                }
                case Shape2DType.Capsule:
                {
                    var q = Quaternion.Euler(0, 0, s.Angle);
                    var c = new Vector3(s.Center.x, s.Center.y, 0);
                    float r = s.Size.x * 0.5f, seg = Mathf.Max(0f, s.Size.y * 0.5f - r);
                    for (int i = 0; i <= 16; i++)
                    {
                        float t = Mathf.PI * i / 16f;
                        pts.Add(c + q * new Vector3(Mathf.Cos(t) * r, seg + Mathf.Sin(t) * r));
                    }
                    for (int i = 0; i <= 16; i++)
                    {
                        float t = Mathf.PI + Mathf.PI * i / 16f;
                        pts.Add(c + q * new Vector3(Mathf.Cos(t) * r, -seg + Mathf.Sin(t) * r));
                    }
                    break;
                }
            }
            return pts;
        }
    }
}
