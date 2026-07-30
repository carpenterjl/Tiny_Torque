using System.Collections.Generic;
using AIHWSim.TrackEd;

namespace AIHWSim.Track
{
    /// <summary>
    /// The hand-authored track scenes the game ships, by scene name.
    ///
    /// A static C# table rather than a ScriptableObject or a Build-Settings scan,
    /// matching <c>TrackPresets.All</c>, <c>TrackCatalog.Items</c> and
    /// <c>PackPaths</c> — this project keeps its catalogues in code, where they are
    /// greppable, diffable and cannot be silently emptied by a lost asset reference.
    /// Track Studio regenerates it when a scene track is created or removed.
    ///
    /// Registering here is NOT enough on its own: the scene must also be in Build
    /// Settings or <c>SceneManager.LoadScene</c> cannot find it at runtime. The
    /// validator checks both, because a row here with no build entry is a picker
    /// item that loads a black screen.
    /// </summary>
    public static class SceneTrackCatalog
    {
        /// <summary>
        /// Picker prefix, so a scene track is visibly not a user save — the same
        /// job "★ " does for built-in presets. Distinct glyph because the two
        /// resolve down completely different paths and "why can't I edit this one
        /// in the Track Builder" is otherwise a fair question.
        /// </summary>
        public const string Prefix = "▣ ";

        public struct Row
        {
            public string scene;    // scene name, the persisted identifier
            public string label;    // shown in pickers
            public TrackPresets.TrackKind kind;
        }

        public static readonly Row[] All =
        {
            new Row { scene = "TTA_Sandbox", label = "Sandbox",
                      kind = TrackPresets.TrackKind.FreeRoam },
        };

        /// <summary>Display names for the pickers. <paramref name="raceable"/>
        /// drops FreeRoam scenes, exactly as <c>TrackPresets.DisplayNames</c> does —
        /// there is nothing to race on a map with no finish line.</summary>
        public static List<string> DisplayNames(bool raceable = true)
        {
            var list = new List<string>();
            foreach (var r in All)
            {
                if (raceable && r.kind == TrackPresets.TrackKind.FreeRoam) continue;
                list.Add(Prefix + r.label);
            }
            return list;
        }

        /// <summary>The scene name behind a picker entry, or null if this is not
        /// a scene track. Accepts the prefixed label, the bare label, or the scene
        /// name itself — the wire and snapshots carry the scene name, pickers carry
        /// the label, and both arrive here.</summary>
        public static string Resolve(string display)
        {
            if (string.IsNullOrEmpty(display)) return null;
            string bare = display.StartsWith(Prefix) ? display.Substring(Prefix.Length) : display;
            foreach (var r in All)
                if (r.label == bare || r.scene == bare) return r.scene;
            return null;
        }

        /// <summary>The picker label for a scene name, for round-tripping a
        /// resumed session back into the menu's selection.</summary>
        public static string LabelFor(string scene)
        {
            foreach (var r in All)
                if (r.scene == scene) return Prefix + r.label;
            return null;
        }

        public static bool IsSceneTrack(string display) => Resolve(display) != null;
    }
}
