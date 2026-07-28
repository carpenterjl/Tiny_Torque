using AIHWSim.Garage;

namespace AIHWSim.Core
{
    /// <summary>One championship: a fixed run of circuits raced back to back on
    /// one points table.</summary>
    public sealed class SeriesDef
    {
        public string id;           // save key — never rename
        public string label;
        public string blurb;
        public string[] tracks;     // menu display names ("" = Classic Oval)
        /// <summary>Set on a themed series, which is what gives the pack's
        /// seasonal crate an honest trigger.</summary>
        public CosmeticTheme? theme;
    }

    /// <summary>
    /// The three championships, built from circuits the game already ships
    /// (<see cref="TrackEd.TrackPresets"/>). Four rounds each: long enough for
    /// a points table to mean something, short enough to finish in a sitting.
    ///
    /// The Midnight Series is the haunted one, and winning it is what pays the
    /// Cursed Casket — the pack describes that box as "seasonal", which had no
    /// meaning in a game with no seasons and now means "win the dark series".
    /// </summary>
    public static class ChampionshipCatalog
    {
        /// <summary>Points by finishing place, 1st first. Places past the table
        /// score nothing, and so does a DNF.</summary>
        public static readonly int[] Points = { 10, 8, 6, 5, 4, 3, 2, 1 };

        public static readonly SeriesDef[] All =
        {
            new SeriesDef
            {
                id = "rookie",
                label = "Rookie Cup",
                blurb = "Four gentle circuits. Where everybody starts.",
                tracks = new[] { "", "★ Boost Speedway", "★ Playroom Raceway", "★ Dust Devil Rally" },
            },
            new SeriesDef
            {
                id = "trophy",
                label = "Torque Trophy",
                blurb = "The main event, across three worlds and back to the speedway.",
                tracks = new[] { "★ Downtown Dash", "★ Neon Vortex", "★ Enchanted Ascent", "★ Boost Speedway" },
            },
            new SeriesDef
            {
                id = "midnight",
                label = "Midnight Series",
                blurb = "Run it after dark. Win it for the Cursed Casket.",
                tracks = new[] { "★ Graveyard Shift", "★ Neon Vortex", "★ Downtown Dash", "★ Enchanted Ascent" },
                theme = CosmeticTheme.Haunted,
            },
        };

        public static SeriesDef ById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var s in All) if (s.id == id) return s;
            return null;
        }

        public static int PointsFor(int place) =>
            place >= 1 && place <= Points.Length ? Points[place - 1] : 0;
    }
}
