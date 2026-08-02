using AIHWSim.Garage;
using AIHWSim.UI;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// The pilot's panel, in two deliberately different halves.
    ///
    /// <b>The flight instruments</b> — navball, altimeter, throttle gauge and the
    /// SAS mode buttons — are laid out like Kerbal Space Program's, and are
    /// GAME-FACING: drawn through <see cref="GarageSkin"/> inside a
    /// <see cref="UIScale"/> block, in UI units. They have to be, because they
    /// contain the only clickable controls in the scene and a button authored in
    /// raw pixels is a quarter of its intended size on a 4K screen.
    ///
    /// <b>The engineering panel</b> — top right — stays a developer overlay:
    /// unscaled, dense, raw <c>Screen</c> coordinates, per the rule in
    /// <see cref="UIScale"/>'s summary. It is a tool, and its density is a feature.
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

        /// <summary>What the target marker points at. Null is fine — the marker and
        /// the Target mode button simply go away.</summary>
        public Transform target;

        /// <summary>Drives the six mode buttons. Null just leaves them off screen.</summary>
        public SasController sas;

        private NavballRig _navball;

        /// <summary>Mode names, in the order the buttons are drawn and the order
        /// the number keys select. Left column then right column.</summary>
        private static readonly string[] SasLabels =
        {
            "Stability", "Prograde", "Retrograde",
            "Target", "Wings Lvl", "Alt Hold",
        };

        private static Texture2D _white;
        private static GUIStyle _rich;
        private static GUIStyle _bigNum, _smallCap, _marker;

        public void Bind(PlaneVehicle p, FlightCameraRig cams,
                         SasController assist = null, Transform tgt = null)
        {
            plane = p;
            cameras = cams;
            sas = assist;
            target = tgt;
        }

        private void Update()
        {
            if (plane == null) return;
            // Rendered from Update rather than OnGUI: OnGUI runs several times a
            // frame (Layout, then Repaint, then once per event) and rendering a
            // camera from inside it would take the picture two or three times for
            // one that gets shown.
            _navball ??= new NavballRig();
            _navball.Tick(plane.transform.rotation);
        }

        private void OnDestroy()
        {
            _navball?.Destroy();
            _navball = null;
        }

        private void OnGUI()
        {
            if (plane == null || _navball == null) return;

            EngineeringPanel();

            GUISkin savedSkin = GUI.skin;
            GUI.skin = GarageSkin.Skin;
            UIScale.Begin();
            Altimeter();
            Navball();
            ThrottleGauge();
            SasButtons();
            UIScale.End();
            GUI.skin = savedSkin;
        }

        // ---- the engineering panel (developer tier, unscaled) ----

        private void EngineeringPanel()
        {
            const float w = 272f, h = 262f;   // two hint lines now, not one
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
            // What the tail is flying in, which is not what the aeroplane is flying
            // in. On the ground at full power the airspeed reads zero and this reads
            // 19 m/s — and that gap IS why the elevator still works there.
            GUILayout.Label($"wash {r.slipstreamIncrement:0.0} m/s   "
                            + (air.Q > 1e-3f
                               ? $"eta_tail {plane.SurfaceDynamicPressure(1) / air.Q:0.00}"
                               : "eta_tail —"));
            GUILayout.Label($"ail {plane.AileronCommand:+0.00;-0.00; 0.00}   "
                            + $"elev {plane.ElevatorCommand:+0.00;-0.00; 0.00}   "
                            + $"rud {plane.RudderCommand:+0.00;-0.00; 0.00}");

            if (r.stalledPanels > 0)
                GUILayout.Label($"<color=#ff6666><b>STALL</b>  {r.stalledPanels}/{r.totalPanels} panels</color>",
                                Rich());

            GUILayout.FlexibleSpace();
            GUILayout.Label("<color=#999999>WASDQE fly   Shift/Ctrl throttle   X cut   "
                            + "Z full\n[T] SAS   [F] hold off   [V] view   [R] reset</color>",
                            Rich());
            GUILayout.EndArea();
        }

        // ---- the flight instruments (game-facing tier, UI units) ----

        /// <summary>Height and climb rate, top centre, where KSP puts them. Height
        /// is above ground level and the world is flat, so it is also altitude.</summary>
        private void Altimeter()
        {
            var box = new Rect(UIScale.W * 0.5f - 150f, 8f, 300f, 56f);
            GUI.Box(box, GUIContent.none);
            GUI.Label(new Rect(box.x, box.y + 2f, box.width, 34f),
                      $"{plane.AltitudeAgl:0.0} m", BigNum());
            GUI.Label(new Rect(box.x, box.y + 34f, box.width, 20f),
                      $"ALT     {plane.VerticalSpeed:+0.0;-0.0} m/s", SmallCap());
        }

        /// <summary>The ball, plus the three direction markers over it.</summary>
        private void Navball()
        {
            Rect box = NavballRect();
            Quaternion att = plane.transform.rotation;

            // A dark disc behind it, so the ball reads as an instrument rather than
            // a sphere floating over the terrain.
            Fill(box, new Color(0.05f, 0.06f, 0.09f, 0.85f));
            if (_navball?.Texture != null) GUI.DrawTexture(box, _navball.Texture);

            float half = box.width * 0.5f;
            var body = plane.GetComponent<Rigidbody>();
            Vector3 vel = body != null ? body.linearVelocity : Vector3.zero;

            // Prograde first, target last, so the marker you asked for is the one
            // on top when two coincide.
            Marker(box, _navball.MarkerOffset(vel, att, half),
                   new Color(0.45f, 0.95f, 0.45f), "o");
            Marker(box, _navball.MarkerOffset(-vel, att, half),
                   new Color(1f, 0.72f, 0.25f), "x");
            if (target != null)
                Marker(box, _navball.MarkerOffset(target.position - plane.transform.position,
                                                  att, half),
                       new Color(0.95f, 0.45f, 0.90f), "^");

            // The fixed reticle: where the nose points, always dead centre. Drawn
            // last so a marker never hides it.
            Fill(new Rect(box.center.x - 16f, box.center.y - 1f, 12f, 2f), GarageSkin.Accent);
            Fill(new Rect(box.center.x + 4f, box.center.y - 1f, 12f, 2f), GarageSkin.Accent);
            Fill(new Rect(box.center.x - 1f, box.center.y - 8f, 2f, 6f), GarageSkin.Accent);
        }

        private void Marker(Rect ball, Vector2? offset, Color c, string glyph)
        {
            if (offset == null) return;
            var r = new Rect(ball.center.x + offset.Value.x - 9f,
                             ball.center.y + offset.Value.y - 9f, 18f, 18f);
            var style = MarkerStyle();
            style.normal.textColor = c;
            GUI.Label(r, glyph, style);
        }

        private static Rect NavballRect() =>
            new Rect(UIScale.W * 0.5f - 110f, UIScale.H - 250f, 220f, 220f);

        /// <summary>A bottom-up bar, because a throttle is a lever and reads as a
        /// height. It shows the COMMANDED setting off the vehicle rather than the
        /// stick, which is the whole point of a ratchet: the input is a rate and
        /// only the gauge says where the lever actually is.</summary>
        private void ThrottleGauge()
        {
            var box = new Rect(UIScale.W * 0.5f - 170f, UIScale.H - 240f, 26f, 200f);
            float t = Mathf.Clamp01(plane.ThrottleCommand);

            Fill(box, new Color(0.06f, 0.07f, 0.10f, 0.95f));
            Fill(new Rect(box.x, box.yMax - box.height * t, box.width, box.height * t),
                 GarageSkin.Accent);
            // Quarter marks, so a glance separates 50 % from 60 % without reading.
            for (int i = 1; i < 4; i++)
                Fill(new Rect(box.x, box.yMax - box.height * (i * 0.25f) - 1f, box.width, 1f),
                     new Color(0f, 0f, 0f, 0.45f));

            GUI.Label(new Rect(box.x - 12f, box.yMax + 2f, box.width + 24f, 18f),
                      $"{t * 100f:0}%", SmallCap());
        }

        /// <summary>Six mode buttons flanking the ball, three a side.
        ///
        /// Plain <c>GUI.Button</c> rather than the <c>MenuNav</c> wrappers the menus
        /// use, and that is deliberate: MenuNav claims the gamepad for the frame and
        /// reads the left stick and d-pad to move its focus ring — the same inputs
        /// this aeroplane is being flown with. Every other runtime user of MenuNav
        /// is a modal screen with the world paused. Pad access to these modes comes
        /// through the d-pad in the SAS controller instead.</summary>
        private void SasButtons()
        {
            if (sas == null) return;

            Rect ball = NavballRect();
            float top = ball.center.y - 60f;
            Color savedBg = GUI.backgroundColor;
            bool savedEnabled = GUI.enabled;

            for (int i = 0; i < SasLabels.Length; i++)
            {
                var mode = (SasController.SasMode)i;
                bool right = i >= 3;
                var r = new Rect(right ? ball.center.x + 140f : ball.center.x - 300f,
                                 top + (i % 3) * 40f, 150f, 34f);

                bool live = sas.Enabled && sas.Mode == mode && !sas.OverrideHeld;
                GUI.enabled = savedEnabled && sas.Available(mode);
                // Gold for the mode that is flying, a dim gold for the one that is
                // selected but suspended under the override — "why is it not
                // holding" should be answerable without letting go of the key.
                GUI.backgroundColor = live ? GarageSkin.Accent
                                   : sas.Enabled && sas.Mode == mode ? GarageSkin.AccentDim
                                                                     : savedBg;
                if (GUI.Button(r, SasLabels[i])) sas.SetMode(mode);
            }

            GUI.backgroundColor = savedBg;
            GUI.enabled = savedEnabled;

            var status = new Rect(ball.center.x - 300f, top + 3 * 40f + 4f, 150f, 20f);
            GUI.Label(status, sas.OverrideHeld ? "SAS HELD OFF" : sas.Enabled ? "SAS ON" : "SAS OFF",
                      SmallCap());
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

        // Built against the skin that is current when first asked for, which is the
        // GarageSkin — these are only ever used inside the game-facing block.
        private static GUIStyle BigNum() =>
            _bigNum ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 26,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = GarageSkin.Text },
            };

        /// <summary>Shared, and its colour is overwritten per marker — three styles
        /// for three glyphs would be three allocations for one difference.</summary>
        private static GUIStyle MarkerStyle() =>
            _marker ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
            };

        private static GUIStyle SmallCap() =>
            _smallCap ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.72f, 0.70f, 0.66f) },
            };

        private static float Wrap180(float deg) => deg > 180f ? deg - 360f : deg;
    }
}
