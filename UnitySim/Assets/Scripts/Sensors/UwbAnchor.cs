using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>A surveyed UWB anchor in the world (SEN-05). Registers itself
    /// while enabled; tags range to every registered anchor in id order.</summary>
    public sealed class UwbAnchor : MonoBehaviour
    {
        public int anchorId;

        private static readonly List<UwbAnchor> _all = new List<UwbAnchor>();
        private static readonly System.Comparison<UwbAnchor> ById = (a, b) => a.anchorId.CompareTo(b.anchorId);

        /// <summary>Registered anchors in id order. Sorted on every read:
        /// AddComponent enables an anchor before its id is assigned.</summary>
        public static IReadOnlyList<UwbAnchor> All
        {
            get { _all.Sort(ById); return _all; }
        }

        private void OnEnable()
        {
            if (!_all.Contains(this)) _all.Add(this);
        }

        private void OnDisable() => _all.Remove(this);

        /// <summary>Unity position → the world frame UWB reports in: x = Unity
        /// +z, y = Unity −x, z = up (right-handed, the FLU of the world origin).</summary>
        public static Vector3 WorldFlu(Vector3 p) => new Vector3(p.z, -p.x, p.y);
    }
}
