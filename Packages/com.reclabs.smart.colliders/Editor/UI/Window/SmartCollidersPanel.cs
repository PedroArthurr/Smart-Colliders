using System;
using System.Collections.Generic;
using System.Linq;
using RecLabs.SmartColliders.EditorTools;
using RecLabs.SmartColliders.Geometry;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>
    /// Full editing UI for one GameObject: metrics, Generate (modes + decomposition options with live progress),
    /// Fit settings, part list (synced with the Scene selection) and Bake. Hosted by the window and the inspector.
    /// Every change goes through SerializedObject or the operation classes, so everything is undoable.
    /// </summary>
    internal sealed class SmartCollidersPanel : VisualElement
    {
        static readonly string[] ModeLabels = { "Single", "Per Mesh", "Decompose", "Per Bone" };
        static readonly string[] ModeTips =
        {
            "One collider around everything",
            "One collider per mesh (MeshFilter / SkinnedMeshRenderer)",
            "Approximate convex decomposition into several colliders",
            "Skinned meshes: one collider per bone, following the skeleton",
        };
        static readonly string[] QualityLabels = { "Low", "Medium", "High", "Ultra" };
        static readonly string[] EngineLabels = { "Auto", "V-HACD", "CoACD", "Built-in" };
        static readonly string[] TargetLabels = { "Same Object", "Container", "Source Objects" };
        static readonly string[] TargetTips =
        {
            "Colliders on this GameObject (rotated primitives get a child)",
            "A child container with one named child per collider",
            "Each collider on the GameObject that owns its mesh / bone (follows animated parts)",
        };

        readonly bool _compact;
        GameObject _target;
        SmartColliderAuthoring _authoring;
        int _authoringId;
        bool _has2D;
        SerializedObject _so;
        string _lastSignature;

        // Live elements.
        Label _title, _subtitle, _scope, _accuracy, _worst, _cost, _engineNote, _progressLabel, _bakeStatus, _partsHint;
        readonly Label[] _counts = new Label[4];
        readonly VisualElement[] _countItems = new VisualElement[4];
        VisualElement _staleBanner, _decomposeOptions, _progress, _generateButton, _containerRow, _cleanLink, _editButton, _pauseButton;
        HudIconElement _pauseIcon;
        Segmented _mode, _quality, _engine, _bakeTarget;
        ChipRow _maxChips, _tolChips;
        IntegerField _maxField;
        Toggle _autoMerge, _async;
        Bar _bar;
        ListView _list;
        TextField _search;
        readonly List<int> _visible = new List<int>();
        readonly (VisualElement btn, HudIconElement icon)[] _allowed = new (VisualElement, HudIconElement)[4];
        bool _syncingSelection;

        public SmartCollidersPanel(bool compact)
        {
            _compact = compact;
            AddToClassList("sc-panel-root");
            UIKit.ApplyStyle(this);
            RegisterCallback<AttachToPanelEvent>(_ => Subscribe());
            RegisterCallback<DetachFromPanelEvent>(_ => Unsubscribe());
            schedule.Execute(Tick).Every(250);
        }

        void Subscribe()
        {
            Undo.undoRedoPerformed += OnUndo;
            PartSelection.Changed += SyncListSelection;
            DecompositionRunner.Changed += RefreshProgress;
            SmartColliderEditTool.ModeChanged += RefreshEditButton;
        }

        void Unsubscribe()
        {
            Undo.undoRedoPerformed -= OnUndo;
            PartSelection.Changed -= SyncListSelection;
            DecompositionRunner.Changed -= RefreshProgress;
            SmartColliderEditTool.ModeChanged -= RefreshEditButton;
        }

        void OnUndo()
        {
            _lastSignature = null;
            Tick();
        }

        // ---------- binding ----------

        public void SetTarget(GameObject go)
        {
            var authoring = go != null ? go.GetComponent<SmartColliderAuthoring>() : null;
            int id = authoring != null ? authoring.GetInstanceID() : 0;
            if (go == _target && id == _authoringId && childCount > 0) return;
            _target = go;
            _authoring = id != 0 ? authoring : null;
            _authoringId = id;
            _has2D = go != null && go.GetComponent<SmartCollider2DAuthoring>() != null;
            _so = authoring != null ? new SerializedObject(authoring) : null;
            _lastSignature = null;
            Rebuild();
        }

        void Rebuild()
        {
            Clear();
            if (_target != null && !HasMeshes(_target) && _target.GetComponent<SpriteRenderer>() != null)
            {
                Build2D();
                return;
            }
            if (_target == null || !HasMeshes(_target))
            {
                Empty(_target == null
                    ? "Select a GameObject with meshes"
                    : $"'{_target.name}' has no MeshFilter or SkinnedMeshRenderer");
                return;
            }
            if (_authoring == null)
            {
                BuildCreate();
                return;
            }
            BuildHeader();
            BuildMetrics();
            BuildGenerate();
            BuildFit();
            BuildParts();
            BuildBake();
            Tick();
            RefreshProgress();
        }

        void Build2D()
        {
            var a2 = _target.GetComponent<SmartCollider2DAuthoring>();
            if (a2 != null) { Add(new Panel2D(a2)); return; }
            var box = new VisualElement().Class("sc-empty");
            box.Add(UIKit.Icon(HudIcon.Sprite, SmartCollidersTheme.Accent, "sc-empty__icon"));
            box.Add(UIKit.Text(_target.name, "sc-empty__title"));
            box.Add(UIKit.Text("Sprite without Smart Colliders 2D. Add it to preview and bake Collider2D shapes.", "sc-empty__text"));
            box.Add(UIKit.Primary("Add Smart Colliders 2D", () =>
            {
                Undo.AddComponent<SmartCollider2DAuthoring>(_target);
                _authoringId = -1;
                Rebuild();
                SceneView.RepaintAll();
            }).Class("sc-empty__button"));
            Add(box);
        }

        static bool HasMeshes(GameObject go) =>
            go.GetComponentInChildren<MeshFilter>(true) != null || go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;

        void Empty(string message)
        {
            var box = new VisualElement().Class("sc-empty");
            box.Add(UIKit.Icon(HudIcon.Hull, UIKit.IconMuted, "sc-empty__icon"));
            box.Add(UIKit.Text(message, "sc-empty__text"));
            Add(box);
        }

        void BuildCreate()
        {
            var box = new VisualElement().Class("sc-empty");
            box.Add(UIKit.Icon(HudIcon.Generate, SmartCollidersTheme.Accent, "sc-empty__icon"));
            box.Add(UIKit.Text(_target.name, "sc-empty__title"));
            box.Add(UIKit.Text("No Smart Colliders yet. Pick how to split the meshes and generate.", "sc-empty__text"));
            var mode = new Segmented(ModeLabels, ModeTips);
            bool skinned = _target.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
            mode.SetValueWithoutNotify(skinned ? (int)GenerateMode.PerBone : (int)GenerateMode.PerMesh);
            mode.SetItemEnabled((int)GenerateMode.PerBone, skinned, skinned ? null : "Needs a SkinnedMeshRenderer");
            box.Add(mode);
            box.Add(UIKit.Primary("Generate Colliders", () =>
            {
                Undo.IncrementCurrentGroup();
                var a = Undo.AddComponent<SmartColliderAuthoring>(_target);
                a.generateMode = (GenerateMode)mode.Value;
                Generate(a);
                SetTarget(_target);
            }, "Add Smart Colliders and generate (Undo removes everything)").Class("sc-empty__button"));
            Add(box);
        }

        // ---------- header + metrics ----------

        void BuildHeader()
        {
            var header = new VisualElement().Class("sc-header");
            var titles = new VisualElement().Class("sc-header__titles");
            _title = UIKit.Text(_target.name, "sc-header__title");
            _subtitle = UIKit.Text("", "sc-header__subtitle");
            titles.Add(_title);
            titles.Add(_subtitle);
            header.Add(titles);

            _editButton = new VisualElement { tooltip = "Edit the colliders in the Scene view (select, move, rotate, resize, paint)" }.Class("sc-edit-button");
            _editButton.Add(UIKit.Icon(HudIcon.Select, null, "sc-edit-button__icon"));
            _editButton.Add(UIKit.Text("Edit in Scene", "sc-edit-button__label"));
            _editButton.AddManipulator(new Clickable(() =>
            {
                if (SmartColliderEditTool.IsActive) SmartColliderEditTool.Deactivate();
                else
                {
                    Selection.activeGameObject = _target;
                    EditorApplication.delayCall += SmartColliderEditTool.Activate;
                }
                EditorApplication.delayCall += RefreshEditButton;
            }));
            header.Add(_editButton);
            Add(header);

            _staleBanner = new VisualElement().Class("sc-banner");
            _staleBanner.Add(UIKit.Text("Source meshes changed since generation.", "sc-banner__text"));
            _staleBanner.Add(UIKit.Button("Regenerate", () => Generate(_authoring)));
            Add(_staleBanner);
            RefreshEditButton();
        }

        void RefreshEditButton()
        {
            if (_editButton == null) return;
            bool on = SmartColliderEditTool.IsActive && Selection.activeGameObject == _target;
            _editButton.EnableInClassList("sc-edit-button--on", on);
        }

        void BuildMetrics()
        {
            var card = new VisualElement().Class("sc-card", "sc-metrics-card");
            _scope = UIKit.Text("", "sc-metrics__scope");
            card.Add(_scope);
            var row = new VisualElement().Class("sc-metrics");
            _accuracy = Metric(row, "Accuracy", "Volume overlap between colliders and the mesh hulls (volume weighted)");
            _worst = Metric(row, "Worst Fit", "Largest gap between a collider surface and its mesh (scene units)");
            _cost = Metric(row, "Physics Cost", "Estimated narrow-phase cost of all colliders");
            card.Add(row);

            var counts = new VisualElement().Class("sc-counts");
            var icons = new[] { HudIcon.Sphere, HudIcon.Capsule, HudIcon.Box, HudIcon.Hull };
            for (int i = 0; i < 4; i++)
            {
                var item = new VisualElement { tooltip = ((ShapeType)i).ToString() }.Class("sc-count");
                item.Add(UIKit.Icon(icons[i], null, "sc-count__icon"));
                _counts[i] = UIKit.Text("×0", "sc-count__label");
                item.Add(_counts[i]);
                _countItems[i] = item;
                counts.Add(item);
            }
            card.Add(counts);
            Add(card);
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

        // ---------- generate ----------

        void BuildGenerate()
        {
            UIKit.Section(this, HudIcon.Generate, "Generate", out var body);

            _mode = new Segmented(ModeLabels, ModeTips);
            _mode.Changed += v => SetProp("generateMode", p => p.enumValueIndex = v);
            body.Add(_mode);
            bool skinned = _target.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
            _mode.SetItemEnabled((int)GenerateMode.PerBone, skinned, skinned ? null : "Needs a SkinnedMeshRenderer");

            _decomposeOptions = new VisualElement().Class("sc-suboptions");
            body.Add(_decomposeOptions);

            var q = UIKit.Row(_decomposeOptions, "Quality", "Low/Medium favour speed, High/Ultra favour accuracy");
            _quality = new Segmented(QualityLabels, new[]
            {
                "Fastest preview (V-HACD when available)",
                "Balanced (V-HACD when available)",
                "Precise concavity cuts (CoACD when available, else built-in)",
                "Maximum precision (slow)",
            });
            _quality.Changed += v => SetProp("decomposition.Quality", p => p.enumValueIndex = v);
            q.Add(_quality);

            var e = UIKit.Row(_decomposeOptions, "Engine", "Decomposition backend");
            _engine = new Segmented(EngineLabels, new[]
            {
                "Pick by quality and plugin availability",
                "V-HACD v4 (native, voxel based, fast)",
                "CoACD (native, collision-aware cuts)",
                "Built-in C# engine (always available)",
            });
            _engine.Changed += v => SetProp("decomposition.Engine", p => p.enumValueIndex = v);
            e.Add(_engine);
            _engine.SetItemEnabled(1, NativeDecomposer.IsAvailable(NativeDecomposer.EngineVHacd), "Native plugin not available on this platform");
            _engine.SetItemEnabled(2, NativeDecomposer.IsAvailable(NativeDecomposer.EngineCoAcd), "This plugin build has no CoACD (see Native~/README)");

            var m = UIKit.Row(_decomposeOptions, "Max Colliders", "Upper bound on the number of colliders");
            _maxChips = new ChipRow(DecompositionSettings.MaxColliderPresets.Select(x => (float)x).ToArray(), x => x.ToString("0"));
            _maxChips.Picked += v => SetProp("decomposition.MaxColliders", p => p.intValue = (int)v);
            m.Add(_maxChips);
            _maxField = new IntegerField { isDelayed = true }.Class("sc-int");
            _maxField.RegisterValueChangedCallback(ev => SetProp("decomposition.MaxColliders", p => p.intValue = Mathf.Clamp(ev.newValue, 1, 512)));
            m.Add(_maxField);

            var am = UIKit.Row(_decomposeOptions, "Auto Merge", "Merge neighbouring parts whose combined hull wastes at most this fraction of their volume");
            _autoMerge = new Toggle().Class("sc-toggle");
            _autoMerge.RegisterValueChangedCallback(ev => SetProp("decomposition.AutoMerge", p => p.boolValue = ev.newValue));
            am.Add(_autoMerge);
            _tolChips = new ChipRow(DecompositionSettings.TolerancePresets, x => $"{x * 100f:0}%");
            _tolChips.Picked += v => SetProp("decomposition.MergeTolerance", p => p.floatValue = v);
            am.Add(_tolChips);

            var bg = UIKit.Row(_decomposeOptions, "Background", "Run asynchronously with pause / resume / cancel");
            _async = new Toggle().Class("sc-toggle");
            _async.RegisterValueChangedCallback(ev => SetProp("decomposition.Async", p => p.boolValue = ev.newValue));
            bg.Add(_async);
            var speed = new SliderInt(2, 100) { tooltip = "Work per editor frame (ms): higher = faster, less responsive editor" }.Class("sc-slider");
            speed.BindProperty(_so.FindProperty("decomposition.YieldMilliseconds"));
            bg.Add(UIKit.Text("Speed", "sc-inline-label"));
            bg.Add(speed);

            _engineNote = UIKit.Text("", "sc-note");
            _decomposeOptions.Add(_engineNote);

            _generateButton = UIKit.Primary("Generate", () => Generate(_authoring), "Replace all colliders (Undo restores them)");
            body.Add(_generateButton);

            _progress = new VisualElement().Class("sc-progress");
            _progressLabel = UIKit.Text("", "sc-progress__label");
            _bar = new Bar();
            var buttons = new VisualElement().Class("sc-progress__buttons");
            _pauseButton = UIKit.IconButton(HudIcon.Pause, () =>
            {
                var r = DecompositionRunner.Current;
                if (r == null) return;
                if (r.IsPaused) r.Resume(); else r.Pause();
            }, "Pause / Resume", out _pauseIcon);
            buttons.Add(_pauseButton);
            var cancel = UIKit.IconButton(HudIcon.Stop, () => DecompositionRunner.Current?.Cancel(), "Cancel (keeps the current colliders)", out _);
            cancel.AddToClassList("sc-icon-button--danger");
            buttons.Add(cancel);
            var top = new VisualElement().Class("sc-progress__top");
            top.Add(_progressLabel);
            top.Add(buttons);
            _progress.Add(top);
            _progress.Add(_bar);
            body.Add(_progress);
        }

        internal static void Generate(SmartColliderAuthoring a)
        {
            if (a == null) return;
            if (a.generateMode == GenerateMode.Decompose && a.decomposition.Async)
            {
                DecompositionRunner.Start(a);
                return;
            }
            Undo.IncrementCurrentGroup();
            if (a.generateMode == GenerateMode.Decompose)
                EditorUtility.DisplayProgressBar("Smart Colliders", "Decomposing…", 0.3f);
            try { SmartColliderOperations.Generate(a); }
            finally { EditorUtility.ClearProgressBar(); }
            SceneView.RepaintAll();
        }

        void RefreshProgress()
        {
            if (_progress == null) return;
            var r = DecompositionRunner.Current;
            bool running = r != null && r.Target == _authoring;
            _progress.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;
            _generateButton.style.display = running ? DisplayStyle.None : DisplayStyle.Flex;
            if (!running) return;
            _bar.Value = r.Progress;
            _bar.Paused = r.IsPaused;
            _pauseIcon.Icon = r.IsPaused ? HudIcon.Play : HudIcon.Pause;
            _progressLabel.text = $"{(r.IsPaused ? "Paused" : r.Status)} · {r.EngineName} · {r.Progress * 100f:0}% · {r.ElapsedSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s · {r.PieceCount} parts";
        }

        // ---------- fit ----------

        void BuildFit()
        {
            UIKit.Section(this, HudIcon.FitToMesh, "Fit", out var body, "auto type selection");

            var allowed = UIKit.Row(body, "Allowed", "Collider types the automatic selection may pick (manual conversion is always possible)");
            var icons = new[] { HudIcon.Sphere, HudIcon.Capsule, HudIcon.Box, HudIcon.Hull };
            var names = new[] { "AllowSphere", "AllowCapsule", "AllowBox", "AllowConvexHull" };
            for (int i = 0; i < 4; i++)
            {
                string prop = "fitSettings." + names[i];
                var b = UIKit.IconButton(icons[i], () => SetProp(prop, p => p.boolValue = !p.boolValue), $"Allow {(ShapeType)i}", out var icon);
                b.AddToClassList("sc-icon-toggle");
                _allowed[i] = (b, icon);
                allowed.Add(b);
            }

            AddSlider(body, "Min Accuracy", "A type must reach this volume overlap to be picked automatically", "fitSettings.MinAccuracy", 0.5f, 1f);
            AddSlider(body, "Prefer Cheap", "Accuracy traded per unit of physics cost when ranking types", "fitSettings.CostWeight", 0f, 0.1f);
            var hv = UIKit.Row(body, "Max Hull Vertices", "Vertex cap of convex hulls (129 ⇒ ≤ 254 triangles, the PhysX limit)");
            var hvSlider = new SliderInt(4, ConvexHull3D.MaxConvexColliderVertices) { showInputField = true }.Class("sc-slider", "sc-slider--wide");
            hvSlider.BindProperty(_so.FindProperty("fitSettings.MaxHullVertices"));
            hv.Add(hvSlider);
            var th = UIKit.Row(body, "Min Thickness", "Minimum box thickness for flat meshes (scene units)");
            var thField = new FloatField().Class("sc-float");
            thField.BindProperty(_so.FindProperty("fitSettings.MinThickness"));
            th.Add(thField);

            var actions = new VisualElement().Class("sc-actions");
            actions.Add(UIKit.Button("Refit All", () => { SmartColliderOperations.RefitAll(_authoring); SceneView.RepaintAll(); },
                "Re-fit every collider with these settings (keeps manual types)"));
            body.Add(actions);
        }

        void AddSlider(VisualElement body, string label, string tooltip, string path, float min, float max)
        {
            var row = UIKit.Row(body, label, tooltip);
            var s = new Slider(min, max) { showInputField = true }.Class("sc-slider", "sc-slider--wide");
            s.BindProperty(_so.FindProperty(path));
            row.Add(s);
        }

        // ---------- parts ----------

        void BuildParts()
        {
            var card = UIKit.Section(this, HudIcon.Hull, "Colliders", out var body, "");
            _partsHint = card.Q<Label>(className: "sc-card__hint");

            _search = new TextField { tooltip = "Filter by name" }.Class("sc-search");
            _search.textEdition.placeholder = "Search colliders";
            _search.RegisterValueChangedCallback(_ => { _lastSignature = null; Tick(); });
            body.Add(_search);

            _list = new ListView
            {
                fixedItemHeight = 28,
                selectionType = SelectionType.Multiple,
                itemsSource = _visible,
                makeItem = MakeRow,
                bindItem = BindRow,
                showAlternatingRowBackgrounds = AlternatingRowBackground.None,
                reorderable = false,
            }.Class("sc-list");
            _list.style.height = _compact ? 180 : 260;
            _list.selectedIndicesChanged += OnListSelection;
            _list.itemsChosen += _ => FrameSelection();
            body.Add(_list);

            var actions = new VisualElement().Class("sc-actions");
            actions.Add(SmallAction(HudIcon.Merge, "Merge selected", a => { int i = SmartColliderOperations.MergeParts(a, Selected()); if (i >= 0) PartSelection.Set(a, i); }, 2));
            actions.Add(SmallAction(HudIcon.FitToMesh, "Fit to mesh", a => SmartColliderRefine.FitToMesh(a, Selected()), 1));
            actions.Add(SmallAction(HudIcon.AlignAxes, "Align to axes", a => SmartColliderRefine.AlignToAxes(a, Selected()), 1));
            actions.Add(SmallAction(HudIcon.Reset, "Reset (automatic type, discard edits)", a => SmartColliderRefine.ResetParts(a, Selected()), 1));
            var types = new[] { HudIcon.Sphere, HudIcon.Capsule, HudIcon.Box, HudIcon.Hull };
            actions.Add(new VisualElement().Class("sc-actions__gap"));
            for (int t = 0; t < 4; t++)
            {
                var type = (ShapeType)t;
                actions.Add(SmallAction(types[t], $"Convert to {type}", a => { foreach (int p in Selected()) SmartColliderOperations.SetType(a, p, type); }, 1));
            }
            actions.Add(new VisualElement().Class("sc-actions__gap"));
            var del = SmallAction(HudIcon.Delete, "Remove selected", a => { SmartColliderOperations.RemoveParts(a, Selected()); PartSelection.Clear(a); }, 1);
            del.AddToClassList("sc-icon-button--danger");
            actions.Add(del);
            body.Add(actions);
        }

        readonly List<(VisualElement button, int minSelection)> _selectionActions = new List<(VisualElement, int)>();

        VisualElement SmallAction(HudIcon icon, string tooltip, Action<SmartColliderAuthoring> action, int minSelection)
        {
            var b = UIKit.IconButton(icon, () =>
            {
                if (_authoring == null) return;
                action(_authoring);
                _lastSignature = null;
                SceneView.RepaintAll();
            }, tooltip, out _);
            b.AddToClassList("sc-icon-button--small");
            _selectionActions.Add((b, minSelection));
            return b;
        }

        List<int> Selected() => PartSelection.Get(_authoring).ToList();

        VisualElement MakeRow()
        {
            var row = new VisualElement().Class("sc-part");
            row.Add(new VisualElement { name = "swatch" }.Class("sc-part__swatch"));
            row.Add(new Label { name = "name" }.Class("sc-part__name"));
            var rename = new TextField { name = "rename", isDelayed = true }.Class("sc-part__rename");
            rename.style.display = DisplayStyle.None;
            row.Add(rename);
            row.Add(new HudIconElement(HudIcon.Bone) { name = "bone", Color = UIKit.IconMuted }.Class("sc-part__bone"));
            row.Add(new Label { name = "auto" }.Class("sc-part__auto"));
            row.Add(new HudIconElement(HudIcon.Box) { name = "type" }.Class("sc-part__type"));
            row.Add(new Label { name = "acc" }.Class("sc-part__acc"));

            row.RegisterCallback<MouseDownEvent>(e =>
            {
                if (e.clickCount != 2 || !(row.userData is int index)) return;
                var label = row.Q<Label>("name");
                var field = row.Q<TextField>("rename");
                field.SetValueWithoutNotify(_authoring.Parts[index].Name);
                label.style.display = DisplayStyle.None;
                field.style.display = DisplayStyle.Flex;
                field.Focus();
                field.SelectAll();
                e.StopPropagation();
            });
            var renameField = row.Q<TextField>("rename");
            renameField.RegisterValueChangedCallback(e =>
            {
                if (row.userData is int index) SmartColliderOperations.Rename(_authoring, index, e.newValue);
                EndRename(row);
                _lastSignature = null;
            });
            renameField.RegisterCallback<FocusOutEvent>(_ => EndRename(row));
            return row;
        }

        static void EndRename(VisualElement row)
        {
            row.Q<TextField>("rename").style.display = DisplayStyle.None;
            row.Q<Label>("name").style.display = DisplayStyle.Flex;
        }

        void BindRow(VisualElement row, int i)
        {
            if (_authoring == null || i >= _visible.Count) return;
            int index = _visible[i];
            if (index >= _authoring.Parts.Count) return;
            var part = _authoring.Parts[index];
            row.userData = index;
            row.Q("swatch").style.backgroundColor = part.Color;
            row.Q<Label>("name").text = part.Name;
            row.Q<Label>("auto").text = part.AutoType ? "auto" : "";
            row.Q("bone").style.display = part.Bone != null ? DisplayStyle.Flex : DisplayStyle.None;
            var type = row.Q<HudIconElement>("type");
            type.Icon = part.Type switch
            {
                ShapeType.Sphere => HudIcon.Sphere,
                ShapeType.Capsule => HudIcon.Capsule,
                ShapeType.Box => HudIcon.Box,
                _ => HudIcon.Hull,
            };
            type.Color = UIKit.IconColor;
            var acc = row.Q<Label>("acc");
            acc.text = part.HasFit ? $"{part.Report.Accuracy * 100f:0}%" : "–";
            acc.style.color = !part.HasFit ? UIKit.IconMuted
                : part.Report.Accuracy >= 0.9f ? MetricsSummary.Good
                : part.Report.Accuracy >= 0.75f ? SmartCollidersTheme.Accent : MetricsSummary.Bad;
            EndRename(row);
        }

        void OnListSelection(IEnumerable<int> indices)
        {
            if (_syncingSelection || _authoring == null) return;
            _syncingSelection = true;
            PartSelection.SetMany(_authoring, indices.Where(i => i < _visible.Count).Select(i => _visible[i]));
            _syncingSelection = false;
            SceneView.RepaintAll();
        }

        void SyncListSelection()
        {
            if (_list == null || _syncingSelection || _authoring == null) return;
            _syncingSelection = true;
            var sel = PartSelection.Get(_authoring);
            var rows = new List<int>();
            for (int r = 0; r < _visible.Count; r++) if (sel.Contains(_visible[r])) rows.Add(r);
            _list.SetSelectionWithoutNotify(rows);
            if (rows.Count > 0) _list.ScrollToItem(rows[0]);
            _syncingSelection = false;
            RefreshActions();
            RefreshMetrics();
        }

        void FrameSelection()
        {
            if (_authoring == null || SceneView.lastActiveSceneView == null) return;
            Selection.activeGameObject = _authoring.gameObject;
            SceneView.lastActiveSceneView.FrameSelected();
        }

        void RefreshActions()
        {
            int n = _authoring != null ? PartSelection.Get(_authoring).Count : 0;
            foreach (var (button, min) in _selectionActions) button.SetEnabled(n >= min);
        }

        // ---------- bake ----------

        void BuildBake()
        {
            UIKit.Section(this, HudIcon.Bake, "Bake", out var body, "real Unity colliders");

            _bakeTarget = new Segmented(TargetLabels, TargetTips);
            _bakeTarget.Changed += v => SetProp("bakeTarget", p => p.enumValueIndex = v);
            body.Add(_bakeTarget);

            _containerRow = UIKit.Row(body, "Container", "Name of the child GameObject holding the colliders");
            var container = new TextField { isDelayed = true }.Class("sc-text");
            container.BindProperty(_so.FindProperty("containerName"));
            _containerRow.Add(container);

            var mat = UIKit.Row(body, "Material", "Physics material assigned to every baked collider");
            var matField = new ObjectField { objectType = typeof(PhysicsMaterial), allowSceneObjects = false }.Class("sc-object");
            matField.BindProperty(_so.FindProperty("material"));
            mat.Add(matField);

            var trig = UIKit.Row(body, "Is Trigger", "Bake colliders as triggers");
            var trigToggle = new Toggle().Class("sc-toggle");
            trigToggle.BindProperty(_so.FindProperty("isTrigger"));
            trig.Add(trigToggle);

            var actions = new VisualElement().Class("sc-actions", "sc-actions--bake");
            actions.Add(UIKit.Primary("Bake", () => { SmartColliderBaker.Bake(_authoring); _lastSignature = null; Tick(); }, "Create / replace the real colliders"));
            actions.Add(UIKit.Button("Clear", () => { SmartColliderBaker.ClearBaked(_authoring); _lastSignature = null; Tick(); }, "Remove the colliders created by the last bake"));
            actions.Add(UIKit.Button("Bake & Finish", () =>
            {
                if (!EditorUtility.DisplayDialog("Bake & Finish", "Bake the colliders and remove the Smart Colliders component (editing state is lost, Undo restores it)?", "Bake & Finish", "Cancel")) return;
                var go = _target;
                SmartColliderBaker.BakeAndRemoveAuthoring(_authoring);
                SetTarget(go);
            }, "Bake, then remove the editing component"));
            body.Add(actions);

            var status = new VisualElement().Class("sc-bake-status");
            _bakeStatus = UIKit.Text("", "sc-note");
            status.Add(_bakeStatus);
            _cleanLink = UIKit.Button("Clean unused meshes", () =>
            {
                int n = SmartColliderBaker.CountUnusedMeshes(_authoring);
                if (n == 0 || !EditorUtility.DisplayDialog("Clean unused meshes", $"Delete {n} hull mesh(es) no baked collider uses? This cannot be undone.", "Delete", "Cancel")) return;
                SmartColliderBaker.CleanUnusedMeshes(_authoring);
                _lastSignature = null;
                Tick();
            }, "Delete hull meshes in the bank that no baked collider references", "sc-link");
            status.Add(_cleanLink);
            body.Add(status);
        }

        // ---------- refresh ----------

        void SetProp(string path, Action<SerializedProperty> set)
        {
            if (_so == null || _so.targetObject == null) return;
            _so.Update();
            var p = _so.FindProperty(path);
            if (p == null) return;
            set(p);
            _so.ApplyModifiedProperties();
            _lastSignature = null;
            Tick();
        }

        void Tick()
        {
            // Component added / removed elsewhere (Undo, inspector, Bake & Finish) → rebuild.
            var current = _target != null ? _target.GetComponent<SmartColliderAuthoring>() : null;
            int id = current != null ? current.GetInstanceID() : 0;
            bool has2D = _target != null && _target.GetComponent<SmartCollider2DAuthoring>() != null;
            if (has2D != _has2D) { _authoringId = -1; SetTarget(_target); return; }
            if (id != _authoringId) { SetTarget(_target); return; }
            if (_authoring == null || _so == null || _so.targetObject == null) return;
            string sig = Signature();
            if (sig == _lastSignature) return;
            _lastSignature = sig;
            _so.Update();

            var a = _authoring;
            _title.text = _target.name;
            _subtitle.text = $"{a.Parts.Count} collider{(a.Parts.Count == 1 ? "" : "s")} · {(a.IsBaked ? $"baked ({a.bakedColliders.Count})" : "not baked")}";
            _staleBanner.style.display = SmartColliderOperations.IsStale(a) ? DisplayStyle.Flex : DisplayStyle.None;

            // Generate.
            _mode.SetValueWithoutNotify((int)a.generateMode);
            _decomposeOptions.style.display = a.generateMode == GenerateMode.Decompose ? DisplayStyle.Flex : DisplayStyle.None;
            var d = a.decomposition;
            _quality.SetValueWithoutNotify((int)d.Quality);
            _engine.SetValueWithoutNotify((int)d.Engine);
            _maxChips.Highlight(d.MaxColliders);
            _maxField.SetValueWithoutNotify(d.MaxColliders);
            _autoMerge.SetValueWithoutNotify(d.AutoMerge);
            _tolChips.Highlight(d.MergeTolerance);
            _tolChips.SetEnabled(d.AutoMerge);
            _async.SetValueWithoutNotify(d.Async);
            var resolved = DecompositionRunner.Resolve(d);
            _engineNote.text = d.Engine == DecompositionEngine.Auto || d.Engine == resolved
                ? $"Engine: {DecompositionRunner.DisplayName(resolved)}"
                : $"Engine: {DecompositionRunner.DisplayName(resolved)} ({DecompositionRunner.DisplayName(d.Engine)} not available)";
            if (_generateButton is Label gl) gl.text = a.Parts.Count > 0 ? "Regenerate" : "Generate";

            // Fit.
            var f = a.fitSettings;
            var allowed = new[] { f.AllowSphere, f.AllowCapsule, f.AllowBox, f.AllowConvexHull };
            for (int i = 0; i < 4; i++)
            {
                _allowed[i].btn.EnableInClassList("sc-icon-toggle--on", allowed[i]);
                _allowed[i].icon.Color = allowed[i] ? SmartCollidersTheme.Accent : UIKit.IconMuted;
            }

            // Parts.
            string filter = _search.value?.Trim() ?? "";
            _visible.Clear();
            for (int i = 0; i < a.Parts.Count; i++)
                if (filter.Length == 0 || a.Parts[i].Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    _visible.Add(i);
            _list.RefreshItems();
            _partsHint.text = filter.Length > 0 ? $"{_visible.Count} of {a.Parts.Count}" : $"{a.Parts.Count} total · double-click to rename";
            SyncListSelection();

            // Bake.
            _bakeTarget.SetValueWithoutNotify((int)a.bakeTarget);
            _containerRow.style.display = a.bakeTarget == BakeTarget.ChildContainer ? DisplayStyle.Flex : DisplayStyle.None;
            int unused = SmartColliderBaker.CountUnusedMeshes(a);
            _bakeStatus.text = a.IsBaked ? $"{a.bakedColliders.Count} collider(s) baked" : "Not baked yet — colliders exist only as preview";
            _cleanLink.style.display = unused > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (_cleanLink is Label cl) cl.text = $"Clean {unused} unused mesh{(unused == 1 ? "" : "es")}";

            RefreshMetrics();
            RefreshActions();
            RefreshEditButton();
        }

        void RefreshMetrics()
        {
            if (_authoring == null || _accuracy == null) return;
            var m = MetricsSummary.Of(_authoring, PartSelection.Get(_authoring));
            int selected = PartSelection.Get(_authoring).Count;
            _scope.text = selected > 0 ? $"SELECTION · {selected} of {_authoring.Parts.Count}"
                : m.Fill > 0f ? $"ALL COLLIDERS · MESH FILLS {m.Fill * 100f:0}% OF COLLIDER VOLUME" : "ALL COLLIDERS";
            _scope.tooltip = selected > 0 ? null : "Closed mesh volume ÷ total collider volume (overlapping colliders count twice). Shown only for closed meshes.";
            _scope.EnableInClassList("sc-metrics__scope--selection", selected > 0);
            _accuracy.text = m.AccuracyText;
            _accuracy.style.color = m.AccuracyColor;
            _worst.text = m.WorstText;
            _cost.text = m.CostText;
            _cost.style.color = m.CostColor;
            for (int t = 0; t < 4; t++)
            {
                int n = m.CountOf((ShapeType)t);
                _counts[t].text = $"×{n}";
                _countItems[t].EnableInClassList("sc-count--zero", n == 0);
            }
        }

        string Signature()
        {
            var a = _authoring;
            unchecked
            {
                int h = a.Parts.Count * 397 ^ (int)a.generateMode ^ ((int)a.bakeTarget << 4) ^ a.bakedColliders.Count << 8;
                foreach (var p in a.Parts)
                    h = h * 31 + ((int)p.Type * 7919 ^ p.Report.Accuracy.GetHashCode() ^ p.Name.GetHashCode() ^ (p.AutoType ? 1 : 0));
                var d = a.decomposition;
                h = h * 31 + d.GetHashCode();
                h = h * 31 + a.fitSettings.GetHashCode();
                h = h * 31 + (_search?.value?.GetHashCode() ?? 0);
                h = h * 31 + SmartColliderOperations.IsStale(a).GetHashCode();
                return h.ToString();
            }
        }
    }

}
