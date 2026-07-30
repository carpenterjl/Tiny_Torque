using UnityEngine;

namespace AIHWSim.TrackTools
{
    /// <summary>
    /// Shared naming, logging and menu ordering for Track Studio — the editor-side
    /// track builder for hand-authored scene tracks.
    ///
    /// A separate menu root from <c>Tools/AIHWSim/</c>, which is a flat list of
    /// one-shot actions, and deliberately NOT called "Track Builder": that name
    /// belongs to the shipped in-game tile editor (TrackBuilderScene /
    /// TrackBuilderUI), and two things with one name is a support problem.
    /// </summary>
    public static class TrackStudio
    {
        public const string Menu = "Tools/Track Studio/";

        /// <summary>Log prefix, greppable in a headless log next to [PMV], [TPV],
        /// [COS] and [PACK].</summary>
        public const string Tag = "[TRK]";

        public static void Log(string msg) => Debug.Log(Tag + " " + msg);
        public static void Warn(string msg) => Debug.LogWarning(Tag + " " + msg);
        public static void Error(string msg) => Debug.LogError(Tag + " " + msg);

        /// <summary>Where baked line and sector assets are written.</summary>
        public const string DataRoot = "Assets/TrackData";
        public const string RacingLineDir = DataRoot + "/RacingLines";
        public const string SectorDir = DataRoot + "/Sectors";

        /// <summary>
        /// Menu priorities. Unity draws a separator when consecutive priorities
        /// differ by 11 or more, which is what puts the windows, the pipeline steps
        /// and the checks into three visual blocks — the same trick
        /// Tools/TinyTorque Assets uses.
        /// </summary>
        public const int PrioWindow = 20;
        public const int PrioBrush = 21;
        public const int PrioSetup = 100;
        public const int PrioBakeRibbon = 101;
        public const int PrioRenumber = 102;
        public const int PrioBakeLine = 103;
        public const int PrioCalibrate = 104;
        public const int PrioSectors = 105;
        public const int PrioValidate = 200;
    }
}
