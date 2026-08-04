using System.Collections.Generic;
using AIHWSim.Core.Boot;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// The stock inspector, plus the one thing the stock inspector cannot show:
    /// whether the assets in these slots belong to this scene or to another one.
    ///
    /// An object field renders a shared asset and a private one identically, so
    /// "I changed the laps in my copy" and "I changed the laps in the template
    /// and in every scene ever saved from it" look the same while you are doing
    /// them. Naming the owner here is the cheap half of the fix;
    /// <see cref="SceneSettingsOwnership"/> is the half that acts.
    /// </summary>
    [CustomEditor(typeof(DrivingSceneDescriptor))]
    public sealed class DrivingSceneDescriptorEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var d = (DrivingSceneDescriptor)target;
            var scene = d.gameObject.scene;
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) return;

            var foreign = new List<string>();
            var shared = new List<string>();
            Classify(d.level, "Rules", scene.name, foreign, shared);
            Classify(d.physics, "Physics", scene.name, foreign, shared);
            Classify(d.assists, "Assists", scene.name, foreign, shared);
            Classify(d.modes, "Mode tuning", scene.name, foreign, shared);
            Classify(d.arcade, "Arcade tuning", scene.name, foreign, shared);

            EditorGUILayout.Space();
            if (foreign.Count > 0)
                EditorGUILayout.HelpBox(
                    $"{string.Join(", ", foreign)} — editing these changes those scenes "
                    + "too. Saving this scene under its own name takes private copies "
                    + "automatically.", MessageType.Warning);
            else if (shared.Count > 0)
                EditorGUILayout.HelpBox(
                    $"{string.Join(", ", shared)} use the project-wide defaults. That is "
                    + "usually right for world tuning — take copies only if this scene "
                    + "needs its own physics or arcade feel.", MessageType.Info);
            else
                EditorGUILayout.HelpBox($"Every settings asset here belongs to "
                    + $"'{scene.name}' alone.", MessageType.None);

            if (GUILayout.Button("Give This Scene Its Own Settings"))
                SceneSettingsOwnership.LocaliseOpenScene();
        }

        private static void Classify(Object asset, string label, string sceneName,
                                     List<string> foreign, List<string> shared)
        {
            if (asset == null) return;
            string path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path)) return;
            if (SceneSettingsOwnership.OwnedBy(path, sceneName)) return;

            if (SceneSettingsOwnership.IsShared(path)) { shared.Add(label); return; }
            string owner = SceneSettingsOwnership.OwnerOf(path);
            foreign.Add(owner == null ? label : $"{label} belongs to {owner}");
        }
    }
}
