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

    /// <summary>
    /// What the rules of the next session ARE, as distinct from
    /// <see cref="SessionMode"/>, which says who is playing and on whose machine.
    ///
    /// Lives here rather than in AIHWSim.Modes for the same reason
    /// <see cref="DriveControl"/> does: SessionConfig has to name it, and Core
    /// must not depend on the layers built on top of it.
    ///
    /// APPEND-ONLY — the value goes on the wire in the LAN welcome.
    /// </summary>
    public enum MatchMode
    {
        Race = 0,     // first to N laps; the only mode before this existed
        Derby = 1,    // last car standing in a walled arena
        Ctf = 2,      // two teams, two flags
        Soccer = 3,   // two teams, one ball, two goals
        FreeRoam = 4, // no rules at all: a town, and a car
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

        /// <summary>Team index in a team mode; -1 = free-for-all, which is what
        /// a race and a demolition derby both are.</summary>
        public int team = -1;
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

        /// <summary>
        /// How long the race keeps running after the FIRST car finishes, before
        /// the results screen appears and everyone still out there is a DNF.
        /// 0 = wait for the whole field.
        ///
        /// Waiting for everyone is only reasonable when everyone can finish. A bot
        /// wedged against a wall never does, so a race with no grace period simply
        /// never ends — which is what this defaulting to 30 rather than 0 is for.
        /// </summary>
        public static int ResultsWaitSeconds = 30;

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

        /// <summary>
        /// Whether tyre temperature and pressure stay switched on under
        /// <see cref="ArcadeHandling"/>.
        ///
        /// Off by default, and only because the arcade grip floor was balanced
        /// without it — a car whose tyres are cold for the first half-lap is not
        /// what "drivable on a keyboard" was tuned against. It is a checkbox rather
        /// than a rule because the two are genuinely independent: wanting forgiving
        /// grip and wanting tyres that warm up is a perfectly coherent pair of
        /// preferences, and nothing in the physics objects to it.
        ///
        /// Read only by <see cref="HandlingFloor"/>, and only meaningful when
        /// arcade handling is on: a sim-handling session always leaves the thermal
        /// model to the wheels' own <c>pressureKpa</c>.
        /// </summary>
        public static bool ArcadeTyreThermal;

        /// <summary>What rules the next session runs. Race keeps every legacy
        /// entry path behaving exactly as it did.</summary>
        public static MatchMode Match = MatchMode.Race;

        /// <summary>Score that ends a CTF or soccer match (captures / goals).</summary>
        public static int TargetScore = 3;

        /// <summary>Match clock in seconds; 0 = no time limit.</summary>
        public static int TimeLimitSec;

        /// <summary>Is the next session one of the arena mini-games? Every one of
        /// them wants the arena composition path and none of them wants laps.
        /// Free roam is deliberately NOT one: it has spawn points, but no
        /// director, no scoreboard and nothing to win.</summary>
        public static bool IsArenaMatch =>
            Match == MatchMode.Derby || Match == MatchMode.Ctf || Match == MatchMode.Soccer;

        /// <summary>Do the rules split the grid into two sides?</summary>
        public static bool IsTeamMatch => Match == MatchMode.Ctf || Match == MatchMode.Soccer;

        /// <summary>Reset to a plain single-player session (legacy entry paths).</summary>
        public static void SetSinglePlayer()
        {
            Mode = SessionMode.SinglePlayer;
            Players.Clear();
            TargetLaps = 0; // legacy entry paths are free-drive; the menu sets laps after
            RubberBand = false;
            CountdownSeconds = 0;
            ResultsWaitSeconds = 30;   // the default the menu offers next time
            // Every legacy entry path (garage Drive, builder Drive, the stale-LAN
            // guard) funnels through here, which makes this the one place that has
            // to clear arcade — otherwise a race's flags leak into a free-drive.
            Arcade = false;
            TrackLimits = false;
            ArcadeHandling = true;   // the default the menu offers next time
            ArcadeTyreThermal = false;
            ChampionshipRound = false;
            Match = MatchMode.Race;
            TargetScore = 3;
            TimeLimitSec = 0;
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

        /// <summary>
        /// One AI opponent: a preset car with a distinct paint colour.
        ///
        /// Lives here rather than in the menu because it now has two callers — the
        /// menu's race and championship starts, and a scene whose
        /// <c>LevelSettings</c> asks for opponents on a direct Play. Two copies of
        /// "what a bot slot is" would drift in the one field that matters
        /// (<c>assists</c>, which is deliberately empty so bots race on raw
        /// physics) and the drift would present as bots that handle differently
        /// depending on how you started.
        /// </summary>
        /// <param name="k">1-based opponent number: picks the preset and the hue,
        /// so opponent 1 always looks the same.</param>
        public static PlayerSlot MakeBotSlot(int k, int difficulty)
        {
            var preset = VehiclePresets.All[(k - 1) % VehiclePresets.All.Length];
            var design = preset.build();
            design.liveryPng = "";                                   // show the flat colour
            design.bodyColor = Color.HSVToRGB((k * 0.137f) % 1f, 0.65f, 0.95f);
            return new PlayerSlot
            {
                name = $"Bot {k} · {preset.name}",
                profileId = $"Bot {k}",
                design = design,
                isBot = true,
                control = DriveControl.BotAI,
                botDifficulty = difficulty,
                assists = new Vehicles.AssistSettings(),   // bots race on raw physics
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
            { steer = 0.45f, stability = 0.50f, traction = 0.60f, abs = 0.60f, launch = 0.50f },
            // Full sits deliberately ABOVE each ramp's floor (steer .80 /
            // stability .70 / traction .90 / abs .90 — see AssistTuning), because
            // a preset that landed exactly on them would get none of the extra
            // authority those ramps exist to provide and would feel identical to
            // Arcade handling.
            AssistPreset.Full => new Vehicles.AssistSettings
            { steer = 0.90f, stability = 0.90f, traction = 0.95f, abs = 0.95f, launch = 1f },
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
                launch = s.p1AssistLaunch,
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
                launch = s.p2AssistLaunch,
            };
        }
    }
}
