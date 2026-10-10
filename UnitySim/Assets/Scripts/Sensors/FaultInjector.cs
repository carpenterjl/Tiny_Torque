using System;
using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>What a fault does (VAL-11). The same catalogue as the host-side
    /// injector the firmware's unit test uses (Controllers/tests/tt_fault.h).</summary>
    public enum FaultKind
    {
        None = 0,
        Stale,      // a sensor stops updating: value and stamp held (I²C NACK, unplugged)
        Stuck,      // a sensor's value freezes; its samples keep arriving
        Nan,        // a sensor reads NaN
        EncGlitch,  // an encoder's count jumps by `mag`, every `periodS` (0 = once)
        DrvTrip,    // a driver trips: MotorPart Fault* bits `mag` (bridge off)
        DrvHot,     // a driver's winding reads `mag` °C hotter
        Brownout,   // the pack's terminal voltage held at `mag` V
        UwbLoss,    // anchor `target` stops replying ("all" = every anchor)
        RcLink,     // the radio link drops (the receiver goes to failsafe after its hold)
        RcKill,     // the kill switch is thrown
        RcDisarm,   // the arm switch is turned off
        CoreStall,  // the firmware stops running: no step, no command frames
        McuReset,   // the MCU resets: silent for `durS`, then boots again
        ImuNan,     // the car's IMU (gyro[] / accel[]) reads NaN
        TipOver,    // the car is rolled `mag` degrees onto its side
    }

    /// <summary>One fault in a run. Times are from the run's start, or from the
    /// mission entering <see cref="afterPhase"/> when that is set.</summary>
    [Serializable]
    public class FaultSpec
    {
        public FaultKind kind;
        /// <summary>The sensor part's name (Stale, Stuck, Nan, EncGlitch,
        /// DrvTrip, DrvHot), or the anchor id / "all" (UwbLoss).</summary>
        public string target = "";
        public float startS;
        /// <summary>How long it lasts; 0 = to the end of the run.</summary>
        public float durS;
        public float mag;
        public float periodS;
        /// <summary>Mission phase (dbg/state) the start time counts from;
        /// -99 = the run's start.</summary>
        public int afterPhase = -99;

        public override string ToString() =>
            $"{kind}{(string.IsNullOrEmpty(target) ? "" : ":" + target)}" +
            $"@{(afterPhase != -99 ? "p" + afterPhase + "+" : "")}{startS:0.##}s" +
            $"{(durS > 0f ? "/" + durS.ToString("0.##") + "s" : "")}" +
            $"{(mag != 0f ? " " + mag.ToString("0.###") : "")}";

        /// <summary>Parse "kind[:target]@[pN+]start[/dur][ mag][ every period]",
        /// e.g. "RcKill@p4+1", "Stale:imu@3/0.5", "DrvTrip:motor_rl@p4+1 1".</summary>
        public static FaultSpec Parse(string s)
        {
            var f = new FaultSpec();
            s = s.Trim();
            string[] words = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string head = words[0];
            int at = head.IndexOf('@');
            string kt = at >= 0 ? head.Substring(0, at) : head;
            string when = at >= 0 ? head.Substring(at + 1) : "0";
            int colon = kt.IndexOf(':');
            f.kind = (FaultKind)Enum.Parse(typeof(FaultKind), colon >= 0 ? kt.Substring(0, colon) : kt, true);
            if (colon >= 0) f.target = kt.Substring(colon + 1);
            if (when.StartsWith("p", StringComparison.OrdinalIgnoreCase))
            {
                int plus = when.IndexOf('+');
                f.afterPhase = int.Parse(plus >= 0 ? when.Substring(1, plus - 1) : when.Substring(1));
                when = plus >= 0 ? when.Substring(plus + 1) : "0";
            }
            int slash = when.IndexOf('/');
            f.startS = F(slash >= 0 ? when.Substring(0, slash) : when);
            if (slash >= 0) f.durS = F(when.Substring(slash + 1));
            for (int i = 1; i < words.Length; i++)
            {
                if (words[i] == "every" && i + 1 < words.Length) { f.periodS = F(words[++i]); continue; }
                f.mag = F(words[i]);
            }
            return f;
        }

        private static float F(string s) =>
            float.Parse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Fault injection in the sim (VAL-11). Each fault acts at its physical
    /// source, so the firmware meets it the way it would on the car:
    ///
    ///  - sensor faults on the rig's output, after the part's own model and
    ///    latency (<see cref="ApplySensor"/>): a stale part keeps its old value
    ///    AND its old stamp; a stuck one keeps its value under fresh stamps;
    ///  - driver trips and temperatures on the <see cref="MotorPart"/>, which
    ///    then behaves as a tripped driver does (bridge off);
    ///  - a brownout on the pack (<see cref="CarVehicle.FaultPackVolts"/>),
    ///    which the drivers and the pack sense both see;
    ///  - RC faults on the <see cref="RcReceiverSensor"/>'s link and switches;
    ///  - a core stall or an MCU reset in the scheduler
    ///    (<see cref="Core.SimulationRunner"/>): no firmware step, no command
    ///    frames, so the drivers' own watchdogs act;
    ///  - a tip-over by rolling the body.
    ///
    /// Owned by the SimulationRunner, which ticks it once per control step.
    /// Inert with no faults.
    /// </summary>
    public sealed class FaultInjector
    {
        public readonly List<FaultSpec> Faults = new List<FaultSpec>();

        private sealed class Live
        {
            public FaultSpec spec;
            public double t0 = double.NaN;     // resolved start, sim s
            public bool active, done;
            public float[] held;
            public uint heldSeq;
            public uint heldUs;
            public int glitchOffset;
            public double nextGlitch = double.NaN;
        }

        private readonly List<Live> _live = new List<Live>();
        private SensorRig _rig;
        private CarVehicle _car;
        private double _now;
        private int _phase = -99;
        private bool _resetPending;

        public bool Any => Faults.Count > 0;

        /// <summary>The firmware does not run this tick.</summary>
        public bool CoreStalled { get; private set; }

        /// <summary>The built-in IMU reads NaN this tick.</summary>
        public bool ImuNan { get; private set; }

        /// <summary>Earliest start among the faults that have started (sim s),
        /// or NaN.</summary>
        public double FirstStart
        {
            get
            {
                double t = double.NaN;
                foreach (var l in _live)
                    if (!double.IsNaN(l.t0) && l.t0 <= _now && (double.IsNaN(t) || l.t0 < t)) t = l.t0;
                return t;
            }
        }

        public void Bind(SensorRig rig, CarVehicle car)
        {
            _rig = rig;
            _car = car;
        }

        /// <summary>Replace the fault list and forget all fault state (a new run).</summary>
        public void Set(IEnumerable<FaultSpec> faults)
        {
            Restore();
            Faults.Clear();
            if (faults != null) Faults.AddRange(faults);
            _live.Clear();
            foreach (var f in Faults) _live.Add(new Live { spec = f });
            _phase = -99;
            CoreStalled = ImuNan = false;
            _resetPending = false;
        }

        /// <summary>A due MCU reset, once: the runner re-inits the firmware.</summary>
        public bool TakeMcuReset()
        {
            bool r = _resetPending;
            _resetPending = false;
            return r;
        }

        /// <summary>Once per control step, before the sensors are read.
        /// <paramref name="phase"/> is the mission phase the firmware last
        /// reported (dbg/state).</summary>
        public void Tick(double simTime, int phase)
        {
            _now = simTime;
            _phase = phase;
            CoreStalled = false;
            ImuNan = false;
            if (_live.Count == 0) return;

            float packV = 0f;
            var motorBits = new Dictionary<MotorPart, int>();
            var motorHot = new Dictionary<MotorPart, float>();
            RcReceiverSensor rc = FindRc();
            bool rcLost = false, rcKill = false, rcDisarm = false;

            foreach (var l in _live)
            {
                var f = l.spec;
                if (double.IsNaN(l.t0))
                {
                    if (f.afterPhase == -99) l.t0 = f.startS;
                    else if (phase == f.afterPhase) l.t0 = simTime + f.startS;
                }
                bool was = l.active;
                l.active = !double.IsNaN(l.t0) && simTime >= l.t0 - 1e-9 &&
                           (f.durS <= 0f || simTime < l.t0 + f.durS - 1e-9);
                if (was && !l.active)
                {
                    l.done = true;
                    // The dead time is over: the MCU boots on this tick.
                    if (f.kind == FaultKind.McuReset) _resetPending = true;
                }
                if (!l.active) { l.held = null; continue; }

                switch (f.kind)
                {
                    case FaultKind.Brownout:
                        packV = packV > 0f ? Mathf.Min(packV, f.mag) : f.mag;
                        break;
                    case FaultKind.DrvTrip:
                    case FaultKind.DrvHot:
                        foreach (var m in Motors(f.target))
                        {
                            if (f.kind == FaultKind.DrvTrip)
                                motorBits[m] = (motorBits.TryGetValue(m, out int b) ? b : 0) | (int)f.mag;
                            else motorHot[m] = f.mag;
                        }
                        break;
                    case FaultKind.RcLink: rcLost = true; break;
                    case FaultKind.RcKill: rcKill = true; break;
                    case FaultKind.RcDisarm: rcDisarm = true; break;
                    case FaultKind.CoreStall: CoreStalled = true; break;
                    case FaultKind.McuReset:
                        CoreStalled = true;
                        break;
                    case FaultKind.ImuNan: ImuNan = true; break;
                    case FaultKind.TipOver:
                        if (!was && _car != null) _car.FaultRoll(f.mag != 0f ? f.mag : 100f);
                        break;
                }
            }

            if (_car != null) _car.FaultPackVolts = packV;
            if (_rig != null)
                foreach (var m in _rig.Motors)
                {
                    m.InjectedFaults = motorBits.TryGetValue(m, out int b) ? b : 0;
                    m.InjectedTempOffsetC = motorHot.TryGetValue(m, out float h) ? h : 0f;
                }
            if (rc != null)
            {
                rc.linkLost = rcLost;
                rc.killSwitch = rcKill;
                rc.armSwitch = !rcDisarm;
            }
        }

        /// <summary>
        /// The rig calls this for each part after its output for the control
        /// tick is written: what the firmware will read, and its stamp.
        /// </summary>
        public void ApplySensor(SensorComponent s, float[] flat, int off, ref SensorStamp stamp)
        {
            if (_live.Count == 0) return;
            int n = s.DataCount;
            foreach (var l in _live)
            {
                var f = l.spec;
                switch (f.kind)
                {
                    case FaultKind.Stale:
                    case FaultKind.Stuck:
                        if (!l.active || !Named(s, f.target)) break;
                        if (l.held == null)
                        {
                            l.held = new float[n];
                            Array.Copy(flat, off, l.held, 0, n);
                            l.heldSeq = stamp.seq;
                            l.heldUs = stamp.t_sample_us;
                        }
                        Array.Copy(l.held, 0, flat, off, n);
                        if (f.kind == FaultKind.Stale) { stamp.seq = l.heldSeq; stamp.t_sample_us = l.heldUs; }
                        break;
                    case FaultKind.Nan:
                        if (!l.active || !Named(s, f.target)) break;
                        for (int i = 0; i < n; i++) flat[off + i] = float.NaN;
                        break;
                    case FaultKind.EncGlitch:
                        if (!(s is WheelEncoderSensor enc) || !Named(s, f.target)) break;
                        if (l.active)
                        {
                            if (double.IsNaN(l.nextGlitch)) l.nextGlitch = l.t0;
                            if (_now + 1e-9 >= l.nextGlitch)
                            {
                                l.glitchOffset += (int)f.mag;
                                l.nextGlitch = f.periodS > 0f ? l.nextGlitch + f.periodS : double.PositiveInfinity;
                            }
                        }
                        if (l.glitchOffset != 0)
                        {
                            // The jump stays in the counter, as a miscount would.
                            double v = flat[off + 1] + l.glitchOffset;
                            if (enc.wrap > 0f) { v %= enc.wrap; if (v < 0) v += enc.wrap; }
                            flat[off + 1] = (float)v;
                        }
                        break;
                    case FaultKind.UwbLoss:
                        if (!l.active || s.Type != SensorType.Uwb) break;
                        bool all = string.IsNullOrEmpty(f.target) || f.target == "all";
                        if (all || (int.TryParse(f.target, out int id) && Mathf.RoundToInt(flat[off]) == id))
                        {
                            flat[off] = -1f;
                            for (int i = 1; i < n; i++) flat[off + i] = 0f;
                        }
                        break;
                }
            }
        }

        /// <summary>Clear every effect on the car and its parts.</summary>
        public void Restore()
        {
            if (_car != null) _car.FaultPackVolts = 0f;
            if (_rig != null)
            {
                foreach (var m in _rig.Motors) { m.InjectedFaults = 0; m.InjectedTempOffsetC = 0f; }
                var rc = FindRc();
                if (rc != null) { rc.linkLost = false; rc.killSwitch = false; rc.armSwitch = true; }
            }
            CoreStalled = ImuNan = false;
        }

        private static bool Named(SensorComponent s, string target) =>
            string.Equals(s.sensorName, target, StringComparison.OrdinalIgnoreCase);

        private IEnumerable<MotorPart> Motors(string target)
        {
            if (_rig == null) yield break;
            foreach (var m in _rig.Motors)
                if (string.IsNullOrEmpty(target) || target == "all" || Named(m, target)) yield return m;
        }

        private RcReceiverSensor FindRc()
        {
            if (_rig == null) return null;
            foreach (var s in _rig.Sensors)
                if (s is RcReceiverSensor rc) return rc;
            return null;
        }
    }
}
