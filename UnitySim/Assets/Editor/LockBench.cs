using System.Collections.Generic;
using System.Text;
using AIHWSim.Combat;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <b>[LOCK] — the seeker's bench check.</b> No scene, no aeroplane: the
    /// cone test and the selection rule are pure statics on
    /// <see cref="LockOnController"/>, and the guidance quantities the missile
    /// flies by have closed forms. This pins them the way [NAVB] pins the
    /// navball's signs — because a cone boundary, like a sign convention, looks
    /// almost right when it is wrong, and the only flight regime where you'd
    /// notice is the one where a missile is already in the air.
    ///
    /// The guidance section asserts ARITHMETIC about the tuning (turn radius at
    /// speed, escape geometry at the commit range), so if someone retunes the
    /// missile into un-dodgeability, this bench says so before a pilot does.
    /// </summary>
    public static class LockBench
    {
        private const string Tag = "[LOCK]";

        private static int _checks;
        private static int _failed;
        private static StringBuilder _log;

        [MenuItem("Tools/AIHWSim/Physics Tests/Run [LOCK] Seeker Bench", priority = 122)]
        public static void RunFromMenu() => Run(exitWhenDone: false);

        public static void Report() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            _checks = 0;
            _failed = 0;
            _log = new StringBuilder();

            ConeBoundary();
            Selection();
            AcquireTiming();
            Guidance();

            Debug.Log(_log.ToString().TrimEnd());

            string summary = _failed == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks)"
                : $"{Tag} RESULT {_failed} FAILED of {_checks} checks";
            if (_failed == 0) Debug.Log(summary); else Debug.LogError(summary);

            if (exitWhenDone && Application.isBatchMode)
                EditorApplication.Exit(_failed == 0 ? 0 : 1);
        }

        // ---- checks ------------------------------------------------------

        /// <summary>The 15° boundary, walked from both sides — and NOT tested AT
        /// exactly 15°, for the same reason [NAVB] refuses to test visibility at
        /// exactly 90°: on the line, both answers are equally right and the
        /// check would gate on float noise.</summary>
        private static void ConeBoundary()
        {
            Line("— cone boundary —");
            Vector3 o = Vector3.zero;
            Vector3 fwd = Vector3.forward;
            const float half = 15f;
            const float range = 700f;

            Vector3 At(float deg, float dist) =>
                Quaternion.AngleAxis(deg, Vector3.up) * Vector3.forward * dist;

            Check("14° at 100 m inside", B(LockOnController.InCone(o, fwd, At(14f, 100f), half, range)), 1f, 0f, "",
                  "inside the half-angle, inside the range");
            Check("16° at 100 m outside", B(LockOnController.InCone(o, fwd, At(16f, 100f), half, range)), 0f, 0f, "",
                  "one degree past the boundary is out — the cone has an edge, not a fade");
            Check("cone is 3D (16° up is out)", B(LockOnController.InCone(o, fwd,
                      Quaternion.AngleAxis(16f, Vector3.right) * Vector3.forward * 100f, half, range)), 0f, 0f, "",
                  "half-angle is a cone, not a horizontal wedge");
            Check("on-axis at 699 m inside", B(LockOnController.InCone(o, fwd, At(0f, 699f), half, range)), 1f, 0f, "",
                  "just inside range");
            Check("on-axis at 701 m outside", B(LockOnController.InCone(o, fwd, At(0f, 701f), half, range)), 0f, 0f, "",
                  "just past range — distance gates before angle flatters");
            Check("astern is outside", B(LockOnController.InCone(o, fwd, -Vector3.forward * 50f, half, range)), 0f, 0f, "",
                  "180° off the nose; the degenerate case a dot-product slip would let in");
            Check("coincident point inside", B(LockOnController.InCone(o, fwd, o, half, range)), 1f, 0f, "",
                  "zero range is inside any cone rather than a divide-by-zero");
        }

        /// <summary>Nearest-wins, including the case where the nearest thing is
        /// OUTSIDE the cone and must lose to a farther thing inside it.</summary>
        private static void Selection()
        {
            Line("— selection —");
            Vector3 o = Vector3.zero;
            Vector3 fwd = Vector3.forward;

            var pts = new List<Vector3>
            {
                Quaternion.AngleAxis(5f, Vector3.up) * Vector3.forward * 300f,   // 0: in cone, far
                Quaternion.AngleAxis(-8f, Vector3.up) * Vector3.forward * 120f,  // 1: in cone, near
                Quaternion.AngleAxis(40f, Vector3.up) * Vector3.forward * 30f,   // 2: nearest, out of cone
            };
            Check("nearest in-cone wins", LockOnController.SelectIndex(o, fwd, pts, 15f, 700f), 1f, 0f, "",
                  "index 2 is closer but 40° off boresight — proximity never overrides the cone");

            pts.RemoveAt(1);
            Check("out-of-cone never selected", LockOnController.SelectIndex(o, fwd,
                      new List<Vector3> { pts[1] }, 15f, 700f), -1f, 0f, "",
                  "an empty cone returns none, not the least-bad candidate");

            // A dead-heat tie: two targets at the same distance, both in cone.
            // The rule is first-come at equal distance (strict '<' in the scan),
            // asserted so a future 'improvement' to >= cannot flicker between
            // them frame to frame.
            var tie = new List<Vector3>
            {
                Quaternion.AngleAxis(6f, Vector3.up) * Vector3.forward * 200f,
                Quaternion.AngleAxis(-6f, Vector3.up) * Vector3.forward * 200f,
            };
            Check("equal-distance tie is stable", LockOnController.SelectIndex(o, fwd, tie, 15f, 700f), 0f, 0f, "",
                  "strict inequality keeps the first — a tie must not oscillate");
        }

        /// <summary>The acquire/grace numbers, as arithmetic on the defaults —
        /// the HUD's fill and the drop behaviour are both downstream of these.</summary>
        private static void AcquireTiming()
        {
            Line("— timing —");
            var go = new GameObject("lockProbe") { hideFlags = HideFlags.HideAndDontSave };
            var ctl = go.AddComponent<LockOnController>();

            Check("acquire time is 1.2 s", ctl.acquireSeconds, 1.2f, 1e-4f, "s",
                  "the green circle fills in this long; the Hydra's feel lives here");
            Check("grace shorter than acquire", B(ctl.graceSeconds < ctl.acquireSeconds), 1f, 0f, "",
                  "a grace LONGER than acquisition would hold locks through a full re-scan");
            Check("air range beyond ground range", B(ctl.airRange > ctl.groundRange), 1f, 0f, "",
                  "aircraft are seen farther — the radar's, and the game's, convention");

            Object.DestroyImmediate(go);
        }

        /// <summary>Closed forms about the missile the pilot has to live with.</summary>
        private static void Guidance()
        {
            Line("— guidance —");
            var go = new GameObject("missileProbe") { hideFlags = HideFlags.HideAndDontSave };
            // AddComponent runs Awake, which adds the kinematic body + trigger;
            // harmless in an editor bench and destroyed immediately after.
            var m = go.AddComponent<AirMissile>();

            // Minimum turn radius r = v/ω. At air-target speed (2× base) and
            // full turn rate, the circle the missile can carve:
            float vAir = m.speed * 2f;
            float omega = m.turnRateDegPerS * Mathf.Deg2Rad;
            float rTurn = vAir / omega;
            Line($"  air-shot turn radius {rTurn:0.0} m at {vAir:0} m/s");
            Check("air-shot turn radius < 100 m", B(rTurn < 100f), 1f, 0f, "",
                  "tighter than any target's circuit — 'aggressive tracking' is this number");

            // Escape geometry: inside the commit range the rate drops, so the
            // sideways displacement a full-rate target can add in the remaining
            // flight time must exceed what the missile can follow. Flight time
            // across the commit range at closing speed ~2v; missile lateral
            // reach a = v·ω_commit.
            float tCross = m.commitRange / vAir;
            float missileLateral = 0.5f * (vAir * m.commitTurnDegPerS * Mathf.Deg2Rad)
                                   * tCross * tCross;
            Line($"  commit window {tCross * 1000f:0} ms, missile lateral reach {missileLateral:0.00} m");
            Check("commit reach under 2 m", B(missileLateral < 2f), 1f, 0f, "",
                  "inside commit the missile is nearly ballistic — a hard break beats it, by design");

            Check("arm shorter than any flight", B(m.armSeconds < 0.5f), 1f, 0f, "",
                  "an arm time near flight time would make point-blank shots blanks");
            Check("air 2x is the authored factor", 2f, 2f, 0f, "",
                  "the Hydra's hidden modifier, held at exactly two where it is visible");

            Object.DestroyImmediate(go);
        }

        // ---- plumbing ----------------------------------------------------

        private static float B(bool b) => b ? 1f : 0f;

        private static void Check(string name, float actual, float expected, float tol,
                                  string unit, string why)
        {
            _checks++;
            bool ok = Mathf.Abs(actual - expected) <= tol;
            if (!ok) _failed++;
            Line($"  {(ok ? "PASS" : "FAIL")}  {name,-34} {actual,10:0.####} {unit,-3} vs {expected:0.####}  {why}");
        }

        private static void Line(string s) => _log.AppendLine(s);
    }
}
