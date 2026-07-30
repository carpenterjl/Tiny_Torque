using UnityEditor;

namespace AIHWSim.Pack
{
    /// <summary>
    /// Runs the whole pack pipeline in order, for the menu and for headless runs.
    ///
    /// The steps are separate menu items because each is individually re-runnable
    /// and you usually only want one of them; this is the "rebuild the kit from
    /// scratch" button, and the headless entry point the verification gate calls:
    ///
    ///     Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt; ^
    ///         -executeMethod AIHWSim.Pack.PackBuildAll.RunHeadless -logFile pack.log
    ///
    /// No <c>-nographics</c>: material generation needs a graphics device to
    /// resolve the Standard shader, the same reason TPV omits it.
    /// </summary>
    public static class PackBuildAll
    {
        [MenuItem("Tools/TinyTorque Assets/Rebuild everything", priority = 20)]
        public static void RunAll()
        {
            PackMaterialGenerator.ResetCaches();
            PackImportMenu.CopyFromResources();
            PackMaterialGenerator.Generate();
            PackPrefabGenerator.Generate();
            PackBrushMenu.GeneratePresets();
            PackSceneMenu.CreateDebugScenes();
            PackMapGenerator.Generate();
            PackPaths.Log("REBUILD complete");
        }

        public static void RunHeadless()
        {
            RunAll();
            PackValidator.Report();
        }
    }
}
