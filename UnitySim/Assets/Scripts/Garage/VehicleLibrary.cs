using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// Loads/saves <see cref="VehicleDesign"/>s as JSON files under
    /// &lt;project&gt;/UnitySim/Vehicles/ (a sibling of TelemetryLogs). Files are
    /// hand-editable and diffable.
    /// </summary>
    public static class VehicleLibrary
    {
        public static string Dir => Path.Combine(AIHWSim.Persistence.AppPaths.BaseDir, "Vehicles");

        private static string PathFor(string name) =>
            Path.Combine(Dir, Sanitize(name) + ".json");

        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "vehicle";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }

        /// <summary>
        /// Names (without extension) of all saved designs. Designs saved before
        /// the RC-scale rescale (Iteration 11) are hidden: a mass over 50 kg can
        /// only be an old full-car-scale JSON, which would be absurd (and
        /// undriveable) next to the 1/10-scale physics.
        /// </summary>
        public static List<string> List()
        {
            var names = new List<string>();
            if (!Directory.Exists(Dir)) return names;
            foreach (var f in Directory.GetFiles(Dir, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                var d = Load(name);
                if (d == null) continue;
                if (d.mass > 50f)
                {
                    Debug.Log($"[VehicleLibrary] Hiding old-scale design '{name}' (mass {d.mass:0} kg).");
                    continue;
                }
                names.Add(name);
            }
            names.Sort();
            return names;
        }

        public static bool Exists(string name) => File.Exists(PathFor(name));

        public static VehicleDesign Load(string name)
        {
            string path = PathFor(name);
            if (!File.Exists(path)) return null;
            try
            {
                var d = JsonUtility.FromJson<VehicleDesign>(File.ReadAllText(path));
                if (d != null && string.IsNullOrEmpty(d.name)) d.name = name;
                return d;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[VehicleLibrary] Failed to load '{name}': {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Write a design to disk, filling in its string keys first.
        ///
        /// This is the ONLY caller of <see cref="VehicleDesign.Migrate"/>, and
        /// deliberately: writing a file is the one moment a design's on-disk form
        /// is being decided, so it is the one moment the pair can be brought into
        /// agreement without rewriting anything the player did not touch.
        /// </summary>
        public static string Save(VehicleDesign design)
        {
            design.Migrate();
            Directory.CreateDirectory(Dir);
            string path = PathFor(design.name);
            File.WriteAllText(path, JsonUtility.ToJson(design, true));
            return path;
        }

        public static void Delete(string name)
        {
            string path = PathFor(name);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
