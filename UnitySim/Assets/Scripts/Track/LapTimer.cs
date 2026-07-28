using System;
using System.Collections.Generic;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Finish-line lap timer. Attach to a trigger volume across the start/finish
    /// line. Lap state is tracked <b>per car</b> (a <see cref="LapTracker"/> per
    /// <see cref="CarVehicle"/>), so split-screen players race independently:
    /// each car's first crossing arms its own timer; each later crossing records
    /// a lap. Rapid re-crossings within <see cref="minLapTime"/> are ignored.
    ///
    /// Optional ordered checkpoints: when <see cref="CheckpointCount"/> &gt; 0,
    /// a car's finish crossing only counts once that car has hit every checkpoint
    /// in order. With 0 checkpoints behavior is the classic line timer.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class LapTimer : MonoBehaviour
    {
        public float minLapTime = 3f;

        /// <summary>Number of ordered checkpoints on this track (0 = none).</summary>
        public int CheckpointCount { get; set; }

        /// <summary>Draw the classic bottom-right lap box (disabled by split-screen HUD).</summary>
        public bool showDefaultHud = true;

        /// <summary>Fired whenever a car completes a counted lap.</summary>
        public event Action<CarVehicle, LapTracker> LapCompleted;

        private readonly Dictionary<CarVehicle, LapTracker> _trackers =
            new Dictionary<CarVehicle, LapTracker>();

        // ---- single-player convenience surface (first/only tracker) ----------

        private LapTracker First
        {
            get
            {
                foreach (var t in _trackers.Values) return t;
                return null;
            }
        }

        public int LapCount => First?.LapCount ?? 0;
        public float CurrentLap => First?.CurrentLap ?? 0f;
        public float LastLap => First?.LastLap ?? 0f;
        public float BestLap => First != null && First.HasBest ? First.BestLap : float.PositiveInfinity;
        public int NextCheckpoint => First?.NextCheckpoint ?? 0;

        // ---- per-car API ------------------------------------------------------

        /// <summary>This car's lap state (created on first use).</summary>
        public LapTracker GetTracker(CarVehicle car)
        {
            if (!_trackers.TryGetValue(car, out var t))
            {
                t = new LapTracker();
                _trackers[car] = t;
            }
            return t;
        }

        /// <summary>Clear ALL cars' lap state (pause-menu Restart).</summary>
        public void ResetTimer() => _trackers.Clear();

        /// <summary>Clear one car's lap state (per-player respawn).</summary>
        public void ResetTimer(CarVehicle car)
        {
            if (car != null) _trackers.Remove(car);
        }

        /// <summary>Called by <see cref="Checkpoint"/>; out-of-order hits are ignored.</summary>
        public void NotifyCheckpoint(CarVehicle car, int index)
        {
            if (car == null) return;
            var t = GetTracker(car);
            if (!t.Armed) return;
            if (index == t.NextCheckpoint && t.NextCheckpoint < CheckpointCount)
                t.NextCheckpoint++;
        }

        /// <summary>Restore a car's lap state from a session snapshot.</summary>
        public void RestoreTracker(CarVehicle car, LapTracker state)
        {
            if (car == null || state == null) return;
            state.lastCrossTime = -999f;
            _trackers[car] = state;
        }

        private void Reset()
        {
            var col = GetComponent<Collider>();
            if (col != null) col.isTrigger = true;
        }

        private void Update()
        {
            foreach (var t in _trackers.Values)
                if (t.Armed) t.CurrentLap += Time.deltaTime;
        }

        private void OnTriggerEnter(Collider other)
        {
            // The car's WheelColliders/visuals are children; look up the parent.
            var car = other.GetComponentInParent<CarVehicle>();
            if (car == null) return;

            var t = GetTracker(car);
            if (Time.time - t.lastCrossTime < minLapTime) return;
            t.lastCrossTime = Time.time;

            if (!t.Armed)
            {
                t.Armed = true;
                t.CurrentLap = 0f;
                t.LapCount = 0;
                t.NextCheckpoint = 0;
                return;
            }

            // With checkpoints, a lap only counts once they've all been hit in order.
            if (CheckpointCount > 0 && t.NextCheckpoint < CheckpointCount) return;
            t.NextCheckpoint = 0;

            t.LastLap = t.CurrentLap;
            if (!t.HasBest || t.LastLap < t.BestLap) t.BestLap = t.LastLap;
            t.LapCount++;
            t.CurrentLap = 0f;

            LapCompleted?.Invoke(car, t);
        }

        private static string Fmt(float t) =>
            float.IsInfinity(t) || t < 0f ? "--:--.---" : $"{(int)(t / 60f):00}:{t % 60f:00.000}";

        private void OnGUI()
        {
            if (!showDefaultHud) return;
            UI.UIScale.Begin();
            var t = First;
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleLeft, fontSize = 13 };
            bool cps = CheckpointCount > 0;
            float w = 210f, h = cps ? 110f : 92f;
            var rect = new Rect(UI.UIScale.W - w - 10f, UI.UIScale.H - h - 10f, w, h);
            string body =
                $"Lap: {(t?.LapCount ?? 0)}\n" +
                $"Current: {(t != null && t.Armed ? Fmt(t.CurrentLap) : "cross line to start")}\n" +
                $"Last:    {Fmt(t?.LastLap ?? 0f)}\n" +
                $"Best:    {(t != null && t.HasBest ? Fmt(t.BestLap) : "--:--.---")}" +
                (cps ? $"\nCP:      {(t?.NextCheckpoint ?? 0)}/{CheckpointCount}" : "");
            GUI.Box(rect, body, style);
            UI.UIScale.End();
        }
    }
}
