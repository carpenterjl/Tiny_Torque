using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// Loads/saves <see cref="TrackDesign"/>s as JSON files under
    /// &lt;project&gt;/UnitySim/Tracks/ (a sibling of Vehicles and TelemetryLogs).
    /// Mirrors VehicleLibrary.
    /// </summary>
    public static class TrackLibrary
    {
        public static string Dir => Path.Combine(AIHWSim.Persistence.AppPaths.BaseDir, "Tracks");

        private static string PathFor(string name) =>
            Path.Combine(Dir, Sanitize(name) + ".json");

        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "track";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }

        public static List<string> List()
        {
            var names = new List<string>();
            if (!Directory.Exists(Dir)) return names;
            foreach (var f in Directory.GetFiles(Dir, "*.json"))
                names.Add(Path.GetFileNameWithoutExtension(f));
            names.Sort();
            return names;
        }

        public static bool Exists(string name) => File.Exists(PathFor(name));

        public static TrackDesign Load(string name)
        {
            string path = PathFor(name);
            if (!File.Exists(path)) return null;
            try
            {
                var d = JsonUtility.FromJson<TrackDesign>(File.ReadAllText(path));
                if (d != null)
                {
                    if (string.IsNullOrEmpty(d.name)) d.name = name;
                    d.EnsureFloor();
                    // Maps saved before per-item scale existed have no `scale`
                    // in their JSON, which deserialises to 0 — repair before
                    // anything can build them at nothing.
                    d.EnsureItems();
                }
                return d;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[TrackLibrary] Failed to load '{name}': {e.Message}");
                return null;
            }
        }

        public static string Save(TrackDesign design)
        {
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
