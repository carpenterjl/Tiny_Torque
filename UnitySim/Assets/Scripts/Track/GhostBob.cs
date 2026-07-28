using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Cosmetic hover for the haunted spirits: a slow vertical bob plus a lazy
    /// yaw sway. Attached to the prop's MESH instance, not its root — the root
    /// carries the selection hull and the placed-item marker, which must not
    /// drift under the builder's raycasts. Phase comes from the instance id so
    /// a graveyard of ghosts doesn't breathe in unison. Purely local; no
    /// physics, no network state (every peer rebuilds the same track and runs
    /// its own copy).
    /// </summary>
    public sealed class GhostBob : MonoBehaviour
    {
        private const float BobAmp = 0.04f;     // m
        private const float BobHz = 0.35f;
        private const float SwayDeg = 8f;
        private const float SwayHz = 0.11f;

        private Vector3 _basePos;
        private Quaternion _baseRot;
        private float _phase;

        private void Start()
        {
            _basePos = transform.localPosition;
            _baseRot = transform.localRotation;
            _phase = (GetInstanceID() * 0.013f) % (2f * Mathf.PI);
        }

        private void Update()
        {
            float t = Time.time * 2f * Mathf.PI + _phase;
            transform.localPosition = _basePos
                + Vector3.up * (BobAmp * Mathf.Sin(t * BobHz));
            transform.localRotation = _baseRot
                * Quaternion.Euler(0f, SwayDeg * Mathf.Sin(t * SwayHz), 0f);
        }
    }
}
