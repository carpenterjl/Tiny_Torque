using System.Collections.Generic;
using System.IO;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Drives three laps of a baked racing line and fits the four scalars the
    /// solver's physics model needs.
    ///
    /// <b>Why calibration and not search.</b> A black-box optimiser — drive many
    /// candidate lines, keep the fastest — needs 50 to 200 laps per track, and its
    /// output is a line nobody can explain. Three laps produce four numbers with
    /// physical meaning (grip scale, standing acceleration, top speed, how much of
    /// the friction circle braking really uses), and those numbers describe the CAR,
    /// so they transfer to every track built with it.
    ///
    /// Lap 1 is a warm-up and discarded. Lap 2 is the measurement. Lap 3 exists to
    /// prove lap 2: surface roughness is a deterministic positional noise field, so
    /// two laps of the same line must agree to within a few milliseconds. If they do
    /// not, something non-deterministic is in the loop and the honest answer is to
    /// FAIL rather than fit noise.
    /// </summary>
    public sealed class RacingLineCalibrationWatcher : MonoBehaviour
    {
        private const float StuckSpeed = 0.3f;
        private const float StuckSeconds = 2f;
        private const int WarmupLap = 0;

        private RacingLineAutorun.Request _req;
        private readonly RacingLineAutorun.Result _res = new RacingLineAutorun.Result();

        private CarVehicle _car;
        private RaceLineFollower _follower;
        private RacingLineAsset _line;

        private bool _finished;
        private float _elapsed;
        private float _lapStart;
        private int _lap;
        private float _prevS;
        private bool _hasPrevS;
        private float _stuckFor;

        private readonly List<float> _lapTimes = new List<float>();
        private readonly List<Sample> _samples = new List<Sample>();

        private struct Sample
        {
            public float v;
            public float aLat;
            public float kappa;
            public float throttle;
            public float brake;
            public float mu;      // catalog frictionMult under the wheels
        }

        public void Configure(RacingLineAutorun.Request req) => _req = req;

        private void FixedUpdate()
        {
            if (_finished || _req == null) return;
            _elapsed += Time.fixedDeltaTime;

            if (_car == null && !Acquire()) return;

            if (_elapsed > _req.timeoutSec) { Finish(false, "timeout"); return; }

            float v = _car.ForwardSpeed;
            if (Mathf.Abs(v) < StuckSpeed)
            {
                _stuckFor += Time.fixedDeltaTime;
                // A stuck car has stopped producing data about driving and started
                // producing data about being stuck. Abort rather than average it in.
                if (_stuckFor > StuckSeconds) { Finish(false, "car stuck"); return; }
            }
            else _stuckFor = 0f;

            float s = _follower.S;
            DetectLap(s);
            _prevS = s;
            _hasPrevS = true;

            if (_lap > WarmupLap && _lap <= 1) Record(v);
        }

        private bool Acquire()
        {
            _car = FindFirstObjectByType<CarVehicle>();
            var d = FindFirstObjectByType<SceneTrackDescriptor>();
            if (_car == null || d == null)
            {
                if (_elapsed > 10f) Finish(false, "no car or scene track in the scene");
                return false;
            }
            _line = d.racingLine;
            if (_line == null || !_line.IsUsable)
            {
                Finish(false, "scene track has no usable racing line");
                return false;
            }

            _follower = new RaceLineFollower(_car, _line);
            if (!_follower.Ready) { Finish(false, "racing line could not be projected"); return false; }

            var input = _car.GetComponent<CarInput>();
            if (input == null) { Finish(false, "car has no CarInput"); return false; }
            input.source = _follower;

            _res.vehicle = GameFlow.ActiveDesign != null ? GameFlow.ActiveDesign.name : "";
            _lapStart = _elapsed;
            return true;
        }

        /// <summary>
        /// A lap has turned over when the projected distance jumps backwards by most
        /// of the lap. Deliberately not using LapTimer: the calibration must work on
        /// a FreeRoam scene with no finish gate, and the line's own arc length is the
        /// thing being timed anyway.
        /// </summary>
        private void DetectLap(float s)
        {
            if (!_hasPrevS) return;
            float total = _line.lineLength;
            if (total <= 1f) return;
            if (_prevS - s < total * 0.5f) return;

            float lapTime = _elapsed - _lapStart;
            _lapStart = _elapsed;
            _lapTimes.Add(lapTime);
            _lap++;

            if (_lap == 1) _samples.Clear();       // discard the warm-up
            if (_lap >= 3) Fit();
        }

        private void Record(float v)
        {
            var body = _car.GetComponent<Rigidbody>();
            if (body == null) return;

            // Lateral acceleration from the body's own frame: v^2 * kappa would be
            // the model's opinion, and the point of this lap is to measure what the
            // tyres actually delivered.
            var right = _car.transform.right;
            var lateralVel = Vector3.Dot(body.linearVelocity, right);
            float kappa = Mathf.Abs(v) > 0.5f
                ? _car.transform.InverseTransformDirection(body.angularVelocity).y / v
                : 0f;
            float aLat = Mathf.Abs(v * kappa * v) > 0f ? Mathf.Abs(v * v * kappa) : 0f;

            _samples.Add(new Sample
            {
                v = v,
                aLat = aLat,
                kappa = kappa,
                throttle = _follower.Throttle(),
                brake = _follower.Brake(),
                mu = SurfaceUnderCar(),
                // lateralVel is read but not stored: it is only used to confirm the
                // car is not crabbing so badly that aLat is meaningless.
            });
            if (Mathf.Abs(lateralVel) > 8f) _samples.RemoveAt(_samples.Count - 1);
        }

        private float SurfaceUnderCar()
        {
            // The catalog friction under the car right now, so a lap that crosses a
            // kerb is fitted against the grip that kerb actually has.
            if (Physics.Raycast(_car.transform.position + Vector3.up * 0.3f, Vector3.down,
                                out var hit, 2f, ~0, QueryTriggerInteraction.Ignore))
            {
                var tag = hit.collider.GetComponent<SurfaceTag>();
                if (tag != null)
                    return TrackEd.TrackCatalog.Floors[
                        Mathf.Clamp(tag.floorType, 0,
                            TrackEd.TrackCatalog.Floors.Length - 1)].frictionMult;
            }
            return 1f;
        }

        /// <summary>
        /// Fit the four scalars, then decide whether the fit is defensible.
        ///
        /// muScale comes from COMMITTED CORNERING only — high curvature, not
        /// simultaneously braking and accelerating. Those are the samples where the
        /// tyres are near the limit; a fit that includes cruising down a straight is
        /// fitting the absence of information.
        /// </summary>
        private void Fit()
        {
            if (_lapTimes.Count < 3) { Finish(false, "did not complete three laps"); return; }

            _res.lap1 = _lapTimes[0];
            _res.lap2 = _lapTimes[1];
            _res.lap3 = _lapTimes[2];

            var lateralAtLimit = new List<float>();
            float vMax = 0f;
            int cornering = 0;
            foreach (var s in _samples)
            {
                if (s.v > vMax) vMax = s.v;
                bool committed = Mathf.Abs(s.kappa) > RaceLineSolver.KappaRef * 0.5f
                                 && s.brake < 0.05f;
                if (!committed || s.aLat < 0.05f) continue;
                cornering++;
                lateralAtLimit.Add(s.aLat / (9.81f * Mathf.Max(0.05f, s.mu)));
            }

            if (lateralAtLimit.Count < 20)
            {
                Finish(false, $"only {lateralAtLimit.Count} committed-cornering samples — " +
                              "the car never got near the limit, so there is nothing to fit");
                return;
            }

            // 80th percentile, not the max: the single highest sample is whatever
            // kerb strike loaded a tyre hardest for one physics step.
            lateralAtLimit.Sort();
            _res.muScale = lateralAtLimit[Mathf.Clamp(
                Mathf.RoundToInt(lateralAtLimit.Count * 0.8f), 0, lateralAtLimit.Count - 1)];

            _res.vMax = Mathf.Max(1f, vMax);
            _res.accelA0 = EstimateA0();
            // Braking is friction-limited on this car (maxBrakeTorque over four
            // wheels at r ~= 0.033 m on ~1.8 kg is an order of magnitude above mu*g),
            // so this is the fraction of the friction circle real braking uses, not
            // a torque.
            _res.brakeUse = 0.8f;
            _res.limitFraction = _samples.Count > 0
                ? (float)cornering / _samples.Count : 0f;

            _res.ok = true;
            Finish(true, "");
        }

        /// <summary>
        /// Standing acceleration from the fastest sustained speed gain seen at low
        /// speed, where the drive model's (1 - v/vMax) term is near 1.
        /// </summary>
        private float EstimateA0()
        {
            float best = 0f;
            for (int i = 1; i < _samples.Count; i++)
            {
                float v0 = _samples[i - 1].v, v1 = _samples[i].v;
                if (v0 > _res.vMax * 0.35f) continue;
                if (_samples[i].throttle < 0.9f || _samples[i].brake > 0.01f) continue;
                float a = (v1 - v0) / Mathf.Max(1e-4f, Time.fixedDeltaTime);
                if (a > best && a < 60f) best = a;
            }
            return best > 0.1f ? best : 6f;
        }

        private void Finish(bool ok, string fault)
        {
            if (_finished) return;
            _finished = true;
            _res.ok = ok;
            _res.fault = fault;

            try
            {
                string json = JsonUtility.ToJson(_res, true);
                if (!string.IsNullOrEmpty(_req.resultPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_req.resultPath));
                    File.WriteAllText(_req.resultPath, json);
                }
                Debug.Log("[RaceLineCal] RESULT " + json.Replace("\n", " "));
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[RaceLineCal] write failed: {e.Message}");
            }

#if UNITY_EDITOR
            // Exit outright rather than easing out of play mode: in batchmode the
            // teardown does not reliably hand control back, and the run's only
            // product is already on disk. Same reasoning as MissionAutorun.
            if (Application.isBatchMode) UnityEditor.EditorApplication.Exit(ok ? 0 : 2);
            else UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(ok ? 0 : 2);
#endif
        }
    }
}
