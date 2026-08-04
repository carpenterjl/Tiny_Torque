using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.Track;
using AIHWSim.UI;
using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// Capture the flag. Two teams, two flags on two plinths: drive into the
    /// other side's flag to pick it up, carry it home to score. Get rammed and
    /// you drop it where you stand; a team-mate driving through a dropped flag
    /// sends it home, an opponent picks it up and carries on.
    ///
    /// Every containment test here is a distance poll in
    /// <see cref="OnMatchTick"/> rather than a trigger volume — see the note on
    /// <see cref="Flag"/> for why a persistent zone cannot use triggers in this
    /// codebase.
    /// </summary>
    public sealed class CtfDirector : ModeDirector
    {
        public static CtfDirector Instance { get; private set; }

        private readonly Flag[] _flags = new Flag[2];
        private readonly Vector3[] _bases = new Vector3[2];
        private readonly int[] _captures = new int[2];

        protected override string ModeName => "CAPTURE THE FLAG";

        protected override void OnMatchStart()
        {
            Instance = this;
            base.OnMatchStart();
            BuildField();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Put a base and a flag at each team's end. The arena states where
        /// those ends are through its spawn ring: the average of a team's own
        /// spawns is, by construction, that team's half.
        ///
        /// A <see cref="CtfBaseMarker"/> overrides that for the team it names, and
        /// only for that team — an arena that authored one base and not the other
        /// still plays, with the unauthored end where the spawn ring puts it.
        /// </summary>
        private void BuildField()
        {
            var root = new GameObject("CtfField").transform;
            var marks = FindObjectsByType<CtfBaseMarker>(FindObjectsSortMode.None);
            for (int team = 0; team < 2; team++)
            {
                CtfBaseMarker mine = null;
                foreach (var m in marks)
                    if (m != null && Mathf.Clamp(m.team, 0, 1) == team) { mine = m; break; }

                _bases[team] = ArenaNav.Drop(mine != null ? mine.transform.position
                                                          : TeamEnd(team));
                var plate = TrackBuilder.StandardMat(TeamColors[team] * 0.7f);
                TrackBuilder.Cylinder($"base_{team}", _bases[team] + Vector3.up * 0.005f,
                    new Vector3(0.9f, 0.005f, 0.9f), Quaternion.identity, plate, root,
                    collider: false);
                _flags[team] = Flag.Create(root, team, _bases[team]);
            }
        }

        /// <summary>Centroid of a team's spawns, or a point offset from the
        /// arena centre when the map did not split them.</summary>
        private Vector3 TeamEnd(int team)
        {
            var sum = Vector3.zero;
            int n = 0;
            foreach (var s in ArenaNav.Spawns)
                if (s.team == team) { sum += s.pos; n++; }
            if (n > 0) return sum / n;

            float side = team == 0 ? -1f : 1f;
            return ArenaNav.Centre + new Vector3(0f, 0f, side * ArenaNav.Radius * 0.75f);
        }

        // ---- rules -------------------------------------------------------------

        protected override void OnMatchTick()
        {
            base.OnMatchTick();
            for (int i = 0; i < _flags.Length; i++) _flags[i]?.Tick(Time.deltaTime);
            if (!IsAuthority || ShowingResults) return;

            foreach (var flag in _flags)
            {
                if (flag == null) continue;

                // A dropped flag nobody rescues goes home on its own, so a punt
                // into a corner cannot deadlock the match.
                if (flag.state == Flag.State.Dropped &&
                    Clock - flag.droppedAt > ModeConfig.FlagAutoReturnSec)
                {
                    flag.SendHome();
                    continue;
                }

                // A carrier who died drops it on the spot.
                if (flag.state == Flag.State.Carried &&
                    (flag.carrier == null || !flag.carrier.alive))
                {
                    if (flag.carrier != null) flag.carrier.carrying = -1;
                    flag.Drop(Clock);
                    continue;
                }

                foreach (var r in Racers)
                {
                    if (!r.alive || r.car == null) continue;
                    float d = Flat2(r.car.transform.position - flag.transform.position);
                    if (d > ModeConfig.FlagTouchRadius * ModeConfig.FlagTouchRadius) continue;
                    Touch(flag, r);
                }
            }

            if (BotPickDue())
                foreach (var r in Racers)
                {
                    if (!r.isBot || !r.alive || r.team < 0) continue;
                    int mine = Mathf.Clamp(r.team, 0, 1);
                    BotPolicy.Ctf(this, r, _flags[mine], _flags[1 - mine], _bases[mine]);
                }

            // Scoring is checked separately: a carrier has to reach their OWN
            // base, which is a different flag's home.
            foreach (var r in Racers)
            {
                if (r.carrying < 0 || !r.alive || r.car == null || r.team < 0) continue;
                var own = _flags[Mathf.Clamp(r.team, 0, 1)];
                if (own == null || own.state != Flag.State.Home) continue;   // yours must be home
                if (Flat2(r.car.transform.position - _bases[r.team]) >
                    ModeConfig.FlagTouchRadius * ModeConfig.FlagTouchRadius) continue;
                Capture(r);
            }
        }

        private void Touch(Flag flag, MatchRacer r)
        {
            if (r.team < 0) return;

            if (flag.team == r.team)
            {
                // Your own flag: only meaningful when it is lying on the floor,
                // and then only to send it home.
                if (flag.state == Flag.State.Dropped)
                {
                    flag.SendHome();
                    Audio.SfxPlayer.Ensure()?.PlayUi(Audio.ProceduralAudio.UiUnlock);
                }
                return;
            }

            // Theirs: pick it up from its plinth or off the floor.
            if (flag.state == Flag.State.Carried || r.carrying >= 0) return;
            flag.PickUp(r);
            r.carrying = flag.team;
            Audio.SfxPlayer.Ensure()?.PlayUi(Audio.ProceduralAudio.UiLevelUp);
        }

        private void Capture(MatchRacer r)
        {
            var carried = _flags[Mathf.Clamp(r.carrying, 0, 1)];
            r.carrying = -1;
            r.score++;
            _captures[Mathf.Clamp(r.team, 0, 1)]++;
            carried?.SendHome();
            Audio.SfxPlayer.Ensure()?.PlayUi(Audio.ProceduralAudio.UiUnlock);

            if (_captures[r.team] < Mathf.Max(1, SessionConfig.TargetScore)) return;
            foreach (var m in Racers)
                if (m.team == r.team) { m.place = 1; RaiseFinished(m.rig, 1); }
                else m.place = 2;
            EnterResults();
        }

        /// <summary>A hard enough shunt knocks the flag loose. Called by the
        /// impact classifier on the car that was hit.</summary>
        protected override void OnImpact(MatchRacer self, CarImpact.Hit hit)
        {
            if (!IsAuthority || self.carrying < 0) return;
            if (hit.kind == CarImpact.Kind.Wall) return;
            var other = RacerOf(hit.other);
            if (other == null || other.team == self.team) return;
            if (hit.speed < ModeConfig.FlagDropImpulse) return;

            var flag = _flags[Mathf.Clamp(self.carrying, 0, 1)];
            self.carrying = -1;
            flag?.Drop(Clock);
        }

        private static float Flat2(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude;
        }

        // ---- HUD + results -----------------------------------------------------

        protected override void DrawLiveBanner()
        {
            var area = new Rect((UIScale.W - 260f) * 0.5f, 40f, 260f, 62f);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label($"CAPTURE THE FLAG — first to {SessionConfig.TargetScore}",
                GarageSkin.Header);
            GUILayout.Label($"Blue {_captures[0]}   ·   Orange {_captures[1]}", GarageSkin.StatLabel);
            string held = "";
            foreach (var r in Racers)
                if (r.carrying >= 0) held += $"{NameOf(r)} has the {TeamName(r.carrying)} flag  ";
            if (held.Length > 0) GUILayout.Label(held, GarageSkin.StatLabel);
            GUILayout.EndArea();
        }

        protected override float ResultRowsHeight => 26f + Racers.Count * 22f;

        protected override void DrawResultRows()
        {
            bool blueWon = _captures[0] > _captures[1];
            var head = new GUIStyle(GarageSkin.Header) { alignment = TextAnchor.MiddleCenter };
            GUILayout.Label(_captures[0] == _captures[1]
                ? $"DRAW — {_captures[0]} each"
                : $"{(blueWon ? "BLUE" : "ORANGE")} WINS — {_captures[0]} : {_captures[1]}", head);

            var sorted = new List<MatchRacer>(Racers);
            sorted.Sort((a, b) => b.score.CompareTo(a.score));
            foreach (var r in sorted)
                GUILayout.Label($"{TeamName(r.team)}  {NameOf(r)}   {r.score} captures");
        }

        private static string TeamName(int team) => team == 0 ? "Blue" : team == 1 ? "Orange" : "—";
    }
}
