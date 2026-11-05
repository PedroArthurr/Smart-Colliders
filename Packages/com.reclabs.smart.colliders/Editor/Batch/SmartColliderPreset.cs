using RecLabs.SmartColliders.Geometry;
using UnityEngine;

namespace RecLabs.SmartColliders.EditorTools
{
    /// <summary>Reusable generation + bake settings for the batch tool (and "Apply Preset" on single objects).</summary>
    [CreateAssetMenu(menuName = "Smart Colliders/Preset", fileName = "Smart Colliders Preset", order = 400)]
    public sealed class SmartColliderPreset : ScriptableObject
    {
        public GenerateMode mode = GenerateMode.PerMesh;
        [Tooltip("Gather meshes from children too.")]
        public bool includeChildren = true;
        public ShapeFitSettings fit = ShapeFitSettings.Default;
        public DecompositionSettings decomposition = DecompositionSettings.Default;
        public BakeTarget bakeTarget = BakeTarget.SourceObjects;
        public string containerName = "Colliders";
        public PhysicsMaterial material;
        public bool isTrigger;

        void Reset()
        {
            fit = ShapeFitSettings.Default;
            decomposition = DecompositionSettings.Default;
        }

        /// <summary>Copies the preset into an authoring component (caller records Undo).</summary>
        public void ApplyTo(SmartColliderAuthoring a)
        {
            a.generateMode = mode;
            a.includeChildren = includeChildren;
            a.fitSettings = fit;
            a.decomposition = decomposition;
            a.bakeTarget = bakeTarget;
            a.containerName = containerName;
            a.material = material;
            a.isTrigger = isTrigger;
        }

        /// <summary>Default settings used when no preset is assigned.</summary>
        public static SmartColliderPreset CreateDefault()
        {
            var p = CreateInstance<SmartColliderPreset>();
            p.hideFlags = HideFlags.HideAndDontSave;
            return p;
        }
    }
}
