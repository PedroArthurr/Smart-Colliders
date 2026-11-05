using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RecLabs.SmartColliders.EditorTools
{
    /// <summary>Removes editor-only authoring data from scenes in player builds (baked colliders stay).</summary>
    internal sealed class StripAuthoringOnBuild : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null) return; // entering Play Mode, not a build
            foreach (var root in scene.GetRootGameObjects())
            foreach (var authoring in root.GetComponentsInChildren<SmartColliderAuthoring>(true))
                Object.DestroyImmediate(authoring);
            foreach (var root in scene.GetRootGameObjects())
            foreach (var authoring in root.GetComponentsInChildren<SmartCollider2DAuthoring>(true))
                Object.DestroyImmediate(authoring);
        }
    }
}
