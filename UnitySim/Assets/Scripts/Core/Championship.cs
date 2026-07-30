using System.Collections.Generic;
using AIHWSim.Persistence;

namespace AIHWSim.Core
{
    /// <summary>
    /// The championship state machine: begin a series, race its rounds in
    /// order, bank points, and pay out at the end.
    ///
    /// It lives in <c>progress.json</c> rather than in memory so a series
    /// survives quitting the game — four races is more than one sitting for
    /// most people, and losing the standings on exit would make the mode a
    /// chore rather than a reason to come back.
    ///
    /// The roster is pinned when the series begins. Bot names and difficulty
    /// are part of that: a points table only means something if the same
    /// drivers contested every round, and letting the player re-pick opponents
    /// between rounds would turn the last race into a formality.
    ///
    /// This class reads and writes <see cref="Progression"/>, so it lives on
    /// the UI side of the progression gating rule — MenuUI and RaceDirector
    /// call it, nothing below the menu does.
    /// </summary>
    public static class Championship
    {
        public static ChampionshipState State => Progression.Current.championship;

        public static bool Active => State != null && State.active;

        public static SeriesDef Series =>
            State == null ? null : ChampionshipCatalog.ById(State.seriesId);

        /// <summary>Rounds in the current series (0 when none is running).</summary>
        public static int Rounds => Series?.tracks.Length ?? 0;

        /// <summary>Every round has been raced; the standings are final.</summary>
        public static bool Complete => Active && State.round >= Rounds;

        /// <summary>1-based round number for display.</summary>
        public static int RoundNumber => Active ? UnityEngine.Mathf.Min(State.round + 1, Rounds) : 0;

        /// <summary>The map the next round is run on, as a menu display name.</summary>
        public static string NextTrack()
        {
            var s = Series;
            if (s == null || State.round < 0 || State.round >= s.tracks.Length) return "";
            return s.tracks[State.round];
        }

        /// <summary>
        /// Start a series. Wipes any series already in progress — the menu asks
        /// first. The roster is (player, then bots) in grid order, matching how
        /// SessionConfig.Players is built.
        /// </summary>
        public static void Begin(string seriesId, string playerName, IList<string> botNames,
            int botDifficulty)
        {
            var s = ChampionshipCatalog.ById(seriesId);
            if (s == null) return;

            var st = State;
            st.active = true;
            st.paid = false;
            st.seriesId = seriesId;
            st.round = 0;
            st.botDifficulty = botDifficulty;
            st.driverNames.Clear();
            st.driverPoints.Clear();
            st.driverIsBot.Clear();

            st.driverNames.Add(playerName);
            st.driverPoints.Add(0);
            st.driverIsBot.Add(false);
            if (botNames != null)
                foreach (var b in botNames)
                {
                    st.driverNames.Add(b);
                    st.driverPoints.Add(0);
                    st.driverIsBot.Add(true);
                }
            Progression.Save();
        }

        /// <summary>Bin the series in progress.</summary>
        public static void Abandon()
        {
            var st = State;
            st.active = false;
            st.paid = false;
            st.seriesId = "";
            st.round = 0;
            st.driverNames.Clear();
            st.driverPoints.Clear();
            st.driverIsBot.Clear();
            Progression.Save();
        }

        /// <summary>
        /// Bank one round's finishing order and advance. Names are matched
        /// against the pinned roster; anything unrecognised is ignored rather
        /// than added, so a mid-series roster change cannot corrupt the table.
        /// Awards the championship crates when the last round lands and the
        /// player is on top — once, guarded by <c>paid</c>.
        /// </summary>
        public static void RecordRound(IList<(string name, int place)> rows)
        {
            if (!Active || Complete || rows == null) return;
            var st = State;

            foreach (var (name, place) in rows)
            {
                int i = st.driverNames.IndexOf(name);
                if (i < 0 || i >= st.driverPoints.Count) continue;
                st.driverPoints[i] += ChampionshipCatalog.PointsFor(place);
            }
            st.round++;

            if (st.round >= Rounds && !st.paid)
            {
                st.paid = true;
                if (PlayerWon())
                    Progression.OnChampionshipWon(Series?.theme == Garage.CosmeticTheme.Haunted);
            }
            Progression.Save();
        }

        /// <summary>
        /// Point <see cref="GameFlow.ActiveTrack"/> at the next round's map.
        /// Shared by the menu's "Race round N" and the results screen's "Next
        /// round" so the two can never disagree about which circuit is next.
        /// </summary>
        public static void LoadNextRoundTrack()
        {
            string name = NextTrack();
            if (string.IsNullOrEmpty(name)) { GameFlow.ActiveTrack = null; return; }

            // A round may name a hand-authored scene track. Checked first: it is
            // not TrackDesign data, so the resolvers below would both miss it and
            // quietly run the round on the classic oval.
            string scene = Track.SceneTrackCatalog.Resolve(name);
            if (scene != null) { GameFlow.ActiveSceneTrack = scene; return; }

            GameFlow.ActiveTrack =
                TrackEd.TrackPresets.Resolve(name) ?? TrackEd.TrackLibrary.Load(name);
        }

        /// <summary>Is the human alone on top? A tie on points is not a win —
        /// the Gold Vault is for winning it outright.</summary>
        public static bool PlayerWon()
        {
            var st = State;
            if (st.driverPoints.Count == 0) return false;
            int mine = st.driverPoints[0];
            for (int i = 1; i < st.driverPoints.Count; i++)
                if (st.driverPoints[i] >= mine) return false;
            return true;
        }

        /// <summary>The table, best first. Ties keep roster order, which puts
        /// the player above a bot they are level with — the only place this
        /// code is generous, and only for display.</summary>
        public static List<(string name, int points, bool isBot)> Standings()
        {
            var st = State;
            var rows = new List<(string, int, bool)>(st.driverNames.Count);
            for (int i = 0; i < st.driverNames.Count; i++)
                rows.Add((st.driverNames[i],
                          i < st.driverPoints.Count ? st.driverPoints[i] : 0,
                          i < st.driverIsBot.Count && st.driverIsBot[i]));
            rows.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            return rows;
        }
    }
}
