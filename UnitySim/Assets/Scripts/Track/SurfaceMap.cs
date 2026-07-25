using AIHWSim.TrackEd;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Per-tile surface properties resolved from a wheel's ground contact.
    /// frictionMult scales the wheel friction stiffness; rollingResist is an
    /// extra brake torque; boostAccel/bumpAmp drive boost pads and rumble strips.
    /// </summary>
    public struct SurfaceInfo
    {
        public float frictionMult;
        public float rollingResist;
        public float boostAccel;
        public float bumpAmp;
        public float roughAmp;    // roughness force amplitude (fraction of per-wheel weight)
        public float roughLen;    // roughness feature wavelength (m)

        public static readonly SurfaceInfo Baseline = new SurfaceInfo { frictionMult = 1f };
    }

    /// <summary>
    /// Positional floor-surface lookup for custom tile maps. Lives on the single
    /// invisible floor-slab collider built by TrackFactory; a wheel contact on
    /// that collider maps its hit point to a tile index and the tile's catalog
    /// surface. Anywhere else (oval scene, ramps, platforms, diff-drive scene)
    /// <see cref="At"/> returns the baseline — those scenes are untouched.
    /// </summary>
    public sealed class SurfaceMap : MonoBehaviour
    {
        public static SurfaceMap Active { get; private set; }

        private TrackDesign _design;
        private Collider _floor;
        private readonly System.Collections.Generic.Dictionary<Collider, int> _tagCache =
            new System.Collections.Generic.Dictionary<Collider, int>();

        public void Bind(TrackDesign design, Collider floorCollider)
        {
            _design = design;
            _floor = floorCollider;
            _tagCache.Clear();
        }

        private void OnEnable() => Active = this;

        private void OnDisable()
        {
            if (Active == this) Active = null;
            _tagCache.Clear();
        }

        /// <summary>Surface under a wheel contact; baseline off the tile floor.</summary>
        public static SurfaceInfo At(in WheelHit hit)
        {
            var a = Active;
            if (a == null || a._design == null || hit.collider == null)
                return SurfaceInfo.Baseline;

            // Tagged colliders (spline ribbon runs) win over positional lookup.
            if (!a._tagCache.TryGetValue(hit.collider, out int tagged))
            {
                var tag = hit.collider.GetComponent<SurfaceTag>();
                tagged = tag != null ? tag.floorType : -1;
                a._tagCache[hit.collider] = tagged;
            }
            if (tagged >= 0) return FromFloor(tagged);

            if (hit.collider != a._floor) return SurfaceInfo.Baseline;
            return a.Lookup(hit.point);
        }

        private static SurfaceInfo FromFloor(int type)
        {
            var f = TrackCatalog.Floors[Mathf.Clamp(type, 0, TrackCatalog.Floors.Length - 1)];
            return new SurfaceInfo
            {
                frictionMult = f.frictionMult,
                rollingResist = f.rollingResist,
                boostAccel = f.boostAccel,
                bumpAmp = f.bumpAmp,
                roughAmp = f.roughAmp,
                roughLen = f.roughLen,
            };
        }

        private SurfaceInfo Lookup(Vector3 worldPoint)
        {
            if (!_design.WorldToTile(worldPoint, out int tx, out int tz))
                return SurfaceInfo.Baseline;
            return FromFloor(_design.FloorAt(tx, tz));
        }
    }
}
