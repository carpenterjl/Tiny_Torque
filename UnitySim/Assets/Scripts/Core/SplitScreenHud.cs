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
            bool arcade = rig.arcade != null;
            var area = new Rect(px.x + 10f, Screen.height - px.yMax + 10f, 230f, arcade ? 118f : 96f);

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

            // Arcade: the held item goes in the player's own box rather than a
            // second overlay — the viewport translation is already right here.
            if (arcade)
            {
                var a = rig.arcade;
                string item = a.rolling
                    ? Arcade.ArcadeConfig.DisplayName(a.rollFace) + " …"
                    : (a.HasItem
                        ? Arcade.ArcadeConfig.DisplayName(a.held) + (a.charges > 1 ? $" x{a.charges}" : "")
                        : "— no item —");
                if (a.penalized) item += "   SLOWED";
                GUILayout.Label($"P{a.livePosition}   {item}", GarageSkin.Header);
            }
            GUILayout.EndArea();

            // Hit banner, impact flash and incoming-missile warning, drawn inside
            // THIS player's viewport so neither half bleeds onto the other. Same
            // renderer the solo HUD uses, so the two can never drift apart.
            if (arcade)
                Arcade.ArcadeFeedback.Draw(
                    new Rect(px.x, Screen.height - px.yMax, px.width, px.height),
                    rig.arcade, Arcade.ArcadeDirector.Instance);
        }

        private static string Fmt(float t) =>
            t <= 0f ? "--:--" : $"{(int)(t / 60f):00}:{t % 60f:00.0}";
    }
}
