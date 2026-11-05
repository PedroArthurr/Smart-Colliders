using UnityEditor;
using UnityEngine;

namespace RecLabs.SmartColliders.EditorTools
{
    /// <summary>Per-project settings, stored in ProjectSettings/SmartColliders.asset.</summary>
    [FilePath("ProjectSettings/SmartColliders.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class SmartCollidersProjectSettings : ScriptableSingleton<SmartCollidersProjectSettings>
    {
        [Tooltip("Folder (under Assets/) where baked convex hull meshes are stored.")]
        [SerializeField] internal string generatedFolder = "Assets/SmartColliders Generated";

        public string GeneratedFolder
        {
            get => string.IsNullOrWhiteSpace(generatedFolder) ? "Assets/SmartColliders Generated" : generatedFolder.TrimEnd('/');
            set { generatedFolder = value; Save(true); }
        }
    }
}
