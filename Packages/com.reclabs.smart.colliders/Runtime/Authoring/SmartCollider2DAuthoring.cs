using System.Collections.Generic;
using RecLabs.SmartColliders.Geometry;
using UnityEngine;

namespace RecLabs.SmartColliders
{
    public enum Generate2DMode
    {
        /// <summary>Cheapest single primitive that meets the accuracy, else the outline polygon.</summary>
        Auto,
        Circle,
        Box,
        Capsule,
        /// <summary>PolygonCollider2D from the (simplified) sprite outline.</summary>
        Polygon,
        /// <summary>Convex decomposition, each piece fitted with its best primitive or a polygon path.</summary>
        Decompose,
    }

    /// <summary>
    /// Editor-only 2D collider generation settings + bake bookkeeping for a SpriteRenderer.
    /// Stripped from scenes in player builds; baked colliders stay.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SpriteRenderer))]
    [AddComponentMenu("Physics 2D/Smart Colliders 2D")]
    public sealed class SmartCollider2DAuthoring : MonoBehaviour
    {
        [SerializeField] internal Generate2DMode mode = Generate2DMode.Auto;
        [Tooltip("Primitives are used when outline area / collider area ≥ this.")]
        [Range(0.5f, 1f)] [SerializeField] internal float minAccuracy = 0.85f;
        [Tooltip("Outline simplification tolerance, in sprite pixels.")]
        [Range(0f, 8f)] [SerializeField] internal float simplifyPixels = 1f;
        [Tooltip("Decompose: upper bound on the number of colliders.")]
        [SerializeField] internal int maxColliders = 10;
        [Tooltip("Decompose: merge neighbouring convex pieces when their combined hull wastes at most this fraction of their area.")]
        [Range(0f, 0.5f)] [SerializeField] internal float mergeTolerance = 0.05f;
        [SerializeField] internal bool allowCircle = true, allowCapsule = true, allowBox = true, allowPolygon = true;
        [SerializeField] internal bool isTrigger;
        [SerializeField] internal PhysicsMaterial2D material;

        [SerializeField] internal List<Collider2D> bakedColliders = new List<Collider2D>();
        [SerializeField] internal List<GameObject> bakedObjects = new List<GameObject>();

        public Generate2DMode Mode => mode;
        public bool IsBaked => bakedColliders.Count > 0;

        internal Fit2DSettings FitSettings => new Fit2DSettings
        {
            AllowCircle = allowCircle, AllowCapsule = allowCapsule, AllowBox = allowBox, AllowPolygon = allowPolygon,
            MinAccuracy = minAccuracy, CostWeight = 0.02f,
        };
    }
}
