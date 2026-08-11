using System.Collections.Generic;
using AIHWSim.Garage;
using AIHWSim.Persistence;
using AIHWSim.Tutorial;

namespace AIHWSim.Core
{
    /// <summary>
    /// The tutorial state machine: begin one (or a queue of them), remember how
    /// far in the player got, bank the payout, and hand back the next.
    ///
    /// It lives in <c>progress.json</c> rather than in memory for the same reason
    /// <see cref="Championship"/> does — a sequence of twelve lessons is more
    /// than one sitting, and a player who quits half way through should be
    /// offered their place back rather than the list they already chose from.
    ///
    /// This class reads and writes <see cref="Progression"/>, so it sits on the
    /// UI side of the progression gating rule: MenuUI, PauseMenu and the tutorial
    /// director call it, nothing below the menu does.
    ///
    /// It also owns SESSION SETUP for a driving tutorial (<see cref="Launch"/>),
    /// which is the only reason it is in Core rather than beside the catalogue.
    /// That is deliberate and matches <c>Championship.LoadNextRoundTrack</c>: the
    /// menu's "Start", the pause menu's skip-to-next and the results screen's
    /// "Next tutorial" must agree about what running a tutorial means, and three
    /// copies of a SessionConfig block is how they would stop agreeing.
    /// </summary>
    public static class Tutorials
    {
        public static TutorialState State => Progression.Current.tutorial;

        public static bool Active => State != null && State.active;

        /// <summary>The tutorial being run, or "" when none is.</summary>
        public static string CurrentId => Active ? State.id : "";

        /// <summary>0-based step the player is up to — the resume point.</summary>
        public static int StepIndex => Active ? State.stepIndex : 0;

        public static bool SequenceMode => Active && State.sequenceMode;

        public static bool IsDone(string id) =>
            !string.IsNullOrEmpty(id) && Progression.Current.tutorialsDone.Contains(id);

        public static int DoneCount
        {
            get
            {
                int n = 0;
                foreach (var r in TutorialCatalog.All) if (IsDone(r.id)) n++;
                return n;
            }
        }

        public static bool AllDone => DoneCount >= TutorialCatalog.All.Length;

        /// <summary>Is another tutorial queued behind this one?</summary>
        public static bool HasNext => Active && State.queue.Count > 0;

        /// <summary>
        /// Nudge a brand-new player toward the tutorial: nothing completed,
        /// nothing in progress, and no race ever finished. All three, because a
        /// player who has been racing for a week and simply never opened the
        /// tutorial does not need the menu shouting at them about it.
        /// </summary>
        public static bool ShouldNudge
        {
            get
            {
                var p = Progression.Current;
                return p.tutorialsDone.Count == 0 && p.racesFinished == 0 && !Active;
            }
        }

        /// <summary>
        /// Set by the results screen's "Tutorial menu" button and consumed by
        /// MenuUI on the way in, so leaving a tutorial lands back on the list
        /// rather than the root. A static rather than a saved field: it describes
        /// this trip back to the menu, not the player's progress.
        /// </summary>
        public static bool PendingOpenHub { get; set; }

        // ---- lifecycle --------------------------------------------------------

        /// <summary>
        /// Start a run. The first id becomes the current tutorial and the rest
        /// queue behind it. Wipes anything already in progress — the hub asks
        /// first.
        /// </summary>
        public static void Begin(IList<string> ids, bool sequence)
        {
            if (ids == null || ids.Count == 0) return;
            var st = State;
            st.active = true;
            st.id = ids[0];
            st.stepIndex = 0;
            st.sequenceMode = sequence;
            st.queue.Clear();
            for (int i = 1; i < ids.Count; i++) st.queue.Add(ids[i]);
            Progression.Save();
        }

        public static void Begin(string id) => Begin(new[] { id }, false);

        /// <summary>Everything, in catalogue order — the "play all" button.</summary>
        public static void BeginAll() => Begin(TutorialCatalog.AllIds(), true);

        /// <summary>Bin the run in progress, with no credit for it.</summary>
        public static void Abandon()
        {
            var st = State;
            st.active = false;
            st.id = "";
            st.stepIndex = 0;
            st.sequenceMode = false;
            st.queue.Clear();
            Progression.Save();
        }

        /// <summary>
        /// Remember the step the player is up to. Called on every step
        /// completion, so quitting anywhere loses at most the step in hand.
        /// </summary>
        public static void SaveStep(int index)
        {
            if (!Active) return;
            State.stepIndex = index;
            Progression.Save();
        }

        /// <summary>
        /// The current tutorial is finished. Pays scrap the FIRST time only,
        /// records the completion, and pays the finish-them-all crate once — then
        /// advances to whatever is queued behind it.
        ///
        /// Returns what was paid, for the results screen to show.
        ///
        /// Call this from a bank-to-disk point, never from OnGUI: IMGUI runs a
        /// Layout pass and a Repaint pass over the same frame, and this pays.
        /// </summary>
        public static Payout CompleteCurrent()
        {
            var paid = new Payout();
            if (!Active) return paid;

            var p = Progression.Current;
            string id = State.id;
            var row = TutorialCatalog.ById(id);

            paid.firstTime = !string.IsNullOrEmpty(id) && !p.tutorialsDone.Contains(id);
            if (paid.firstTime)
            {
                p.tutorialsDone.Add(id);
                paid.scrap = row.scrap;
                Progression.AddScrap(row.scrap);
            }

            // The crate for finishing every one of them. Guarded by its own flag
            // rather than by AllDone alone, so replaying the last tutorial does
            // not pay a second vault — the ChampionshipState.paid idiom.
            if (AllDone && !p.tutorialAllPaid)
            {
                p.tutorialAllPaid = true;
                paid.crate = true;
                Progression.OnTutorialsComplete();   // saves
            }

            Advance();
            return paid;
        }

        /// <summary>
        /// The player skipped out through the pause menu. No scrap, and NOT
        /// marked done — the hub's ✓ marks have to stay honest, and a lesson
        /// somebody bailed on is one they may well want later. It does advance
        /// the queue, so skipping in a sequence run moves on rather than dumping
        /// the player out of the whole thing.
        /// </summary>
        public static void SkipCurrent() => Advance();

        /// <summary>Take the next queued tutorial, or end the run.</summary>
        private static void Advance()
        {
            var st = State;
            if (st.queue.Count > 0)
            {
                st.id = st.queue[0];
                st.queue.RemoveAt(0);
                st.stepIndex = 0;
                Progression.Save();
                return;
            }
            st.active = false;
            st.id = "";
            st.stepIndex = 0;
            st.sequenceMode = false;
            Progression.Save();
        }

        /// <summary>What finishing one paid — the results screen reads this.</summary>
        public sealed class Payout
        {
            public bool firstTime;
            public int scrap;
            public bool crate;
        }

        // ---- launching --------------------------------------------------------

        /// <summary>
        /// Compose the session for a driving tutorial and point
        /// <see cref="GameFlow.ActiveSceneTrack"/> at its map. Returns false for
        /// an overlay tutorial (nothing to load) or an id this build does not
        /// know — the caller decides what to do instead.
        ///
        /// The rules are a free drive on purpose. A tutorial scene composes NO
        /// director of its own through TrackBootstrap (MatchMode.FreeRoam is the
        /// one mode that builds none), which leaves the TutorialDirector authored
        /// in the scene as the only one alive — no countdown it did not ask for,
        /// no second results overlay behind its own.
        /// </summary>
        public static bool Launch(string id)
        {
            string scene = TutorialCatalog.SceneOf(id);
            if (string.IsNullOrEmpty(scene)) return false;

            var s = SettingsStore.Current;
            SessionConfig.SetSinglePlayer();      // clears roster, laps, arcade, countdown
            SessionConfig.Match = MatchMode.FreeRoam;
            SessionConfig.ArcadeHandling = s.spArcadeHandling;

            GameFlow.ActiveDesign = VehicleDesign.Default();
            GameFlow.ActiveSceneTrack = scene;

            string pname = string.IsNullOrWhiteSpace(s.player1Name) ? "Player" : s.player1Name;
            SessionConfig.Players.Add(new PlayerSlot
            {
                name = pname,
                profileId = pname,
                design = GameFlow.ActiveDesign,
                deviceKind = InputDeviceKind.MergedKeyboardGamepad,
                assists = SessionConfig.P1Assists(s),
                isBot = false,
                control = DriveControl.Human,
            });
            return true;
        }

        /// <summary>Compose the session for whatever is current. Sugar for the
        /// two callers that just advanced and want to run what they landed on.</summary>
        public static bool LaunchCurrent() => Active && Launch(State.id);
    }
}
