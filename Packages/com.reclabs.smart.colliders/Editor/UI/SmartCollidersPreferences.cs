using System.Collections.Generic;
using RecLabs.SmartColliders.EditorTools;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>Per-user preferences (EditorPrefs) of the Scene view preview and HUD.</summary>
    internal static class SmartCollidersPreferences
    {
        public const string Path = "Preferences/Smart Colliders";
        const string Prefix = "RecLabs.SmartColliders.Prefs.";

        public static float FillOpacity
        {
            get => EditorPrefs.GetFloat(Prefix + "FillOpacity", 1f);
            set { EditorPrefs.SetFloat(Prefix + "FillOpacity", Mathf.Clamp(value, 0f, 3f)); SceneView.RepaintAll(); }
        }

        public static float WireWidth
        {
            get => EditorPrefs.GetFloat(Prefix + "WireWidth", 1f);
            set { EditorPrefs.SetFloat(Prefix + "WireWidth", Mathf.Clamp(value, 0.25f, 3f)); SceneView.RepaintAll(); }
        }

        public static bool ShowHud
        {
            get => EditorPrefs.GetBool(Prefix + "ShowHud", true);
            set { EditorPrefs.SetBool(Prefix + "ShowHud", value); SceneView.RepaintAll(); }
        }

        public static bool ShowHints
        {
            get => EditorPrefs.GetBool(Prefix + "ShowHints", true);
            set { EditorPrefs.SetBool(Prefix + "ShowHints", value); SceneView.RepaintAll(); }
        }

        public static bool AutoEditAfterGenerate
        {
            get => EditorPrefs.GetBool(Prefix + "AutoEdit", true);
            set => EditorPrefs.SetBool(Prefix + "AutoEdit", value);
        }

        [SettingsProvider]
        static SettingsProvider CreateUser() => new SettingsProvider(Path, SettingsScope.User)
        {
            label = "Smart Colliders",
            keywords = new HashSet<string> { "collider", "preview", "x-ray", "hud", "opacity" },
            activateHandler = (_, root) =>
            {
                UIKit.ApplyStyle(root);
                root.AddToClassList("sc-settings");
                root.Add(UIKit.Text("Smart Colliders", "sc-settings__title"));

                UIKit.Section(root, HudIcon.Eye, "Scene Preview", out var preview);
                AddToggle(preview, "Show preview", "Draw the colliders of the selected objects in the Scene view", () => PreviewPrefs.Enabled, v => PreviewPrefs.Enabled = v);
                AddToggle(preview, "X-Ray", "Show colliders hidden behind geometry (dimmed)", () => PreviewPrefs.ShowOccluded, v => PreviewPrefs.ShowOccluded = v);
                AddSlider(preview, "Fill opacity", "Multiplier of the translucent fill", 0f, 3f, () => FillOpacity, v => FillOpacity = v);
                AddSlider(preview, "Wire width", "Multiplier of the outline thickness", 0.25f, 3f, () => WireWidth, v => WireWidth = v);

                UIKit.Section(root, HudIcon.Select, "Scene HUD", out var hud);
                AddToggle(hud, "Show HUD", "Floating metrics card and toolbar while editing", () => ShowHud, v => ShowHud = v);
                AddToggle(hud, "Keyboard hints", "Shortcut hints under the toolbar", () => ShowHints, v => ShowHints = v);
                AddToggle(hud, "Edit after generate", "Activate the Scene edit tool after generating from the Hierarchy menu", () => AutoEditAfterGenerate, v => AutoEditAfterGenerate = v);

                UIKit.Section(root, HudIcon.Folder, "Project", out var project);
                var row = UIKit.Row(project, "Generated folder", "Where hull mesh assets are saved (shared by the team, stored in ProjectSettings)");
                var folder = new TextField { isDelayed = true, value = SmartCollidersProjectSettings.instance.GeneratedFolder }.Class("sc-text");
                folder.RegisterValueChangedCallback(e =>
                {
                    SmartCollidersProjectSettings.instance.GeneratedFolder = e.newValue;
                });
                row.Add(folder);

                UIKit.Section(root, HudIcon.Decompose, "Decomposition engines", out var engines);
                engines.Add(UIKit.Text($"V-HACD: {(NativeDecomposer.IsAvailable(NativeDecomposer.EngineVHacd) ? "available" : "not available on this platform")}", "sc-note"));
                engines.Add(UIKit.Text($"CoACD: {(NativeDecomposer.IsAvailable(NativeDecomposer.EngineCoAcd) ? "available" : "not in this plugin build (see Native~/README.md)")}", "sc-note"));
                engines.Add(UIKit.Text("Built-in: always available", "sc-note"));

                UIKit.Section(root, HudIcon.Search, "Shortcuts (Scene view, edit tool active)", out var keys);
                foreach (var (k, d) in new[]
                {
                    ("1 – 5", "Select · Move · Rotate · Resize · Paint"), ("X", "Local / Global handles"), ("M", "Merge selected"),
                    ("Del", "Remove selected"), ("F", "Frame selected"), ("Ctrl+A", "Select all"), ("Esc", "Deselect / clear paint"),
                    ("[ ]  Shift+Wheel", "Brush size (Paint)"), ("Ctrl", "Erase (Paint)"), ("Enter", "Split painted triangles"),
                })
                {
                    var r = UIKit.Row(keys, k);
                    r.Add(UIKit.Text(d, "sc-note"));
                }
            },
        };

        static void AddToggle(VisualElement parent, string label, string tooltip, System.Func<bool> get, System.Action<bool> set)
        {
            var row = UIKit.Row(parent, label, tooltip);
            var t = new Toggle { value = get() }.Class("sc-toggle");
            t.RegisterValueChangedCallback(e => set(e.newValue));
            row.Add(t);
        }

        static void AddSlider(VisualElement parent, string label, string tooltip, float min, float max, System.Func<float> get, System.Action<float> set)
        {
            var row = UIKit.Row(parent, label, tooltip);
            var s = new Slider(min, max) { value = get(), showInputField = true }.Class("sc-slider", "sc-slider--wide");
            s.RegisterValueChangedCallback(e => set(e.newValue));
            row.Add(s);
        }
    }
}
