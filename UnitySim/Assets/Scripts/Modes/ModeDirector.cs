using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.Track;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// What the three arena modes share: a roster of <see cref="MatchRacer"/>
    /// state bags, an authority flag, a match clock, collision plumbing, and the
    /// spectate/respawn moves that every mode needs and none of them should
    /// re-implement.
    ///
    /// The shape is <c>ArcadeDirector</c>'s, deliberately — singleton-ish
    /// director + <c>IsAuthority</c> + <c>Register(rig)</c> returning a per-car
    /// bag + an authority loop — because that shape is already proven against
    /// LAN, bots and split-screen in this codebase. What it adds on top of
    /// <see cref="MatchDirector"/> is everything that assumes an ARENA.
    ///
    /// Authority: on a LAN host and in every local session this machine decides;
    /// on a client the director still runs (it draws the HUD and holds state)
    /// but never rules on a hit. Same split as ArcadeDirector, same reason.
    /// </summary>
    public abstract class ModeDirector : MatchDirector
    {
        /// <summary>Does this machine adjudicate? Set by TrackBootstrap.</summary>
        public bool IsAuthority = true;

        /// <summary>Seconds since the match was composed. A single clock the
        /// whole mode shares, so grace windows and cooldowns are comparable and
        /// a pause stops all of them together.</summary>
        public float Clock { get; private set; }

        private readonly List<MatchRacer> _racers = new List<MatchRacer>();
        public IReadOnlyList<MatchRacer> Racers => _racers;

        /// <summary>Team tints, used for the car accent and the HUD. Two teams
        /// is all any mode here needs.</summary>
        public static readonly Color[] TeamColors =
        {
            new Color(0.25f, 0.55f, 1f),   // 0 — blue
            new Color(1f, 0.42f, 0.22f),   // 1 — orange
        };

        protected abstract string ModeName { get; }

        protected override string ResultsTitle => $"{ModeName} — RESULTS";

        protected override float ResultButtonsHeight => 30f;

        protected override void OnMatchStart()
        {
            foreach (var rig in players) Register(rig);
            foreach (var r in _racers) r.ResetForMatch(Clock);
        }

        /// <summary>
        /// Give a rig its mini-game state. Refuses a firmware rig for the same
        /// reason the arcade layer does: the C controller is driving a science
        /// experiment and has no business being rammed.
        /// </summary>
        public MatchRacer Register(PlayerRig rig)
        {
            if (rig == null || rig.car == null) return null;
            if (rig.slot != null && rig.slot.control == DriveControl.Firmware) return null;
            if (rig.match != null) return rig.match;

            var racer = rig.car.gameObject.AddComponent<MatchRacer>();
            racer.rig = rig;
            racer.car = rig.car;
            racer.netSlot = rig.netSlot;
            racer.isBot = rig.slot != null && rig.slot.isBot;
            racer.slotIsLocal = rig.runner != null;
            racer.team = rig.slot?.team ?? -1;
            racer.ResetForMatch(Clock);

            // Impacts are classified on the car and adjudicated here.
            var impact = rig.car.gameObject.AddComponent<CarImpact>();
            impact.Impact += hit => OnImpact(racer, hit);

            rig.match = racer;
            _racers.Add(racer);
            return racer;
        }

        /// <summary>A classified collision on one of our cars. Authority only —
        /// every override should return early when <see cref="IsAuthority"/> is
        /// false, so a client never decides who got hurt.</summary>
        protected virtual void OnImpact(MatchRacer self, CarImpact.Hit hit) { }

        protected override void Update()
        {
            // The clock runs through the countdown too: spawn grace should be
            // measured from composition, not from GO.
            Clock += Time.deltaTime;
            base.Update();
        }

        private float _nextBotPick;

        /// <summary>True a few times a second, so a director can re-target its
        /// bots without doing it every frame (which makes them jitter between
        /// two equally good choices).</summary>
        protected bool BotPickDue()
        {
            if (Clock < _nextBotPick) return false;
            _nextBotPick = Clock + BotPolicy.RepickSec;
            return true;
        }

        public override void ResetMatch()
        {
            base.ResetMatch();
            foreach (var r in _racers)
            {
                r.ResetForMatch(Clock);
                Revive(r);
            }
        }

        // ---- roster helpers ----------------------------------------------------

        public MatchRacer RacerOf(CarVehicle car)
        {
            if (car == null) return null;
            foreach (var r in _racers) if (r.car == car) return r;
            return null;
        }

        public int AliveCount()
        {
            int n = 0;
            foreach (var r in _racers) if (r.alive) n++;
            return n;
        }

        public int TeamScore(int team)
        {
            int n = 0;
            foreach (var r in _racers) if (r.team == team) n += r.score;
            return n;
        }

        protected static string NameOf(MatchRacer r) =>
            r?.rig?.slot?.name ?? "car";

        protected static float DeathAt(MatchRacer r) => r?.deathAt ?? 0f;

        protected static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        // ---- life and death ----------------------------------------------------

        /// <summary>
        /// Park a dead car: frozen, invisible, and out of the physics of the
        /// fight. NOT destroyed — its camera is still a viewport in split-screen
        /// and its rig is still in every list — so it becomes a spectator rather
        /// than a hole in the roster.
        /// </summary>
        protected void Spectate(MatchRacer r)
        {
            if (r?.car == null) return;
            r.car.Frozen = true;
            foreach (var mr in r.car.GetComponentsInChildren<Renderer>(true)) mr.enabled = false;
            foreach (var col in r.car.GetComponentsInChildren<Collider>(true)) col.enabled = false;
        }

        /// <summary>Undo <see cref="Spectate"/> — used by a restart, and by the
        /// modes that respawn instead of eliminating.</summary>
        protected void Revive(MatchRacer r)
        {
            if (r?.car == null) return;
            foreach (var mr in r.car.GetComponentsInChildren<Renderer>(true)) mr.enabled = true;
            foreach (var col in r.car.GetComponentsInChildren<Collider>(true)) col.enabled = true;
            r.car.Frozen = false;
        }

        /// <summary>
        /// Put a car back on a free spawn, upright and facing the fight, with a
        /// grace window. Teleports through <c>RestoreState</c> rather than
        /// writing the transform, because that is what syncs the physics body.
        /// </summary>
        protected void Respawn(MatchRacer r, bool healFull)
        {
            if (r?.car == null) return;
            Vector3 pos;
            Quaternion rot;
            if (!ArenaNav.TrySpawn(r.team, IndexOf(r), out pos, out rot))
                ArenaNav.TryNearestFree(r.car.transform.position, 0.45f, out pos, out rot);

            r.car.RestoreState(pos, rot, Vector3.zero, Vector3.zero);
            Revive(r);
            r.alive = true;
            r.invulnUntil = Clock + ModeConfig.SpawnGrace;
            if (healFull) r.health = r.maxHealth;
        }

        private int IndexOf(MatchRacer r)
        {
            for (int i = 0; i < _racers.Count; i++) if (_racers[i] == r) return i;
            return 0;
        }

        protected override void DrawResultButtons()
        {
            if (UI.MenuNav.Button("Rematch", GUILayout.Height(30)))
            {
                Time.timeScale = 1f;
                UI.AwardReveal.Dismiss();
                UI.ScreenFade.To(GameFlow.LoadTrack); // SessionConfig persists
            }
        }
    }
}
