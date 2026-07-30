using AIHWSim.Garage;
using AIHWSim.TrackEd;
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
        /// Load whatever track is selected. A tile map or the oval loads
        /// <c>TrackScene</c>; a scene track loads ITSELF single (so its render
        /// settings, skybox and baked lighting become the active scene's) and pulls
        /// <c>TrackScene</c> in additively on top to supply the one
        /// <c>TrackBootstrap</c> that composes the session. Every caller —
        /// MenuUI, NetSession, Championship, PauseMenu — keeps calling this.
        /// </summary>
        public static void LoadTrack()
        {
            if (HasSceneTrack)
            {
                SceneManager.LoadScene(_activeSceneTrack, LoadSceneMode.Single);
                SceneManager.LoadScene(TrackSceneName, LoadSceneMode.Additive);
                return;
            }
            SceneManager.LoadScene(TrackSceneName);
        }

        public static void LoadGarage() => SceneManager.LoadScene(GarageSceneName);
        public static void LoadTrackBuilder() => SceneManager.LoadScene(TrackBuilderSceneName);
        public static void LoadMenu() => SceneManager.LoadScene(MenuSceneName);
    }
}
