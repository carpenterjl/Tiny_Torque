using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AIHWSim.Persistence
{
    /// <summary>
    /// JSON save-file I/O under &lt;project&gt;/UnitySim/Saves/ (a sibling of
    /// Vehicles/ and Tracks/, same BaseDir idiom as VehicleLibrary). Holds
    /// settings.json, profiles.json, and Snapshots/*.json.
    /// </summary>
    public static class SaveSystem
    {
        public static string Dir => Path.Combine(AppPaths.BaseDir, "Saves");
        public static string SnapshotDir => Path.Combine(Dir, "Snapshots");

        /// <summary>Load a JSON save file; null on missing or corrupt.</summary>
        public static T LoadJson<T>(string fileName) where T : class
        {
            string path = Path.Combine(Dir, fileName);
            if (!File.Exists(path)) return null;
            try
            {
                return JsonUtility.FromJson<T>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Failed to load '{fileName}': {e.Message}");
                return null;
            }
        }

        public static void SaveJson<T>(string fileName, T data)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(Path.Combine(Dir, fileName), JsonUtility.ToJson(data, true));
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Failed to save '{fileName}': {e.Message}");
            }
        }

        // ---- session snapshots -------------------------------------------

        /// <summary>Snapshot file names (without extension), newest first.</summary>
        public static List<string> ListSnapshots()
        {
            var names = new List<string>();
            if (!Directory.Exists(SnapshotDir)) return names;
            foreach (var f in Directory.GetFiles(SnapshotDir, "*.json"))
                names.Add(Path.GetFileNameWithoutExtension(f));
            names.Sort();
            names.Reverse(); // timestamped names sort newest-last; flip
            return names;
        }

        public static SessionSnapshot LoadSnapshot(string name)
        {
            string path = Path.Combine(SnapshotDir, name + ".json");
            if (!File.Exists(path)) return null;
            try
            {
                return JsonUtility.FromJson<SessionSnapshot>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Failed to load snapshot '{name}': {e.Message}");
                return null;
            }
        }

        /// <summary>Write a snapshot with a timestamped name; returns the path (null on failure).</summary>
        public static string SaveSnapshot(SessionSnapshot s)
        {
            try
            {
                Directory.CreateDirectory(SnapshotDir);
                string name = $"snapshot_{DateTime.Now:yyyyMMdd_HHmmss}";
                string path = Path.Combine(SnapshotDir, name + ".json");
                File.WriteAllText(path, JsonUtility.ToJson(s, true));
                return path;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Failed to save snapshot: {e.Message}");
                return null;
            }
        }

        public static void DeleteSnapshot(string name)
        {
            string path = Path.Combine(SnapshotDir, name + ".json");
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
