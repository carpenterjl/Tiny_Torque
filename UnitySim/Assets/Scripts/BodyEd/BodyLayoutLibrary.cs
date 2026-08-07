using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// Reads and writes <see cref="VehicleLayoutData"/> as JSON under
    /// &lt;base&gt;/BodyLayouts/ — a sibling of Vehicles/ and Tracks/, on the same
    /// terms: hand-editable, diffable, and in the editor sitting next to the
    /// project rather than in a hidden per-user folder
    /// (<c>AIHWSim.Persistence.AppPaths</c>).
    ///
    /// Structurally this is <c>VehicleLibrary</c> with a different payload, and
    /// deliberately so — there is no reason for a second file format or a second
    /// place to look.
    /// </summary>
    public static class BodyLayoutLibrary
    {
        public static string Dir => Path.Combine(AIHWSim.Persistence.AppPaths.BaseDir, "BodyLayouts");

        private static string PathFor(string name) =>
            Path.Combine(Dir, Sanitize(name) + ".json");

        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "layout";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }

        /// <summary>Saved layout names, without extension, sorted.</summary>
        public static List<string> List()
        {
            var names = new List<string>();
            if (!Directory.Exists(Dir)) return names;
            foreach (string f in Directory.GetFiles(Dir, "*.json"))
                names.Add(Path.GetFileNameWithoutExtension(f));
            names.Sort();
            return names;
        }

        public static bool Exists(string fileName) => File.Exists(PathFor(fileName));

        /// <summary>
        /// Write a layout. Returns the path written, or null if it could not be.
        ///
        /// The log line reports what was actually stored rather than that
        /// something happened — a save that silently wrote zero offsets is the
        /// failure worth being able to see at a glance.
        /// </summary>
        public static string SaveVehicleToFile(string fileName, VehicleLayoutData data)
        {
            if (data == null)
            {
                Debug.LogError("[BodyLayoutLibrary] Refusing to save a null layout.");
                return null;
            }
            try
            {
                Directory.CreateDirectory(Dir);
                string path = PathFor(fileName);
                File.WriteAllText(path, JsonUtility.ToJson(data, true));

                Debug.Log($"[BodyLayoutLibrary] Saved '{fileName}' — {Describe(data)} → {path}");
                return path;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BodyLayoutLibrary] Failed to save '{fileName}': {e.Message}");
                return null;
            }
        }

        /// <summary>Read a layout, or null when it is missing or unreadable.</summary>
        public static VehicleLayoutData LoadVehicleFromFile(string fileName)
        {
            string path = PathFor(fileName);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[BodyLayoutLibrary] No layout named '{fileName}' at {path}.");
                return null;
            }
            try
            {
                var d = JsonUtility.FromJson<VehicleLayoutData>(File.ReadAllText(path));
                if (d == null)
                {
                    Debug.LogError($"[BodyLayoutLibrary] '{fileName}' parsed to nothing — " +
                                   "the file is not a vehicle layout.");
                    return null;
                }
                Debug.Log($"[BodyLayoutLibrary] Loaded '{fileName}' — {Describe(d)}.");
                return d;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BodyLayoutLibrary] Failed to load '{fileName}': {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// What a layout actually contains, for the save/load log lines.
        ///
        /// Reporting the CONTENTS rather than that something happened is the
        /// point: a save that silently wrote zero offsets, or a load that quietly
        /// dropped every prop, is the failure worth being able to see at a glance
        /// in the console.
        /// </summary>
        private static string Describe(VehicleLayoutData d)
        {
            int morphs = d.blendShapeWeights?.Length ?? 0;
            int offsets = d.offsetIndex?.Length ?? 0;
            int props = d.props?.Length ?? 0;
            int tints = d.tints?.Length ?? 0;
            int hidden = d.hiddenChannels?.Length ?? 0;
            int nodes = d.latticeNodes?.Length ?? 0;
            int beams = d.latticeBeams?.Length ?? 0;
            return $"body '{d.carBasePrefabID}', v{d.version}, {morphs} morph weights, " +
                   $"{offsets} vertex offsets, {props} parts, {tints} tints, {hidden} hidden, " +
                   $"{nodes} frame nodes, {beams} beams";
        }

        public static void Delete(string fileName)
        {
            string path = PathFor(fileName);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
