using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.Track;
using AIHWSim.UI;
using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// Car soccer. Two teams, one ball, a goal mouth at each end; first to the
    /// target score, or ahead when the clock runs out.
    ///
    /// This is the mode that turns the aerials on: every car gets an
    /// <see cref="AerialControl"/>, and only here does <c>CarVehicle</c>'s
    /// aerial channel come off its default of off.
    ///
    /// Goals are polled, not triggered — the same reasoning as the flag zones.
    /// </summary>
    public sealed class SoccerDirector : ModeDirector
    {
        public static SoccerDirector Instance { get; private set; }

        private SoccerBall _ball;
        private readonly Vector3[] _goals = new Vector3[2];
        private readonly int[] _score = new int[2];
        private float _celebrateUntil = -1f;
        private int _lastScorer = -1;

        /// <summary>Half-width and half-height of a goal mouth. A goal is a
        /// volume test rather than a trigger, so these are the box.</summary>
        private static readonly Vector3 GoalHalf = new Vector3(0.9f, 0.5f, 0.35f);

        /// <summary>Each goal's orientation and size, alongside its position in
        /// <see cref="_goals"/>. Filled with identity and <see cref="GoalHalf"/>
        /// when the arena authored no <see cref="ArenaGoalMarker"/>, which is what
        /// makes the derived and the authored pitch the same code: a point test
        /// rotated by identity IS the world-axis test this mode shipped with.</summary>
        private readonly Quaternion[] _goalRot = { Quaternion.identity, Quaternion.identity };
        private readonly Vector3[] _goalHalf = { GoalHalf, GoalHalf };

        public SoccerBall Ball => _ball;
        public Vector3 GoalOf(int team) => _goals[Mathf.Clamp(team, 0, 1)];

        protected override string ModeName => "SOCCER";

        protected override void OnMatchStart()
        {
            Instance = this;
            base.OnMatchStart();
            BuildPitch();

            // Aerials, on for this mode only.
            foreach (var r in Racers)
            {
                if (r.car == null) continue;
                AerialControl.Attach(r.car, r.rig?.input);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void BuildPitch()
        {
            var root = new GameObject("SoccerPitch").transform;
            ResolveGoals();

            for (int team = 0; team < 2; team++)
            {
                var mat = TrackBuilder.StandardMat(TeamColors[team] * 0.8f);
                var rot = _goalRot[team];
                var half = _goalHalf[team];
                // A visible mouth: two posts and a bar. Collider-less, because a
                // goal that blocks the ball is not a goal. Offsets go through the
                // goal's rotation so an angled goal gets angled posts; with the
                // derived pitch that rotation is identity and the arithmetic is
                // the world-axis one this mode shipped with.
                for (int s = -1; s <= 1; s += 2)
                    TrackBuilder.Box($"post_{team}_{s}",
                        _goals[team] + rot * new Vector3(s * half.x, 0f, 0f),
                        new Vector3(0.05f, half.y * 2f, 0.05f),
                        rot, mat, root, collider: false);
                TrackBuilder.Box($"bar_{team}",
                    _goals[team] + rot * new Vector3(0f, half.y, 0f),
                    new Vector3(half.x * 2f, 0.05f, 0.05f),
                    rot, mat, root, collider: false);
            }

            // The centre spot, authored if the pitch says where it is. The
            // marker's height is taken verbatim — see ArenaBallSpawn for why.
            var spot = ArenaBallSpawn.Find();
            Vector3 home = spot != null
                ? spot.transform.position
                : ArenaNav.Drop(ArenaNav.Centre) + Vector3.up * 0.2f;
            _ball = SoccerBall.Create(root, home, IsAuthority);
        }

        /// <summary>
        /// Where the two goals are, how they are turned and how big they are.
        ///
        /// A team's <see cref="ArenaGoalMarker"/> wins when the scene has one;
        /// otherwise that team's goal is derived the way it always was — the
        /// centroid of its spawns, dropped onto the floor, lifted half a mouth
        /// height, unrotated, at the shipped size. The two are resolved per team
        /// rather than all-or-nothing so a pitch that authored one end and not the
        /// other still plays, with the unauthored end where it would have been.
        /// </summary>
        private void ResolveGoals()
        {
            var marks = FindObjectsByType<ArenaGoalMarker>(FindObjectsSortMode.None);
            for (int team = 0; team < 2; team++)
            {
                ArenaGoalMarker mine = null;
                foreach (var m in marks)
                    if (m != null && Mathf.Clamp(m.team, 0, 1) == team) { mine = m; break; }

                if (mine != null)
                {
                    _goals[team] = mine.transform.position;
                    _goalRot[team] = mine.transform.rotation;
                    _goalHalf[team] = mine.halfExtents;
                    continue;
                }

                _goals[team] = ArenaNav.Drop(TeamEnd(team)) + Vector3.up * GoalHalf.y;
                _goalRot[team] = Quaternion.identity;
                _goalHalf[team] = GoalHalf;
            }
        }

        /// <summary>Centroid of a team's spawns — the end they defend.</summary>
        private Vector3 TeamEnd(int team)
        {
            var sum = Vector3.zero;
            int n = 0;
            foreach (var s in ArenaNav.Spawns)
                if (s.team == team) { sum += s.pos; n++; }
            if (n > 0) return sum / n;

            float side = team == 0 ? -1f : 1f;
            return ArenaNav.Centre + new Vector3(0f, 0f, side * ArenaNav.Radius * 0.9f);
        }

        // ---- rules -------------------------------------------------------------

        protected override void OnMatchTick()
        {
            base.OnMatchTick();
            if (_ball == null) return;

            // Celebration freeze: the ball and everyone else are held while the
            // goal lands, then the pitch resets and play resumes.
            if (_celebrateUntil > 0f)
            {
                if (Clock < _celebrateUntil) return;
                _celebrateUntil = -1f;
                KickOff();
                return;
            }

            if (!IsAuthority || ShowingResults) return;

            // A ball that has escaped the arena (a wall gap, a physics blow-out)
            // comes back rather than ending the match in confusion.
            if (!ArenaNav.Contains(_ball.Position)) { _ball.Reset(); return; }

            if (BotPickDue())
                foreach (var r in Racers)
                {
                    if (!r.isBot || !r.alive || r.team < 0) continue;
                    int mine = Mathf.Clamp(r.team, 0, 1);
                    // You attack the goal you do NOT defend.
                    BotPolicy.Soccer(this, r, _ball.Position, _goals[mine], _goals[1 - mine]);
                }

            for (int team = 0; team < 2; team++)
            {
                // Into the goal's own axes first. On the derived pitch the
                // rotation is identity, so this reduces to the world-axis delta
                // the mode has always tested.
                Vector3 d = Quaternion.Inverse(_goalRot[team]) * (_ball.Position - _goals[team]);
                var half = _goalHalf[team];
                if (Mathf.Abs(d.x) > half.x ||
                    Mathf.Abs(d.y) > half.y ||
                    Mathf.Abs(d.z) > half.z) continue;
                // Scored ON team `team`, so the OTHER side gets the point.
                Score(1 - team);
                break;
            }
        }

        private void Score(int team)
        {
            _score[team]++;
            _lastScorer = team;
            _celebrateUntil = Clock + ModeConfig.GoalCelebrationSec;

            // Credit the nearest attacker, so the results table has a scorer.
            MatchRacer best = null;
            float bestD = float.MaxValue;
            foreach (var r in Racers)
            {
                if (r.team != team || r.car == null) continue;
                float d = (r.car.transform.position - _ball.Position).sqrMagnitude;
                if (d >= bestD) continue;
                bestD = d; best = r;
            }
            if (best != null) best.score++;

            FreezeCars(true);
            Audio.SfxPlayer.Ensure()?.PlayUi(Audio.ProceduralAudio.UiUnlock);

            if (_score[team] < Mathf.Max(1, SessionConfig.TargetScore)) return;
            foreach (var m in Racers)
            {
                m.place = m.team == team ? 1 : 2;
                if (m.team == team) RaiseFinished(m.rig, 1);
            }
            _celebrateUntil = -1f;
            EnterResults();
        }

        /// <summary>Reset the pitch: ball on the spot, cars on their spawns,
        /// tanks topped up.</summary>
        private void KickOff()
        {
            _ball?.Reset();
            foreach (var r in Racers)
            {
                Respawn(r, healFull: true);
                AerialControl.Of(r.car)?.AddBoost(ModeConfig.BoostTankSec);
            }
            FreezeCars(false);
        }

        // ---- HUD + results -----------------------------------------------------

        protected override void DrawLiveBanner()
        {
            var area = new Rect((UIScale.W - 260f) * 0.5f, 40f, 260f, 62f);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label($"SOCCER — first to {SessionConfig.TargetScore}", GarageSkin.Header);
            GUILayout.Label($"Blue {_score[0]}   ·   Orange {_score[1]}", GarageSkin.StatLabel);
            if (_celebrateUntil > 0f)
                GUILayout.Label($"GOAL — {(_lastScorer == 0 ? "Blue" : "Orange")}!",
                    GarageSkin.Header);
            GUILayout.EndArea();
        }

        protected override float ResultRowsHeight => 26f + Racers.Count * 22f;

        protected override void DrawResultRows()
        {
            var head = new GUIStyle(GarageSkin.Header) { alignment = TextAnchor.MiddleCenter };
            GUILayout.Label(_score[0] == _score[1]
                ? $"DRAW — {_score[0]} each"
                : $"{(_score[0] > _score[1] ? "BLUE" : "ORANGE")} WINS — {_score[0]} : {_score[1]}",
                head);

            var sorted = new List<MatchRacer>(Racers);
            sorted.Sort((a, b) => b.score.CompareTo(a.score));
            foreach (var r in sorted)
                GUILayout.Label($"{(r.team == 0 ? "Blue" : "Orange")}  {NameOf(r)}   {r.score} goals");
        }
    }
}
