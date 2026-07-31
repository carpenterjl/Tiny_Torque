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
        /// <summary>Game sound effects — pickups, weapons, impacts. Sits under
        /// <see cref="masterVolume"/>, which is applied globally to the
        /// AudioListener. Field initializers are the back-compat mechanism:
        /// JsonUtility leaves them alone for keys an old settings.json predates.</summary>
        public float sfxVolume = 0.8f;
        /// <summary>Vehicle motor and tyre sound, separately adjustable because a
        /// continuous drone is the first thing anyone wants to turn down.</summary>
        public float engineVolume = 0.7f;
        /// <summary>Menu + race music (MusicDirector). Like the others, sits
        /// under masterVolume, which rides AudioListener.volume.</summary>
        public float musicVolume = 0.7f;
        public int qualityLevel = -1;      // -1 = leave the project default
        public bool fullscreen = true;
        public bool vSync = true;
        // Read live by CameraBloom every frame, so the toggle needs no apply
        // step. Initializer true: JsonUtility only overwrites fields it finds
        // in the JSON, so an old settings.json without this field keeps the
        // default — upgrades get bloom on, matching new installs.
        public bool bloom = true;
        public bool mouseSteer = false;

        // Keyboard steer shaping, 0 (instant/raw) .. 1 (full transmitter-style
        // ramp). Field initializer survives old JSON (JsonUtility keeps ctor
        // defaults for missing fields). Gamepad sticks are never shaped.
        public float kbSteerSmoothing = 1f;

        // Keyboard throttle shaping, same 0..1 scale and the same back-compat
        // trick: an existing settings.json has no such key, so the initializer
        // is what an upgrading player gets — which is deliberately full shaping,
        // because a raw digital throttle is most of why keyboard driving is hard.
        public float kbThrottleSmoothing = 1f;

        /// <summary>
        /// Driving-assist preset: 0 Off, 1 Standard, 2 Full, 3 Custom (use the
        /// p1/p2 sliders below).
        ///
        /// The initializer is 1 and NOT 0, deliberately. An existing settings.json
        /// has p1Assist* fields saved as zeroes, so defaulting the preset to Off
        /// would leave every upgrading player with exactly what they have now —
        /// no assists at all — and no reason to ever discover the feature.
        /// </summary>
        public int assistPreset = 1;

        // Defaults for the menu pages.
        public string player1Name = "Player 1";
        public string player2Name = "Player 2";
        // Fresh installs open on the TT Coupe — the face of the arcade side
        // and the progression system's starter car. Existing settings files
        // keep whatever they had (initializers only fill absent JSON keys),
        // and "" still means the stock engineering design.
        public string lastVehicle = "★ TT Coupe";
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
        // Launch control joined the struct later; separate line so an old
        // settings.json deserializes it to 0 like the rest.
        public float p1AssistLaunch, p2AssistLaunch;

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
        /// <summary>Seconds the race keeps running after the first car finishes
        /// before the results screen appears (0 = wait for the whole field). The
        /// field initializer is the back-compat mechanism: a settings.json written
        /// before this key existed reads as 30, not as "wait forever".</summary>
        public int spResultsWait = 30;
        public bool spArcade = false;      // power-ups, weapons, arcade scoreboard
        public bool spTrackLimits = true;  // off-track penalty (only used in arcade)
        /// <summary>Arcade handling: grip baseline + assist floor for everyone in
        /// the session. The field initializer is the back-compat mechanism —
        /// JsonUtility leaves it alone for keys a saved settings.json predates,
        /// so an existing install reads as Arcade rather than as false.</summary>
        public bool spArcadeHandling = true;

        /// <summary>Controller DLL last chosen on the Simulate Controller screen,
        /// as a bare file name in Assets/Plugins/x86_64.</summary>
        public string simControllerDll = "controller.dll";

        // ---- in-game controller build ----------------------------------------

        /// <summary>Explicit path to the Controllers/ source folder. Empty means
        /// "find it" — see ControllerWorkspace, which probes upward from the data
        /// folder. Only a player build shipped somewhere odd needs this set.</summary>
        public string controllerWorkspace = "";

        /// <summary>Rebuild the controller whenever a source file is saved.
        /// Explicit opt-in, OFF by default: a watcher that fires on every save is
        /// a compiler running in the background of somebody's race.</summary>
        public bool controllerRebuildOnSave = false;

        /// <summary>CMake build type passed to build.ps1 (Release / Debug).</summary>
        public string controllerBuildConfig = "Release";

        /// <summary>Put the car back on the grid after a hot reload. Default OFF:
        /// the point of a hot reload is watching the SAME situation respond to new
        /// code, and a reset throws that situation away (along with the telemetry
        /// buffer, which RestartRun clears).</summary>
        public bool controllerResetOnReload = false;

        /// <summary>
        /// Keyboard and gamepad bindings. Nested rather than flattened into
        /// twenty more fields here because it is one coherent thing the player
        /// resets as a unit — and JsonUtility serializes a nested [Serializable]
        /// class fine. A settings.json written before this existed leaves the
        /// initializer alone, which is the default WASD layout.
        /// </summary>
        public Core.KeyBindings keys = new Core.KeyBindings();
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
