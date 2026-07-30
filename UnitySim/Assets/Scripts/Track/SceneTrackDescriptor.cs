using System.Collections.Generic;
using AIHWSim.TrackEd;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Declares that the scene it sits in IS a track — the third track source,
    /// alongside the classic procedural oval and a <see cref="TrackDesign"/> tile
    /// map. Everything the game needs that hand-authored geometry cannot answer
    /// lives here: what the track is FOR, where laps start, what the terrain is
    /// made of, and the corridor bots drive.
    ///
    /// <b>The trade this represents.</b> A tile map is data — it saves to JSON,
    /// travels the LAN wire, round-trips through a resume snapshot and opens in
    /// the in-game Track Builder. A scene track buys terrain, ProBuilder geometry
    /// and authored lighting at the cost of all four: it ships inside the build and
    /// is identified across the wire by NAME. That is the deliberate bargain, not
    /// an oversight, and <c>TrackStudioValidator</c> is what keeps the bargain
    /// honest.
    ///
    /// Markers are collected from the hierarchy rather than listed in arrays here,
    /// so parenting a checkpoint to the bridge it sits on cannot desynchronise it
    /// from a list someone forgot to update.
    /// </summary>
    [AddComponentMenu("Tiny Torque/Track/Scene Track Descriptor")]
    [DisallowMultipleComponent]
    public sealed class SceneTrackDescriptor : MonoBehaviour
    {
        // ---- identity -------------------------------------------------------

        [Header("Identity")]
        [Tooltip("Shown in the track pickers. The SCENE NAME is the persisted " +
                 "identifier — renaming this is cosmetic, renaming the scene is not.")]
        public string displayName = "";

        /// <summary>
        /// Circuit or FreeRoam only. <b>Arena is refused</b> — <c>ArenaNav.Drop</c>
        /// reads <c>BuiltTrack.floorCollider.bounds</c> with no raycast fallback,
        /// and a hand-authored scene has no floor slab to give it. Arena scene
        /// tracks need an explicit playfield volume, which is a separate pass.
        /// </summary>
        /// <remarks>Defaults to FreeRoam because that is what a brand-new track
        /// scene actually IS: no finish line, no checkpoints, nothing to lap.
        /// Defaulting to Circuit would make every freshly promoted scene fail
        /// validation for a claim its author never made.</remarks>
        [Tooltip("Circuit = laps, finish line, ordered checkpoints. " +
                 "FreeRoam = spawn only, no race. Arena is not supported.")]
        public TrackPresets.TrackKind kind = TrackPresets.TrackKind.FreeRoam;

        // ---- atmosphere -----------------------------------------------------

        [Header("Atmosphere")]
        [Tooltip("MapAmbience key for fog/sun/ground tint. Leave empty for the " +
                 "scene's own RenderSettings.")]
        public string ambience = "";

        /// <summary>
        /// Keep the scene's own skybox and camera clear flags.
        ///
        /// <c>MapAmbience.ApplyCamera</c> forces <c>CameraClearFlags.SolidColor</c>
        /// unconditionally, because a tile map's sky is a 400 m painted dome and the
        /// background only shows where the dome does not reach. A hand-authored
        /// scene has a real skybox and baked reflection probes, and that assignment
        /// silently replaces the lot with a flat colour. This is the opt-out.
        /// </summary>
        [Tooltip("On: the scene's skybox and lighting win. Off: MapAmbience paints " +
                 "the sky, same as a tile map.")]
        public bool sceneOwnsSky = true;

        /// <summary>
        /// Skybox to install at load, overriding whatever the scene's RenderSettings
        /// hold. Optional — leave null and the scene's own skybox is used, which is
        /// the normal case. This exists because a skybox assigned by code was the one
        /// genuinely load-bearing thing in the hand-written sandbox branch, and a
        /// field is a better home for it than a scene-name comparison.
        /// </summary>
        [Tooltip("Optional. Null = use the scene's own RenderSettings skybox.")]
        public Material skyboxOverride;

        // ---- surfaces -------------------------------------------------------

        [Header("Surfaces")]
        [Tooltip("TerrainLayer -> floor type. Required if the scene contains any " +
                 "Terrain; without it every terrain drives as the 1.0 baseline.")]
        public TerrainFloorTable terrainFloors;

        /// <summary>
        /// Floor type for a mesh collider carrying no <see cref="SurfaceTag"/>.
        ///
        /// Defaults to asphalt (1) rather than the 1.0 baseline so a road you forgot
        /// to tag drives like a road instead of like nothing. Worth knowing:
        /// <c>frictionMult</c> doubles as the arcade track-limit classifier against
        /// <c>ArcadeConfig.OffTrackFrictionThreshold</c> (0.90), so a scene whose
        /// surfaces all sit above that threshold has track limits that never trigger
        /// anywhere. That is a property of the paint job, not a bug.
        /// </summary>
        [Tooltip("Fallback for untagged mesh colliders. 1 = asphalt.")]
        public int sceneFallbackFloor = 1;

        // ---- bot corridor ---------------------------------------------------

        [Header("Bot corridor (baked by Track Studio)")]
        [Tooltip("Ordered centreline bots follow. Baked from the authoring spline; " +
                 "leave empty to fall back to the checkpoint gates.")]
        public Vector3[] centerline = System.Array.Empty<Vector3>();

        [Tooltip("Usable half road width at each centreline node, index-for-index.")]
        public float[] halfWidths = System.Array.Empty<float>();

        public bool corridorClosed = true;

        /// <summary>
        /// The baked ideal line for this track, if one has been solved.
        ///
        /// Nothing in the shipped game reads it — bots still run their own runtime
        /// heuristic — so this is analysis and visualization only, and assigning it
        /// changes no behaviour. It is referenced from the descriptor rather than
        /// loaded by path so the link is visible in the inspector and survives a
        /// rename.
        /// </summary>
        [Tooltip("Baked by Track Studio. Not consumed by bots; visualization and " +
                 "sector targets only.")]
        public RacingLineAsset racingLine;

        /// <summary>
        /// Sector boundaries and target times. When set, the scene build attaches a
        /// <see cref="SectorTimer"/> to the lap timer, which records splits locally
        /// and publishes the three sector telemetry channels. Null means no sector
        /// timing, which is the default and costs nothing.
        /// </summary>
        [Tooltip("Optional. Built by Track Studio's sector configurator.")]
        public TrackSectorSet sectors;

        /// <summary>True when the baked corridor is usable — both arrays present,
        /// same length, at least two nodes. A half-baked corridor is worse than
        /// none, because the checkpoint fallback would never run.</summary>
        public bool HasCorridor =>
            centerline != null && halfWidths != null &&
            centerline.Length >= 2 && centerline.Length == halfWidths.Length;

        // ---- marker collection ----------------------------------------------

        /// <summary>The scene's finish marker, or null on a FreeRoam track.
        /// More than one is an authoring error the validator catches; at runtime
        /// the first found wins so a bad scene still loads.</summary>
        public TrackFinishMarker Finish() => GetComponentInChildren<TrackFinishMarker>(true);

        /// <summary>Checkpoints sorted by <c>order</c>. The sort is what makes a
        /// sparse or duplicated order still produce a dense runtime sequence —
        /// the same forgiveness <c>TrackDesign.OrderedCheckpoints</c> gives tile
        /// maps. The validator still demands a dense 0..n-1 run, because a track
        /// that laps by accident is not a track that laps.</summary>
        public List<TrackCheckpointMarker> Checkpoints()
        {
            var list = new List<TrackCheckpointMarker>(
                GetComponentsInChildren<TrackCheckpointMarker>(true));
            list.Sort((a, b) => a.order.CompareTo(b.order));
            return list;
        }

        /// <summary>Spawns in grid order. The first is the grid anchor that
        /// <c>TrackBootstrap.SpawnPose</c> lays the rest of the row out from.</summary>
        public List<TrackSpawnMarker> Spawns()
        {
            var list = new List<TrackSpawnMarker>(
                GetComponentsInChildren<TrackSpawnMarker>(true));
            list.Sort((a, b) => a.gridOrder.CompareTo(b.gridOrder));
            return list;
        }

        /// <summary>Every Terrain under this scene track, for the surface bake.</summary>
        public Terrain[] Terrains() =>
            Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None);
    }
}
