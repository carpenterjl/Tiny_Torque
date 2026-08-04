using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <c>Tools ▸ AIHWSim ▸ Mode Templates</c> — a starting scene per mode,
    /// opened from a menu.
    ///
    /// The same shape as <see cref="PhysicsTestMenu"/>, deliberately: one item
    /// that generates the whole set, one item per scene that opens it, and no
    /// entry in Build Settings for any of them.
    ///
    /// <b>Regenerate rather than hand-edit.</b> A template is this file plus
    /// <see cref="ModeTemplateBuilder"/>; the .unity files are output. Editing
    /// one in place is fine while you are looking at it, and will be replaced the
    /// next time somebody rebuilds the set — the same bargain the physics test
    /// scenes make, and for the same reason: nine scene files that can drift from
    /// the code they demonstrate are nine things to be wrong about.
    /// </summary>
    public static class ModeTemplateMenu
    {
        private const string Root = "Tools/AIHWSim/Mode Templates/";

        [MenuItem(Root + "Create All Template Scenes", priority = 100)]
        public static void CreateAll()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            if (Directory.Exists(ModeTemplateBuilder.SceneDir) &&
                !EditorUtility.DisplayDialog("AIHWSim",
                    $"Rebuild every scene in {ModeTemplateBuilder.SceneDir}?\n\n"
                    + "Templates are generated files: anything you changed in one by "
                    + "hand will be replaced. Copy a template to its own scene before "
                    + "building a real level in it.",
                    "Rebuild them", "Cancel"))
                return;

            ModeTemplateBuilder.CreateAll();

            EditorUtility.DisplayDialog("AIHWSim",
                $"Built {ModeTemplateBuilder.All.Length} template scenes in "
                + $"{ModeTemplateBuilder.SceneDir}.\n\n"
                + "Open one from this menu and press Play — it runs the mode it is "
                + "named after, with the opponents its LevelSettings asks for.\n\n"
                + "Deliberately NOT added to Build Settings: these are tools, not "
                + "levels, and the in-game track pickers are unchanged.\n\n"
                + "Re-run this after changing a template in code — and remember that "
                + "it overwrites hand edits.",
                "OK");
        }

        public static void Open(ModeTemplateBuilder.Template id)
        {
            var found = ModeTemplateBuilder.Find(id);
            if (found == null) { Debug.LogError($"[TPL] unknown template '{id}'"); return; }
            var e = found.Value;

            if (!File.Exists(e.ScenePath))
            {
                if (!EditorUtility.DisplayDialog("AIHWSim",
                        $"{e.ScenePath} does not exist yet.\n\nBuild the template scenes now?",
                        "Build", "Cancel"))
                    return;
                ModeTemplateBuilder.CreateAll();
            }

            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(e.ScenePath, OpenSceneMode.Single);
        }

        [MenuItem(Root + "Simulation physics", priority = 200)]
        private static void OpenSim() => Open(ModeTemplateBuilder.Template.SimPhysics);

        [MenuItem(Root + "Arcade physics", priority = 201)]
        private static void OpenArcade() => Open(ModeTemplateBuilder.Template.ArcadePhysics);

        [MenuItem(Root + "Free roam", priority = 202)]
        private static void OpenFreeRoam() => Open(ModeTemplateBuilder.Template.FreeRoam);

        [MenuItem(Root + "Lapped race", priority = 203)]
        private static void OpenLapRace() => Open(ModeTemplateBuilder.Template.LapRace);

        [MenuItem(Root + "Point-to-point race", priority = 204)]
        private static void OpenSprint() => Open(ModeTemplateBuilder.Template.SprintRace);

        [MenuItem(Root + "Soccer", priority = 205)]
        private static void OpenSoccer() => Open(ModeTemplateBuilder.Template.Soccer);

        [MenuItem(Root + "Capture the flag", priority = 206)]
        private static void OpenCtf() => Open(ModeTemplateBuilder.Template.Ctf);

        [MenuItem(Root + "Demolition derby", priority = 207)]
        private static void OpenDemolition() => Open(ModeTemplateBuilder.Template.Demolition);

        [MenuItem(Root + "RC plane", priority = 208)]
        private static void OpenRcPlane() => Open(ModeTemplateBuilder.Template.RcPlane);
    }
}
