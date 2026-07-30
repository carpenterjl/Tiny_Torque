using System.IO;
using AIHWSim.Core;
using AIHWSim.Garage;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Runtime half of the headless racing-line calibration: arms itself from a
    /// request file written by the editor, drives the baked line for three laps,
    /// measures what the car actually did, and writes a result JSON.
    ///
    /// A REQUEST FILE rather than static fields, exactly as MissionAutorun does:
    /// entering play mode triggers a domain reload that wipes anything the editor
    /// side set. The file is consumed and DELETED on read — one reader, one run —
    /// so a killed run cannot arm the next launch.
    ///
    /// Completely inert when no request file exists, which is every normal play.
    /// </summary>
    public static class RacingLineAutorun
    {
        [System.Serializable]
        public sealed class Request
        {
            public string linePath = "";     // asset path, for the editor to write back
            public string resultPath = "";
            public string vehicle = "";
            public float timeoutSec = 180f;
        }

        [System.Serializable]
        public sealed class Result
        {
            public bool ok;
            public string fault = "";
            public float lap1, lap2, lap3;
            public float muScale;
            public float accelA0;
            public float vMax;
            public float brakeUse;
            public float limitFraction;
            public string vehicle = "";
        }

        public static string RequestPath =>
            Path.Combine(Path.GetTempPath(), "tinytorque_raceline_request.json");

        private static Request _req;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Arm()
        {
            _req = null;
            string path = RequestPath;
            if (!File.Exists(path)) return;

            try
            {
                string json = File.ReadAllText(path);
                File.Delete(path);            // consume before acting on it
                _req = JsonUtility.FromJson<Request>(json);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[RaceLineCal] request unreadable: {e.Message}");
                return;
            }
            if (_req == null) return;

            if (!string.IsNullOrEmpty(_req.vehicle))
            {
                var design = VehiclePresets.Resolve(_req.vehicle);
                if (design != null) GameFlow.ActiveDesign = design;
            }
            SessionConfig.SetSinglePlayer();
            SessionConfig.TargetLaps = 0;
            SessionConfig.Arcade = false;
            SessionConfig.CountdownSeconds = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Attach()
        {
            if (_req == null) return;
            var go = new GameObject("RaceLineCalibrationWatcher");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<RacingLineCalibrationWatcher>().Configure(_req);
            _req = null;
        }
    }
}
