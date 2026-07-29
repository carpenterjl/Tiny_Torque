using UnityEngine;

namespace AIHWSim.UI
{
    /// <summary>
    /// The shared geometry of a full-screen 3D page: panels parked at the edges
    /// with the rig visible between them, and a bottom bar carrying the verbs.
    ///
    /// The Showroom, the crate room and the Garage all want the same shape, and
    /// before this they each re-typed the same literals — so a nudge to one left
    /// the others sitting a few pixels off. Everything is in UI units
    /// (<see cref="UIScale"/>), not screen pixels.
    /// </summary>
    public static class PanelLayout
    {
        /// <summary>Clear of the top edge, with room for a page note above the
        /// panels.</summary>
        public const float Top = 54f;

        /// <summary>Height of the bottom bar plus its margin, subtracted from
        /// the side panels so they never run underneath it.</summary>
        public const float BottomBand = 56f;

        public const float Margin = 10f;

        /// <summary>Height a side panel gets at the current screen size.</summary>
        public static float PanelHeight => UIScale.H - Top - BottomBand;

        /// <summary>Left panel: the page's list or palette.</summary>
        public static Rect LeftRect(float width = 250f) =>
            new Rect(Margin, Top, width, PanelHeight);

        /// <summary>Right panel: the page's detail or inspector column.</summary>
        public static Rect RightRect(float width = 270f) =>
            new Rect(UIScale.W - width - Margin, Top, width, PanelHeight);

        /// <summary>Centred bottom bar. Back lives at its left end on every page,
        /// so the same control is always in the same place.</summary>
        public static Rect BottomBarRect(float width = 520f, float height = 40f) =>
            new Rect((UIScale.W - width) * 0.5f, UIScale.H - height - 8f, width, height);

        /// <summary>A centred strip just above the bottom bar, for a one-line
        /// hint or status that belongs with the verbs rather than in a panel.</summary>
        public static Rect HintRect(float width = 460f, float height = 24f) =>
            new Rect((UIScale.W - width) * 0.5f, UIScale.H - height - 60f, width, height);

        /// <summary>A centred strip at the top, for a transient page note.</summary>
        public static Rect NoteRect(float width = 460f, float height = 24f) =>
            new Rect((UIScale.W - width) * 0.5f, Top + 4f, width, height);
    }
}
