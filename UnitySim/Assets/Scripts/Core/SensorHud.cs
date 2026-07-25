using AIHWSim.Bridge;
using AIHWSim.Sensors;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// On-screen readout for the vehicle's configurable sensors: a
    /// picture-in-picture of the primary camera's view and a row of horizontal
    /// bars for each ToF range finder (bar shrinks as an obstacle nears). Purely
    /// diagnostic; reads the live values the <see cref="SensorRig"/> already
    /// sampled this frame.
    /// </summary>
    public sealed class SensorHud : MonoBehaviour
    {
        public SensorRig rig;

        [Tooltip("Pixel size of the camera picture-in-picture.")]
        public Vector2 camViewSize = new Vector2(160f, 120f);

        private GUIStyle _label;

        private void OnGUI()
        {
            if (rig == null) return;
            _label ??= new GUIStyle(GUI.skin.label) { fontSize = 11 };

            float x = 10f, y = Screen.height - 10f;

            // Camera PiP (bottom-left).
            var cam = rig.PrimaryCamera;
            if (cam != null && cam.Preview != null)
            {
                float w = camViewSize.x, h = camViewSize.y;
                var box = new Rect(x, y - h, w, h);
                GUI.Box(new Rect(box.x - 2, box.y - 18, w + 4, h + 20), GUIContent.none);
                GUI.Label(new Rect(box.x, box.y - 17, w, 16), $"cam: {cam.sensorName} ({cam.Width}x{cam.Height})", _label);
                // Camera renders bottom-up; flip vertically for display.
                GUI.DrawTextureWithTexCoords(box, cam.Preview, new Rect(0f, 1f, 1f, -1f));
                y -= h + 24f;
            }

            // ToF bars (stacked upward from just above the camera PiP).
            for (int i = 0; i < rig.SensorCount; i++)
            {
                var s = rig.Sensors[i];
                if (s.Type != SensorType.Tof) continue;
                float dist = rig.FirstValue(i);
                float max = Mathf.Max(0.01f, s.rangeMax);
                float frac = Mathf.Clamp01(dist / max);

                const float bw = 160f, bh = 16f;
                var bg = new Rect(x, y - bh, bw, bh);
                GUI.Box(bg, GUIContent.none);
                // Closer = shorter + redder.
                var fill = new Rect(x, y - bh, bw * frac, bh);
                Color prev = GUI.color;
                GUI.color = Color.Lerp(new Color(0.9f, 0.2f, 0.1f, 0.85f), new Color(0.2f, 0.8f, 0.3f, 0.85f), frac);
                GUI.DrawTexture(fill, Texture2D.whiteTexture);
                GUI.color = prev;
                GUI.Label(new Rect(x + 4f, y - bh, bw, bh), $"{s.sensorName}: {dist:0.0} m", _label);
                y -= bh + 3f;
            }
        }
    }
}
