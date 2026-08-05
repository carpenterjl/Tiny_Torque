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

        /// <summary>
        /// The map the Simulate Controller screen opens on, as a picker display
        /// name. A hand-authored scene rather than a generated oval because that
        /// screen's job is "does my C code drive", and answering it wants a real
        /// road with corners, kerbs and walls that stay put between runs — a
        /// procedural track is regenerated per session and is a moving reference.
        ///
        /// Public and const so <c>GameSettings.lastControllerMap</c> can use it as
        /// its field initializer: a settings file that predates the key reads as
        /// this map rather than as the classic oval.
        /// </summary>
        public const string ControllerMap = Prefix + ControllerLabel;
        private const string ControllerLabel = "UCSD Test Track";

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
            // FreeRoam, and it still times laps. The kind decides what the map is
            // FOR — this one is a place to test a controller, not a race to enter,
            // so it stays out of the race picker and starts with nothing to win.
            // The finish line and four checkpoints it carries are there so a
            // line-following controller can be scored: SceneTrackBuilder builds
            // the gates and the LapTimer from the markers, never from the kind, so
            // the lap box is on the HUD whatever mode you arrived in.
            new Row { scene = "UCSD_TrackScene", label = ControllerLabel,
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

        /// <summary>Free-roam scene tracks, prefixed — the rows
        /// <see cref="DisplayNames"/> drops. The same split
        /// <c>TrackPresets.RoamNames</c> makes, so free roam's picker is built
        /// from both catalogues the same way the race picker is.</summary>
        public static List<string> RoamNames()
        {
            var list = new List<string>();
            foreach (var r in All)
                if (r.kind == TrackPresets.TrackKind.FreeRoam) list.Add(Prefix + r.label);
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
