using AIHWSim.Core.Flight;
using AIHWSim.Garage;
using AIHWSim.UI;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Combat
{
    /// <summary>
    /// The weapons layer of the flight panel: the lock circle over the target,
    /// the weapon/ammo readout, the nozzle tape, and the turret crosshair. Its
    /// own component rather than more surface on <c>FlightHud</c> — the flight
    /// instruments ship with every aircraft, this ships only with an armed one.
    ///
    /// Game-facing tier throughout: <c>GarageSkin</c> inside a
    /// <c>UIScale.Begin/End</c> block, in UI units. The ~40 lines of Fill/arc
    /// helpers are duplicated from FlightHud rather than exposed from it —
    /// widening that file's private surface for a debug armament is the wrong
    /// trade. The <c>GUI.matrix</c> trap documented there applies verbatim
    /// here: never <c>GUIUtility.RotateAroundPivot</c> inside the scaled block
    /// (it composes OUTSIDE <c>GUI.matrix</c>, in screen pixels).
    ///
    /// <b>The lock circle is the Hydra's.</b> A large segmented ring over the
    /// tracked vehicle: green and filling clockwise while the seeker acquires,
    /// then solid red pulsing at 2 Hz once locked. Projection is
    /// <c>WorldToScreenPoint</c> → the UIScale conversion (screen y is
    /// bottom-up, GUI y is top-down, and the whole block is in UI units);
    /// anything with <c>z ≤ 0</c> is behind the camera and clamps to an edge
    /// chevron instead of drawing mirrored garbage.
    /// </summary>
    public sealed class WeaponHud : MonoBehaviour
    {
        public PlaneVehicle plane;
        public WeaponsController weapons;
        public LockOnController lockOn;
        public TurretController turret;

        private static Texture2D _white;
        private static GUIStyle _cap;

        private static readonly Color Green = new Color(0.25f, 0.90f, 0.35f, 0.95f);
        private static readonly Color Red = new Color(0.95f, 0.15f, 0.12f, 0.95f);

        private void OnGUI()
        {
            if (plane == null) return;

            GUISkin prior = GUI.skin;
            GUI.skin = GarageSkin.Skin;
            UIScale.Begin();

            LockCircle();
            WeaponReadout();
            NozzleTape();
            if (turret != null && turret.InTurret) TurretOverlay();

            UIScale.End();
            GUI.skin = prior;
        }

        // ---- lock circle -------------------------------------------------

        private void LockCircle()
        {
            if (lockOn == null || lockOn.Target == null) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            bool locked = lockOn.State == LockOnController.LockState.Locked;
            Color c = locked ? Red : Green;

            Vector3 sp = cam.WorldToScreenPoint(lockOn.Target.AimPoint);
            Vector2 ui = new Vector2(sp.x / UIScale.S,
                                     (Screen.height - sp.y) / UIScale.S);

            bool onScreen = sp.z > 0f
                && ui.x > 0f && ui.x < UIScale.W && ui.y > 0f && ui.y < UIScale.H;
            if (!onScreen)
            {
                EdgeChevron(ui, sp.z <= 0f, c);
                return;
            }

            // Radius from the target's physical size at its distance, floored
            // so the ring never vanishes into a dot — it is an instrument, not
            // a decal on the target.
            float px = lockOn.Target.radius * Screen.height
                       / (2f * sp.z * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            float radius = Mathf.Max(34f, px / UIScale.S * 1.6f);
            if (locked)
                radius *= 1f + 0.08f * Mathf.Sin(Time.time * 2f * Mathf.PI * 2f);

            // Twelve segments. Acquiring: they light clockwise with progress
            // and the whole ring turns slowly — the seeker visibly working.
            // Locked: all twelve solid, no rotation. Steadiness IS the signal.
            const int segs = 12;
            float spin = locked ? 0f : Time.time * 40f;
            int lit = locked ? segs
                : Mathf.Clamp(Mathf.FloorToInt(lockOn.Progress * segs + 0.5f), 1, segs);
            for (int i = 0; i < segs; i++)
            {
                if (i >= lit) continue;
                ArcSegment(ui, spin + i * (360f / segs), radius, 5f, 18f, c);
            }

            if (locked)
                GUI.Label(new Rect(ui.x - 30f, ui.y + radius + 8f, 60f, 18f),
                          "LOCK", Cap(c));
        }

        /// <summary>Where the eye should go when the tracked target is not on
        /// screen: a chevron pinned to the screen edge nearest it. Behind the
        /// camera, the projection mirrors — so flip it back before clamping.</summary>
        private void EdgeChevron(Vector2 ui, bool behind, Color c)
        {
            Vector2 centre = new Vector2(UIScale.W * 0.5f, UIScale.H * 0.5f);
            Vector2 dir = ui - centre;
            if (behind) dir = -dir;
            if (dir.sqrMagnitude < 1e-3f) return;
            dir.Normalize();

            float half = Mathf.Min(centre.x, centre.y) - 30f;
            Vector2 p = centre + dir * half;
            GUI.Label(new Rect(p.x - 12f, p.y - 12f, 24f, 24f), "^",
                      Cap(c, 20));
            // Rotate the glyph toward the target: hand-composed, per the trap.
            // (A label glyph reads fine unrotated; the position carries the
            // information, so no matrix needed at all.)
        }

        // ---- readouts ----------------------------------------------------

        private void WeaponReadout()
        {
            if (weapons == null) return;

            float x = 16f, y = UIScale.H - 96f;
            string name;
            string count;
            switch (weapons.Selected)
            {
                case WeaponsController.Weapon.Missiles:
                    name = "MISSILES";
                    count = weapons.MissilesLeft.ToString();
                    break;
                case WeaponsController.Weapon.Bombs:
                    name = "BOMBS";
                    count = weapons.BombsLeft.ToString();
                    break;
                default:
                    name = weapons.GunLocked ? "GUN  HOT" : "GUN";
                    count = "";
                    break;
            }

            GUI.Label(new Rect(x, y, 200f, 20f), name + (count == "" ? "" : "  " + count),
                      Cap(new Color(0.92f, 0.88f, 0.80f)));

            if (weapons.Selected == WeaponsController.Weapon.Gun)
            {
                // Heat bar, red past the lockout threshold.
                Rect bar = new Rect(x, y + 24f, 150f, 8f);
                Fill(bar, new Color(0.10f, 0.11f, 0.14f, 0.85f));
                Rect fill = new Rect(bar.x, bar.y, bar.width * weapons.GunHeat, bar.height);
                Fill(fill, weapons.GunHeat > 0.75f ? Red : GarageSkin.Accent);
            }

            GUI.Label(new Rect(x, y + 40f, 260f, 16f),
                      "[Space] fire   [Tab] swap   [C] turret",
                      Cap(new Color(0.60f, 0.58f, 0.54f), 11));
        }

        /// <summary>Nozzle angle as a small vertical tape by the throttle side:
        /// full aft at the bottom, hover at the top — the lever it stands for.</summary>
        private void NozzleTape()
        {
            if (plane.spec == null || !plane.spec.IsJet) return;

            float h = 90f;
            Rect tape = new Rect(UIScale.W * 0.5f + 150f, UIScale.H - 60f - h, 10f, h);
            Fill(tape, new Color(0.10f, 0.11f, 0.14f, 0.85f));
            float frac = Mathf.InverseLerp(plane.spec.jet.nozzleMinDeg,
                                           plane.spec.jet.nozzleMaxDeg,
                                           plane.NozzleDeg);
            Fill(new Rect(tape.x, tape.y + tape.height * (1f - frac),
                          tape.width, tape.height * frac), GarageSkin.Accent);
            GUI.Label(new Rect(tape.x - 14f, tape.y - 20f, 60f, 16f),
                      $"NOZ {plane.NozzleDeg:0}°",
                      Cap(new Color(0.92f, 0.88f, 0.80f), 11));
        }

        private void TurretOverlay()
        {
            Vector2 c = new Vector2(UIScale.W * 0.5f, UIScale.H * 0.5f);
            // A plain cross: two thin fills. The gun shoots where this points.
            Fill(new Rect(c.x - 12f, c.y - 1f, 8f, 2f), Green);
            Fill(new Rect(c.x + 4f, c.y - 1f, 8f, 2f), Green);
            Fill(new Rect(c.x - 1f, c.y - 12f, 2f, 8f), Green);
            Fill(new Rect(c.x - 1f, c.y + 4f, 2f, 8f), Green);
            GUI.Label(new Rect(c.x - 90f, c.y + 40f, 180f, 18f),
                      "TURRET — [C] back to pilot",
                      Cap(new Color(0.92f, 0.88f, 0.80f), 12));
        }

        // ---- local drawing helpers (duplicated from FlightHud on purpose) --

        private static void Fill(Rect r, Color c)
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            Color prior = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = prior;
        }

        /// <summary>One tangent tile of an arc — angle measured from straight
        /// up, clockwise. Matrix composed BY HAND inside the current transform;
        /// RotateAroundPivot composes outside GUI.matrix in screen pixels and
        /// is exactly the bug FlightHud documents.</summary>
        private static void ArcSegment(Vector2 centre, float angleDeg, float radius,
                                       float thickness, float widthDeg, Color c)
        {
            float w = 2f * Mathf.PI * radius * (widthDeg / 360f);
            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = saved
                * Matrix4x4.TRS(centre, Quaternion.Euler(0f, 0f, angleDeg), Vector3.one)
                * Matrix4x4.TRS(-centre, Quaternion.identity, Vector3.one);
            Fill(new Rect(centre.x - w * 0.5f, centre.y - radius - thickness * 0.5f,
                          w, thickness), c);
            GUI.matrix = saved;
        }

        private static GUIStyle Cap(Color c, int size = 14)
        {
            if (_cap == null)
                _cap = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                };
            _cap.fontSize = size;
            _cap.normal.textColor = c;
            return _cap;
        }
    }
}
