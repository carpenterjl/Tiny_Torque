using System.IO;
using UnityEngine;

namespace AIHWSim.Persistence
{
    /// <summary>
    /// Single source of truth for the writable base directory that holds user data
    /// (Saves/, Vehicles/, Tracks/, TelemetryLogs/).
    ///
    /// In the editor this stays next to the project (the parent of Application.dataPath =
    /// .../UnitySim/Assets → .../UnitySim), so the dev workflow and any content that
    /// already lives beside the project are unchanged. In a shipped player build it
    /// resolves to <see cref="Application.persistentDataPath"/> (…/AppData/LocalLow/
    /// &lt;company&gt;/&lt;product&gt;), which is always writable regardless of where the game is
    /// installed — so an installed build (even under Program Files) can save.
    /// </summary>
    public static class AppPaths
    {
        public static string BaseDir =>
            Application.isEditor
                ? (Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath)
                : Application.persistentDataPath;
    }
}
