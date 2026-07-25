using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Net
{
    /// <summary>
    /// Single-viewport LAN HUD, identical on host and clients: own speed and
    /// lap/CP/last/best from the shared standings model, plus a race banner
    /// with live positions while racing. Speed comes from the host rig's car
    /// (host) or the own ghost's stream estimate (client).
    /// </summary>
    public sealed class LanHud : MonoBehaviour
    {
        public CarVehicle ownCar;          // host: own simulated car
        public ClientCarView ownView;      // client: own ghost

        private NetSession S => NetSession.Instance;

        private void OnGUI()
        {
            if (S == null) return;
            GUI.skin = GarageSkin.Skin;
            DrawOwnBox();
            if (S.State == NetSession.LanState.Racing || S.State == NetSession.LanState.Countdown)
                DrawRaceBanner();
        }

        private void DrawOwnBox()
        {
            var area = new Rect(10f, Screen.height - 106f, 240f, 96f);
            GUILayout.BeginArea(area, GUI.skin.box);

            var me = S.Roster.Find(p => p.slot == S.LocalSlot);
            GUILayout.Label(me != null ? me.name : "—", GarageSkin.Header);

            float speed = ownCar != null ? Mathf.Abs(ownCar.ForwardSpeed)
                : ownView != null ? ownView.SpeedEstimate : 0f;
            var st = S.LocalSlot >= 0 ? S.Standings[S.LocalSlot] : null;

            string line = $"{speed,5:0.0} m/s";
            if (st != null)
            {
                line += $"   Lap {st.lap}" + (S.TargetLaps > 0 ? $"/{S.TargetLaps}" : "");
                if (st.cpTotal > 0) line += $"   CP {st.cp}/{st.cpTotal}";
            }
            GUILayout.Label(line);
            if (st != null)
                GUILayout.Label(
                    $"Last {Fmt(st.lastLap)}   Best {(st.bestLap >= 0f ? Fmt(st.bestLap) : "--:--")}",
                    GarageSkin.StatLabel);
            GUILayout.EndArea();
        }

        private void DrawRaceBanner()
        {
            float h = 26f + S.Roster.Count * 18f;
            var area = new Rect((Screen.width - 250f) * 0.5f, 8f, 250f, h);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label($"RACE — first to {S.TargetLaps} laps", GarageSkin.Header);
            foreach (var p in S.Roster)
            {
                var st = S.Standings[p.slot];
                string state = st.finished ? $"FINISHED P{st.place}" : $"lap {st.lap}/{S.TargetLaps}";
                GUILayout.Label($"{p.name}: {state}", GarageSkin.StatLabel);
            }
            GUILayout.EndArea();
        }

        private static string Fmt(float t) =>
            t <= 0f ? "--:--" : $"{(int)(t / 60f):00}:{t % 60f:00.0}";
    }
}
