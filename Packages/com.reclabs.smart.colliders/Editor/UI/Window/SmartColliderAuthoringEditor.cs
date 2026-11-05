using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>UI Toolkit inspector: the same panel as the Smart Colliders window, in compact form.</summary>
    [CustomEditor(typeof(SmartColliderAuthoring))]
    internal sealed class SmartColliderAuthoringEditor : Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            UIKit.ApplyStyle(root);
            root.AddToClassList("sc-inspector");
            var panel = new SmartCollidersPanel(compact: true);
            panel.SetTarget(((Component)target).gameObject);
            root.Add(panel);
            var open = UIKit.Button("Open Smart Colliders window", SmartCollidersWindow.Open, "Window/Smart Colliders", "sc-link");
            open.AddToClassList("sc-inspector__open");
            root.Add(open);
            return root;
        }
    }
}
