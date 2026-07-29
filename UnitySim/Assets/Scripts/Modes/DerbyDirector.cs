using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.Track;
using AIHWSim.UI;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// Demolition derby: everyone in a walled arena, ram each other, last one
    /// still running wins.
    ///
    /// A sibling of <c>ArcadeDirector</c>, not of RaceDirector — it owns per-car
    /// state, runs an authority loop, and raises the shared finish event that
    /// pays crates. What it borrows from the arcade layer is the WRECK: being
    /// eliminated punts and tumbles the car exactly the way a missile hit does,
    /// because that already looks right and already works over LAN.
    /// </summary>
    public sealed class DerbyDirector : ModeDirector
    {
        public static DerbyDirector Instance { get; private set; }

        private readonly List<MatchRacer> _dying = new List<MatchRacer>();
        private int _nextPlaceFromLast;

        protected override string ModeName => "DEMOLITION";

        protected override void OnMatchStart()
        {
            Instance = this;
            base.OnMatchStart();
            // Places are handed out from the BOTTOM: the first car wrecked is
            // last, and whoever survives takes P1. A derby has no finish line to
            // count up from.
            _nextPlaceFromLast = Racers.Count;
            BuildPickups();
        }

        /// <summary>
        /// Scatter repairs and bomb crates on two rings inside the arena —
        /// repairs further out (a wounded car has to leave the middle to heal),
        /// bombs nearer the centre (arming one means going where the fight is).
        /// Placed procedurally rather than authored so any arena works, the same
        /// way ArcadeDirector generates item boxes on a map that has none.
        /// </summary>
        private void BuildPickups()
        {
            var root = new GameObject("DerbyPickups").transform;
            const int Repairs = 4, Mines = 4;

            for (int i = 0; i < Repairs; i++)
            {
                float a = i * Mathf.PI * 2f / Repairs + Mathf.PI / Repairs;
                var p = ArenaNav.Centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a))
                        * (ArenaNav.Radius * 0.62f);
                ArenaPickup.Create(root, ArenaNav.Drop(p) + Vector3.up * 0.06f,
                    ArenaPickup.Kind.Repair);
            }
            for (int i = 0; i < Mines; i++)
            {
                float a = i * Mathf.PI * 2f / Mines;
                var p = ArenaNav.Centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a))
                        * (ArenaNav.Radius * 0.28f);
                ArenaPickup.Create(root, ArenaNav.Drop(p) + Vector3.up * 0.06f,
                    ArenaPickup.Kind.Mine);
            }
        }

        /// <summary>The use-item button, routed here by CarInput. Drops the held
        /// mine behind the car — behind, because a mine you drive over yourself
        /// is a joke rather than a weapon.</summary>
        public void RequestUse(CarVehicle car)
        {
            if (!IsAuthority) return;
            var r = RacerOf(car);
            if (r == null || !r.alive || r.heldMines <= 0) return;
            r.heldMines--;

            Vector3 behind = car.transform.position - car.transform.forward * 0.35f;
            Landmine.Create(null, ArenaNav.Drop(behind) + Vector3.up * 0.03f, r, Clock);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        protected override void OnImpact(MatchRacer self, CarImpact.Hit hit)
        {
            if (!IsAuthority || !self.alive) return;

            switch (hit.kind)
            {
                case CarImpact.Kind.Ram:
                {
                    // The damage lands on the car that was hit, scaled by how
                    // hard it was hit. The rammer pays nothing — that is the
                    // whole incentive to line a hit up.
                    var victim = RacerOf(hit.other);
                    if (victim != null && victim != self)
                        Damage(victim, ModeConfig.RamDamage * hit.severity, self);
                    break;
                }
                case CarImpact.Kind.Side:
                {
                    // Trading paint costs both of them. Only the car raising the
                    // event is charged here; the other car raises its own.
                    Damage(self, ModeConfig.SideDamage * hit.severity, RacerOf(hit.other));
                    break;
                }
                default:
                    Damage(self, ModeConfig.WallDamage * hit.severity, null);
                    break;
            }
        }

        /// <summary>Take health off a car and, if that killed it, wreck it.</summary>
        public void Damage(MatchRacer victim, float amount, MatchRacer attacker)
        {
            if (victim == null || !IsAuthority) return;
            if (!victim.ApplyDamage(amount, Clock)) return;
            Eliminate(victim, attacker);
        }

        private void Eliminate(MatchRacer victim, MatchRacer attacker)
        {
            victim.place = Mathf.Max(1, _nextPlaceFromLast--);
            victim.deathAt = Clock;
            _dying.Add(victim);

            // The arcade layer's wreck, reused: a forward punt plus a tumble.
            // Direction is away from the attacker when there was one, otherwise
            // straight up out of whatever the car drove into.
            Vector3 dir = attacker != null && attacker.car != null
                ? Flat(victim.car.transform.position - attacker.car.transform.position)
                : Flat(-victim.car.transform.forward);
            victim.car.ArcadeImpulse(dir * 2.2f + Vector3.up * 1.6f,
                                     victim.car.transform.position);
            victim.car.ArcadeTorqueImpulse(new Vector3(
                Random.Range(-0.04f, 0.04f), Random.Range(-0.06f, 0.06f), 0.05f));

            RaiseFinished(victim.rig, victim.place);
            Audio.SfxPlayer.Ensure()?.PlayUi(Audio.ProceduralAudio.UiDeny);
        }

        protected override void OnMatchTick()
        {
            base.OnMatchTick();
            if (!IsAuthority || ShowingResults) return;

            // A wrecked car is left to tumble for a beat before it is frozen
            // into a spectator, so the kill has time to read.
            for (int i = _dying.Count - 1; i >= 0; i--)
            {
                var r = _dying[i];
                if (r == null) { _dying.RemoveAt(i); continue; }
                if (Clock - DeathAt(r) < ModeConfig.DeathBeatSec) continue;
                Spectate(r);
                _dying.RemoveAt(i);
            }

            if (BotPickDue())
                foreach (var r in Racers)
                    if (r.isBot && r.alive) BotPolicy.Derby(this, r);

            if (AliveCount() > 1) return;

            // One (or nobody) left: the survivor takes first.
            foreach (var r in Racers)
                if (r.alive && r.place == 0)
                {
                    r.place = 1;
                    RaiseFinished(r.rig, 1);
                }
            EnterResults();
        }

        // ---- results ----------------------------------------------------------

        protected override void DrawResultRows()
        {
            var sorted = new List<MatchRacer>(Racers);
            sorted.Sort((a, b) =>
            {
                int pa = a.place == 0 ? int.MaxValue : a.place;
                int pb = b.place == 0 ? int.MaxValue : b.place;
                return pa.CompareTo(pb);
            });
            foreach (var r in sorted)
            {
                string place = r.place > 0 ? $"P{r.place}" : "--";
                string state = r.alive ? $"{Mathf.RoundToInt(r.health)} hp left" : "wrecked";
                GUILayout.Label($"{place}  {NameOf(r)}   {state}");
            }
        }

        protected override void DrawLiveBanner()
        {
            var area = new Rect((UIScale.W - 240f) * 0.5f, 40f, 240f, 26f + Racers.Count * 18f);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label($"DEMOLITION — {AliveCount()} still running", GarageSkin.Header);
            foreach (var r in Racers)
            {
                string state = r.alive ? $"{Mathf.RoundToInt(r.health)} hp" : "wrecked";
                GUILayout.Label($"{NameOf(r)}: {state}", GarageSkin.StatLabel);
            }
            GUILayout.EndArea();
        }
    }
}
