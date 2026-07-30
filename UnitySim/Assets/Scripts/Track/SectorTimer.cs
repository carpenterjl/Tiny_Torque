using System.Collections.Generic;
using AIHWSim.Arcade;
using AIHWSim.Core;
using AIHWSim.Telemetry;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Records per-sector split times by projecting each car onto the racing line.
    ///
    /// <b>Deliberately does NOT touch <c>LapTracker</c>.</b> LapTracker is
    /// <c>[Serializable]</c>, round-trips through JsonUtility in session snapshots
    /// and rides the LAN protocol; adding a <c>float[] SectorTimes</c> to it would be
    /// a snapshot change and a wire change for something that is a presentation
    /// concern. Splits are recomputed locally from a position that already
    /// replicates, so a remote car's splits cost nothing extra and no protocol moves.
    ///
    /// The tradeoff, stated: local recomputation drifts if position replication is
    /// lossy. Put splits on LapTracker the day championship scoring needs
    /// authoritative remote sector times — not before.
    /// </summary>
    [RequireComponent(typeof(LapTimer))]
    public sealed class SectorTimer : MonoBehaviour
    {
        /// <summary>Boundary crossings are checked at 10 Hz. TrackSpine's own doc
        /// calls that cheap across a full grid, and a sector boundary is not a
        /// thing that needs sub-frame precision.</summary>
        private const float SampleHz = 10f;

        public TrackSectorSet sectors;

        private LapTimer _timer;
        private TrackSpine _spine;
        private TelemetryHub _hub;
        private float _next;

        private sealed class CarState
        {
            public int hint = -1;
            public int sector = -1;
            public float lapTime;
            public float sectorEntryTime;
            public float[] splits;
            public float[] best;
        }

        private readonly Dictionary<CarVehicle, CarState> _cars =
            new Dictionary<CarVehicle, CarState>();

        /// <summary>Current sector splits for a car, or null if it has none yet.</summary>
        public float[] SplitsFor(CarVehicle car) =>
            _cars.TryGetValue(car, out var st) ? st.splits : null;

        private void Awake()
        {
            _timer = GetComponent<LapTimer>();
            if (sectors != null && sectors.line != null && sectors.line.IsUsable)
                _spine = TrackSpine.From(sectors.line.points, sectors.line.closed);

            if (_spine == null || sectors == null || sectors.sectors.Length == 0)
            {
                enabled = false;
                return;
            }

            // The hub belongs to the SimulationRunner, and channels must be
            // registered before the first Commit or the CSV's column layout shifts
            // under the logger. Awake is early enough; a later registration would
            // appear as a column that starts halfway down the file.
            var runner = FindFirstObjectByType<SimulationRunner>();
            _hub = runner != null ? runner.Hub : null;
            if (_hub != null)
            {
                // Three channels, NOT one per sector. Sector count is track-dependent,
                // and CsvLogger freezes its column layout from the registered set — a
                // per-sector channel would change the CSV's columns per track, which is
                // exactly what the stable-layout contract forbids.
                _hub.RegisterChannel("race/sector");
                _hub.RegisterChannel("race/sector_time");
                _hub.RegisterChannel("race/sector_delta");
            }

            _timer.LapCompleted += OnLapCompleted;
        }

        private void OnDestroy()
        {
            if (_timer != null) _timer.LapCompleted -= OnLapCompleted;
        }

        private void OnLapCompleted(CarVehicle car, LapTracker tracker)
        {
            if (car == null || !_cars.TryGetValue(car, out var st)) return;
            // Roll over: the last sector closes at the line, and the next lap starts
            // its clock from here rather than from the next boundary crossing.
            CloseSector(st, st.lapTime);
            st.lapTime = 0f;
            st.sectorEntryTime = 0f;
            st.sector = -1;
        }

        private void Update()
        {
            if (_spine == null) return;

            foreach (var kv in _cars) kv.Value.lapTime += Time.deltaTime;

            if (Time.time < _next) return;
            _next = Time.time + 1f / SampleHz;

            foreach (var car in FindObjectsByType<CarVehicle>(FindObjectsSortMode.None))
            {
                if (car == null) continue;
                if (!_cars.TryGetValue(car, out var st))
                {
                    st = new CarState
                    {
                        splits = new float[sectors.sectors.Length],
                        best = new float[sectors.sectors.Length],
                    };
                    for (int i = 0; i < st.best.Length; i++) st.best[i] = -1f;
                    _cars[car] = st;
                }

                float s = _spine.Project(car.transform.position, ref st.hint);
                int now = sectors.SectorAt(s);
                if (now < 0) continue;

                if (st.sector < 0) { st.sector = now; st.sectorEntryTime = st.lapTime; continue; }

                // Accept a boundary only when it is the NEXT one, wrapping on a
                // closed lap. A car that spins and re-crosses backwards, or rejoins
                // mid-sector, must not stamp a split it did not drive — the same
                // monotonic discipline LapTimer.NotifyCheckpoint uses.
                int expected = (st.sector + 1) % sectors.sectors.Length;
                if (now != expected) continue;

                CloseSector(st, st.lapTime);
                st.sector = now;
                st.sectorEntryTime = st.lapTime;
                Publish(st, s);
            }
        }

        private void CloseSector(CarState st, float atLapTime)
        {
            if (st.sector < 0 || st.sector >= st.splits.Length) return;
            float split = Mathf.Max(0f, atLapTime - st.sectorEntryTime);
            st.splits[st.sector] = split;
            if (st.best[st.sector] < 0f || split < st.best[st.sector])
                st.best[st.sector] = split;
        }

        private void Publish(CarState st, float s)
        {
            if (_hub == null) return;
            float running = st.lapTime - st.sectorEntryTime;
            float target = st.sector >= 0 && st.sector < sectors.sectors.Length
                ? sectors.sectors[st.sector].targetSec : 0f;
            _hub.SetValue("race/sector", st.sector);
            _hub.SetValue("race/sector_time", running);
            _hub.SetValue("race/sector_delta", running - target);
        }
    }
}
