using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>Main window (Window/Smart Colliders): the selected object's colliders, or the batch tool.</summary>
    internal sealed class SmartCollidersWindow : EditorWindow
    {
        const string TabPref = "RecLabs.SmartColliders.Window.Tab";

        SmartCollidersPanel _panel;
        ScrollView _objectView;
        VisualElement _batchView;
        Segmented _tabs;

        [MenuItem("Window/Smart Colliders", false, 2000)]
        public static void Open()
        {
            var w = GetWindow<SmartCollidersWindow>();
            w.minSize = new Vector2(360, 420);
            w.Show();
        }

        void OnEnable()
        {
            titleContent = new GUIContent("Smart Colliders", SmartCollidersIcons.Window);
            Selection.selectionChanged += OnSelection;
        }

        void OnDisable() => Selection.selectionChanged -= OnSelection;

        void CreateGUI()
        {
            var root = rootVisualElement;
            UIKit.ApplyStyle(root);
            root.AddToClassList("sc-window");

            var top = new VisualElement().Class("sc-topbar");
            var brand = new VisualElement().Class("sc-brand");
            brand.Add(UIKit.Icon(HudIcon.Hull, SmartCollidersTheme.Accent, "sc-brand__icon"));
            brand.Add(UIKit.Text("Smart Colliders", "sc-brand__title"));
            top.Add(brand);
            _tabs = new Segmented(new[] { "Object", "Batch" }, new[] { "The selected GameObject", "Many prefabs / objects at once" });
            _tabs.AddToClassList("sc-tabs");
            _tabs.Changed += ShowTab;
            top.Add(_tabs);
            var gear = UIKit.IconButton(HudIcon.Settings, () => SettingsService.OpenUserPreferences(SmartCollidersPreferences.Path),
                "Preferences (preview colors, opacity, HUD)", out _);
            top.Add(gear);
            root.Add(top);

            _objectView = new ScrollView(ScrollViewMode.Vertical).Class("sc-scroll");
            _panel = new SmartCollidersPanel(compact: false);
            _objectView.Add(_panel);
            root.Add(_objectView);

            var batchScroll = new ScrollView(ScrollViewMode.Vertical).Class("sc-scroll");
            batchScroll.Add(new BatchView());
            _batchView = batchScroll;
            root.Add(_batchView);

            int tab = EditorPrefs.GetInt(TabPref, 0);
            _tabs.SetValueWithoutNotify(tab);
            ShowTab(tab);
            OnSelection();
        }

        void ShowTab(int tab)
        {
            EditorPrefs.SetInt(TabPref, tab);
            _objectView.style.display = tab == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _batchView.style.display = tab == 1 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void OnSelection()
        {
            if (_panel == null) return;
            var go = Selection.activeGameObject;
            // Selecting a child of an authoring object keeps showing the authoring object.
            var owner = go != null ? go.GetComponentInParent<SmartColliderAuthoring>(true) : null;
            _panel.SetTarget(owner != null ? owner.gameObject : go);
        }
    }
}
