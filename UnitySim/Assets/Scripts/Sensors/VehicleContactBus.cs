using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Per-vehicle contact recorder feeding <see cref="BumpSensor"/>s: sits on
    /// the vehicle root (the Rigidbody's GameObject, where VehicleAudio and the
    /// crash lattice already receive collision callbacks — every component on
    /// that GO gets them, so nothing conflicts) and keeps a small ring of
    /// recent contact points with a monotonic sequence number. Each bump sensor
    /// remembers the last sequence it consumed, so several sensors can share
    /// one bus without starving each other. No new colliders, no triggers.
    /// </summary>
    public sealed class VehicleContactBus : MonoBehaviour
    {
        public struct ContactRecord
        {
            public Vector3 point;    // world
            public Vector3 normal;   // world, pointing away from the other body
            public float impulse;    // N·s over the physics step
            public long seq;
        }

        private const int RingSize = 32;
        private readonly ContactRecord[] _ring = new ContactRecord[RingSize];
        private int _head = -1;
        private int _count;
        private long _seq;

        /// <summary>Sequence of the newest recorded contact (0 = none yet).</summary>
        public long LatestSeq => _seq;

        /// <summary>Ensure a bus exists on the vehicle root.</summary>
        public static VehicleContactBus Ensure(Transform vehicleRoot)
        {
            var bus = vehicleRoot.GetComponent<VehicleContactBus>();
            return bus != null ? bus : vehicleRoot.gameObject.AddComponent<VehicleContactBus>();
        }

        private void OnCollisionEnter(Collision c) => Record(c);
        private void OnCollisionStay(Collision c) => Record(c);

        private void Record(Collision c)
        {
            // One record per contact point; impulse split evenly across them
            // (Unity reports the aggregate impulse for the pair).
            int points = c.contactCount;
            if (points <= 0) return;
            float impulsePer = c.impulse.magnitude / points;
            for (int i = 0; i < points; i++)
            {
                var cp = c.GetContact(i);
                _head = (_head + 1) % RingSize;
                if (_count < RingSize) _count++;
                _ring[_head] = new ContactRecord
                {
                    point = cp.point,
                    normal = cp.normal,
                    impulse = impulsePer,
                    seq = ++_seq,
                };
            }
        }

        /// <summary>
        /// Copy every recorded contact newer than <paramref name="afterSeq"/>
        /// into dest, oldest first, up to dest.Length. Returns the count. The
        /// ring is never cleared — consumers advance their own cursor (pass
        /// <see cref="LatestSeq"/> back next call), so a tap registers for
        /// exactly the ticks it spans and several sensors can't starve each
        /// other. No allocation.
        /// </summary>
        public int CopySince(long afterSeq, ContactRecord[] dest)
        {
            int written = 0;
            if (_count == 0 || _seq <= afterSeq) return 0;
            for (int i = _count - 1; i >= 0 && written < dest.Length; i--)
            {
                int idx = (_head - i + RingSize) % RingSize;
                if (_ring[idx].seq > afterSeq) dest[written++] = _ring[idx];
            }
            return written;
        }
    }
}
