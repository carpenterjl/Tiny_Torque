using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Sensors.Signals
{
    /// <summary>One receiver slot from <see cref="RfField.StrongestAt"/>.
    /// Empty slot sentinel: beaconId = -1, rssiDbm = -100, bearingDeg = 0.</summary>
    public struct RfReading
    {
        public int beaconId;
        public float rssiDbm;
        public float bearingDeg;
    }

    /// <summary>
    /// The simulated RF field, mirroring <see cref="SoundField"/>'s shape:
    /// static registry, insertion-order iteration, mutation-free queries,
    /// Reset() per session from the world sensor host.
    ///
    /// Propagation: free-space path loss —
    /// rssi = txPowerDbm − 20·log10(max(d, 0.05 m) / 1 m), floored at
    /// <see cref="RssiFloorDbm"/>. No occlusion (see IRfEmitter).
    /// </summary>
    public static class RfField
    {
        public const float RssiFloorDbm = -100f;

        private static readonly List<IRfEmitter> _emitters = new List<IRfEmitter>();

        public static IReadOnlyList<IRfEmitter> Emitters => _emitters;

        public static void Register(IRfEmitter e)
        {
            if (e != null && !_emitters.Contains(e)) _emitters.Add(e);
        }

        public static void Unregister(IRfEmitter e)
        {
            if (e != null) _emitters.Remove(e);
        }

        public static void Reset() => _emitters.Clear();

        /// <summary>RSSI of one emitter at a point (free-space path loss).</summary>
        public static float RssiFrom(IRfEmitter e, Vector3 pos)
        {
            if (e == null || !e.RfActive) return RssiFloorDbm;
            float d = Mathf.Max(0.05f, (e.RfPosition - pos).magnitude);
            float rssi = e.TxPowerDbm - 20f * Mathf.Log10(d);
            return Mathf.Max(RssiFloorDbm, rssi);
        }

        /// <summary>
        /// Strongest-k pings at a point, sorted by RSSI descending with
        /// beaconId ascending as the deterministic tie-break. Bearing is the
        /// signed yaw (−180..+180°, positive = to the right) from the given
        /// forward direction, projected on XZ, to each emitter. Ignore one
        /// emitter via <paramref name="exclude"/> (an antenna must not hear
        /// its own transmission). No allocation.
        /// </summary>
        public static int StrongestAt(Vector3 pos, Vector3 forward, int k, RfReading[] dest,
                                      IRfEmitter exclude = null)
        {
            k = Mathf.Min(k, dest.Length);
            for (int i = 0; i < k; i++)
                dest[i] = new RfReading { beaconId = -1, rssiDbm = RssiFloorDbm, bearingDeg = 0f };

            int found = 0;
            for (int i = 0; i < _emitters.Count; i++)
            {
                var e = _emitters[i];
                if (e == null || !e.RfActive || ReferenceEquals(e, exclude)) continue;
                float rssi = RssiFrom(e, pos);
                if (rssi <= RssiFloorDbm) continue;

                for (int s = 0; s < k; s++)
                {
                    bool wins = dest[s].beaconId < 0
                        || rssi > dest[s].rssiDbm
                        || (rssi == dest[s].rssiDbm && e.BeaconId < dest[s].beaconId);
                    if (!wins) continue;
                    for (int t = k - 1; t > s; t--) dest[t] = dest[t - 1];
                    dest[s] = new RfReading
                    {
                        beaconId = e.BeaconId,
                        rssiDbm = rssi,
                        bearingDeg = Bearing(pos, forward, e.RfPosition),
                    };
                    break;
                }
                found++;
            }
            return Mathf.Min(found, k);
        }

        /// <summary>Signed yaw from forward (XZ-projected) to a target, degrees.</summary>
        public static float Bearing(Vector3 pos, Vector3 forward, Vector3 target)
        {
            Vector3 fwd = new Vector3(forward.x, 0f, forward.z);
            Vector3 to = new Vector3(target.x - pos.x, 0f, target.z - pos.z);
            if (fwd.sqrMagnitude < 1e-8f || to.sqrMagnitude < 1e-8f) return 0f;
            return Vector3.SignedAngle(fwd, to, Vector3.up);
        }
    }
}
