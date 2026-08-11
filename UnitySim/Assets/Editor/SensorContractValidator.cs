using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Sensors;
using AIHWSim.Sensors.Signals;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// [SENS] — the sensor-contract gate. Instantiates each self-contained
    /// SensorComponent on a bare GameObject and asserts the base-class
    /// contract the rig and the ABI both rest on: FieldNames.Count ==
    /// DataCount, Sample writes exactly DataCount floats inside its slice and
    /// none outside it, and with zero noise two consecutive samples are
    /// bit-identical (inert-by-construction, the [PHYS] precondition). Also
    /// exercises the signal fields: strongest-K ordering, the id tie-break,
    /// empty-slot sentinels, and call-to-call determinism.
    ///
    /// Vehicle-bound sensors (motor, encoder, suspension, battery, camera,
    /// IMU) need a built car to sample and are covered by the physics gate and
    /// play-mode instead — this gate is for the contract every sensor states
    /// and the new environmental sensors implement standalone.
    ///
    /// Batch: Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt;
    ///   -executeMethod AIHWSim.EditorTools.SensorContractValidator.Report
    /// </summary>
    public static class SensorContractValidator
    {
        private const string Tag = "[SENS]";

        private static readonly List<string> Fails = new List<string>();
        private static int _checks;

        [MenuItem("Tools/AIHWSim/Validate Sensors [SENS]", priority = 404)]
        public static void RunFromMenu() => Run(exitWhenDone: false);

        public static void Report() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            Fails.Clear();
            _checks = 0;

            CheckContract<TofSensor>();
            CheckContract<ColorSensor>();
            CheckContract<MagSensor>();
            CheckContract<BumpSensor>();
            CheckContract<RfSensor>();
            CheckContract<LedPart>();
            CheckAbiTags();
            CheckSoundField();
            CheckRfField();
            CheckRfSensorSlots();

            foreach (string f in Fails) Debug.LogError($"{Tag} FAIL {f}");
            string line = Fails.Count == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks)"
                : $"{Tag} RESULT {Fails.Count} FAILED of {_checks} checks";
            if (Fails.Count == 0) Debug.Log(line); else Debug.LogError(line);

            if (exitWhenDone) EditorApplication.Exit(Fails.Count == 0 ? 0 : 1);
        }

        private static void True(string what, bool cond)
        {
            _checks++;
            if (!cond) Fails.Add(what);
        }

        // ---- the SensorComponent contract ---------------------------------

        private static void CheckContract<T>() where T : SensorComponent
        {
            string n = typeof(T).Name;
            var go = new GameObject("sens_probe");
            try
            {
                var s = go.AddComponent<T>();
                s.Bind(null, go.transform);

                True($"{n}: FieldNames.Count == DataCount",
                    s.FieldNames.Count == s.DataCount);
                True($"{n}: DataCount > 0", s.DataCount > 0);

                // NaN sentinels around the slice prove Sample writes exactly
                // its DataCount floats and nothing else.
                const int pad = 2;
                int count = s.DataCount;
                var buf = new float[pad + count + pad];
                for (int i = 0; i < buf.Length; i++) buf[i] = float.NaN;
                s.Sample(0.01f, buf, pad);

                bool inSlice = true, outSlice = true;
                for (int i = 0; i < count; i++)
                    if (float.IsNaN(buf[pad + i])) inSlice = false;
                for (int i = 0; i < pad; i++)
                    if (!float.IsNaN(buf[i]) || !float.IsNaN(buf[buf.Length - 1 - i]))
                        outSlice = false;
                True($"{n}: Sample writes all {count} floats of its slice", inSlice);
                True($"{n}: Sample writes nothing outside its slice", outSlice);

                // Zero noise (the default) ⇒ bit-identical resamples.
                var a = new float[count];
                var b = new float[count];
                s.Sample(0.01f, a, 0);
                s.Sample(0.01f, b, 0);
                bool identical = true;
                for (int i = 0; i < count; i++)
                    if (!a[i].Equals(b[i])) identical = false;
                True($"{n}: zero-noise samples are bit-identical", identical);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static void CheckAbiTags()
        {
            // The C# enum mirrors controller_api.h; numbers are the ABI and
            // append-only. Freezing them here catches an accidental renumber.
            True("SensorType.Color == 8", (int)SensorType.Color == 8);
            True("SensorType.Rf == 9", (int)SensorType.Rf == 9);
            True("SensorType.Mag == 10", (int)SensorType.Mag == 10);
            True("SensorType.Bump == 11", (int)SensorType.Bump == 11);
            True("SensorType.Led == 12", (int)SensorType.Led == 12);
        }

        // ---- signal fields -------------------------------------------------

        private sealed class FakeSound : ISoundEmitter
        {
            public Vector3 pos; public float loud; public float tone;
            public bool SoundActive => true;
            public Vector3 SoundPosition => pos;
            public float Loudness => loud;
            public float ToneHz => tone;
            public int SoundEmitterId { get; set; }
        }

        private sealed class FakeRf : IRfEmitter
        {
            public Vector3 pos; public float dbm; public int id; public bool active = true;
            public bool RfActive => active;
            public Vector3 RfPosition => pos;
            public float TxPowerDbm => dbm;
            public int BeaconId => id;
        }

        private static void CheckSoundField()
        {
            SoundField.Reset();
            try
            {
                var near = new FakeSound { pos = new Vector3(1f, 0f, 0f), loud = 1f, tone = 440f };
                var far = new FakeSound { pos = new Vector3(10f, 0f, 0f), loud = 1f, tone = 880f };
                var twin = new FakeSound { pos = new Vector3(0f, 0f, 10f), loud = 1f, tone = 220f };
                SoundField.Register(near);
                SoundField.Register(far);
                SoundField.Register(twin);

                var slots = new SoundReading[3];
                int found = SoundField.StrongestAt(Vector3.zero, 3, slots);
                True("SoundField: three emitters found", found == 3);
                True("SoundField: nearest is slot 0", slots[0].id == near.SoundEmitterId);
                True("SoundField: slot 0 tone follows the emitter",
                    Mathf.Approximately(slots[0].toneHz, 440f));
                // far and twin are equidistant ⇒ equal level ⇒ tie-break by id
                // ascending (registration order).
                True("SoundField: equal levels tie-break by id ascending",
                    slots[1].id == far.SoundEmitterId && slots[2].id == twin.SoundEmitterId);

                // Determinism: same query, same answer.
                var again = new SoundReading[3];
                SoundField.StrongestAt(Vector3.zero, 3, again);
                bool same = true;
                for (int i = 0; i < 3; i++)
                    if (again[i].id != slots[i].id || !again[i].level.Equals(slots[i].level))
                        same = false;
                True("SoundField: StrongestAt is deterministic call-to-call", same);

                // Near clamp: on top of an emitter, level == loudness.
                True("SoundField: 1 m near clamp",
                    Mathf.Approximately(SoundField.LevelFrom(near, near.pos), near.loud));

                SoundField.Unregister(near);
                var empty = new SoundReading[3];
                SoundField.StrongestAt(Vector3.zero, 3, empty);
                True("SoundField: unregistered emitter is gone",
                    empty[0].id != near.SoundEmitterId);
            }
            finally
            {
                SoundField.Reset();
            }
        }

        private static void CheckRfField()
        {
            RfField.Reset();
            try
            {
                var a = new FakeRf { pos = new Vector3(0f, 0f, 2f), dbm = 0f, id = 3 };
                var b = new FakeRf { pos = new Vector3(0f, 0f, 8f), dbm = 0f, id = 1 };
                var off = new FakeRf { pos = Vector3.zero, dbm = 0f, id = 9, active = false };
                RfField.Register(a);
                RfField.Register(b);
                RfField.Register(off);

                var slots = new RfReading[3];
                int found = RfField.StrongestAt(Vector3.zero, Vector3.forward, 3, slots);
                True("RfField: two active pings found", found == 2);
                True("RfField: nearest ping is slot 0", slots[0].beaconId == 3);
                True("RfField: rssi orders by distance", slots[0].rssiDbm > slots[1].rssiDbm);
                True("RfField: inactive emitter is silent",
                    slots[0].beaconId != 9 && slots[1].beaconId != 9 && slots[2].beaconId != 9);
                True("RfField: empty slot sentinel",
                    slots[2].beaconId == -1
                    && Mathf.Approximately(slots[2].rssiDbm, RfField.RssiFloorDbm)
                    && slots[2].bearingDeg == 0f);
                // a sits dead ahead: bearing ≈ 0. A target to the RIGHT of
                // forward must read positive.
                True("RfField: dead-ahead bearing is ~0",
                    Mathf.Abs(slots[0].bearingDeg) < 0.01f);
                True("RfField: right-of-forward bearing is positive",
                    RfField.Bearing(Vector3.zero, Vector3.forward, new Vector3(5f, 0f, 5f)) > 0f);

                // Free-space check: 0 dBm at 1 m ⇒ −6.02 dB at 2 m.
                True("RfField: path loss is 20·log10(d)",
                    Mathf.Abs(RfField.RssiFrom(a, Vector3.zero) - (-20f * Mathf.Log10(2f))) < 0.01f);
            }
            finally
            {
                RfField.Reset();
            }
        }

        private static void CheckRfSensorSlots()
        {
            RfField.Reset();
            var go = new GameObject("rf_probe");
            try
            {
                var sensor = go.AddComponent<RfSensor>();
                sensor.Bind(null, go.transform);

                var near = new FakeRf { pos = new Vector3(0f, 0f, 1f), dbm = 0f, id = 7 };
                var mid = new FakeRf { pos = new Vector3(0f, 0f, 4f), dbm = 0f, id = 2 };
                RfField.Register(near);
                RfField.Register(mid);

                var buf = new float[sensor.DataCount];
                sensor.Sample(0.01f, buf, 0);
                True("RfSensor: count reflects audible beacons", buf[0] == 2f);
                True("RfSensor: slot 0 is the nearest beacon", buf[1] == 7f);
                True("RfSensor: slot 1 is the next beacon", buf[4] == 2f);
                True("RfSensor: empty slot id is -1", buf[7] == -1f);

                // The sensor's own emission must not be audible to itself.
                sensor.emitEnabled = true;
                sensor.emitId = 5;
                RfField.Register(sensor); // OnEnable normally does this
                sensor.Sample(0.01f, buf, 0);
                True("RfSensor: excludes its own transmission",
                    buf[1] != 5f && buf[4] != 5f && buf[7] != 5f);
            }
            finally
            {
                Object.DestroyImmediate(go);
                RfField.Reset();
            }
        }
    }
}
