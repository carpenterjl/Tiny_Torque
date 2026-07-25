using System.Collections.Generic;
using AIHWSim.Garage;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Per-viewport HUD for split-screen: each player's name, speed, lap /
    /// checkpoint progress, and last/best lap drawn inside their own camera
    /// viewport. Replaces the full-screen LapTimer box, SensorHud, GraphOverlay,
    /// and runner mode box, which are disabled in split-screen sessions.
    /// </summary>
    public sealed class SplitScreenHud : MonoBehaviour
    {
        public List<PlayerRig> rigs = new List<PlayerRig>();

        private void OnGUI()
        {
            GUI.skin = GarageSkin.Skin;
            foreach (var rig in rigs)
            {
                if (rig?.car == null || rig.camera == null) continue;
                DrawPlayerBox(rig);
            }
        }

        private void DrawPlayerBox(PlayerRig rig)
        {
            // Camera pixel rect is bottom-left origin; IMGUI is top-left.
            Rect px = rig.camera.pixelRect;
            var area = new Rect(px.x + 10f, Screen.height - px.yMax + 10f, 230f, 96f);

            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label(rig.slot.name, GarageSkin.Header);

            float speed = rig.car.ForwardSpeed;
            var tracker = rig.lapTimer != null ? rig.lapTimer.GetTracker(rig.car) : null;

            string line = $"{speed,5:0.0} m/s";
            if (tracker != null)
            {
                line += $"   Lap {tracker.LapCount}" +
                        (SessionConfig.TargetLaps > 0 ? $"/{SessionConfig.TargetLaps}" : "");
                if (rig.lapTimer.CheckpointCount > 0)
                    line += $"   CP {tracker.NextCheckpoint}/{rig.lapTimer.CheckpointCount}";
            }
            GUILayout.Label(line);

            if (tracker != null)
            {
                GUILayout.Label(
                    $"Now  {(tracker.Armed ? Fmt(tracker.CurrentLap) : "— cross the line —")}\n" +
                    $"Last {Fmt(tracker.LastLap)}   Best {(tracker.HasBest ? Fmt(tracker.BestLap) : "--:--")}",
                    GarageSkin.StatLabel);
            }
            GUILayout.EndArea();
        }

        private static string Fmt(float t) =>
            t <= 0f ? "--:--" : $"{(int)(t / 60f):00}:{t % 60f:00.0}";
    }
}
