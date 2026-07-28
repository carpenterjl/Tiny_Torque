using System;
using System.Collections.Generic;
using AIHWSim.Garage;
using UnityEngine;

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

        /// <summary>Arcade mode: item boxes, power-ups, weapons, arcade scoreboard.</summary>
        public static bool Arcade;

        /// <summary>Off-track detection + penalty (sub-toggle of arcade).</summary>
        public static bool TrackLimits;

        /// <summary>This race is a championship round: the results screen banks
        /// points into <see cref="Championship"/> and offers the next round
        /// instead of a rematch.</summary>
        public static bool ChampionshipRound;

        /// <summary>
        /// Arcade handling: every car in the session — humans and bots alike — is
        /// raised to an assist floor and a grip baseline, so the themed circuits
        /// can be driven on a keyboard without catching a slide.
        ///
        /// False races the same maps on the honest I22 brush-tyre model. This
        /// exists as a choice rather than an always-on because the sim fidelity is
        /// the point of the project; arcade is just a different way to use it.
        /// </summary>
        public static bool ArcadeHandling = true;

        /// <summary>Reset to a plain single-player session (legacy entry paths).</summary>
        public static void SetSinglePlayer()
        {
            Mode = SessionMode.SinglePlayer;
            Players.Clear();
            TargetLaps = 0; // legacy entry paths are free-drive; the menu sets laps after
            RubberBand = false;
            CountdownSeconds = 0;
            // Every legacy entry path (garage Drive, builder Drive, the stale-LAN
            // guard) funnels through here, which makes this the one place that has
            // to clear arcade — otherwise a race's flags leak into a free-drive.
            Arcade = false;
            TrackLimits = false;
            ArcadeHandling = true;   // the default the menu offers next time
            ChampionshipRound = false;
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

        /// <summary>Named assist levels. Custom means "use the four sliders".</summary>
        public enum AssistPreset { Off = 0, Standard = 1, Full = 2, Custom = 3 }

        /// <summary>
        /// The four values behind a preset. Standard is the shipping default and
        /// is meant to feel like a well-sorted car rather than like help: enough
        /// traction control that the rears do not light up out of every slow
        /// corner, not enough that the tyre model stops deciding whether you make
        /// the turn.
        /// </summary>
        public static Vehicles.AssistSettings PresetValues(AssistPreset p) => p switch
        {
            AssistPreset.Standard => new Vehicles.AssistSettings
            { steer = 0.45f, stability = 0.50f, traction = 0.60f, abs = 0.60f },
            // Full sits deliberately ABOVE each ramp's floor (steer .80 /
            // stability .70 / traction .90 / abs .90 — see AssistTuning), because
            // a preset that landed exactly on them would get none of the extra
            // authority those ramps exist to provide and would feel identical to
            // Arcade handling.
            AssistPreset.Full => new Vehicles.AssistSettings
            { steer = 0.90f, stability = 0.90f, traction = 0.95f, abs = 0.95f },
            _ => default,     // Off, and Custom (whose caller reads the sliders)
        };

        /// <summary>
        /// Player 1's / player 2's assist prefs from the saved options.
        ///
        /// These two methods are the single choke point every entry path already
        /// funnels through — the menu, the LAN session, and the local player
        /// resolver all call them — which is why routing the preset through HERE
        /// puts it into every session type without editing any of those files.
        /// </summary>
        public static Vehicles.AssistSettings P1Assists(Persistence.GameSettings s)
        {
            var p = (AssistPreset)Mathf.Clamp(s.assistPreset, 0, 3);
            if (p != AssistPreset.Custom) return PresetValues(p);
            return new Vehicles.AssistSettings
            {
                steer = s.p1AssistSteer, stability = s.p1AssistStability,
                traction = s.p1AssistTraction, abs = s.p1AssistAbs,
            };
        }

        public static Vehicles.AssistSettings P2Assists(Persistence.GameSettings s)
        {
            var p = (AssistPreset)Mathf.Clamp(s.assistPreset, 0, 3);
            if (p != AssistPreset.Custom) return PresetValues(p);
            return new Vehicles.AssistSettings
            {
                steer = s.p2AssistSteer, stability = s.p2AssistStability,
                traction = s.p2AssistTraction, abs = s.p2AssistAbs,
            };
        }
    }
}
