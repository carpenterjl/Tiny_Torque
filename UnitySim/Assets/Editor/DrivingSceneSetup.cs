using System.IO;
using AIHWSim.Core.Boot;
using AIHWSim.Core.Config;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Give the open driving scene a <see cref="DrivingSceneDescriptor"/>, and
    /// make it the settings assets to point at.
    ///
    /// This exists rather than the descriptors being pre-placed in the four
    /// shipped driving scenes, for a reason worth stating: adding a component to
    /// a scene is an edit to a file the author owns, and doing it to four of
    /// them silently — including two circuits converted from Blender — is the
    /// kind of help nobody asked for. Running a menu item is one click, it is
    /// undoable, and it leaves the same result.
    ///
    /// <b>The assets it writes are inert by construction.</b> Every field
    /// initialiser in all five — level rules, physics, assists, mode tuning and
    /// arcade tuning — is the number the code already used, so a
    /// scene that has just been given a descriptor behaves exactly as it did
    /// before — until somebody edits a value on purpose. Assets are created only
    /// when absent; running this twice reuses them and moves no bytes.
    /// </summary>
    public static class DrivingSceneSetup
    {
        private const string Tag = "[DSC]";
        private const string Dir = "Assets/Settings/Driving";

        [MenuItem("Tools/AIHWSim/Driving Scene/Add Descriptor to Open Scene", priority = 400)]
        public static void AddToOpenScene()
        {
            var existing = Object.FindFirstObjectByType<DrivingSceneDescriptor>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorUtility.DisplayDialog("Driving Scene",
                    $"'{existing.gameObject.scene.name}' already has a descriptor, on "
                    + $"'{existing.gameObject.name}'. Selected it instead of adding a "
                    + "second one.", "OK");
                return;
            }

            // Prefer the object that already composes this scene, so the two
            // descriptors and the bootstrap read as one unit in the hierarchy.
            var host = Object.FindFirstObjectByType<AIHWSim.Core.TrackBootstrap>()?.gameObject
                       ?? Object.FindFirstObjectByType<AIHWSim.Track.SceneTrackDescriptor>()?.gameObject
                       ?? new GameObject("DrivingScene");

            var d = Undo.AddComponent<DrivingSceneDescriptor>(host);
            d.level = LoadOrCreate<LevelSettings>("LevelSettings_Default");
            d.physics = LoadOrCreate<PhysicsSettings>("PhysicsSettings_Default");
            d.assists = LoadOrCreate<AIHWSim.Vehicles.AssistTuningOverride>("AssistTuning_Default");
            d.modes = LoadOrCreate<AIHWSim.Modes.ModeConfigOverride>("ModeTuning_Default");
            d.arcade = LoadOrCreate<AIHWSim.Arcade.ArcadeConfigOverride>("ArcadeTuning_Default");
            EditorUtility.SetDirty(d);
            EditorSceneManager.MarkSceneDirty(host.scene);
            Selection.activeGameObject = host;

            Debug.Log($"{Tag} added a DrivingSceneDescriptor to '{host.name}' in scene "
                      + $"'{host.scene.name}', pointing at the shared default assets under "
                      + $"{Dir}. Nothing about the scene's behaviour has changed — those "
                      + "assets hold the values the code already used. Save the scene to keep it.");
        }

        [MenuItem("Tools/AIHWSim/Driving Scene/Create Default Settings Assets", priority = 401)]
        public static void CreateDefaults()
        {
            var lvl = LoadOrCreate<LevelSettings>("LevelSettings_Default");
            LoadOrCreate<PhysicsSettings>("PhysicsSettings_Default");
            LoadOrCreate<AIHWSim.Vehicles.AssistTuningOverride>("AssistTuning_Default");
            LoadOrCreate<AIHWSim.Modes.ModeConfigOverride>("ModeTuning_Default");
            LoadOrCreate<AIHWSim.Arcade.ArcadeConfigOverride>("ArcadeTuning_Default");
            Selection.activeObject = lvl;
            Debug.Log($"{Tag} defaults ready under {Dir}: level rules, physics, assists, "
                      + "mode tuning and arcade tuning. Every one of them holds the numbers "
                      + "the code already used, so assigning them changes nothing until you "
                      + "edit a value — and you can edit them while the game is playing.");
        }

        /// <summary>
        /// The asset at <c>Dir/name.asset</c>, created with its own defaults if
        /// it is not there yet.
        ///
        /// Folders are made with <see cref="Directory.CreateDirectory"/> and a
        /// Refresh rather than <c>AssetDatabase.CreateFolder</c>, which
        /// uniquifies a name that already exists and would quietly produce
        /// "Driving 1" — the same trap PackPaths documents.
        /// </summary>
        internal static T LoadOrCreate<T>(string name) where T : ScriptableObject =>
            LoadOrCreate<T>(Dir, name);

        /// <summary>Same, into a caller-chosen folder — what the mode templates
        /// use for their per-scene <c>LevelSettings</c>, so there is still exactly
        /// one piece of code in the project that decides what "find or create a
        /// settings asset" means.</summary>
        internal static T LoadOrCreate<T>(string dir, string name) where T : ScriptableObject
        {
            string path = $"{dir}/{name}.asset";
            var found = AssetDatabase.LoadAssetAtPath<T>(path);
            if (found != null) return found;

            string abs = Path.Combine(Directory.GetCurrentDirectory(), dir);
            if (!Directory.Exists(abs)) { Directory.CreateDirectory(abs); AssetDatabase.Refresh(); }

            var made = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(made, path);
            AssetDatabase.SaveAssets();
            return made;
        }
    }
}
