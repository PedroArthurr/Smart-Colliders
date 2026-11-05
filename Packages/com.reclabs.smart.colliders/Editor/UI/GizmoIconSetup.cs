using System;
using UnityEditor;
using UnityEngine;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>
    /// The custom component icons would also be drawn as large Scene view gizmos over every object; the preview already
    /// shows the colliders, so hide those gizmo icons once per project and type (users can re-enable them in the Gizmos
    /// menu). Unity creates a script's annotation only once an instance exists, so this waits for hierarchy changes.
    /// </summary>
    [InitializeOnLoad]
    internal static class GizmoIconSetup
    {
        static readonly Type[] Types = { typeof(SmartColliderAuthoring), typeof(SmartCollider2DAuthoring) };

        static GizmoIconSetup()
        {
            if (Application.isBatchMode) return; // annotation state is not persisted in batch mode
            if (AllDone()) return;
            // Annotations exist once the Scene view has drawn an instance: check on (cheap) Scene view repaints.
            SceneView.duringSceneGui += OnSceneGui;
        }

        static string Key(Type t) => $"RecLabs.SmartColliders.GizmoIconHidden.{t.Name}.{Application.dataPath.GetHashCode()}";

        static bool AllDone()
        {
            foreach (var t in Types) if (!EditorPrefs.GetBool(Key(t))) return false;
            return true;
        }

        static void OnSceneGui(SceneView _)
        {
            if (Event.current.type == EventType.Repaint) EditorApplication.delayCall += Apply;
        }

        static void Apply()
        {
            if (AllDone()) { SceneView.duringSceneGui -= OnSceneGui; return; }
            // Script annotations are populated lazily (e.g. by the Gizmos dropdown); GetAnnotations forces it.
            try
            {
                typeof(Editor).Assembly.GetType("UnityEditor.AnnotationUtility")?
                    .GetMethod("GetAnnotations", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?
                    .Invoke(null, null);
            }
            catch (Exception) { }
            foreach (var t in Types)
            {
                if (EditorPrefs.GetBool(Key(t)) || !GizmoUtility.TryGetGizmoInfo(t, out var info)) continue;
                if (info.iconEnabled) GizmoUtility.SetIconEnabled(t, false);
                EditorPrefs.SetBool(Key(t), true);
                SceneView.RepaintAll();
            }
            if (AllDone()) SceneView.duringSceneGui -= OnSceneGui;
        }
    }
}
