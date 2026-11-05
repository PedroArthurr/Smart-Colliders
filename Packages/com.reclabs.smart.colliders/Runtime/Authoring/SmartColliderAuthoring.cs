using System.Collections.Generic;
using RecLabs.SmartColliders.Geometry;
using UnityEngine;

namespace RecLabs.SmartColliders
{
    public enum BakeTarget
    {
        /// <summary>Axis-aligned primitives and hulls go on this GameObject; rotated primitives get a child.</summary>
        SameObject,
        /// <summary>A child container holds one named child per part (tidiest hierarchy).</summary>
        ChildContainer,
        /// <summary>
        /// Each part goes onto the GameObject that owns its mesh, so it follows that object
        /// (animated doors, wheels, turrets). Parts spanning several meshes fall back to this GameObject.
        /// </summary>
        SourceObjects,
    }

    /// <summary>
    /// Editor-time authoring state: parts, fit settings, and bookkeeping of what Bake produced.
    /// Does nothing at runtime; stripped from scenes during player builds.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Physics/Smart Colliders")]
    public sealed class SmartColliderAuthoring : MonoBehaviour
    {
        [Tooltip("Gather meshes from children (MeshFilter and SkinnedMeshRenderer), not only this GameObject.")]
        [SerializeField] internal bool includeChildren = true;

        [SerializeField] internal ShapeFitSettings fitSettings = ShapeFitSettings.Default;

        [Tooltip("How Generate splits the meshes into colliders.")]
        [SerializeField] internal GenerateMode generateMode = GenerateMode.PerMesh;
        [SerializeField] internal DecompositionSettings decomposition = DecompositionSettings.Default;

        [Tooltip("Where baked colliders are created.")]
        [SerializeField] internal BakeTarget bakeTarget = BakeTarget.SourceObjects;
        [SerializeField] internal string containerName = "Colliders";
        [SerializeField] internal PhysicsMaterial material;
        [SerializeField] internal bool isTrigger;

        [SerializeField] internal List<SmartColliderPart> parts = new List<SmartColliderPart>();
        [SerializeField] internal SmartColliderMeshBank meshBank;

        // Source signature at generation time → detects stale parts after mesh edits.
        [SerializeField] internal int sourceVertexCount;
        [SerializeField] internal int sourceTriangleCount;
        /// <summary>Enclosed volume of the source meshes (0 = not closed), for the aggregate fill metric.</summary>
        [SerializeField] internal float sourceVolume;

        // Bake bookkeeping: only these are removed/replaced on re-bake.
        [SerializeField] internal List<Collider> bakedColliders = new List<Collider>();
        [SerializeField] internal List<GameObject> bakedObjects = new List<GameObject>();

        public IReadOnlyList<SmartColliderPart> Parts => parts;
        public ShapeFitSettings FitSettings => fitSettings;
        public GenerateMode Mode => generateMode;
        public DecompositionSettings Decomposition => decomposition;
        public BakeTarget Target => bakeTarget;
        public bool IsBaked => bakedColliders.Count > 0;

        void Reset()
        {
            fitSettings = ShapeFitSettings.Default;
            decomposition = DecompositionSettings.Default;
        }
    }
}
