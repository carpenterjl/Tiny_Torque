using System.Text;
using AIHWSim.Audio;
using AIHWSim.Garage;
using AIHWSim.Persistence;
using UnityEngine;

namespace AIHWSim.UI
{
    /// <summary>
    /// The end-of-race payout notice on the results screens, shared by
    /// RaceDirector's local overlay and LanSessionMenu's LAN one.
    ///
    /// Crates are not opened here. The results screen is a place a player wants
    /// to leave — half the time somebody is already asking for a rematch — and
    /// a three-pull reveal is a thing you want to sit down for. So this says
    /// what was earned and where it is waiting, and the Showroom's crate room
    /// does the theatre.
    ///
    /// Pass-safe by construction: exactly two labels are drawn in every phase
    /// (only their TEXT changes, which never touches the control count), and
    /// the timing clock is Time.unscaledTime, identical across one frame's
    /// Layout and Repaint.
    /// </summary>
    public static class AwardReveal
    {
        private const float StampSeconds = 0.5f;

        private static float _start = -1f;
        private static bool _played;

        /// <summary>True while there is an award to show — callers can size
        /// their panel for the extra rows.</summary>
        public static bool Pending => Progression.LastAward != null;

        /// <summary>Draw inside the caller's layout. No-op without an award.</summary>
        public static void Draw()
        {
            var award = Progression.LastAward;
            if (award == null) { _start = -1f; return; }
            if (_start < 0f) { _start = Time.unscaledTime; _played = false; }

            GUILayout.Space(6);
            var title = new GUIStyle(GarageSkin.Header) { alignment = TextAnchor.MiddleCenter };
            var big = new GUIStyle(GarageSkin.Title) { fontSize = 20 };

            bool landed = Time.unscaledTime - _start >= StampSeconds;
            if (landed && !_played)
            {
                _played = true;
                SfxPlayer.Ensure()?.PlayUi(award.crates.Count > 1
                    ? ProceduralAudio.UiUnlock
                    : ProceduralAudio.UiLevelUp);
            }

            if (award.crates.Count > 0)
            {
                GUILayout.Label(landed ? "CRATES EARNED" : "COUNTING UP…", title);
                GUILayout.Label(landed
                    ? $"{CrateList(award)}\nOpen them from the main menu"
                    : $"+{award.xpGained} XP", big);
            }
            else
            {
                GUILayout.Label($"+{award.xpGained} XP", title);
                GUILayout.Label(award.leveledUp
                    ? $"LEVEL {award.newLevel}!"
                    : $"Level {award.newLevel} · {Progression.Current.xp}/{100 * award.newLevel} XP", big);
            }
        }

        private static string CrateList(AwardResult award)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < award.crates.Count; i++)
            {
                var def = CosmeticCatalog.CrateById(award.crates[i]);
                if (def == null) continue;
                if (sb.Length > 0) sb.Append("  +  ");
                sb.Append(def.label);
            }
            return sb.Length > 0 ? sb.ToString() : "Nothing this time";
        }

        /// <summary>Consume the award — called when the results screen closes
        /// (any exit), so the next race starts clean.</summary>
        public static void Dismiss()
        {
            _start = -1f;
            Progression.ConsumeAward();
        }
    }
}
