using System;
using UnityEngine;

namespace RecLabs.SmartColliders
{
    /// <summary>How Generate splits the source triangles into parts. Serialized: only append.</summary>
    public enum GenerateMode
    {
        /// <summary>One part enclosing everything.</summary>
        Single,
        /// <summary>One part per source mesh (MeshFilter / SkinnedMeshRenderer).</summary>
        PerMesh,
        /// <summary>Approximate convex decomposition into up to <see cref="DecompositionSettings.MaxColliders"/> parts.</summary>
        Decompose,
        /// <summary>Skinned meshes: one part per dominant bone (colliders follow the skeleton).</summary>
        PerBone,
    }

    /// <summary>Decomposition quality. Low/Medium favour speed (voxel engine), High/Ultra favour accuracy (concavity cuts).</summary>
    public enum DecompositionQuality
    {
        Low,
        Medium,
        High,
        Ultra,
    }

    /// <summary>Decomposition backend. Auto = pick by quality and plugin availability.</summary>
    public enum DecompositionEngine
    {
        Auto,
        /// <summary>V-HACD v4 (native plugin, voxel based, fast).</summary>
        VHacd,
        /// <summary>CoACD (native plugin, collision-aware cuts, best quality).</summary>
        CoAcd,
        /// <summary>Built-in C# concavity-driven plane cutting (always available).</summary>
        BuiltIn,
    }

    [Serializable]
    public struct DecompositionSettings
    {
        public DecompositionQuality Quality;
        public DecompositionEngine Engine;
        [Tooltip("Upper bound on the number of colliders produced.")]
        public int MaxColliders;
        [Tooltip("Merge neighbouring parts when the merged convex hull stays within the tolerance.")]
        public bool AutoMerge;
        [Tooltip("Auto Merge tolerance: two neighbouring parts merge when their combined hull wastes at most this fraction of their volume (0.05 = 5%).")]
        public float MergeTolerance;
        [Tooltip("Run in the background with pause / resume / cancel.")]
        public bool Async;
        [Tooltip("Background work time slice per editor frame (ms). Higher = faster, less responsive editor.")]
        public int YieldMilliseconds;

        public static DecompositionSettings Default => new DecompositionSettings
        {
            Quality = DecompositionQuality.Medium,
            Engine = DecompositionEngine.Auto,
            MaxColliders = 25,
            AutoMerge = true,
            MergeTolerance = 0.05f,
            Async = true,
            YieldMilliseconds = 16,
        };

        public static readonly int[] MaxColliderPresets = { 2, 5, 10, 25, 50, 100 };
        public static readonly float[] TolerancePresets = { 0.01f, 0.05f, 0.1f, 0.25f, 0.5f };
    }
}
