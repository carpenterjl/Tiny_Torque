using AIHWSim.Garage;
using AIHWSim.TrackEd;
using UnityEngine.SceneManagement;

namespace AIHWSim.Core
{
    /// <summary>
    /// Tiny static bridge that carries the chosen <see cref="VehicleDesign"/> and
    /// <see cref="TrackDesign"/> across scene loads (garage/builder → track) and
    /// centralizes the scene names. The designs are plain managed objects, so they
    /// survive a SceneManager.LoadScene without any DontDestroyOnLoad plumbing.
    /// </summary>
    public static class GameFlow
    {
        public const string TrackSceneName = "TrackScene";
        public const string GarageSceneName = "GarageScene";
        public const string TrackBuilderSceneName = "TrackBuilderScene";
        public const string MenuSceneName = "MenuScene";

        /// <summary>Design to spawn on the track; null means the stock default.</summary>
        public static VehicleDesign ActiveDesign;

        /// <summary>Custom map to drive; null means the classic procedural oval.</summary>
        public static TrackDesign ActiveTrack;

        /// <summary>Set by the menu's Resume page; consumed once by TrackBootstrap.</summary>
        public static Persistence.SessionSnapshot PendingSnapshot;

        public static void LoadTrack() => SceneManager.LoadScene(TrackSceneName);
        public static void LoadGarage() => SceneManager.LoadScene(GarageSceneName);
        public static void LoadTrackBuilder() => SceneManager.LoadScene(TrackBuilderSceneName);
        public static void LoadMenu() => SceneManager.LoadScene(MenuSceneName);
    }
}
