using AIHWSim.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <c>Tools ▸ AIHWSim ▸ Debug Vehicle</c> — put the full-scale reference car
    /// into whatever scene is open, without hand-wiring anything.
    ///
    /// The two edit-mode items add and remove a <see cref="DebugVehicleSpawner"/>;
    /// everything the spawner does is described on that component, and this file
    /// deliberately holds no configuration of its own beyond the defaults, so
    /// there is one place to read and one place to change.
    ///
    /// <b>The scene is left dirty on purpose.</b> Adding the spawner is a change
    /// to the scene asset, and saving it silently — into a hand-authored circuit,
    /// say — would ship a debug tool inside content. Whether this is a scratch
    /// experiment or something worth keeping is the author's call, so the
    /// decision is left where Ctrl+S already is.
    /// </summary>
    public static class DebugVehicleMenu
    {
        private const string Root = "Tools/AIHWSim/Debug Vehicle/";

        [MenuItem(Root + "Add Spawner to Open Scene", priority = 100)]
        public static void AddSpawner()
        {
            var existing = Object.FindFirstObjectByType<DebugVehicleSpawner>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorUtility.DisplayDialog("AIHWSim",
                    "This scene already has a Debug Vehicle Spawner — selected it "
                    + "for you.\n\nSet its Mode in the Inspector:\n"
                    + "  • Replace Player Car — drive the Tiguan instead of your car\n"
                    + "  • Spawn Alongside — add it next to whatever is there", "OK");
                return;
            }

            var go = new GameObject("DebugVehicleSpawner");
            go.AddComponent<DebugVehicleSpawner>();
            Undo.RegisterCreatedObjectUndo(go, "Add Debug Vehicle Spawner");
            Selection.activeGameObject = go;
            EditorSceneManager.MarkSceneDirty(go.scene);

            EditorUtility.DisplayDialog("AIHWSim",
                "Added a Debug Vehicle Spawner. Press Play to drive the full-scale "
                + "VW Tiguan in this scene.\n\n"
                + "The scene is left UNSAVED — save it only if you want the "
                + "spawner to stay.\n\n"
                + "Note: measurements belong in PhysicsDebugScene, whose ground is "
                + "frictionless. Here you get this scene's surfaces, which is a "
                + "driving impression rather than a measurement.", "OK");
        }

        [MenuItem(Root + "Remove Spawner from Open Scene", priority = 101)]
        public static void RemoveSpawner()
        {
            int n = 0;
            foreach (var s in Object.FindObjectsByType<DebugVehicleSpawner>(
                         FindObjectsSortMode.None))
            {
                var scene = s.gameObject.scene;
                Undo.DestroyObjectImmediate(s.gameObject);
                EditorSceneManager.MarkSceneDirty(scene);
                n++;
            }
            Debug.Log($"[DebugVehicle] Removed {n} spawner(s).");
        }

        [MenuItem(Root + "Remove Spawner from Open Scene", validate = true)]
        private static bool CanRemove() =>
            Object.FindFirstObjectByType<DebugVehicleSpawner>() != null;

        /// <summary>
        /// Play-mode only: drop the car in live, next to whatever is being driven,
        /// and take the camera and keys.
        ///
        /// Only SpawnAlongside is offered here, and that is a consequence rather
        /// than a preference: ReplacePlayerCar works by getting in front of the
        /// scene's bootstrap, and by the time you can click a menu item the
        /// bootstrap has long since built its car.
        /// </summary>
        [MenuItem(Root + "Spawn Alongside Now (Play Mode)", priority = 120)]
        public static void SpawnNow()
        {
            // Deactivated first, or AddComponent runs Awake immediately — with
            // mode still at its ReplacePlayerCar default, which would arm the
            // pre-bootstrap substitution in a session that has already started
            // and leave a displaced design behind for the garage to find.
            var go = new GameObject("DebugVehicleSpawner (runtime)");
            go.SetActive(false);
            var s = go.AddComponent<DebugVehicleSpawner>();
            s.mode = DebugVehicleSpawner.Mode.SpawnAlongside;
            go.SetActive(true);
        }

        [MenuItem(Root + "Spawn Alongside Now (Play Mode)", validate = true)]
        private static bool CanSpawnNow() =>
            EditorApplication.isPlaying &&
            Object.FindFirstObjectByType<DebugVehicleSpawner>() == null;
    }
}
