using AIHWSim.Garage;
using AIHWSim.TrackEd;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIHWSim.Core
{
    /// <summary>
    /// Tiny static bridge that carries the chosen <see cref="VehicleDesign"/> and
    /// track across scene loads (garage/builder → track) and centralizes the scene
    /// names. The designs are plain managed objects, so they survive a
    /// SceneManager.LoadScene without any DontDestroyOnLoad plumbing.
    /// </summary>
    public static class GameFlow
    {
        public const string TrackSceneName = "TrackScene";
        public const string GarageSceneName = "GarageScene";
        public const string TrackBuilderSceneName = "TrackBuilderScene";
        public const string MenuSceneName = "MenuScene";

        /// <summary>Design to spawn on the track; null means the stock default.</summary>
        public static VehicleDesign ActiveDesign;

        private static TrackDesign _activeTrack;
        private static string _activeSceneTrack;

        /// <summary>
        /// Custom tile map to drive. Null means either the classic procedural oval
        /// or — when <see cref="ActiveSceneTrack"/> is set — a hand-authored scene.
        ///
        /// Assigning this ALWAYS clears <see cref="ActiveSceneTrack"/>, in both
        /// directions and including null. The two track sources are mutually
        /// exclusive and <c>TrackBootstrap</c> branches on them in order, so a stale
        /// value in the other would not throw — it would quietly load the wrong
        /// track. Making the properties enforce it means the eleven existing
        /// assignment sites stay correct without any of them knowing this field
        /// gained a sibling.
        /// </summary>
        public static TrackDesign ActiveTrack
        {
            get => _activeTrack;
            set { _activeTrack = value; _activeSceneTrack = null; }
        }

        /// <summary>
        /// Scene name of a hand-authored track scene; null means the tile-map or
        /// oval path above. Identified by NAME rather than by data because the scene
        /// ships inside the build — which is exactly why a LAN client that lacks it
        /// must be refused rather than silently dropped onto the oval.
        /// </summary>
        public static string ActiveSceneTrack
        {
            get => _activeSceneTrack;
            set { _activeSceneTrack = value; _activeTrack = null; }
        }

        /// <summary>True when a hand-authored scene track is selected.</summary>
        public static bool HasSceneTrack => !string.IsNullOrEmpty(_activeSceneTrack);

        /// <summary>Set by the menu's Resume page; consumed once by TrackBootstrap.</summary>
        public static Persistence.SessionSnapshot PendingSnapshot;

        /// <summary>
        /// True once something outside a scene has chosen this session — the menu,
        /// a championship round, a LAN join, a resumed snapshot. All of them reach
        /// a track through <see cref="LoadTrack"/>, and <see cref="LoadTrack"/> is
        /// the only <c>LoadScene</c> call in the project that reaches one. Pressing
        /// Play in a scene reaches it through nothing.
        ///
        /// That is exactly the distinction a scene's authored defaults need: they
        /// are defaults for the case where nobody else decided. The condition this
        /// replaces — "the scene names itself a track" — was a proxy for it, and
        /// too narrow: a driving scene that gets its geometry from a tile map or a
        /// terrain has no <c>SceneTrackDescriptor</c>, so its
        /// <c>DrivingSceneDescriptor</c> was read for solver and assists and
        /// silently ignored for rules and car.
        ///
        /// <b>Never cleared during a session.</b> Once the menu has chosen a car
        /// and a rule set it stays in charge for the whole process; a restart or a
        /// trip through the garage must not hand the scene's defaults back the
        /// wheel.
        /// </summary>
        public static bool LaunchedFromMenu { get; private set; }

        /// <summary>
        /// Statics survive a scene load, which is the point of this class — but
        /// they also survive entering Play mode when domain reload is disabled,
        /// which would make the second Play of a session look menu-driven. Same
        /// guard, and the same reason, as <c>PhysicsRateAuthority.Reset</c>.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetLaunchState() => LaunchedFromMenu = false;

        /// <summary>
        /// Load whatever track is selected. A tile map or the oval loads
        /// <c>TrackScene</c>; a scene track loads ITSELF single (so its render
        /// settings, skybox and baked lighting become the active scene's) and pulls
        /// <c>TrackScene</c> in additively on top to supply the one
        /// <c>TrackBootstrap</c> that composes the session. Every caller —
        /// MenuUI, NetSession, Championship, PauseMenu — keeps calling this.
        /// </summary>
        public static void LoadTrack()
        {
            // Whoever called this made the choices a scene would otherwise make
            // for itself. Set before the load, because the bootstrap that reads it
            // runs inside it.
            LaunchedFromMenu = true;
            if (HasSceneTrack)
            {
                SceneManager.LoadScene(_activeSceneTrack, LoadSceneMode.Single);
                SceneManager.LoadScene(TrackSceneName, LoadSceneMode.Additive);
                return;
            }
            SceneManager.LoadScene(TrackSceneName);
        }

        /// <summary>
        /// The second half of <see cref="LoadTrack"/>, for a caller that already
        /// has the scene track open rather than needing it loaded — a headless
        /// runner opens a scene directly, and reloading it here would tear down
        /// the very thing it just opened.
        ///
        /// Pulls <c>TrackScene</c> in on top and sets the same
        /// <see cref="LaunchedFromMenu"/> flag, so the two entry points cannot
        /// drift on what "something outside the scene chose this" means.
        /// </summary>
        public static void AdoptOpenSceneTrack()
        {
            LaunchedFromMenu = true;
            if (!SceneManager.GetSceneByName(TrackSceneName).isLoaded)
                SceneManager.LoadScene(TrackSceneName, LoadSceneMode.Additive);
        }

        public static void LoadGarage() => SceneManager.LoadScene(GarageSceneName);
        public static void LoadTrackBuilder() => SceneManager.LoadScene(TrackBuilderSceneName);
        public static void LoadMenu() => SceneManager.LoadScene(MenuSceneName);
    }
}
