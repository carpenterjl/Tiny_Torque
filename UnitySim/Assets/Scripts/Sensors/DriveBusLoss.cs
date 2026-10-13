using System;
using AIHWSim.Bridge;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Frame loss on the drive bus (SIM_TO_REAL_PLAN §9.2, "CAN frame-loss
    /// bursts"; VAL-13). On the car each wheel node reports its current,
    /// speed and encoder count and takes its command over CAN-FD; a burst
    /// of lost frames leaves the firmware with the node's last report under
    /// its old stamp, and the node with the last command it received. Here,
    /// per control tick, each node starts a burst of 1..<see cref="BurstMax"/>
    /// ticks with the probability that makes <c>frac</c> of all ticks lost;
    /// through it the node's encoder and drive feedback are held (value and
    /// stamp, as a stale part) and its actuator slot keeps the previous
    /// tick's command. Seeded from the run's noise seed, so a campaign run
    /// reproduces it. Inert at frac 0.
    /// </summary>
    public sealed class DriveBusLoss
    {
        /// <summary>Longest burst, ticks: 40 ms at 100 Hz, under the safety
        /// layer's 50 ms stale limit, so the knob measures degradation rather
        /// than the trip.</summary>
        public const int BurstMax = 4;

        private readonly float _frac;
        private readonly System.Random _rng;
        private readonly int[] _left = new int[4];
        private readonly float[][] _last = new float[64][];
        private readonly uint[] _lastSeq = new uint[64], _lastUs = new uint[64];
        private readonly bool[] _have = new bool[64];
        private readonly float[] _lastCmd = new float[8];
        private bool _haveCmd;

        public DriveBusLoss(float frac, int seed)
        {
            _frac = Mathf.Clamp01(frac);
            _rng = new System.Random(unchecked(seed * 1181783497 ^ 0x5A17));
        }

        public bool Any => _frac > 0f;

        /// <summary>Wheel node <paramref name="wheel"/>'s frames are lost this tick.</summary>
        public bool Lost(int wheel) => wheel >= 0 && wheel < 4 && _left[wheel] > 0;

        /// <summary>Once per control tick, before the sensors are read.</summary>
        public void Tick()
        {
            if (_frac <= 0f) return;
            double pStart = _frac / (0.5 * (1 + BurstMax));
            for (int w = 0; w < 4; w++)
            {
                // A burst ends with a tick that gets through: two never
                // merge, so a node is silent for 40 ms at most and the
                // firmware's 50 ms stale limit is the fault suite's case.
                if (_left[w] > 0) { _left[w]--; continue; }
                if (_rng.NextDouble() < pStart) _left[w] = 1 + _rng.Next(BurstMax);
            }
        }

        /// <summary>The rig calls this for each part after its output for the
        /// tick is written: a lost node's encoder and drive feedback read as
        /// the last frame that arrived, stamp included.</summary>
        public void ApplySensor(int index, SensorType type, int wheel, float[] flat, int off, int n,
                                ref SensorStamp stamp)
        {
            if (index < 0 || index >= _last.Length || n <= 0) return;
            if (type != SensorType.Encoder && type != SensorType.Motor && type != SensorType.FocFb) return;
            if (_last[index] == null || _last[index].Length != n) { _last[index] = new float[n]; _have[index] = false; }
            if (Lost(wheel) && _have[index])
            {
                Array.Copy(_last[index], 0, flat, off, n);
                stamp.seq = _lastSeq[index];
                stamp.t_sample_us = _lastUs[index];
                return;
            }
            Array.Copy(flat, off, _last[index], 0, n);
            _lastSeq[index] = stamp.seq;
            _lastUs[index] = stamp.t_sample_us;
            _have[index] = true;
        }

        /// <summary>The command vector as the nodes receive it: a lost node
        /// keeps the last command that reached it. <paramref name="slotOfWheel"/>
        /// is each wheel's actuator slot (-1 = none).</summary>
        public void ApplyCommands(float[] cmd, int[] slotOfWheel)
        {
            if (cmd == null || slotOfWheel == null) return;
            if (!_haveCmd) { Array.Copy(cmd, _lastCmd, Math.Min(cmd.Length, _lastCmd.Length)); _haveCmd = true; }
            for (int w = 0; w < 4 && w < slotOfWheel.Length; w++)
            {
                int s = slotOfWheel[w];
                if (s < 0 || s >= cmd.Length || s >= _lastCmd.Length) continue;
                if (Lost(w)) cmd[s] = _lastCmd[s];
                else _lastCmd[s] = cmd[s];
            }
        }
    }
}
