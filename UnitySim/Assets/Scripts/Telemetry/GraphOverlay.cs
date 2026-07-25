using System.Collections.Generic;
using AIHWSim.Core;
using UnityEngine;

namespace AIHWSim.Telemetry
{
    /// <summary>
    /// Live scrolling line-graph overlay drawn with GL in screen space.
    ///
    /// Attach to the main Camera. Configure panes (each a titled group of
    /// channels) after the hub's channels are registered. Deliberately uses GL
    /// rather than UI Toolkit so it needs no scene assets and runs from the
    /// runtime scene builder; swap the renderer later if a richer UI is wanted.
    ///
    /// Keys: [G] toggle graphs, [P] pause, [ / ] shrink / grow time window.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class GraphOverlay : MonoBehaviour
    {
        private sealed class Pane
        {
            public string Title;
            public readonly List<string> Channels = new List<string>();
        }

        public TelemetryHub Hub;
        public bool Visible = true;
        public bool Paused = false;
        public int WindowSamples = 600;

        private readonly List<Pane> _panes = new List<Pane>();
        private Material _lineMat;
        private float[] _scratch;

        private static readonly Color[] Palette =
        {
            new Color(0.30f, 0.69f, 1.00f), // blue
            new Color(1.00f, 0.60f, 0.20f), // orange
            new Color(0.40f, 0.85f, 0.45f), // green
            new Color(0.95f, 0.40f, 0.55f), // pink
            new Color(0.80f, 0.75f, 0.30f), // yellow
            new Color(0.65f, 0.55f, 0.95f), // violet
        };

        public void AddPane(string title, params string[] channels)
        {
            var pane = new Pane { Title = title };
            pane.Channels.AddRange(channels);
            _panes.Add(pane);
        }

        public void ClearPanes() => _panes.Clear();

        private void EnsureMaterial()
        {
            if (_lineMat != null) return;
            // Built-in colored shader is the standard choice for GL debug drawing.
            var shader = Shader.Find("Hidden/Internal-Colored");
            _lineMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _lineMat.SetInt("_ZWrite", 0);
            _lineMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            _lineMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _lineMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _lineMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        }

        private void Update()
        {
            if (InputReader.GraphTogglePressed()) Visible = !Visible;
            if (InputReader.PauseTogglePressed()) Paused = !Paused;
            if (InputReader.WindowShrinkPressed()) WindowSamples = Mathf.Max(60, WindowSamples - 120);
            if (InputReader.WindowGrowPressed()) WindowSamples = Mathf.Min(4000, WindowSamples + 120);
        }

        private void OnPostRender()
        {
            if (!Visible || Hub == null || _panes.Count == 0) return;
            EnsureMaterial();

            GL.PushMatrix();
            _lineMat.SetPass(0);
            // (0,0) top-left, y increases downward — matches OnGUI coordinates.
            GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);

            float margin = 10f;
            float paneW = Mathf.Min(460f, Screen.width * 0.42f);
            float paneH = 130f;
            float x = margin;

            for (int i = 0; i < _panes.Count; i++)
            {
                float y = margin + i * (paneH + margin);
                DrawPane(_panes[i], new Rect(x, y, paneW, paneH));
            }

            GL.PopMatrix();
        }

        private void DrawPane(Pane pane, Rect r)
        {
            // Background.
            DrawQuad(r, new Color(0f, 0f, 0f, 0.55f));
            DrawRectOutline(r, new Color(1f, 1f, 1f, 0.25f));

            int n = Mathf.Min(WindowSamples, Hub.SampleCount);
            if (n < 2) return;

            // Determine shared y-range across this pane's channels.
            float minV = float.MaxValue, maxV = float.MinValue;
            for (int c = 0; c < pane.Channels.Count; c++)
            {
                if (!Hub.TryGetChannel(pane.Channels[c], out var ch)) continue;
                GatherWindow(ch, n);
                for (int k = 0; k < n; k++)
                {
                    float v = _scratch[k];
                    if (v < minV) minV = v;
                    if (v > maxV) maxV = v;
                }
            }
            if (minV == float.MaxValue) return;
            if (Mathf.Approximately(minV, maxV)) { minV -= 1f; maxV += 1f; }
            float pad = (maxV - minV) * 0.1f;
            minV -= pad; maxV += pad;

            // Zero line.
            if (minV < 0f && maxV > 0f)
            {
                float yz = ValueToY(0f, minV, maxV, r);
                DrawLine(new Vector2(r.xMin, yz), new Vector2(r.xMax, yz), new Color(1f, 1f, 1f, 0.15f));
            }

            // Series.
            for (int c = 0; c < pane.Channels.Count; c++)
            {
                if (!Hub.TryGetChannel(pane.Channels[c], out var ch)) continue;
                GatherWindow(ch, n);
                Color col = Palette[c % Palette.Length];
                GL.Begin(GL.LINES);
                GL.Color(col);
                for (int k = 0; k < n - 1; k++)
                {
                    float x0 = r.xMin + (r.width * k) / (n - 1);
                    float x1 = r.xMin + (r.width * (k + 1)) / (n - 1);
                    float y0 = ValueToY(_scratch[k], minV, maxV, r);
                    float y1 = ValueToY(_scratch[k + 1], minV, maxV, r);
                    GL.Vertex3(x0, y0, 0f);
                    GL.Vertex3(x1, y1, 0f);
                }
                GL.End();
            }
        }

        private void GatherWindow(TelemetryHub.Channel ch, int n)
        {
            if (_scratch == null || _scratch.Length < n) _scratch = new float[Mathf.Max(n, 64)];
            int cap = ch.Ring.Length;
            int start = (ch.Head - n + cap) % cap; // oldest sample in window
            for (int k = 0; k < n; k++)
                _scratch[k] = ch.Ring[(start + k) % cap];
        }

        private static float ValueToY(float v, float minV, float maxV, Rect r)
        {
            float t = (v - minV) / (maxV - minV);           // 0 at bottom value
            return r.yMax - t * r.height;                    // invert: high value -> small y (up)
        }

        private void DrawQuad(Rect r, Color col)
        {
            GL.Begin(GL.QUADS);
            GL.Color(col);
            GL.Vertex3(r.xMin, r.yMin, 0);
            GL.Vertex3(r.xMax, r.yMin, 0);
            GL.Vertex3(r.xMax, r.yMax, 0);
            GL.Vertex3(r.xMin, r.yMax, 0);
            GL.End();
        }

        private void DrawRectOutline(Rect r, Color col)
        {
            DrawLine(new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), col);
            DrawLine(new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), col);
            DrawLine(new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax), col);
            DrawLine(new Vector2(r.xMin, r.yMax), new Vector2(r.xMin, r.yMin), col);
        }

        private void DrawLine(Vector2 a, Vector2 b, Color col)
        {
            GL.Begin(GL.LINES);
            GL.Color(col);
            GL.Vertex3(a.x, a.y, 0);
            GL.Vertex3(b.x, b.y, 0);
            GL.End();
        }

        private void OnGUI()
        {
            if (!Visible || Hub == null || _panes.Count == 0) return;

            float margin = 10f;
            float paneW = Mathf.Min(460f, Screen.width * 0.42f);
            float paneH = 130f;

            for (int i = 0; i < _panes.Count; i++)
            {
                var pane = _panes[i];
                float x = margin;
                float y = margin + i * (paneH + margin);

                GUI.Label(new Rect(x + 6, y + 2, paneW, 18), pane.Title);

                // Legend + latest values.
                for (int c = 0; c < pane.Channels.Count; c++)
                {
                    Color col = Palette[c % Palette.Length];
                    string val = "--";
                    if (Hub.TryGetChannel(pane.Channels[c], out var ch))
                        val = ch.Latest.ToString("0.00");
                    var prev = GUI.color;
                    GUI.color = col;
                    GUI.Label(new Rect(x + 6, y + 20 + c * 16, paneW, 16), $"{pane.Channels[c]}: {val}");
                    GUI.color = prev;
                }
            }

            string status = Paused ? "PAUSED" : "live";
            GUI.Label(new Rect(margin, Screen.height - 22,
                600, 20), $"[G]raphs  [P]ause({status})  [ ] window={WindowSamples}");
        }

        private void OnDestroy()
        {
            if (_lineMat != null) DestroyImmediate(_lineMat);
        }
    }
}
