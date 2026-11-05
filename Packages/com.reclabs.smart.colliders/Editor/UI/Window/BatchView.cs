using System.Collections.Generic;
using System.Linq;
using RecLabs.SmartColliders.EditorTools;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>Batch tab: drop prefabs / folders / scene objects, pick a preset, run with progress + cancel, read the report.</summary>
    internal sealed class BatchView : VisualElement
    {
        const string PresetPref = "RecLabs.SmartColliders.Batch.Preset";
        const string BakePref = "RecLabs.SmartColliders.Batch.Bake";
        const string KeepPref = "RecLabs.SmartColliders.Batch.Keep";
        const string SkipPref = "RecLabs.SmartColliders.Batch.Skip";

        readonly List<Object> _inputs = new List<Object>();
        readonly ListView _inputList;
        readonly Label _inputHint, _progressLabel, _summary;
        readonly ObjectField _preset;
        readonly Toggle _bake, _keep, _skip;
        readonly VisualElement _runButton, _progress, _report, _drop;
        readonly Bar _bar;
        readonly ScrollView _reportRows;

        public BatchView()
        {
            AddToClassList("sc-batch");

            // Inputs.
            UIKit.Section(this, HudIcon.Prefab, "Objects", out var inputs, "prefabs · folders · scene objects");
            _drop = new VisualElement().Class("sc-drop");
            _drop.Add(UIKit.Icon(HudIcon.Folder, UIKit.IconMuted, "sc-drop__icon"));
            _drop.Add(UIKit.Text("Drop prefabs, folders or scene objects here", "sc-drop__text"));
            inputs.Add(_drop);
            _drop.RegisterCallback<DragUpdatedEvent>(_ =>
            {
                DragAndDrop.visualMode = DragAndDrop.objectReferences.Length > 0 ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                _drop.AddToClassList("sc-drop--hover");
            });
            _drop.RegisterCallback<DragLeaveEvent>(_ => _drop.RemoveFromClassList("sc-drop--hover"));
            _drop.RegisterCallback<DragPerformEvent>(_ =>
            {
                DragAndDrop.AcceptDrag();
                _drop.RemoveFromClassList("sc-drop--hover");
                AddInputs(DragAndDrop.objectReferences);
            });

            _inputList = new ListView
            {
                fixedItemHeight = 26,
                itemsSource = _inputs,
                selectionType = SelectionType.Multiple,
                makeItem = () =>
                {
                    var row = new VisualElement().Class("sc-part");
                    row.Add(new HudIconElement(HudIcon.Prefab) { name = "icon", Color = UIKit.IconMuted }.Class("sc-part__type"));
                    row.Add(new Label { name = "name" }.Class("sc-part__name"));
                    row.Add(new Label { name = "kind" }.Class("sc-part__auto"));
                    return row;
                },
                bindItem = (row, i) =>
                {
                    var o = _inputs[i];
                    string path = o != null ? AssetDatabase.GetAssetPath(o) : "";
                    bool folder = !string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path);
                    row.Q<HudIconElement>("icon").Icon = folder ? HudIcon.Folder : string.IsNullOrEmpty(path) ? HudIcon.Hull : HudIcon.Prefab;
                    row.Q<Label>("name").text = o != null ? (string.IsNullOrEmpty(path) ? o.name : path) : "(missing)";
                    row.Q<Label>("kind").text = folder ? $"{BatchProcessor.Expand(new[] { o }).Count} prefabs" : string.IsNullOrEmpty(path) ? "scene" : "prefab";
                },
            }.Class("sc-list");
            _inputList.style.height = 150;
            inputs.Add(_inputList);

            var inputActions = new VisualElement().Class("sc-actions");
            inputActions.Add(UIKit.Button("Add Selection", () => AddInputs(Selection.objects), "Add the objects selected in the Hierarchy / Project"));
            inputActions.Add(UIKit.Button("Remove", () =>
            {
                foreach (int i in _inputList.selectedIndices.OrderByDescending(x => x)) _inputs.RemoveAt(i);
                RefreshInputs();
            }, "Remove the highlighted entries"));
            inputActions.Add(UIKit.Button("Clear", () => { _inputs.Clear(); RefreshInputs(); }, "Remove all entries"));
            inputs.Add(inputActions);
            _inputHint = UIKit.Text("", "sc-note");
            inputs.Add(_inputHint);

            // Settings.
            UIKit.Section(this, HudIcon.Settings, "Settings", out var settings);
            var presetRow = UIKit.Row(settings, "Preset", "Generation + bake settings (Create > Smart Colliders > Preset). Empty = defaults");
            _preset = new ObjectField { objectType = typeof(SmartColliderPreset), allowSceneObjects = false }.Class("sc-object");
            string presetPath = EditorPrefs.GetString(PresetPref, "");
            if (!string.IsNullOrEmpty(presetPath)) _preset.value = AssetDatabase.LoadAssetAtPath<SmartColliderPreset>(presetPath);
            _preset.RegisterValueChangedCallback(e => EditorPrefs.SetString(PresetPref, e.newValue != null ? AssetDatabase.GetAssetPath(e.newValue) : ""));
            presetRow.Add(_preset);
            presetRow.Add(UIKit.Button("New", CreatePreset, "Create a preset asset with the default settings"));

            _bake = AddToggle(settings, "Bake", "Write real colliders (hull meshes are saved as assets)", BakePref, true);
            _keep = AddToggle(settings, "Keep editable", "Keep the Smart Colliders component after baking so colliders can be refined later", KeepPref, true);
            _skip = AddToggle(settings, "Skip existing", "Leave objects that already have Smart Colliders untouched", SkipPref, false);
            settings.Add(UIKit.Text("Prefabs are edited in isolation and saved; scene objects are undoable. Model files (.fbx) are read-only: wrap them in a prefab first.", "sc-note"));

            _runButton = UIKit.Primary("Process", Run, "Generate (and bake) colliders for every object above");
            Add(_runButton);

            _progress = new VisualElement().Class("sc-progress", "sc-card");
            var top = new VisualElement().Class("sc-progress__top");
            _progressLabel = UIKit.Text("", "sc-progress__label");
            top.Add(_progressLabel);
            var cancel = UIKit.IconButton(HudIcon.Stop, () => BatchProcessor.Current?.Cancel(), "Cancel (finished objects stay processed)", out _);
            cancel.AddToClassList("sc-icon-button--danger");
            top.Add(cancel);
            _progress.Add(top);
            _bar = new Bar();
            _progress.Add(_bar);
            Add(_progress);

            _report = UIKit.Section(this, HudIcon.Search, "Report", out var report);
            _summary = UIKit.Text("", "sc-note");
            report.Add(_summary);
            _reportRows = new ScrollView().Class("sc-report");
            report.Add(_reportRows);
            _report.style.display = DisplayStyle.None;

            RegisterCallback<AttachToPanelEvent>(_ => BatchProcessor.Changed += Refresh);
            RegisterCallback<DetachFromPanelEvent>(_ => BatchProcessor.Changed -= Refresh);
            RefreshInputs();
            Refresh();
        }

        static Toggle AddToggle(VisualElement parent, string label, string tooltip, string pref, bool def)
        {
            var row = UIKit.Row(parent, label, tooltip);
            var t = new Toggle { value = EditorPrefs.GetBool(pref, def) }.Class("sc-toggle");
            t.RegisterValueChangedCallback(e => EditorPrefs.SetBool(pref, e.newValue));
            row.Add(t);
            return t;
        }

        void CreatePreset()
        {
            string path = EditorUtility.SaveFilePanelInProject("New Smart Colliders Preset", "Smart Colliders Preset", "asset", "");
            if (string.IsNullOrEmpty(path)) return;
            var p = ScriptableObject.CreateInstance<SmartColliderPreset>();
            AssetDatabase.CreateAsset(p, path);
            AssetDatabase.SaveAssets();
            _preset.value = p;
            Selection.activeObject = p;
        }

        void AddInputs(IEnumerable<Object> objects)
        {
            foreach (var o in objects)
            {
                if (o == null || _inputs.Contains(o)) continue;
                string path = AssetDatabase.GetAssetPath(o);
                bool folder = !string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path);
                if (folder || o is GameObject) _inputs.Add(o);
            }
            RefreshInputs();
        }

        void RefreshInputs()
        {
            _inputList.RefreshItems();
            _inputList.style.display = _inputs.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            int n = BatchProcessor.Expand(_inputs).Count;
            _inputHint.text = _inputs.Count == 0 ? "Nothing added yet." : $"{n} object{(n == 1 ? "" : "s")} will be processed.";
            _runButton.SetEnabled(n > 0 && BatchProcessor.Current == null);
            _drop.EnableInClassList("sc-drop--compact", _inputs.Count > 0);
        }

        void Run()
        {
            if (BatchProcessor.Current != null) return;
            int n = BatchProcessor.Expand(_inputs).Count;
            if (n == 0) return;
            if (n > 1 && !EditorUtility.DisplayDialog("Smart Colliders Batch",
                    $"Process {n} objects{(_bake.value ? " and bake their colliders" : "")}? Prefab changes are saved to disk.", "Process", "Cancel")) return;
            BatchProcessor.Start(_inputs.ToList(), _preset.value as SmartColliderPreset, _bake.value, _keep.value, _skip.value);
        }

        BatchProcessor _last;

        void Refresh()
        {
            var p = BatchProcessor.Current;
            if (p != null) _last = p;
            bool running = p != null;
            _progress.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;
            _runButton.style.display = running ? DisplayStyle.None : DisplayStyle.Flex;
            _runButton.SetEnabled(!running && BatchProcessor.Expand(_inputs).Count > 0);
            if (running)
            {
                _bar.Value = p.Total > 0 ? p.Done / (float)p.Total : 0f;
                _progressLabel.text = $"{p.Done}/{p.Total} · {p.CurrentName} · {p.ElapsedSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s";
            }
            if (_last == null) return;

            _report.style.display = DisplayStyle.Flex;
            var results = _last.Results;
            int ok = results.Count(r => r.Ok), skipped = results.Count(r => r.Skipped), failed = results.Count(r => r.Error != null && !r.Skipped);
            _summary.text = $"{ok} processed · {skipped} skipped · {failed} failed · {results.Sum(r => r.Colliders)} colliders · {_last.ElapsedSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s"
                            + (running ? "" : _last.Done < _last.Total ? " · cancelled" : "");
            if (_reportRows.childCount == results.Count) return;
            _reportRows.Clear();
            foreach (var r in results)
            {
                var row = new VisualElement().Class("sc-report__row");
                var status = new VisualElement().Class("sc-report__dot");
                status.style.backgroundColor = r.Ok ? MetricsSummary.Good : r.Skipped ? UIKit.IconMuted : MetricsSummary.Bad;
                row.Add(status);
                var name = UIKit.Text(r.Name, "sc-report__name");
                name.tooltip = r.Path;
                row.Add(name);
                row.Add(UIKit.Text(r.Ok ? $"{r.Colliders} · {r.Accuracy * 100f:0}%" : r.Skipped ? r.Error ?? "skipped" : r.Error, "sc-report__info"));
                string path = r.Path;
                if (!string.IsNullOrEmpty(path))
                    row.AddManipulator(new Clickable(() => EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(path))));
                _reportRows.Add(row);
            }
        }
    }
}
