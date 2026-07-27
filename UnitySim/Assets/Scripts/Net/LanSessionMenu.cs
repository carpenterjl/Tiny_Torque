using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.TrackEd;
using UnityEngine;

namespace AIHWSim.Net
{
    /// <summary>
    /// Esc menu for LAN sessions (replaces PauseMenu, which is never created in
    /// LAN scenes — a network session cannot pause, so Time.timeScale is never
    /// touched). Both roles: player list + Leave. Host: laps stepper + Start
    /// Race, Change Map (track library), Kick. Also draws the race countdown
    /// and the results overlay.
    /// </summary>
    public sealed class LanSessionMenu : MonoBehaviour
    {
        /// <summary>This machine's rigs, set by TrackBootstrap. Only used to give
        /// <see cref="SettingsPanel"/> something to apply a moved assist slider
        /// to — which in LAN is exactly the right list, because since protocol 4
        /// each machine simulates its own car and a local assist change therefore
        /// needs no wire message at all.</summary>
        public System.Collections.Generic.List<PlayerRig> rigs;

        private bool _open;
        private bool _showMaps;
        private bool _showSettings;
        private int _laps = 3;
        private Vector2 _mapScroll;
        private Vector2 _bodyScroll;
        private bool _resultsDismissed;

        private NetSession S => NetSession.Instance;

        private void Update()
        {
            // Escape belongs to the rebind capture while one is open — see
            // PauseMenu.Update for the same guard and the same reason.
            if (!SettingsPanel.Capturing && InputReader.PausePressed())
            {
                _open = !_open;
                if (!_open) SettingsPanel.Reset();
            }
            if (S != null && S.State == NetSession.LanState.Racing) _resultsDismissed = false;
        }

        private void OnGUI()
        {
            if (S == null) return;
            GUI.skin = GarageSkin.Skin;

            DrawCountdown();
            if (S.State == NetSession.LanState.Results && !_resultsDismissed)
            {
                DrawResults();
                return;
            }
            if (!_open) return;

            float w = _showMaps ? 480f : (_showSettings ? 460f : 300f);
            float h = Mathf.Min(Screen.height - 60f, _showSettings ? 600f : 380f);
            var area = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical(GUILayout.Width(_showSettings ? 440f : 280f));
            var title = new GUIStyle(GarageSkin.Header) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
            GUILayout.Label(S.IsHost ? "LAN SESSION (HOST)" : "LAN SESSION", title);
            GUILayout.Space(6);
            _bodyScroll = GUILayout.BeginScrollView(_bodyScroll);

            foreach (var p in S.Roster)
            {
                GUILayout.BeginHorizontal();
                var st = S.Standings[p.slot];
                GUILayout.Label($"{p.name}{(p.slot == S.LocalSlot ? " (you)" : "")}  ·  lap {st.lap}");
                GUILayout.FlexibleSpace();
                if (S.IsHost && p.slot != 0 && GUILayout.Button("Kick", GUILayout.Width(48)))
                    S.HostKick(p.slot);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(8);

            if (S.IsHost)
            {
                bool canRace = S.State == NetSession.LanState.FreeRoam && HostHasFinishLine();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Laps", GUILayout.Width(40));
                if (GUILayout.Button("−", GUILayout.Width(28))) _laps = Mathf.Max(1, _laps - 1);
                GUILayout.Label(_laps.ToString(), GarageSkin.Header, GUILayout.Width(30));
                if (GUILayout.Button("+", GUILayout.Width(28))) _laps = Mathf.Min(50, _laps + 1);
                GUILayout.EndHorizontal();

                GUI.enabled = canRace;
                if (GUILayout.Button("Start Race ▶", GUILayout.Height(30)))
                {
                    S.HostStartRace(_laps);
                    _open = false;
                }
                GUI.enabled = S.State == NetSession.LanState.FreeRoam;
                if (GUILayout.Button(_showMaps ? "Change Map ◀" : "Change Map ▶", GUILayout.Height(30)))
                    _showMaps = !_showMaps;
                GUI.enabled = true;
                if (!HostHasFinishLine())
                    GUILayout.Label("(map has no finish line — free roam only)", GarageSkin.StatLabel);
            }

            // The same panel the pause menu shows. A LAN scene never creates a
            // PauseMenu, so without this a networked player could not reach a
            // single setting without leaving the session.
            GUILayout.Space(6);
            if (GUILayout.Button(_showSettings ? "Hide settings" : "Settings…", GUILayout.Height(28)))
            {
                _showSettings = !_showSettings;
                SettingsPanel.Reset();
            }
            if (_showSettings) SettingsPanel.Draw(rigs, 340f);

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Leave Session", GUILayout.Height(30)))
                S.Leave();
            if (GUILayout.Button("Close (Esc)", GUILayout.Height(26)))
            {
                _open = false;
                SettingsPanel.Reset();
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            if (_showMaps && S.IsHost) DrawMapList();

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private bool HostHasFinishLine()
        {
            if (!S.IsHost) return true;
            // Oval always has one; custom maps need a finish item.
            return GameFlow.ActiveTrack == null ||
                   GameFlow.ActiveTrack.FindByBehavior(ItemBehavior.Finish) != null;
        }

        private void DrawMapList()
        {
            GUILayout.BeginVertical();
            GUILayout.Label("MAPS", GarageSkin.Header);
            _mapScroll = GUILayout.BeginScrollView(_mapScroll);
            if (GUILayout.Button("Classic Oval"))
                ChangeMap(null);
            foreach (var name in TrackLibrary.List())
                if (GUILayout.Button(name))
                    ChangeMap(TrackLibrary.Load(name));
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void ChangeMap(TrackDesign d)
        {
            _showMaps = false;
            _open = false;
            S.HostChangeMap(d);
        }

        private void DrawCountdown()
        {
            if (S.State != NetSession.LanState.Countdown) return;
            float remaining = S.CountdownEndTime - Time.unscaledTime;
            string text = remaining > 0.2f ? Mathf.CeilToInt(remaining).ToString() : "GO!";
            var style = new GUIStyle(GarageSkin.Header)
            {
                fontSize = 72,
                alignment = TextAnchor.MiddleCenter,
            };
            GUI.Label(new Rect(0, Screen.height * 0.28f, Screen.width, 100f), text, style);
        }

        private void DrawResults()
        {
            float w = 380f, h = 200f + S.Roster.Count * 24f;
            var area = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUILayout.BeginArea(area, GUI.skin.box);
            var title = new GUIStyle(GarageSkin.Header) { fontSize = 20, alignment = TextAnchor.MiddleCenter };
            GUILayout.Label("RACE RESULTS", title);
            GUILayout.Space(6);

            foreach (var p in S.Roster)
            {
                var st = S.Standings[p.slot];
                string place = st.finished ? $"P{st.place}" : "DNF";
                string best = st.bestLap >= 0f ? Fmt(st.bestLap) : "--:--";
                GUILayout.Label($"{place}  {p.name}   total {Fmt(st.totalTime)}   best {best}");
            }

            GUILayout.Space(8);
            if (S.IsHost)
            {
                if (GUILayout.Button("Rematch", GUILayout.Height(30)))
                    S.HostStartRace(_laps);
                if (GUILayout.Button("Back to free roam", GUILayout.Height(30)))
                    S.HostEndResults();
            }
            else
            {
                GUILayout.Label("Waiting for the host…", GarageSkin.StatLabel);
                if (GUILayout.Button("Keep driving", GUILayout.Height(28)))
                    _resultsDismissed = true;
            }
            GUILayout.EndArea();
        }

        private static string Fmt(float t) =>
            t <= 0f ? "--:--" : $"{(int)(t / 60f):00}:{t % 60f:00.0}";
    }
}
