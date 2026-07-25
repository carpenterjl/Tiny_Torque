using System;
using UnityEngine;

namespace AIHWSim.Persistence
{
    /// <summary>Player-facing options persisted to Saves/settings.json.</summary>
    [Serializable]
    public sealed class GameSettings
    {
        public int version = 2;

        // Options page.
        public float masterVolume = 1f;
        public int qualityLevel = -1;      // -1 = leave the project default
        public bool fullscreen = true;
        public bool vSync = true;
        public bool mouseSteer = false;

        // Keyboard steer shaping, 0 (instant/raw) .. 1 (full transmitter-style
        // ramp). Field initializer survives old JSON (JsonUtility keeps ctor
        // defaults for missing fields). Gamepad sticks are never shaped.
        public float kbSteerSmoothing = 1f;

        // Defaults for the menu pages.
        public string player1Name = "Player 1";
        public string player2Name = "Player 2";
        public string lastVehicle = "";    // "" = stock default design
        public string lastTrack = "";      // "" = classic oval
        public int lastLaps = 0;           // 0 = free drive

        // Split-screen device defaults (InputDeviceKind ints; see Core).
        public int p1DeviceKind = 1;       // Keyboard
        public int p2DeviceKind = 2;       // Gamepad
        public int p2GamepadIndex = 0;

        // Per-player arcade assists, 0 (pure physics) .. 1 (full help). Old
        // settings.json deserializes these to 0 — realism by default.
        public float p1AssistSteer, p1AssistStability, p1AssistTraction, p1AssistAbs;
        public float p2AssistSteer, p2AssistStability, p2AssistTraction, p2AssistAbs;

        // Simulation realism (old settings.json → 0 = legacy behaviour).
        public int noiseSeed = 0;            // sensor-noise seed; 0 = random each run (logged)
        public int actuationDelayTicks = 0;  // controller→actuator delay in control ticks

        // Telemetry/sensor CSV logging. Explicit opt-in — OFF by default so a
        // normal drive writes nothing. Toggled in Options and the pause Settings
        // panel; old settings.json keeps this default (false).
        public bool logTelemetry = false;

        // Single-player race setup, remembered between sessions (old JSON → 0/false).
        public int spBots = 0;          // number of AI opponents (0..7)
        public int spDifficulty = 1;    // 0 Easy / 1 Medium / 2 Hard
        public int spControl = 0;       // 0 Manual / 1 Autonomous (C firmware) / 2 Autonomous (bot AI)
        public bool spRubberBand = false;
        public int spCountdown = 3;     // race-start countdown seconds (0..60)
    }

    /// <summary>
    /// Loads/applies/saves <see cref="GameSettings"/>. No persistent GameObject:
    /// the statics survive scene loads, and <see cref="ApplyOnBoot"/> applies the
    /// saved options once per play session regardless of which scene starts.
    /// </summary>
    public static class SettingsStore
    {
        private const string FileName = "settings.json";
        private static GameSettings _current;

        public static GameSettings Current => _current ??= SaveSystem.LoadJson<GameSettings>(FileName) ?? new GameSettings();

        public static void Save() => SaveSystem.SaveJson(FileName, Current);

        /// <summary>Push the current settings into the engine.</summary>
        public static void Apply()
        {
            var s = Current;
            AudioListener.volume = Mathf.Clamp01(s.masterVolume);
            if (s.qualityLevel >= 0 && s.qualityLevel < QualitySettings.names.Length)
                QualitySettings.SetQualityLevel(s.qualityLevel, applyExpensiveChanges: true);
            QualitySettings.vSyncCount = s.vSync ? 1 : 0;
#if !UNITY_EDITOR
            Screen.fullScreen = s.fullscreen;
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void ApplyOnBoot() => Apply();
    }
}
