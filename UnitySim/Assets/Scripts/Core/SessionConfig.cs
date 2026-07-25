using System;
using System.Collections.Generic;
using AIHWSim.Garage;

namespace AIHWSim.Core
{
    /// <summary>How the next drive session is populated.</summary>
    public enum SessionMode
    {
        SinglePlayer = 0,
        SplitScreen = 1,
        LanHost = 2,      // this machine simulates every car (listen server)
        LanClient = 3,    // this machine renders ghosts and streams its inputs
    }

    /// <summary>What drives a slot's car.</summary>
    public enum DriveControl
    {
        Human = 0,     // a local player via IDriverInputSource
        BotAI = 1,     // the built-in bot AI (opponents, or the player's "autonomous (bot AI)")
        Firmware = 2,  // the native C controller DLL (Autonomous mode)
    }

    /// <summary>One participant in a drive session.</summary>
    [Serializable]
    public sealed class PlayerSlot
    {
        public string name = "Player 1";
        public VehicleDesign design;          // null = stock default
        public InputDeviceKind deviceKind = InputDeviceKind.MergedKeyboardGamepad;
        public int gamepadIndex;              // into Gamepad.all when deviceKind == Gamepad
        public string profileId = "Player 1"; // key into Saves/profiles.json
        public bool isLocal = true;           // future networking: remote slots are false
        public Vehicles.AssistSettings assists; // this player's arcade assists (0 = realism)

        // Race roles (defaults = classic single human player).
        public bool isBot = false;            // AI opponent: no camera/HUD/CSV, no profile records
        public DriveControl control = DriveControl.Human;
        public int botDifficulty = 1;         // 0 Easy / 1 Medium / 2 Hard (BotAI only)
    }

    /// <summary>
    /// Static carrier describing the next session (who plays, on what devices,
    /// racing how many laps). The multiplayer menu writes it explicitly; every
    /// legacy single-player entry path (garage Drive, builder Drive, pressing
    /// Play directly in TrackScene) goes through <see cref="ResolvePlayers"/>,
    /// which synthesizes a single merged-input slot from GameFlow.ActiveDesign.
    /// </summary>
    public static class SessionConfig
    {
        public static SessionMode Mode = SessionMode.SinglePlayer;
        public static readonly List<PlayerSlot> Players = new List<PlayerSlot>();

        /// <summary>Laps to win the race; 0 = sandbox (no race).</summary>
        public static int TargetLaps;

        /// <summary>Catch-up assist for bot opponents (menu race option).</summary>
        public static bool RubberBand;

        /// <summary>Race-start countdown in seconds (0 = go immediately).</summary>
        public static int CountdownSeconds;

        /// <summary>Reset to a plain single-player session (legacy entry paths).</summary>
        public static void SetSinglePlayer()
        {
            Mode = SessionMode.SinglePlayer;
            Players.Clear();
            TargetLaps = 0; // legacy entry paths are free-drive; the menu sets laps after
            RubberBand = false;
            CountdownSeconds = 0;
        }

        /// <summary>The roster TrackBootstrap builds from — always at least one slot.</summary>
        public static List<PlayerSlot> ResolvePlayers()
        {
            // Explicit rosters (multiplayer setup, snapshot resume) are honored;
            // a split-screen roster must actually have two slots.
            if (Players.Count > 0 && (Mode != SessionMode.SplitScreen || Players.Count >= 2))
                return Players;

            // Single-player (or an incomplete roster): one merged-input slot
            // mirroring the classic GameFlow design carrier.
            var settings = Persistence.SettingsStore.Current;
            string name = settings.player1Name;
            return new List<PlayerSlot>
            {
                new PlayerSlot
                {
                    name = name,
                    profileId = name,
                    design = GameFlow.ActiveDesign,
                    deviceKind = InputDeviceKind.MergedKeyboardGamepad,
                    assists = P1Assists(settings),
                },
            };
        }

        /// <summary>Player 1's / player 2's assist prefs from the saved options.</summary>
        public static Vehicles.AssistSettings P1Assists(Persistence.GameSettings s) =>
            new Vehicles.AssistSettings
            {
                steer = s.p1AssistSteer, stability = s.p1AssistStability,
                traction = s.p1AssistTraction, abs = s.p1AssistAbs,
            };

        public static Vehicles.AssistSettings P2Assists(Persistence.GameSettings s) =>
            new Vehicles.AssistSettings
            {
                steer = s.p2AssistSteer, stability = s.p2AssistStability,
                traction = s.p2AssistTraction, abs = s.p2AssistAbs,
            };
    }
}
