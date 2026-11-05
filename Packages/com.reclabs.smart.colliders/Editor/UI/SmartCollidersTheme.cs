using UnityEditor;
using UnityEngine;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>Single source of truth for colors/sizes. Orange = actions/active tool, violet = selection.</summary>
    internal static class SmartCollidersTheme
    {
        public static readonly Color Accent = new Color32(0xFF, 0xA5, 0x3D, 0xFF);    // amber orange
        public static readonly Color Selection = new Color32(0x9B, 0x7B, 0xFF, 0xFF); // violet
        public static readonly Color Panel = new Color32(0x1E, 0x1F, 0x24, 0xF2);
        public static readonly Color PanelBorder = new Color32(0x34, 0x36, 0x3E, 0xFF);
        public static readonly Color TextMuted = new Color32(0x9A, 0x9D, 0xA8, 0xFF);

        // Preview tuning.
        public const float FillAlpha = 0.16f;
        public const float FillAlphaHover = 0.26f;
        public const float FillAlphaSelected = 0.32f;
        public const float FillAlphaDimmed = 0.08f;
        public const float WireWidth = 1.6f;
        public const float WireWidthHover = 2.6f;
        public const float WireWidthSelected = 3.2f;
        public const float OccludedWireAlpha = 0.22f;
    }

    /// <summary>User preview preferences (EditorPrefs).</summary>
    internal static class PreviewPrefs
    {
        const string EnabledKey = "RecLabs.SmartColliders.Preview.Enabled";
        const string OccludedKey = "RecLabs.SmartColliders.Preview.ShowOccluded";

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledKey, true);
            set { EditorPrefs.SetBool(EnabledKey, value); SceneView.RepaintAll(); }
        }

        public static bool ShowOccluded
        {
            get => EditorPrefs.GetBool(OccludedKey, true);
            set { EditorPrefs.SetBool(OccludedKey, value); SceneView.RepaintAll(); }
        }
    }
}
