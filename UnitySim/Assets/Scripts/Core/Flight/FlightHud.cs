using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// The pilot's panel: airspeed, height, climb, angle of attack with a stall
    /// bar, load factor, attitude, throttle, and the raw stick positions.
    ///
    /// <b>The control positions earn their line.</b> "Was the stick actually
    /// centred?" is the first question of every unexplained roll, and without them
    /// on screen the answer is a guess. Same for the stall bar: an aeroplane drops
    /// a wing because one strip ran out of angle, and a margin that is already
    /// amber says so before the wing goes rather than after.
    ///
    /// IMGUI, like every other overlay here. Free-flight scene only — the scripted
    /// tests use the physics harness's HUD, which shows what the verdict depends on
    /// and nothing else.
    /// </summary>
    public sealed class FlightHud : MonoBehaviour
    {
        public PlaneVehicle plane;
        public FlightCameraRig cameras;

        private static Texture2D _white;
        private static GUIStyle _rich;

        public void Bind(PlaneVehicle p, FlightCameraRig cams)
        {
            plane = p;
            cameras = cams;
        }

        private void OnGUI()
        {
            if (plane == null) return;

            const float w = 272f, h = 226f;
            GUILayout.BeginArea(new Rect(Screen.width - w - 10f, 10f, w, h), GUI.skin.box);

            var air = plane.Air;
            var r = plane.LastAero;

            GUILayout.Label($"<b>{air.Tas:0.0} m/s</b>   ({air.Tas * 3.6f:0} km/h)", Rich());
            GUILayout.Label($"alt {plane.AltitudeAgl:0.0} m      "
                            + $"climb {plane.VerticalSpeed:+0.0;-0.0} m/s");

            // Angle of attack against how much is left before the WORST strip lets
            // go — an averaged margin stays comfortable while one wing stalls.
            float margin = Mathf.Clamp01(r.stallMargin);
            Color bar = r.stalledPanels > 0 ? new Color(1f, 0.40f, 0.40f)
                      : margin < 0.33f ? new Color(1f, 0.80f, 0.27f)
                                       : new Color(0.53f, 0.87f, 0.53f);
            GUILayout.Label($"AoA {air.AlphaDeg:+0.0;-0.0}°   margin {margin * 100f:0}%");
            Rect track = GUILayoutUtility.GetRect(1f, 6f);
            GUI.DrawTexture(track, White(), ScaleMode.StretchToFill, false, 0f,
                            new Color(0.22f, 0.22f, 0.24f), 0f, 0f);
            GUI.DrawTexture(new Rect(track.x, track.y, track.width * margin, track.height),
                            White(), ScaleMode.StretchToFill, false, 0f, bar, 0f, 0f);

            GUILayout.Label($"g {plane.LoadFactor:0.00}      "
                            + $"bank {Wrap180(plane.transform.eulerAngles.z):+0;-0;0}°   "
                            + $"pitch {Wrap180(plane.transform.eulerAngles.x):+0;-0;0}°");
            GUILayout.Label($"throttle {plane.ThrottleCommand * 100f:0}%   "
                            + $"{plane.PropRevsPerSec * 60f:0} rpm   {plane.Thrust:0.0} N");
            GUILayout.Label($"ail {plane.AileronCommand:+0.00;-0.00; 0.00}   "
                            + $"elev {plane.ElevatorCommand:+0.00;-0.00; 0.00}   "
                            + $"rud {plane.RudderCommand:+0.00;-0.00; 0.00}");

            if (r.stalledPanels > 0)
                GUILayout.Label($"<color=#ff6666><b>STALL</b>  {r.stalledPanels}/{r.totalPanels} panels</color>",
                                Rich());

            GUILayout.FlexibleSpace();
            GUILayout.Label("<color=#999999>[V] view   [R] reset   W/S throttle   "
                            + "arrows = stick</color>", Rich());
            GUILayout.EndArea();

            ArtificialHorizon();
        }

        /// <summary>
        /// A small attitude indicator: rotate the GUI matrix by minus the bank
        /// angle and slide the horizon by the pitch. It is what makes the
        /// ground-station view flyable — from 100 m away a model is a silhouette,
        /// and whether it is climbing is precisely what you cannot read off one.
        /// </summary>
        private void ArtificialHorizon()
        {
            const float size = 116f;
            var box = new Rect((Screen.width - size) * 0.5f,
                               Screen.height - size - 24f, size, size);

            float bank = Wrap180(plane.transform.eulerAngles.z);
            float pitch = Wrap180(plane.transform.eulerAngles.x);

            Matrix4x4 saved = GUI.matrix;
            GUIUtility.RotateAroundPivot(-bank, box.center);

            // Oversized so the rotated square still covers the box at any angle.
            float over = size * 1.7f;
            float shift = Mathf.Clamp(pitch, -60f, 60f) * (size / 90f);
            Fill(new Rect(box.center.x - over * 0.5f, box.center.y - over + shift, over, over),
                 new Color(0.35f, 0.55f, 0.80f));
            Fill(new Rect(box.center.x - over * 0.5f, box.center.y + shift, over, over),
                 new Color(0.35f, 0.28f, 0.18f));
            Fill(new Rect(box.center.x - over * 0.5f, box.center.y + shift - 1f, over, 2f),
                 new Color(0.92f, 0.92f, 0.92f));

            GUI.matrix = saved;

            // Fixed aircraft symbol, drawn after the matrix is restored so it stays
            // put while the world rotates behind it — which is the entire idea.
            Fill(new Rect(box.center.x - 22f, box.center.y - 1f, 44f, 2f),
                 new Color(1f, 0.85f, 0.20f));
            Fill(new Rect(box.center.x - 1f, box.center.y - 6f, 2f, 6f),
                 new Color(1f, 0.85f, 0.20f));
        }

        // ---- helpers ----

        private static void Fill(Rect r, Color c) =>
            GUI.DrawTexture(r, White(), ScaleMode.StretchToFill, false, 0f, c, 0f, 0f);

        private static Texture2D White()
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            return _white;
        }

        private static GUIStyle Rich() =>
            _rich ??= new GUIStyle(GUI.skin.label) { richText = true };

        private static float Wrap180(float deg) => deg > 180f ? deg - 360f : deg;
    }
}
