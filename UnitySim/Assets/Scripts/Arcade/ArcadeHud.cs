using AIHWSim.Core;
using AIHWSim.Garage;
using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// The arcade overlay for local sessions: a live position board top-right and,
    /// solo, the held-item panel bottom-centre.
    ///
    /// In split-screen the item panel moves into SplitScreenHud's per-viewport box
    /// (which already does the viewport rect translation) and only the shared board
    /// is drawn here. In LAN this component is not created at all — LanHud owns the
    /// equivalent, reading NetSession.Standings.
    /// </summary>
    public sealed class ArcadeHud : MonoBehaviour
    {
        public ArcadeDirector director;
        public PlayerRig localRig;
        public bool splitScreen;

        /// <summary>False in LAN: LanHud draws the shared board from the network
        /// standings (which include remote players this director only mirrors),
        /// and two boards on one screen is one too many.</summary>
        public bool showBoard = true;

        private static readonly System.Collections.Generic.List<ArcadeRacer> _order =
            new System.Collections.Generic.List<ArcadeRacer>();

        // Built once rather than per OnGUI — IMGUI style allocation every frame
        // is pure GC churn.
        private static GUIStyle _itemStyle;
        private static GUIStyle _limitStyle;

        private void OnGUI()
        {
            if (director == null) return;
            GUI.skin = GarageSkin.Skin;

            if (showBoard) DrawBoard();
            if (!splitScreen)
            {
                DrawItemPanel();
                // Solo owns the whole screen. In split-screen the same overlay is
                // drawn per viewport by SplitScreenHud, which already has the
                // rects.
                ArcadeFeedback.Draw(new Rect(0f, 0f, Screen.width, Screen.height),
                    localRig != null ? localRig.arcade : null, director);
            }
        }

        private void DrawBoard()
        {
            var racers = director.Racers;
            if (racers.Count == 0) return;

            _order.Clear();
            for (int i = 0; i < racers.Count; i++) _order.Add(racers[i]);
            _order.Sort((a, b) => a.livePosition.CompareTo(b.livePosition));

            float w = 210f, h = 26f + _order.Count * 18f;
            GUILayout.BeginArea(new Rect(Screen.width - w - 12f, 12f, w, h), GUI.skin.box);
            GUILayout.Label("POSITIONS", GarageSkin.Header);
            foreach (var r in _order)
            {
                string name = r.rig != null && r.rig.slot != null ? r.rig.slot.name : "?";
                string item = r.rolling ? "…" : (r.HasItem ? ArcadeConfig.DisplayName(r.held) : "");
                if (r.charges > 1) item += $" ×{r.charges}";
                GUILayout.Label($"{r.livePosition}.  {name}   {item}", GarageSkin.StatLabel);
            }
            GUILayout.EndArea();
        }

        private void DrawItemPanel()
        {
            var me = localRig != null ? localRig.arcade : null;
            if (me == null) return;

            const float w = 200f, h = 62f;
            var area = new Rect((Screen.width - w) * 0.5f, Screen.height - h - 16f, w, h);
            GUILayout.BeginArea(area, GUI.skin.box);

            string label;
            if (me.rolling) label = ArcadeConfig.DisplayName(me.rollFace) + " …";
            else if (me.HasItem) label = ArcadeConfig.DisplayName(me.held) +
                                         (me.charges > 1 ? $"  ×{me.charges}" : "");
            else label = "— no item —";

            _itemStyle ??= new GUIStyle(GarageSkin.Header)
                { alignment = TextAnchor.MiddleCenter, fontSize = 18 };
            GUILayout.Label(label, _itemStyle);

            string hint = me.HasItem ? "Shift / X  to use" : "drive through an item box";
            if (ArcadeDirector.Clock < me.shieldUntil) hint = "SHIELD UP · " + hint;
            GUILayout.Label(hint, GarageSkin.StatLabel);
            GUILayout.EndArea();

            DrawTrackLimitWarning(me);
        }

        private void DrawTrackLimitWarning(ArcadeRacer me)
        {
            if (!director.trackLimits) return;

            string msg = null;
            if (me.penalized) msg = "TRACK LIMITS — SLOWED";
            else if (me.warned) msg = "RETURN TO TRACK";
            if (msg == null) return;

            _limitStyle ??= new GUIStyle(GarageSkin.Header)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 22,
            };
            _limitStyle.normal.textColor =
                me.penalized ? new Color(1f, 0.35f, 0.25f) : GarageSkin.Accent;
            GUI.Label(new Rect(0f, Screen.height * 0.62f, Screen.width, 40f), msg, _limitStyle);
        }
    }
}
