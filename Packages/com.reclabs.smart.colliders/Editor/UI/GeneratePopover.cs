using System;
using System.Collections.Generic;
using RecLabs.SmartColliders.EditorTools;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>
    /// Scene HUD popover with the generation parameters: one row per parameter, each with a short description,
    /// preset chips and the action button. Opens from the HUD Generate button or G.
    /// </summary>
    internal sealed class GeneratePopover : VisualElement
    {
        static readonly string[] ModeNames = { "Single", "Per Mesh", "Decompose", "Per Bone" };
        static readonly string[] ModeHelp =
        {
            "One collider around all meshes.",
            "One collider per MeshFilter / SkinnedMeshRenderer.",
            "Approximate convex decomposition into several colliders.",
            "Skinned meshes: one collider per bone, following the skeleton.",
        };
        static readonly string[] QualityNames = { "Low", "Med", "High", "Ultra" };
        static readonly int[] HullVertexPresets = { 16, 32, 64, 129 };

        readonly Func<SmartColliderAuthoring> _target;
        readonly Action _changed;
        readonly List<Label> _modes = new List<Label>(), _qualities = new List<Label>(), _maxes = new List<Label>(),
            _tolerances = new List<Label>(), _hullVerts = new List<Label>();
        readonly Label _modeHelp, _engine;
        readonly VisualElement _decompose;

        public GeneratePopover(Func<SmartColliderAuthoring> target, Action changed)
        {
            _target = target;
            _changed = changed;
            AddToClassList("sc-panel");
            AddToClassList("sc-popover");

            var header = new VisualElement();
            header.AddToClassList("sc-popover__header");
            var title = new Label("Generate");
            title.AddToClassList("sc-popover__title");
            header.Add(title);
            var key = new Label("G");
            key.AddToClassList("sc-key");
            header.Add(key);
            Add(header);

            var modes = Row("Mode", null);
            for (int i = 0; i < ModeNames.Length; i++)
            {
                int m = i;
                _modes.Add(Chip(modes, ModeNames[i], ModeHelp[i], () => Set("generateMode", p => p.enumValueIndex = m)));
            }
            _modeHelp = new Label();
            _modeHelp.AddToClassList("sc-popover__help");
            Add(_modeHelp);

            _decompose = new VisualElement();
            Add(_decompose);
            var quality = Row("Quality", "Low / Medium favour speed (V-HACD), High / Ultra favour precise cuts (CoACD or built-in).", _decompose);
            for (int i = 0; i < QualityNames.Length; i++)
            {
                int q = i;
                _qualities.Add(Chip(quality, QualityNames[i], null, () => Set("decomposition.Quality", p => p.enumValueIndex = q)));
            }
            var max = Row("Max Colliders", "Upper bound on the number of colliders.", _decompose);
            foreach (int v in DecompositionSettings.MaxColliderPresets)
            {
                int value = v;
                _maxes.Add(Chip(max, v.ToString(), null, () => Set("decomposition.MaxColliders", p => p.intValue = value)));
            }
            var merge = Row("Auto Merge", "Merge neighbours whose combined hull wastes at most this much volume. Off = keep every cut.", _decompose);
            _tolerances.Add(Chip(merge, "Off", null, () => Set("decomposition.AutoMerge", p => p.boolValue = false)));
            foreach (float t in DecompositionSettings.TolerancePresets)
            {
                float value = t;
                _tolerances.Add(Chip(merge, $"{t * 100f:0}%", null, () =>
                {
                    Set("decomposition.AutoMerge", p => p.boolValue = true);
                    Set("decomposition.MergeTolerance", p => p.floatValue = value);
                }));
            }
            _engine = new Label();
            _engine.AddToClassList("sc-popover__help");
            _decompose.Add(_engine);

            var hull = Row("Hull Vertices", "Vertex cap of convex hulls (129 ⇒ ≤ 254 triangles, the PhysX limit). Lower = cheaper.");
            foreach (int v in HullVertexPresets)
            {
                int value = v;
                _hullVerts.Add(Chip(hull, v.ToString(), null, () => Set("fitSettings.MaxHullVertices", p => p.intValue = value)));
            }

            var go = new VisualElement { tooltip = "Replace all colliders (Undo restores them)" };
            go.AddToClassList("sc-primary");
            go.AddToClassList("sc-popover__go");
            var label = new Label("Generate");
            label.AddToClassList("sc-primary__label");
            go.Add(label);
            go.AddManipulator(new Clickable(() =>
            {
                var a = _target();
                if (a == null) return;
                style.display = DisplayStyle.None;
                SmartCollidersPanel.Generate(a);
                _changed();
            }));
            Add(go);
        }

        VisualElement Row(string caption, string help, VisualElement parent = null)
        {
            var row = new VisualElement { tooltip = help };
            row.AddToClassList("sc-popover__row");
            var l = new Label(caption);
            l.AddToClassList("sc-popover__label");
            row.Add(l);
            (parent ?? this).Add(row);
            return row;
        }

        static Label Chip(VisualElement row, string text, string tooltip, Action onClick)
        {
            var chip = new Label(text) { tooltip = tooltip };
            chip.AddToClassList("sc-chip");
            chip.AddToClassList("sc-chip--option");
            chip.AddManipulator(new Clickable(onClick));
            row.Add(chip);
            return chip;
        }

        void Set(string path, Action<SerializedProperty> set)
        {
            var a = _target();
            if (a == null) return;
            var so = new SerializedObject(a);
            var p = so.FindProperty(path);
            if (p == null) return;
            set(p);
            so.ApplyModifiedProperties();
            Refresh(a);
            _changed();
        }

        public void Refresh(SmartColliderAuthoring a)
        {
            if (a == null) return;
            bool skinned = a.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
            int mode = (int)a.generateMode;
            for (int i = 0; i < _modes.Count; i++)
            {
                _modes[i].EnableInClassList("sc-chip--on", i == mode);
                if (i == (int)GenerateMode.PerBone) _modes[i].SetEnabled(skinned);
            }
            _modeHelp.text = ModeHelp[Mathf.Clamp(mode, 0, ModeHelp.Length - 1)];

            var d = a.decomposition;
            _decompose.style.display = a.generateMode == GenerateMode.Decompose ? DisplayStyle.Flex : DisplayStyle.None;
            for (int i = 0; i < _qualities.Count; i++) _qualities[i].EnableInClassList("sc-chip--on", i == (int)d.Quality);
            for (int i = 0; i < _maxes.Count; i++) _maxes[i].EnableInClassList("sc-chip--on", DecompositionSettings.MaxColliderPresets[i] == d.MaxColliders);
            _tolerances[0].EnableInClassList("sc-chip--on", !d.AutoMerge);
            for (int i = 1; i < _tolerances.Count; i++)
                _tolerances[i].EnableInClassList("sc-chip--on", d.AutoMerge && Mathf.Abs(DecompositionSettings.TolerancePresets[i - 1] - d.MergeTolerance) < 1e-5f);
            _engine.text = $"Engine: {DecompositionRunner.DisplayName(DecompositionRunner.Resolve(d))}{(d.Async ? " · background" : "")}";
            for (int i = 0; i < _hullVerts.Count; i++) _hullVerts[i].EnableInClassList("sc-chip--on", HullVertexPresets[i] == a.fitSettings.MaxHullVertices);
        }
    }

    /// <summary>
    /// Floating progress card at the bottom of every Scene view while a background decomposition runs:
    /// engine, percentage, elapsed time, part count, Pause / Resume / Cancel.
    /// </summary>
    [InitializeOnLoad]
    internal static class DecompositionOverlay
    {
        const string StylePath = "Packages/com.reclabs.smart.colliders/Editor/UI/SmartCollidersHud.uss";
        static readonly Dictionary<SceneView, Card> s_Cards = new Dictionary<SceneView, Card>();

        static DecompositionOverlay()
        {
            DecompositionRunner.Changed += Refresh;
            SceneView.duringSceneGui += sv => { if (DecompositionRunner.Current != null && !s_Cards.ContainsKey(sv)) Refresh(); };
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                foreach (var c in s_Cards.Values) c.Root.RemoveFromHierarchy();
                s_Cards.Clear();
            };
        }

        static void Refresh()
        {
            var r = DecompositionRunner.Current;
            foreach (SceneView sv in SceneView.sceneViews)
            {
                if (sv == null) continue;
                if (!s_Cards.TryGetValue(sv, out var card))
                {
                    if (r == null) continue;
                    s_Cards[sv] = card = new Card(sv);
                }
                card.Update(r);
            }
        }

        sealed class Card
        {
            public readonly VisualElement Root;
            readonly Label _title, _info;
            readonly VisualElement _fill;
            readonly HudIconElement _pauseIcon;

            public Card(SceneView sv)
            {
                Root = new VisualElement { pickingMode = PickingMode.Ignore };
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
                if (sheet != null) Root.styleSheets.Add(sheet);
                Root.AddToClassList("sc-progress-overlay");

                var card = new VisualElement();
                card.AddToClassList("sc-panel");
                card.AddToClassList("sc-progress-card");
                Root.Add(card);

                var top = new VisualElement();
                top.AddToClassList("sc-progress-card__top");
                var icon = new HudIconElement(HudIcon.Decompose) { Color = SmartCollidersTheme.Accent };
                icon.AddToClassList("sc-icon--small");
                top.Add(icon);
                var texts = new VisualElement();
                texts.AddToClassList("sc-progress-card__texts");
                _title = new Label();
                _title.AddToClassList("sc-progress-card__title");
                _info = new Label();
                _info.AddToClassList("sc-progress-card__info");
                texts.Add(_title);
                texts.Add(_info);
                top.Add(texts);

                var pause = new VisualElement { tooltip = "Pause / Resume" };
                pause.AddToClassList("sc-btn");
                pause.AddToClassList("sc-btn--small");
                _pauseIcon = new HudIconElement(HudIcon.Pause) { Color = new Color(0.86f, 0.87f, 0.9f) };
                pause.Add(_pauseIcon);
                pause.AddManipulator(new Clickable(() =>
                {
                    var r = DecompositionRunner.Current;
                    if (r == null) return;
                    if (r.IsPaused) r.Resume(); else r.Pause();
                }));
                top.Add(pause);
                var cancel = new VisualElement { tooltip = "Cancel (keeps the current colliders)" };
                cancel.AddToClassList("sc-btn");
                cancel.AddToClassList("sc-btn--small");
                cancel.AddToClassList("sc-btn--danger");
                cancel.Add(new HudIconElement(HudIcon.Stop) { Color = new Color(1f, 0.55f, 0.55f) });
                cancel.AddManipulator(new Clickable(() => DecompositionRunner.Current?.Cancel()));
                top.Add(cancel);
                card.Add(top);

                var bar = new VisualElement();
                bar.AddToClassList("sc-progress-card__bar");
                _fill = new VisualElement();
                _fill.AddToClassList("sc-progress-card__fill");
                bar.Add(_fill);
                card.Add(bar);

                sv.rootVisualElement.Add(Root);
            }

            public void Update(DecompositionRunner r)
            {
                Root.EnableInClassList("sc-progress-overlay--hidden", r == null);
                if (r == null) return;
                string name = r.Target != null ? r.Target.name : "";
                _title.text = r.IsPaused ? $"Paused · {name}" : $"Decomposing {name}";
                _info.text = $"{r.EngineName} · {r.Status} · {r.Progress * 100f:0}% · {r.ElapsedSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s · {r.PieceCount} parts";
                _fill.style.width = Length.Percent(Mathf.Clamp01(r.Progress) * 100f);
                _fill.EnableInClassList("sc-progress-card__fill--paused", r.IsPaused);
                _pauseIcon.Icon = r.IsPaused ? HudIcon.Play : HudIcon.Pause;
            }
        }
    }
}
