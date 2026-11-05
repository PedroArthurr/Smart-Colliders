using System.Linq;
using RecLabs.SmartColliders.EditorTools;
using UnityEditor;
using UnityEngine;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>One-click entry points: Hierarchy context menu + Tools menu.</summary>
    internal static class SmartColliderMenu
    {
        const string GameObjectItem = "GameObject/Smart Colliders/Generate Colliders";
        const string ToolsItem = "Tools/Smart Colliders/Generate Colliders for Selection";

        [MenuItem(GameObjectItem, false, 30)]
        static void GenerateFromHierarchy(MenuCommand command)
        {
            // Hierarchy context menus fire once per selected object; run a single batch.
            var selection = Selection.gameObjects;
            if (selection.Length > 1 && command.context != selection[0]) return;
            Generate(selection);
        }

        [MenuItem(ToolsItem, false, 0)]
        static void GenerateFromToolsMenu() => Generate(Selection.gameObjects);

        [MenuItem(GameObjectItem, true)]
        [MenuItem(ToolsItem, true)]
        static bool Validate() => Selection.gameObjects.Any(go => HasMeshes(go) || IsSprite(go));

        static bool IsSprite(GameObject go) => go.GetComponent<SpriteRenderer>() != null && !HasMeshes(go);

        static bool HasMeshes(GameObject go) =>
            go.GetComponentInChildren<MeshFilter>() != null || go.GetComponentInChildren<SkinnedMeshRenderer>() != null;

        static void Generate(GameObject[] targets)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Generate Smart Colliders");

            SmartColliderAuthoring last = null;
            var generated = new System.Collections.Generic.List<GameObject>();
            foreach (var go in targets.Where(HasMeshes))
            {
                var a = go.GetComponent<SmartColliderAuthoring>();
                if (a == null) a = Undo.AddComponent<SmartColliderAuthoring>(go);
                if (a.Parts.Count == 0) SmartColliderOperations.Generate(a, GenerateMode.PerMesh);
                last = a;
                generated.Add(go);
            }
            foreach (var go in targets.Where(IsSprite))
                if (go.GetComponent<SmartCollider2DAuthoring>() == null) Undo.AddComponent<SmartCollider2DAuthoring>(go);
            var sprite = targets.FirstOrDefault(IsSprite);
            Undo.CollapseUndoOperations(group);

            if (last == null)
            {
                if (sprite != null) Selection.activeGameObject = sprite;
                return;
            }
            // Keep every generated object selected (the tool and preview handle multiple targets).
            Selection.objects = generated.Cast<Object>().ToArray();
            // Tool availability updates after the selection change is processed; Activate retries if needed.
            EditorApplication.delayCall += () =>
            {
                if (SmartCollidersPreferences.AutoEditAfterGenerate) SmartColliderEditTool.Activate();
                SceneView.RepaintAll();
            };
        }
    }
}
