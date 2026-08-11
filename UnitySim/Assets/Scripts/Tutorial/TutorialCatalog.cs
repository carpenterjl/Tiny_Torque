using System.Collections.Generic;

namespace AIHWSim.Tutorial
{
    /// <summary>
    /// Every tutorial the game ships, in the order the "play all" sequence runs
    /// them.
    ///
    /// A static C# table for the same reason <c>SceneTrackCatalog</c> and
    /// <c>TrackPresets.All</c> are: this project keeps its catalogues in code,
    /// where they are greppable, diffable and cannot be silently emptied by a
    /// lost asset reference.
    ///
    /// DELIBERATELY NOT rows in <c>SceneTrackCatalog</c>, even though the driving
    /// tutorials are scene tracks and load down exactly that path. That catalogue
    /// feeds the race and free-roam pickers, and a tutorial map has no business
    /// being offered as somewhere to race — it is a lesson with a floor.
    ///
    /// A row whose <see cref="Row.scene"/> is null is an OVERLAY tutorial: it
    /// teaches a screen rather than a place, so it runs as callouts drawn over
    /// the real UI (see <c>TutorialOverlay</c>) and never loads a map. The two
    /// kinds share everything else — the step model, the conditions, the
    /// progress, the payout.
    ///
    /// Registering a scene here is NOT enough on its own: it must also be in
    /// Build Settings or <c>SceneManager.LoadScene</c> cannot find it at runtime.
    /// The [TUT] validator checks both, because a row here with no build entry is
    /// a menu item that loads a black screen.
    /// </summary>
    public static class TutorialCatalog
    {
        /// <summary>
        /// The hub groups its list under these. Order here is the order the
        /// headers appear, and within a category the order of <see cref="All"/>
        /// decides the rows.
        /// </summary>
        public enum Category
        {
            Basics,
            Simulation,
            Modes,
            Garage,
            Online,
        }

        public struct Row
        {
            public string id;           // persisted identifier — never renamed
            public string label;        // shown in the hub
            public Category category;
            public string scene;        // scene name, or null for an overlay tutorial
            public string blurb;        // one line under the label
            public int scrap;           // paid once, on first completion
        }

        /// <summary>
        /// Scrap for finishing one. Flat rather than per-topic: the sensors
        /// tutorial being longer than the arcade one is not a reason to pay more
        /// for it, and a player picking their next lesson should be choosing what
        /// they want to learn rather than what pays.
        /// </summary>
        public const int Reward = 120;

        /// <summary>The crate every tutorial being done pays out, once.</summary>
        public const string CompletionCrate = "vault";

        public static readonly Row[] All =
        {
            new Row { id = "single_player", label = "Getting started",
                      category = Category.Basics, scene = "Tut_SinglePlayer", scrap = Reward,
                      blurb = "Driving, checkpoints, respawning and the results screen." },
            new Row { id = "arcade", label = "Arcade mode",
                      category = Category.Basics, scene = "Tut_Arcade", scrap = Reward,
                      blurb = "Drifting for boost, item boxes and what they do to you." },

            // The simulation set. sim_controllers is the trunk the intake
            // questions branch off: whatever the player answers, they start here,
            // because the other three all assume a car under a controller.
            new Row { id = "sim_controllers", label = "Simulation & controllers",
                      category = Category.Simulation, scene = "Tut_SimControllers", scrap = Reward,
                      blurb = "The simulated car, its telemetry, and who is driving it." },
            new Row { id = "sim_ipc", label = "The external control app",
                      category = Category.Simulation, scene = "Tut_SimIpc", scrap = Reward,
                      blurb = "Turn on the bridge and drive this car from another program." },
            new Row { id = "sim_firmware", label = "Writing your own firmware",
                      category = Category.Simulation, scene = "Tut_SimFirmware", scrap = Reward,
                      blurb = "Your C code, built in-game, driving the car in front of you." },
            new Row { id = "sim_sensors", label = "Sensors and their data",
                      category = Category.Simulation, scene = "Tut_SimSensors", scrap = Reward,
                      blurb = "What each sensor sees and what its numbers actually look like." },

            new Row { id = "mode_race", label = "Racing",
                      category = Category.Modes, scene = "Tut_ModeRace", scrap = Reward,
                      blurb = "Laps, track limits and racing a field of bots." },
            new Row { id = "mode_derby", label = "Demolition derby",
                      category = Category.Modes, scene = "Tut_ModeDerby", scrap = Reward,
                      blurb = "Damage, hits that count, and being the last one running." },
            new Row { id = "mode_ctf", label = "Capture the flag",
                      category = Category.Modes, scene = "Tut_ModeCtf", scrap = Reward,
                      blurb = "Carrying, dropping, and getting a flag home." },
            new Row { id = "mode_soccer", label = "Soccer",
                      category = Category.Modes, scene = "Tut_ModeSoccer", scrap = Reward,
                      blurb = "Pushing a ball much heavier than you into a goal." },

            // Overlay tutorials — no scene. These teach screens, and the real
            // screens are the only honest place to teach them.
            new Row { id = "customize", label = "Building a vehicle",
                      category = Category.Garage, scene = null, scrap = Reward,
                      blurb = "The garage: pick a car, paint it, save it as your own." },
            new Row { id = "online", label = "Playing with friends",
                      category = Category.Online, scene = null, scrap = Reward,
                      blurb = "Hosting a LAN game and what your machine is responsible for." },
        };

        public static bool Exists(string id) => IndexOf(id) >= 0;

        public static int IndexOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < All.Length; i++)
                if (All[i].id == id) return i;
            return -1;
        }

        /// <summary>The row, or a zeroed one for an id this build does not know.
        /// Returning a blank rather than throwing is deliberate: an id can arrive
        /// from a progress file written by a newer build, and a player's profile
        /// naming a tutorial we removed should not stop the menu from drawing.</summary>
        public static Row ById(string id)
        {
            int i = IndexOf(id);
            return i >= 0 ? All[i] : default;
        }

        public static string LabelOf(string id)
        {
            int i = IndexOf(id);
            return i >= 0 ? All[i].label : id;
        }

        /// <summary>The scene behind a tutorial, or null when it is an overlay.</summary>
        public static string SceneOf(string id)
        {
            int i = IndexOf(id);
            return i >= 0 ? All[i].scene : null;
        }

        public static bool IsOverlay(string id) => string.IsNullOrEmpty(SceneOf(id));

        /// <summary>Every id, in catalog order — the "play all in sequence" run.</summary>
        public static List<string> AllIds()
        {
            var list = new List<string>(All.Length);
            foreach (var r in All) list.Add(r.id);
            return list;
        }

        public static List<Row> InCategory(Category c)
        {
            var list = new List<Row>();
            foreach (var r in All) if (r.category == c) list.Add(r);
            return list;
        }

        public static string CategoryLabel(Category c) => c switch
        {
            Category.Basics => "Getting started",
            Category.Simulation => "Simulation",
            Category.Modes => "Game modes",
            Category.Garage => "Garage",
            Category.Online => "Online",
            _ => c.ToString(),
        };

        /// <summary>
        /// The simulation queue the intake questions build. Always starts at
        /// sim_controllers — the other three assume it — then adds only what was
        /// asked for, in the order the topics build on each other.
        /// </summary>
        public static List<string> SimQueue(bool ipc, bool firmware, bool sensors)
        {
            var list = new List<string> { "sim_controllers" };
            if (sensors) list.Add("sim_sensors");
            if (firmware) list.Add("sim_firmware");
            if (ipc) list.Add("sim_ipc");
            return list;
        }
    }
}
