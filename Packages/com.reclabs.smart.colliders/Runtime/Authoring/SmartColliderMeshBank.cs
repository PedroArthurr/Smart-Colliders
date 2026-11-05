using System.Collections.Generic;
using UnityEngine;

namespace RecLabs.SmartColliders
{
    /// <summary>
    /// Asset holding the baked convex hull meshes (as sub-assets) of one <see cref="SmartColliderAuthoring"/>.
    /// Meshes are updated in place on re-bake so existing MeshCollider references stay valid.
    /// </summary>
    public sealed class SmartColliderMeshBank : ScriptableObject
    {
        [SerializeField] internal List<Mesh> meshes = new List<Mesh>();
        public IReadOnlyList<Mesh> Meshes => meshes;
    }
}
