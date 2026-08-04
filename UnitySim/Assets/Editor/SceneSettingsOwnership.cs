using System.Collections.Generic;
using System.IO;
using AIHWSim.Core.Boot;
using AIHWSim.Core.Config;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Who owns a driving scene's settings assets — and the one rule that keeps
    /// two scenes from quietly editing each other.
    ///
    /// <b>The problem this exists for.</b> A <see cref="DrivingSceneDescriptor"/>
    /// points at assets, and Save As copies the scene but not what it points at.
    /// Open a mode template, save it as <c>Arcade_Test_Scene</c>, set it to three
    /// laps — and you have set the TEMPLATE to three laps, along with every other
    /// scene ever saved from it. Regenerating the templates then writes the
    /// template's own values back over yours. Neither step says anything, which
    /// is the worst part: the file you edited is not the file you were looking at.
    ///
    /// <b>The rule.</b> A settings asset's owner is read from where it sits:
    ///
    /// <list type="bullet">
    /// <item><c>Assets/Settings/Driving/*.asset</c> — <b>shared on purpose.</b>
    /// The <c>_Default</c> assets every shipped driving scene points at. One
    /// project-wide answer to "how does the physics step" is the right answer,
    /// and these are never cloned behind your back.</item>
    /// <item><c>Assets/Settings/Driving/Templates/*.asset</c> — owned by the
    /// mode templates under <c>Assets/Scenes/ModeTemplates/</c>, which regenerate
    /// and are expected to be overwritten.</item>
    /// <item><c>Assets/Settings/Driving/Scenes/&lt;SceneName&gt;/*.asset</c> —
    /// owned by exactly that scene.</item>
    /// </list>
    ///
    /// When a scene is saved under a name that does not own the assets it points
    /// at, those assets are cloned into its own folder and the descriptor is
    /// repointed — <b>before</b> the scene file is written, from
    /// <c>sceneSaving</c>, so the save that renames the scene is the same save
    /// that gives it its own settings. The clone carries the values verbatim, so
    /// nothing about the scene changes except which file it edits.
    ///
    /// Shared defaults are deliberately left alone by that automatic path: a
    /// scene pointing at <c>PhysicsSettings_Default</c> is not a mistake. Use
    /// <see cref="LocaliseOpenScene"/> when you want a scene to own its world
    /// tuning too.
    /// </summary>
    [InitializeOnLoad]
    public static class SceneSettingsOwnership
    {
        private const string Tag = "[OWN]";
        private const string Root = "Assets/Settings/Driving";
        private const string SharedDir = Root;
        private const string TemplateDir = Root + "/Templates";
        private const string SceneDir = Root + "/Scenes";

        /// <summary>The scenes that legitimately own <see cref="TemplateDir"/>.
        /// The builder saves nine scenes here and must not have its own assets
        /// cloned out from under it on the way.</summary>
        private const string TemplateScenes = "Assets/Scenes/ModeTemplates/";

        static SceneSettingsOwnership()
        {
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneSaving += OnSceneSaving;
        }

        // -------------------------------------------------------------------
        // the slots
        // -------------------------------------------------------------------

        /// <summary>Every asset reference a descriptor holds, as a get/set pair.
        /// Written out rather than reflected over so that adding a sixth settings
        /// type is a compile error here rather than a slot that silently keeps
        /// being shared.</summary>
        private struct Slot
        {
            public string label;
            public System.Func<DrivingSceneDescriptor, Object> Get;
            public System.Action<DrivingSceneDescriptor, Object> Set;
            /// <summary>True for the rules, which are a property of the level and
            /// belong to one scene. False for world tuning, which a project may
            /// reasonably share — see the class note.</summary>
            public bool perScene;
        }

        private static readonly Slot[] Slots =
        {
            new Slot { label = "level",   perScene = true,
                       Get = d => d.level,   Set = (d, o) => d.level = (LevelSettings)o },
            new Slot { label = "physics", perScene = false,
                       Get = d => d.physics, Set = (d, o) => d.physics = (PhysicsSettings)o },
            new Slot { label = "assists", perScene = false,
                       Get = d => d.assists,
                       Set = (d, o) => d.assists = (AIHWSim.Vehicles.AssistTuningOverride)o },
            new Slot { label = "modes",   perScene = false,
                       Get = d => d.modes,
                       Set = (d, o) => d.modes = (AIHWSim.Modes.ModeConfigOverride)o },
            new Slot { label = "arcade",  perScene = false,
                       Get = d => d.arcade,
                       Set = (d, o) => d.arcade = (AIHWSim.Arcade.ArcadeConfigOverride)o },
        };

        // -------------------------------------------------------------------
        // the automatic half
        // -------------------------------------------------------------------

        /// <summary>
        /// Runs before the scene file is written, with the path it is about to be
        /// written to — which is how Save As is caught at all. <c>scene.name</c>
        /// is still the OLD name here; <paramref name="path"/> is the new one.
        /// </summary>
        private static void OnSceneSaving(Scene scene, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (path.Replace('\\', '/').StartsWith(TemplateScenes)) return;

            string sceneName = Path.GetFileNameWithoutExtension(path);
            var d = FindDescriptorIn(scene);
            if (d == null) return;

            var moved = Localise(d, sceneName, includeShared: false);
            if (moved.Count == 0) return;

            EditorUtility.SetDirty(d);
            Debug.Log($"{Tag} '{sceneName}' now has its own {string.Join(", ", moved)} "
                      + $"under {SceneDir}/{sceneName}. The values are unchanged — it was "
                      + "pointing at another scene's assets, and editing them there would "
                      + "have edited that scene too.", d);
        }

        /// <summary>
        /// Give <paramref name="d"/> its own copy of every asset it does not own.
        /// Returns the slot labels that were cloned, empty when nothing was.
        ///
        /// <paramref name="includeShared"/> false leaves the project-wide
        /// <c>_Default</c> assets alone; true takes a private copy of those too,
        /// which is what the menu item does.
        /// </summary>
        public static List<string> Localise(DrivingSceneDescriptor d, string sceneName,
                                            bool includeShared)
        {
            var moved = new List<string>();
            if (d == null || string.IsNullOrWhiteSpace(sceneName)) return moved;

            foreach (var slot in Slots)
            {
                var asset = slot.Get(d);
                if (asset == null) continue;

                string src = AssetDatabase.GetAssetPath(asset);
                if (string.IsNullOrEmpty(src)) continue;   // not an asset at all
                if (OwnedBy(src, sceneName)) continue;
                if (!includeShared && IsShared(src)) continue;

                var copy = Clone(asset, sceneName);
                if (copy == null) continue;
                slot.Set(d, copy);
                moved.Add(slot.label);
            }
            return moved;
        }

        // -------------------------------------------------------------------
        // the deliberate half
        // -------------------------------------------------------------------

        [MenuItem("Tools/AIHWSim/Driving Scene/Give This Scene Its Own Settings", priority = 402)]
        public static void LocaliseOpenScene()
        {
            var d = Object.FindFirstObjectByType<DrivingSceneDescriptor>();
            if (d == null)
            {
                EditorUtility.DisplayDialog("Driving Scene",
                    "No DrivingSceneDescriptor in the open scenes. Add one first "
                    + "(Driving Scene ▸ Add Descriptor to Open Scene).", "OK");
                return;
            }

            var scene = d.gameObject.scene;
            if (string.IsNullOrEmpty(scene.path))
            {
                EditorUtility.DisplayDialog("Driving Scene",
                    "Save the scene first. A scene's settings are named after it, "
                    + "so an unsaved scene has nothing to name them.", "OK");
                return;
            }

            Undo.RecordObject(d, "Localise scene settings");
            var moved = Localise(d, scene.name, includeShared: true);
            if (moved.Count == 0)
            {
                Debug.Log($"{Tag} '{scene.name}' already owns every settings asset it "
                          + "points at. Nothing to do.", d);
                return;
            }

            EditorUtility.SetDirty(d);
            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeObject = d.level;
            Debug.Log($"{Tag} '{scene.name}' now owns {string.Join(", ", moved)} under "
                      + $"{SceneDir}/{scene.name}. Same values, its own files — edits here "
                      + "no longer reach any other scene. Save the scene to keep it.", d);
        }

        // -------------------------------------------------------------------
        // ownership, read from the path
        // -------------------------------------------------------------------

        /// <summary>True for the project-wide defaults sitting directly in
        /// <see cref="SharedDir"/>.</summary>
        public static bool IsShared(string assetPath) =>
            Dir(assetPath) == SharedDir;

        /// <summary>True when <paramref name="sceneName"/> is the asset's owner:
        /// its own folder under <see cref="SceneDir"/>, or a mode template
        /// pointing into <see cref="TemplateDir"/>.</summary>
        public static bool OwnedBy(string assetPath, string sceneName)
        {
            string dir = Dir(assetPath);
            if (dir == $"{SceneDir}/{sceneName}") return true;
            if (dir == TemplateDir && sceneName.StartsWith("Template_")) return true;
            return false;
        }

        /// <summary>The scene this asset belongs to for display, or null when it
        /// is shared or lives somewhere else entirely.</summary>
        public static string OwnerOf(string assetPath)
        {
            string dir = Dir(assetPath);
            if (dir == TemplateDir) return "the mode templates";
            if (dir.StartsWith(SceneDir + "/")) return dir.Substring(SceneDir.Length + 1);
            return null;
        }

        private static string Dir(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return string.Empty;
            string p = assetPath.Replace('\\', '/');
            int cut = p.LastIndexOf('/');
            return cut <= 0 ? string.Empty : p.Substring(0, cut);
        }

        // -------------------------------------------------------------------
        // cloning
        // -------------------------------------------------------------------

        /// <summary>
        /// A copy of <paramref name="asset"/> in the scene's own folder.
        ///
        /// <see cref="Object.Instantiate(Object)"/> rather than
        /// <c>AssetDatabase.CopyAsset</c>: this runs inside <c>sceneSaving</c>,
        /// and an in-memory copy written once through CreateAsset touches the
        /// database in one place instead of copying a file and waiting for it to
        /// be imported.
        ///
        /// An existing file of the same name is REUSED, not replaced. Saving the
        /// same scene twice must not produce "LevelSettings_Foo 1", and a scene
        /// whose descriptor was reassigned by hand should land back on the assets
        /// it already had.
        /// </summary>
        private static Object Clone(Object asset, string sceneName)
        {
            string dir = $"{SceneDir}/{sceneName}";
            string path = $"{dir}/{asset.GetType().Name}_{sceneName}.asset";

            var existing = AssetDatabase.LoadAssetAtPath(path, asset.GetType());
            if (existing != null) return existing;

            if (!EnsureFolder(dir)) return null;

            var copy = Object.Instantiate(asset);
            copy.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(copy, path);
            AssetDatabase.SaveAssets();
            return copy;
        }

        /// <summary>
        /// Make every folder on the way to <paramref name="dir"/>.
        ///
        /// <c>AssetDatabase.CreateFolder</c> only, and only when the folder is not
        /// already there: it uniquifies a name that exists, so calling it blind
        /// would quietly produce "Scenes 1" — the trap PackPaths and
        /// DrivingSceneSetup both document.
        /// </summary>
        private static bool EnsureFolder(string dir)
        {
            if (AssetDatabase.IsValidFolder(dir)) return true;

            var parts = dir.Split('/');
            string built = parts[0];                       // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{built}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    string guid = AssetDatabase.CreateFolder(built, parts[i]);
                    if (string.IsNullOrEmpty(guid)) return false;
                }
                built = next;
            }
            return true;
        }

        // -------------------------------------------------------------------

        /// <summary>The descriptor in THIS scene. A global find would reach into
        /// an additively loaded TrackScene and localise the wrong scene's
        /// settings, which is the one mistake this whole file exists to
        /// prevent.</summary>
        private static DrivingSceneDescriptor FindDescriptorIn(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            foreach (var root in scene.GetRootGameObjects())
            {
                var d = root.GetComponentInChildren<DrivingSceneDescriptor>(true);
                if (d != null) return d;
            }
            return null;
        }
    }
}
