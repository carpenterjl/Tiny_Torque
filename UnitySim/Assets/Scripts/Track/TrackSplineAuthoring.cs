using AIHWSim.TrackEd;
using UnityEngine;
using UnityEngine.Splines;

namespace AIHWSim.Track
{
    /// <summary>
    /// Sits next to a Unity <see cref="SplineContainer"/> and turns it into a
    /// drivable road: ribbon geometry with per-surface colliders and
    /// <see cref="SurfaceTag"/>s, plus the corridor bots follow.
    ///
    /// Unity splines carry position and tangents and nothing else, so the three
    /// things a road needs — how wide it is, how much it banks, what it is made of —
    /// live here as <see cref="SplineData{T}"/> channels keyed along the curve.
    /// That is the package's own idiom for exactly this, and it means the width of
    /// a corner is authored on the corner rather than in a list somewhere else.
    ///
    /// The ribbon rebuilds itself as the road is edited (see
    /// <see cref="liveRebuild"/>): move a knot, drag a width handle or bank a
    /// corner and the geometry follows, because a road you have to remember to
    /// bake is a road you will look at stale. The rebuild lands when an edit is
    /// committed rather than on every mouse-move — regenerating a few thousand
    /// triangles and re-cooking a MeshCollider at drag rate would stutter — and
    /// the explicit bake stays for scripted and headless use.
    /// </summary>
    [AddComponentMenu("Tiny Torque/Track/Track Spline Authoring")]
    [RequireComponent(typeof(SplineContainer))]
    public sealed class TrackSplineAuthoring : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("Which spline of the container to build. 0 unless the container " +
                 "holds several.")]
        public int splineIndex;

        [Tooltip("Control-point spacing in metres when resampling the Unity spline. " +
                 "Smaller tracks tight corners more faithfully and costs triangles.")]
        public float spacing = UnitySplineSampler.DefaultSpacing;

        [Header("Road")]
        [Tooltip("Width in metres where the width channel says nothing.")]
        public float defaultWidth = SplineSpec.DefaultWidth;

        [FloorType]
        [Tooltip("Surface where the surface channel says nothing.")]
        public int defaultSurface = SplineSpec.DefaultSurface;

        public bool edgeWalls;
        public bool edgeStripes = true;

        [Header("Lane markings")]
        [Tooltip("Paint on the road: a centre line or a double line, optional edge " +
                 "lines, solid or dashed. Set Centre Lines to 0 for a bare road.")]
        public RoadLineStyle lines = new RoadLineStyle();

        [Header("Channels (optional — empty means use the defaults above)")]
        [Tooltip("Road width in metres, keyed along the spline.")]
        public SplineData<float> widthChannel = new SplineData<float>();

        [Tooltip("Banking in degrees. Positive drops the RIGHT edge, matching " +
                 "SplineSpec.rollDeg.")]
        public SplineData<float> rollChannel = new SplineData<float>();

        [Tooltip("TrackCatalog.Floors index, rounded. Held as float because " +
                 "SplineData interpolates; step changes by keying both sides.")]
        public SplineData<float> surfaceChannel = new SplineData<float>();

        [Tooltip("Rebuild the ribbon whenever the curve or a channel changes, so " +
                 "what you see in the Scene view is the road. Turn off for a very " +
                 "long spline where the rebuild becomes noticeable while dragging.")]
        public bool liveRebuild = true;

        [Header("Corridor")]
        [Tooltip("Feed this spline's centreline to the SceneTrackDescriptor as the " +
                 "path bots drive. Turn off for a decorative spline.")]
        public bool contributesCorridor = true;

        /// <summary>Name of the GameObject the baked ribbon is parented under, so a
        /// rebake can find and replace exactly its own output and nothing else.</summary>
        public const string BakedRootName = "BakedRibbon";

        /// <summary>The container this authors. Cached lookups would go stale
        /// across a domain reload, and this is never called per frame.</summary>
        public SplineContainer Container => GetComponent<SplineContainer>();

        /// <summary>True once this spline has geometry. The live rebuild uses it to
        /// tell "keep the road in step with the edit" from "this spline has never
        /// been asked to be a road", so adding the component to a decorative curve
        /// does not silently fill the scene with meshes.</summary>
        public bool HasBaked
        {
            get
            {
                for (int i = 0; i < transform.childCount; i++)
                    if (transform.GetChild(i).name == BakedRootName) return true;
                return false;
            }
        }

        /// <summary>The authored curve as a SplineSpec, in world space.</summary>
        public SplineSpec BuildSpec()
        {
            var settings = new UnitySplineSampler.Settings
            {
                spacing = spacing,
                defaultWidth = defaultWidth,
                defaultSurface = defaultSurface,
                edgeWalls = edgeWalls,
                edgeStripes = edgeStripes,
                lines = lines,
            };
            return UnitySplineSampler.ToSpec(Container, splineIndex, settings,
                widthChannel, rollChannel, surfaceChannel);
        }

        /// <summary>
        /// Replace this spline's baked geometry. Safe to call repeatedly — the old
        /// root is destroyed first, so a rebake after widening a corner leaves one
        /// ribbon rather than two overlapping ones fighting for the same raycasts.
        ///
        /// The ribbon is built through the untouched <see cref="RibbonMeshBuilder"/>,
        /// so a scene road and a tile-map spline road are the same geometry, the
        /// same collider layout and the same SurfaceTags.
        /// </summary>
        public GameObject Bake()
        {
            ClearBaked();
            var spec = BuildSpec();
            if (spec.Count < 2) return null;

            // The spec is already in world space, so the host must contribute no
            // transform of its own. Built at world identity FIRST and parented with
            // worldPositionStays, which survives a container that is rotated or
            // scaled — parenting first and zeroing localScale would not, and the
            // symptom would be a road at the right shape and the wrong size.
            var host = new GameObject(BakedRootName);
            host.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            host.transform.localScale = Vector3.one;
            host.transform.SetParent(transform, worldPositionStays: true);

            RibbonMeshBuilder.Build(spec, splineIndex, host.transform, colliders: true);
            return host;
        }

        public void ClearBaked()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var c = transform.GetChild(i);
                if (c.name != BakedRootName) continue;
                if (Application.isPlaying) Destroy(c.gameObject);
                else DestroyImmediate(c.gameObject);
            }
        }
    }
}
