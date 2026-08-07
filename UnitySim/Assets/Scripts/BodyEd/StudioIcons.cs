using System.Collections.Generic;
using AIHWSim.Garage;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// Palette thumbnails, photographed from the real geometry.
    ///
    /// <b>Rendered lazily, a few per frame, and never from OnGUI.</b> The palette
    /// has a couple of hundred entries once every shell has been harvested, and
    /// each icon costs a camera render; doing them all at startup is a visible
    /// stall, and doing them inside a Repaint is a camera render nested in the
    /// middle of the UI's own draw. So a draw that wants an icon it has not got
    /// REQUESTS one and draws a placeholder this frame, and <see cref="Pump"/> —
    /// called from the editor's Update — renders a handful.
    ///
    /// Uses <c>PartIconFactory.Snapshot</c>, which is the same photographer the
    /// garage and the track builder use, so a studio thumbnail cannot drift from
    /// what the part actually looks like.
    /// </summary>
    public static class StudioIcons
    {
        /// <summary>Icons rendered per Pump. Four is about a millisecond and keeps
        /// a scrolled palette filling in visibly rather than all at once at some
        /// later moment.</summary>
        private const int PerFrame = 4;

        private const int Size = 64;

        private static readonly Dictionary<string, Texture2D> _cache =
            new Dictionary<string, Texture2D>();
        private static readonly List<string> _queue = new List<string>();
        private static readonly HashSet<string> _queued = new HashSet<string>();

        /// <summary>
        /// The icon for a part source, or null if it is not ready yet — in which
        /// case one has been queued. Callers draw the label alone until it arrives.
        /// </summary>
        public static Texture2D Get(string source)
        {
            if (string.IsNullOrEmpty(source)) return null;
            if (_cache.TryGetValue(source, out Texture2D t)) return t;   // may be null: "tried, nothing"
            if (_queued.Add(source)) _queue.Add(source);
            return null;
        }

        /// <summary>Render a few queued icons. Call once a frame from Update.</summary>
        public static void Pump()
        {
            if (!Application.isPlaying) return;   // Snapshot destroys with Object.Destroy
            for (int n = 0; n < PerFrame && _queue.Count > 0; n++)
            {
                string source = _queue[0];
                _queue.RemoveAt(0);
                _queued.Remove(source);
                // Cached even when it comes back empty, so a part with no geometry
                // in this build is attempted once rather than every frame forever.
                _cache[source] = PartIconFactory.Snapshot(
                    p => StudioPartLibrary.Build(p, source), Size);
            }
        }

        public static void ResetCache()
        {
            foreach (var kv in _cache) if (kv.Value != null) Object.Destroy(kv.Value);
            _cache.Clear();
            _queue.Clear();
            _queued.Clear();
        }
    }
}
