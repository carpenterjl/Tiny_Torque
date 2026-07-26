using System;
using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Track;
using AIHWSim.TrackEd;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// The arcade brain: item boxes, the roulette, effect lifetimes, projectiles,
    /// live positions, bot item policy and track limits. One scene singleton, all
    /// gameplay in Update.
    ///
    /// It is a director rather than per-car logic for two reasons. First, cost:
    /// SimulationRunner.FixedUpdate runs once PER RIG at 400 Hz, so a decision put
    /// there would execute eight times per physics step in a full race. Second,
    /// scope: choosing a missile target or a bot's moment to fire needs the whole
    /// field, which no single car can see.
    ///
    /// In LAN only the host is the authority. Clients construct this too — with
    /// <see cref="IsAuthority"/> false — purely to render boxes, mirror HUD state
    /// and fly visual copies of projectiles.
    /// </summary>
    public sealed class ArcadeDirector : MonoBehaviour
    {
        public static ArcadeDirector Instance { get; private set; }

        /// <summary>
        /// Seconds since this race went green. One monotonic clock for every
        /// arcade deadline and every logged event: Time.time is wrong here because
        /// PauseMenu scales it in non-LAN sessions, and a paused or restarted race
        /// would produce an unsplittable timeline.
        /// </summary>
        public static float Clock { get; private set; }

        /// <summary>False on a LAN client: mirror state, never decide it.</summary>
        public bool IsAuthority = true;

        /// <summary>Off-track detection + penalty (SessionConfig.TrackLimits).</summary>
        public bool trackLimits;

        public LapTimer lapTimer;
        public TrackSpine Spine { get; private set; }

        /// <summary>Every discrete happening — consumed by the LAN publisher and,
        /// later, the replay recorder's highlight index.</summary>
        public event Action<ArcadeEvent> Event;

        private readonly List<ArcadeRacer> _racers = new List<ArcadeRacer>();
        private readonly List<ArcadeItemBox> _boxes = new List<ArcadeItemBox>();
        private readonly List<Banana> _bananas = new List<Banana>();
        private readonly List<Missile> _missiles = new List<Missile>();
        private readonly List<ArcadeRacer> _sortBuf = new List<ArcadeRacer>();

        /// <summary>Clearance above the surface for a recovered car. The wheels sit
        /// 0.078 m below the body origin, so anything less drops it into the
        /// ribbon and lets the depenetration solver fling it.</summary>
        private const float RecoverRideHeight = 0.10f;

        /// <summary>Shield orb orbit rate. Fast enough to read as active at a
        /// glance from another car, slow enough not to strobe.</summary>
        private const float ShieldOrbDegPerSec = 180f;

        private BuiltTrack _built;                  // for surface drops (null on the oval)
        private System.Random _rng;
        private float _posAccum, _botAccum, _limitAccum;
        private int _nextObjId = 1;
        private bool _boxesBuilt;

        public IReadOnlyList<ArcadeRacer> Racers => _racers;

        // ---- LAN mirroring surface -------------------------------------------
        // The host publisher and the client consumer live in Net/ArcadeNetLink,
        // which is the only place that knows both namespaces. The director just
        // exposes what it owns; it has no opinion about the network.

        public IReadOnlyList<Missile> Missiles => _missiles;
        public IReadOnlyList<Banana> Bananas => _bananas;
        public int BoxCount => _boxes.Count;

        public bool BoxActive(int i) =>
            i >= 0 && i < _boxes.Count && _boxes[i] != null && _boxes[i].Active;

        /// <summary>Client mirror of a box's up/down state. Item boxes are built
        /// from the same track and the same racing line on every machine, so index
        /// i is the same box everywhere and a bitmask is enough to sync them.</summary>
        public void SetBoxActive(int i, bool active)
        {
            if (IsAuthority || i < 0 || i >= _boxes.Count) return;
            var box = _boxes[i];
            if (box == null || box.Active == active) return;
            if (active) box.Respawn(); else box.Deplete(Clock);
        }

        public ArcadeRacer RacerForSlot(int slot)
        {
            for (int i = 0; i < _racers.Count; i++)
                if (_racers[i].netSlot == slot) return _racers[i];
            return null;
        }

        /// <summary>Client: replay a host event into the local stream, so audio
        /// and HUD subscribers cannot tell where the decision was made.</summary>
        public void RaiseRemote(in ArcadeEvent e)
        {
            if (IsAuthority) return;
            Event?.Invoke(e);
        }

        private void Awake()
        {
            Instance = this;
            Clock = 0f;
            // Seeded from the shared noise seed so a fixed-seed session rolls the
            // same items twice — the sim's repeatability rule applies here too.
            int seed = Sensors.NoiseModel.GlobalSeed;
            _rng = new System.Random(seed != 0 ? seed : Environment.TickCount);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ================= composition =================

        public void SetTrack(BuiltTrack built, IReadOnlyList<Vector3> path, bool closed)
        {
            _built = built;
            Spine = TrackSpine.From(path, closed);
        }

        /// <summary>
        /// Give a rig arcade state. Firmware rigs are refused outright: a boost or
        /// a spin-out would corrupt a controller-validation run and the StepMetrics
        /// comparison that run exists to produce.
        /// </summary>
        public ArcadeRacer Register(PlayerRig rig)
        {
            if (rig == null || rig.car == null) return null;
            if (rig.slot != null && rig.slot.control == DriveControl.Firmware) return null;
            if (rig.arcade != null) return rig.arcade;

            var racer = rig.car.gameObject.AddComponent<ArcadeRacer>();
            racer.rig = rig;
            racer.car = rig.car;
            racer.netSlot = rig.netSlot;
            racer.isBot = rig.slot != null && rig.slot.isBot;
            racer.botNextDecision = Clock + UnityEngine.Random.Range(
                ArcadeConfig.BotReactionMin, ArcadeConfig.BotReactionMax);

            rig.arcade = racer;
            _racers.Add(racer);

            // A respawn (R, or the bot's stuck recovery) clears whatever was
            // acting on the car — otherwise a spin-out follows it to the grid.
            rig.car.VehicleReset += racer.ClearAll;
            return racer;
        }

        public void Unregister(PlayerRig rig)
        {
            if (rig == null || rig.arcade == null) return;
            if (rig.car != null) rig.car.VehicleReset -= rig.arcade.ClearAll;
            _racers.Remove(rig.arcade);
            rig.arcade = null;
        }

        /// <summary>Award finishing points. Wired to RaceDirector.PlayerFinished.</summary>
        public void AwardFinish(PlayerRig rig, int place)
        {
            var r = rig != null ? rig.arcade : null;
            if (r == null) return;
            int idx = Mathf.Clamp(place - 1, 0, ArcadeConfig.PlacePoints.Length - 1);
            r.points += ArcadeConfig.PlacePoints[idx];
            Raise(ArcadeEventKind.Finished, r, null, ItemKind.None, 0,
                r.car != null ? r.car.transform.position : Vector3.zero, Quaternion.identity);
        }

        public ArcadeRacer RacerFor(CarVehicle car)
        {
            if (car == null) return null;
            for (int i = 0; i < _racers.Count; i++)
                if (_racers[i].car == car) return _racers[i];
            return null;
        }

        /// <summary>The slot an event carries for this racer: the LAN roster slot
        /// where there is one, otherwise the local roster index. Public because
        /// the audio layer has to answer "was that event mine?" from an
        /// <see cref="ArcadeEvent"/>, which carries slots and not references.</summary>
        public int SlotOf(ArcadeRacer r)
        {
            if (r == null) return -1;
            if (r.netSlot >= 0) return r.netSlot;
            return _racers.IndexOf(r);
        }

        /// <summary>Clear inventories, effects and projectiles; restart the clock.
        /// Called on race start, rematch and LAN grid teleports.</summary>
        public void ResetArcade()
        {
            Clock = 0f;
            foreach (var r in _racers) r.ClearAll();
            foreach (var m in _missiles) if (m != null) Destroy(m.gameObject);
            foreach (var b in _bananas) if (b != null) Destroy(b.gameObject);
            _missiles.Clear();
            _bananas.Clear();
            foreach (var box in _boxes) if (box != null && !box.Active) box.Respawn();
        }

        // ================= item boxes =================

        private void Start()
        {
            BuildBoxes();
        }

        /// <summary>
        /// Collect authored boxes; if the map has none, lay rows of three along
        /// the racing line. Without the fallback, arcade would only work on maps
        /// re-authored for it — every existing preset and saved track would need
        /// editing before it could be played.
        /// </summary>
        private void BuildBoxes()
        {
            if (_boxesBuilt) return;
            _boxesBuilt = true;

            _boxes.AddRange(FindObjectsByType<ArcadeItemBox>(FindObjectsSortMode.None));
            if (_boxes.Count > 0) { SortBoxes(); return; }
            if (Spine == null) return;

            var root = new GameObject("ArcadeItemBoxes").transform;
            int rows = Mathf.Max(1, Mathf.RoundToInt(Spine.TotalLength / ArcadeConfig.AutoBoxSpacingMetres));
            float step = Spine.TotalLength / rows;

            for (int i = 0; i < rows; i++)
            {
                // Offset half a step so a row never lands exactly on the start line.
                Spine.Sample(i * step + step * 0.5f, out var p, out var fwd);
                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                for (int k = -1; k <= 1; k++)
                {
                    Vector3 want = p + right * (k * ArcadeConfig.AutoBoxLateral);
                    if (!DropToSurface(want, out var onSurface)) onSurface = want;
                    SpawnBox(onSurface + Vector3.up * ArcadeConfig.AutoBoxHeight, root);
                }
            }
        }

        /// <summary>
        /// Put the boxes in a position-determined order.
        ///
        /// In LAN a box is identified across machines by nothing but its index in
        /// this list, and FindObjectsByType guarantees no ordering at all — so
        /// without this, two machines could disagree about which box just went
        /// down. Auto-placed boxes come out in spine order anyway; this makes
        /// authored ones safe too, at the cost of one sort at load.
        /// </summary>
        private void SortBoxes()
        {
            _boxes.Sort((a, b) =>
            {
                if (a == null || b == null) return 0;
                Vector3 pa = a.transform.position, pb = b.transform.position;
                int c = pa.x.CompareTo(pb.x);
                if (c != 0) return c;
                c = pa.z.CompareTo(pb.z);
                return c != 0 ? c : pa.y.CompareTo(pb.y);
            });
        }

        private void SpawnBox(Vector3 pos, Transform parent)
        {
            var go = new GameObject("ItemBox");
            go.transform.SetParent(parent, false);
            go.transform.position = pos;

            var col = go.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = new Vector3(0.34f, 0.34f, 0.34f);   // forgiving to drive through

            var vizGo = new GameObject("Viz");
            vizGo.transform.SetParent(go.transform, false);
            ArcadeVfx.BuildItemBox(vizGo.transform);

            var box = go.AddComponent<ArcadeItemBox>();
            box.viz = vizGo.transform;
            _boxes.Add(box);
        }

        /// <summary>Drop onto the track surface, preferring the factory's
        /// floor/ribbon-only raycast so cars and props are never hit.</summary>
        private bool DropToSurface(Vector3 hint, out Vector3 pos)
        {
            if (_built != null && TrackFactory.DropToSurface(_built, hint, out pos, out _)) return true;

            pos = hint;
            var hits = Physics.RaycastAll(hint + Vector3.up * 3f, Vector3.down, 60f);
            bool any = false;
            foreach (var h in hits)
            {
                if (h.collider.isTrigger) continue;
                if (h.collider.GetComponentInParent<CarVehicle>() != null) continue;
                if (!any || h.point.y > pos.y) { pos = h.point; any = true; }
            }
            return any;
        }

        public void OnItemBoxTouched(CarVehicle car, ArcadeItemBox box)
        {
            if (!IsAuthority || box == null || !box.Active) return;
            var racer = RacerFor(car);
            if (racer == null || racer.Busy || racer.Wrecked) return;   // holding one? drive on through

            box.Deplete(Clock);
            racer.rolling = true;
            racer.rollEndsAt = Clock + ArcadeConfig.RouletteSeconds;
            racer.rollPick = ArcadeConfig.Roll(racer.livePosition, Mathf.Max(1, _racers.Count), _rng);
        }

        // ================= using items =================

        public void RequestUse(CarVehicle car)
        {
            if (!IsAuthority) return;
            var racer = RacerFor(car);
            if (racer == null || !racer.HasItem || racer.Wrecked) return;
            Use(racer);
        }

        private void Use(ArcadeRacer racer)
        {
            var kind = racer.held;
            var car = racer.car;
            if (car == null) return;

            switch (kind)
            {
                case ItemKind.Boost:
                case ItemKind.TripleBoost:
                    racer.boostUntil = Clock + ArcadeConfig.BoostSeconds;
                    Raise(ArcadeEventKind.BoostStarted, racer, null, kind, 0,
                        car.transform.position, car.transform.rotation);
                    break;

                case ItemKind.Shield:
                    racer.shieldUntil = Clock + ArcadeConfig.ShieldSeconds;
                    break;   // the visual is raised by ApplyEffects, one frame later

                case ItemKind.Banana:
                    DropBanana(racer);
                    break;

                case ItemKind.Missile:
                    FireMissile(racer);
                    break;
            }

            Raise(ArcadeEventKind.ItemUsed, racer, null, kind, 0,
                car.transform.position, car.transform.rotation);

            racer.charges--;
            if (racer.charges <= 0)
            {
                racer.held = ItemKind.None;
                racer.charges = 0;
            }
            racer.botHeldSince = 0f;
        }

        private void DropBanana(ArcadeRacer racer)
        {
            var car = racer.car;
            Vector3 pos = car.transform.position - car.transform.forward * ArcadeConfig.BananaDropOffset;
            if (DropToSurface(pos, out var onSurface))
                pos = onSurface + Vector3.up * ArcadeConfig.BananaHeight;

            var go = new GameObject("Banana");
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
            ArcadeVfx.BuildBanana(go.transform);

            var b = go.AddComponent<Banana>();
            b.objId = _nextObjId++;
            b.ownerSlot = SlotOf(racer);
            b.ownerCar = car;
            b.droppedAt = Clock;
            b.expiresAt = Clock + ArcadeConfig.BananaLifetime;
            _bananas.Add(b);

            // Cap per player so a long race doesn't get carpeted.
            int mine = 0;
            for (int i = _bananas.Count - 1; i >= 0; i--)
            {
                if (_bananas[i] == null || _bananas[i].ownerSlot != b.ownerSlot) continue;
                if (++mine > ArcadeConfig.MaxBananasPerPlayer) RemoveBanana(_bananas[i], false);
            }

            Raise(ArcadeEventKind.BananaDropped, racer, null, ItemKind.Banana, b.objId,
                go.transform.position, go.transform.rotation);
        }

        private void FireMissile(ArcadeRacer racer)
        {
            var car = racer.car;
            var target = PickMissileTarget(racer);

            Vector3 fwd = car.transform.forward;
            Vector3 pos = car.transform.position + fwd * ArcadeConfig.MissileMuzzleOffset
                          + Vector3.up * 0.05f;

            var go = new GameObject("Missile");
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(fwd, Vector3.up));
            ArcadeVfx.BuildMissile(go.transform);

            var m = go.AddComponent<Missile>();
            m.objId = _nextObjId++;
            m.ownerSlot = SlotOf(racer);
            m.ownerCar = car;
            m.target = target != null ? target.car : null;
            m.expiresAt = Clock + ArcadeConfig.MissileLifetime;
            _missiles.Add(m);

            Raise(ArcadeEventKind.MissileFired, racer, target, ItemKind.Missile, m.objId,
                pos, go.transform.rotation);
        }

        /// <summary>
        /// The car with the smallest positive gap AHEAD on the racing line, within
        /// lock range. Spine order rather than a camera cone: with up to eight cars
        /// on a tight RC track, "who am I chasing" is unambiguous along the line and
        /// matches what the scoreboard shows. No one ahead (the leader fires) is not
        /// a special case — the missile simply flies straight.
        /// </summary>
        private ArcadeRacer PickMissileTarget(ArcadeRacer src)
        {
            if (Spine == null) return null;
            ArcadeRacer best = null;
            float bestGap = float.MaxValue;

            int hint = src.spineHint;
            float mine = Spine.Project(src.car.transform.position, ref hint);

            foreach (var r in _racers)
            {
                if (r == src || r.car == null) continue;
                if (r.Wrecked || Clock < r.invulnUntil) continue;   // don't waste it
                int h = r.spineHint;
                float s = Spine.Project(r.car.transform.position, ref h);
                float gap = Spine.Gap(mine, s);
                if (gap <= 0f || gap > ArcadeConfig.MaxLockDistance) continue;
                if (gap < bestGap) { bestGap = gap; best = r; }
            }
            return best;
        }

        // ================= hits =================

        public void OnMissileHit(Missile m, CarVehicle victimCar)
        {
            if (!IsAuthority || m == null) return;
            var victim = RacerFor(victimCar);
            Vector3 dir = m.transform.forward;
            Vector3 at = m.transform.position;

            if (victim != null && Clock < victim.invulnUntil)
            {
                // Just recovered — swallow the missile rather than re-killing.
                RemoveMissile(m);
                return;
            }

            if (victim != null && victim.shieldUntil > Clock)
            {
                BreakShield(victim);
                Raise(ArcadeEventKind.ShieldBlocked, null, victim, ItemKind.Missile, m.objId,
                    at, m.transform.rotation);
            }
            else if (victim != null)
            {
                ApplyWreck(victim, dir, at);
                Raise(ArcadeEventKind.MissileHit, null, victim, ItemKind.Missile, m.objId,
                    at, m.transform.rotation);
            }

            RemoveMissile(m);
        }

        public void OnMissileExpired(Missile m)
        {
            if (!IsAuthority || m == null) return;
            Raise(ArcadeEventKind.MissileExpired, null, null, ItemKind.Missile, m.objId,
                m.transform.position, m.transform.rotation);
            RemoveMissile(m);
        }

        public void OnBananaHit(Banana b, CarVehicle victimCar)
        {
            if (!IsAuthority || b == null) return;
            var victim = RacerFor(victimCar);

            if (victim != null && Clock < victim.invulnUntil) return;   // leave it for later

            if (victim != null && victim.shieldUntil > Clock)
            {
                BreakShield(victim);
                Raise(ArcadeEventKind.ShieldBlocked, null, victim, ItemKind.Banana, b.objId,
                    b.transform.position, b.transform.rotation);
            }
            else if (victim != null)
            {
                ApplySpin(victim, victimCar.transform.forward);
                Raise(ArcadeEventKind.BananaHit, null, victim, ItemKind.Banana, b.objId,
                    b.transform.position, b.transform.rotation);
            }

            RemoveBanana(b, false);
        }

        /// <summary>
        /// Banana: spin the victim out. Grip collapses, drive is cut so they
        /// cannot power through it, a yaw torque throws the tail one way or the
        /// other at random, and one impulse punts them off line.
        /// </summary>
        private void ApplySpin(ArcadeRacer victim, Vector3 dir)
        {
            var car = victim.car;
            if (car == null) return;

            victim.spinUntil = Clock + ArcadeConfig.SpinSeconds;
            victim.spinTorqueSigned = _rng.NextDouble() < 0.5 ? -ArcadeConfig.SpinTorque
                                                              : ArcadeConfig.SpinTorque;
            dir = Flat(dir, car.transform.forward);
            car.ArcadeImpulse(dir * ArcadeConfig.HitImpulseFwd + Vector3.up * ArcadeConfig.HitImpulseUp,
                car.transform.position);

            victim.ShowHit("SPUN OUT!", ArcadeConfig.SpinFeedbackColor,
                ArcadeConfig.HitBannerSeconds, ArcadeConfig.HitFlashSeconds);
        }

        /// <summary>
        /// Missile: destroy the victim outright. They are thrown into the air and
        /// tumbled, go limp for <see cref="ArcadeConfig.WreckSeconds"/>, and are
        /// then lifted back onto the racing line by <see cref="RecoverFromWreck"/>.
        ///
        /// Deliberately NOT <c>CarVehicle.ResetVehicle</c>, which returns the car
        /// to the start line: on a 100–140 m circuit that costs most of a lap, so
        /// one missile would effectively end a race. Losing a few seconds and some
        /// places is the punishment; losing the lap is not.
        /// </summary>
        private void ApplyWreck(ArcadeRacer victim, Vector3 dir, Vector3 at)
        {
            var car = victim.car;
            if (car == null) return;

            victim.wreckedUntil = Clock + ArcadeConfig.WreckSeconds;
            victim.awaitingRecover = true;
            victim.spinUntil = 0f;              // the wreck supersedes any spin
            victim.boostUntil = 0f;

            dir = Flat(dir, car.transform.forward);
            car.ArcadeImpulse(dir * ArcadeConfig.WreckImpulseFwd
                              + Vector3.up * ArcadeConfig.WreckImpulseUp,
                car.transform.position);

            // One torque impulse rather than a sustained torque: the car should
            // tumble ballistically and land however it lands, not be driven round
            // like the banana spin.
            float sign = _rng.NextDouble() < 0.5 ? -1f : 1f;
            car.ArcadeTorqueImpulse(new Vector3(
                (float)(_rng.NextDouble() - 0.5) * ArcadeConfig.WreckTorque,
                sign * ArcadeConfig.WreckTorque,
                (float)(_rng.NextDouble() - 0.5) * ArcadeConfig.WreckTorque));

            ArcadeBurst.Spawn(at, 0.9f, ArcadeConfig.SpinFeedbackColor,
                ArcadeConfig.ExplosionSeconds);

            victim.ShowHit("WRECKED!", ArcadeConfig.WreckFeedbackColor,
                ArcadeConfig.WreckBannerSeconds, ArcadeConfig.HitFlashSeconds);

            Raise(ArcadeEventKind.Wrecked, null, victim, ItemKind.Missile, 0,
                at, car.transform.rotation);
        }

        /// <summary>
        /// Put a wrecked car back on the racing line, upright and pointing the
        /// right way, roughly where it was destroyed.
        ///
        /// The spine gives both halves of that for free: Project turns the wreck
        /// position into an arc length, Sample turns arc length back into a pose
        /// with a forward direction. DropToSurface then lands it on the actual
        /// ribbon, which matters on the elevated sections — Neon Vortex II's
        /// bridge and Workshop's plank are metres above the floor beneath them.
        /// </summary>
        private void RecoverFromWreck(ArcadeRacer r)
        {
            r.awaitingRecover = false;
            var car = r.car;
            if (car == null) return;

            Vector3 pos = car.transform.position;
            Vector3 fwd = Flat(car.transform.forward, Vector3.forward);

            if (Spine != null)
            {
                int hint = r.spineHint;
                float s = Spine.Project(pos, ref hint);
                r.spineHint = hint;
                Spine.Sample(s + ArcadeConfig.WreckRecoverAhead, out var onLine, out var lineFwd);
                pos = onLine;
                fwd = lineFwd;
            }

            if (DropToSurface(pos, out var surf)) pos = surf;
            pos += Vector3.up * RecoverRideHeight;

            car.RestoreState(pos, Quaternion.LookRotation(fwd, Vector3.up),
                Vector3.zero, Vector3.zero);

            r.spinUntil = 0f;
            r.wreckedUntil = 0f;
            r.invulnUntil = Clock + ArcadeConfig.InvulnSeconds;
            r.RestoreCar();

            Raise(ArcadeEventKind.Recovered, null, r, ItemKind.None, 0,
                pos, car.transform.rotation);
        }

        /// <summary>Consume a shield. One place, so the visual teardown and the
        /// state change can never drift apart across the two hit handlers.</summary>
        private void BreakShield(ArcadeRacer victim)
        {
            victim.shieldUntil = 0f;
            if (victim.car != null)
                ArcadeBurst.Spawn(victim.car.transform.position, 0.8f,
                    ArcadeConfig.ShieldFeedbackColor, 0.35f);
            victim.HideShield();
            victim.ShowHit("SHIELD BLOCKED!", ArcadeConfig.ShieldFeedbackColor,
                ArcadeConfig.HitBannerSeconds, ArcadeConfig.HitFlashSeconds);
        }

        /// <summary>Flatten to the ground plane, falling back when degenerate.</summary>
        private static Vector3 Flat(Vector3 v, Vector3 fallback)
        {
            v.y = 0f;
            if (v.sqrMagnitude < 1e-6f) { v = fallback; v.y = 0f; }
            if (v.sqrMagnitude < 1e-6f) v = Vector3.forward;
            return v.normalized;
        }

        private void RemoveMissile(Missile m)
        {
            _missiles.Remove(m);
            if (m != null) Destroy(m.gameObject);
        }

        private void RemoveBanana(Banana b, bool expired)
        {
            _bananas.Remove(b);
            if (b == null) return;
            if (expired)
                Raise(ArcadeEventKind.BananaExpired, null, null, ItemKind.Banana, b.objId,
                    b.transform.position, b.transform.rotation);
            Destroy(b.gameObject);
        }

        // ================= per-frame =================

        private void Update()
        {
            float dt = Time.deltaTime;
            Clock += dt;

            ResolveRoulette();
            ApplyEffects(dt);
            RespawnBoxes();
            ExpireBananas();

            _limitAccum += dt;
            float limitStep = 1f / ArcadeConfig.TrackLimitSampleHz;
            if (_limitAccum >= limitStep)
            {
                UpdateTrackLimits(_limitAccum);
                _limitAccum = 0f;
            }

            _posAccum += dt;
            if (_posAccum >= 1f / ArcadeConfig.PositionUpdateHz)
            {
                _posAccum = 0f;
                UpdatePositions();
            }

            _botAccum += dt;
            if (_botAccum >= 1f / ArcadeConfig.BotDecisionHz)
            {
                _botAccum = 0f;
                UpdateBots();
            }
        }

        // ================= bots =================

        /// <summary>
        /// Decide when the AI fires. This lives here, not in BotDriver, because a
        /// bot knows only its own racing line — choosing a moment needs the whole
        /// field. BotDriver just exposes a latch, exactly like its stuck-recovery
        /// respawn, so the bot still "presses the button" through the same input
        /// seam a human uses.
        /// </summary>
        private void UpdateBots()
        {
            if (!IsAuthority || !ArcadeConfig.BotsUseItems) return;
            // Nobody fires into the back of the grid the instant the lights go out.
            if (Clock < ArcadeConfig.BotNoUseAfterGo) return;

            foreach (var r in _racers)
            {
                if (!r.isBot || !r.HasItem || r.car == null || r.Wrecked) continue;
                if (Clock < r.botNextDecision) continue;

                // Re-arm with jitter so a whole pack never fires on one frame.
                r.botNextDecision = Clock + 1f / ArcadeConfig.BotDecisionHz +
                    (float)_rng.NextDouble() *
                    (ArcadeConfig.BotReactionMax - ArcadeConfig.BotReactionMin);

                if (ShouldBotUse(r)) TriggerBotUse(r);
            }
        }

        private bool ShouldBotUse(ArcadeRacer r)
        {
            // Sitting on an item forever looks worse than using it imperfectly.
            if (r.botHeldSince > 0f && Clock - r.botHeldSince > ArcadeConfig.BotHoldTimeout)
                return true;

            var car = r.car;
            switch (r.held)
            {
                case ItemKind.Boost:
                case ItemKind.TripleBoost:
                    // Straight and already rolling — boosting into a corner just
                    // scatters the pack and fights the rubber-band.
                    return Mathf.Abs(car.CurrentSteerAngle) < ArcadeConfig.BotStraightSteerDeg &&
                           car.ForwardSpeed > ArcadeConfig.BotBoostMinSpeed;

                case ItemKind.Missile:
                {
                    var t = PickMissileTarget(r);
                    if (t == null || Spine == null) return false;
                    int a = r.spineHint, b = t.spineHint;
                    float gap = Spine.Gap(Spine.Project(car.transform.position, ref a),
                                          Spine.Project(t.car.transform.position, ref b));
                    return gap > 0f && gap < ArcadeConfig.BotMissileRange;
                }

                case ItemKind.Banana:
                    return SomeoneBehind(r, ArcadeConfig.BotBananaRange);

                case ItemKind.Shield:
                    // Pop it when something is actually incoming.
                    return IncomingMissile(car);
            }
            return false;
        }

        /// <summary>
        /// Is a live missile currently locked onto this car?
        ///
        /// The director owns the missile list, so this is just a look. Shared by
        /// the bot shield policy and the player's incoming warning deliberately:
        /// a missile the AI would defend against is exactly the one a human
        /// should be told about, and one implementation cannot drift from two.
        /// </summary>
        public bool IncomingMissile(CarVehicle car)
        {
            if (car == null) return false;
            // A client owns no missiles — it is sent their poses, not their
            // targeting — so the answer arrives in the sync stream instead.
            if (!IsAuthority)
            {
                var mirror = RacerFor(car);
                return mirror != null && mirror.incomingRemote;
            }
            for (int i = 0; i < _missiles.Count; i++)
            {
                var m = _missiles[i];
                if (m != null && m.target == car) return true;
            }
            return false;
        }

        private bool SomeoneBehind(ArcadeRacer r, float range)
        {
            if (Spine == null) return false;
            int a = r.spineHint;
            float mine = Spine.Project(r.car.transform.position, ref a);
            foreach (var o in _racers)
            {
                if (o == r || o.car == null) continue;
                int b = o.spineHint;
                float gap = Spine.Gap(mine, Spine.Project(o.car.transform.position, ref b));
                if (gap < 0f && gap > -range) return true;
            }
            return false;
        }

        private void TriggerBotUse(ArcadeRacer r)
        {
            var bot = r.rig != null && r.rig.input != null ? r.rig.input.source as BotDriver : null;
            if (bot != null) bot.RequestUseItem();   // through the normal input seam
            else Use(r);
        }

        private void ResolveRoulette()
        {
            // Clients are told what they rolled; deciding locally would hand two
            // machines two different items for the same box.
            if (!IsAuthority) return;
            foreach (var r in _racers)
            {
                if (!r.rolling) continue;
                // Cycle the displayed face while it spins (HUD only).
                int face = 1 + (int)(Clock * ArcadeConfig.RouletteFaceHz) % 5;
                r.rollFace = (ItemKind)face;

                if (Clock < r.rollEndsAt) continue;
                r.rolling = false;
                r.held = r.rollPick;
                r.charges = ArcadeConfig.ChargesFor(r.held);
                r.rollFace = r.held;
                r.botHeldSince = Clock;
                Raise(ArcadeEventKind.ItemGranted, r, null, r.held, 0,
                    r.car != null ? r.car.transform.position : Vector3.zero, Quaternion.identity);
            }
        }

        private void ApplyEffects(float dt)
        {
            foreach (var r in _racers)
            {
                var car = r.car;
                if (car == null) continue;

                // The limp phase has expired: lift them back onto the line. Done
                // here rather than on a timer so it can never fire twice, and so
                // a paused race resumes the recovery instead of skipping it.
                // Authority only — a client teleporting its own ghost would fight
                // the pose stream.
                if (IsAuthority && r.awaitingRecover && Clock >= r.wreckedUntil)
                    RecoverFromWreck(r);

                bool wrecked = Clock < r.wreckedUntil;
                bool spun = !wrecked && Clock < r.spinUntil;

                // Boost, faded out as it approaches BoostTopSpeed. The force is
                // applied to the body with no ceiling of its own, so without this
                // a 1.6 s boost just keeps accelerating past anything the
                // drivetrain could reach. Surface boost pads are maxed in
                // separately inside CarVehicle and are NOT capped by this.
                car.arcadeBoostAccel = (!wrecked && Clock < r.boostUntil)
                    ? ArcadeConfig.BoostAccel * Mathf.Clamp01(
                        (ArcadeConfig.BoostTopSpeed - car.ForwardSpeed) / ArcadeConfig.BoostFadeBand)
                    : 0f;

                // Clients mirror effects for the VISUALS only (shield bubble,
                // HUD); the physics channels below are the host's business and
                // writing them on a kinematic ghost would be meaningless anyway.
                if (!IsAuthority) { UpdateShieldViz(r, dt); continue; }

                if (wrecked)
                {
                    // Limp: no drive, no grip, no held torque — it is tumbling
                    // from the impulse the hit already gave it.
                    car.arcadeGripMult = ArcadeConfig.SpinGripMult * r.gripBase;
                    car.arcadeYawTorque = 0f;
                    car.arcadeDriveMult = 0f;
                }
                else if (spun)
                {
                    car.arcadeGripMult = ArcadeConfig.SpinGripMult * r.gripBase;
                    car.arcadeYawTorque = r.spinTorqueSigned;
                    car.arcadeDriveMult = ArcadeConfig.SpinDriveMult * r.driveBase;
                }
                else
                {
                    car.arcadeGripMult = r.gripBase;
                    car.arcadeYawTorque = 0f;
                    car.arcadeDriveMult = r.driveBase;
                }

                UpdateShieldViz(r, dt);

                // Off-track penalty: bleed speed down to the cap with a rearward
                // drag. Needs the frame clock, not the 10 Hz limits sampler.
                if (!r.penalized) continue;
                if (Clock >= r.penaltyUntil) { r.penalized = false; continue; }
                float over = car.ForwardSpeed - ArcadeConfig.PenaltyCapSpeed;
                if (over > 0f)
                    car.ArcadeImpulse(-car.transform.forward * (over * ArcadeConfig.PenaltyDragGain * dt),
                        car.transform.position);
            }
        }

        /// <summary>
        /// Raise, spin and drop the shield visual from <c>shieldUntil</c> alone.
        ///
        /// Driving it off the deadline rather than from the use/block call sites
        /// means every way a shield can end — used up, blocked a hit, respawned,
        /// race restarted — is covered by one check, and a car can never be left
        /// wearing a bubble it no longer owns.
        /// </summary>
        private void UpdateShieldViz(ArcadeRacer r, float dt)
        {
            bool up = Clock < r.shieldUntil && r.car != null;

            if (!up) { r.HideShield(); return; }
            if (r.shieldViz == null)
            {
                r.shieldViz = ArcadeVfx.BuildShield(r.car.transform);
                r.shieldOrbs = r.shieldViz.Find("Orbs");
            }
            if (r.shieldOrbs != null)
                r.shieldOrbs.Rotate(0f, ShieldOrbDegPerSec * dt, 0f, Space.Self);
        }

        // ================= track limits =================

        /// <summary>
        /// Off-track detection from the surface each wheel is actually standing on.
        /// A wheel is off when it is ungrounded or its floor type's friction is at
        /// or below the threshold — which classifies grass/sand/ice/mud (and the
        /// themed carpet, wet sand and lava) as off with no per-tile authoring at
        /// all, and works on spline ribbons for free because SurfaceMap resolves
        /// the ribbon's SurfaceTag first.
        ///
        /// The car counts as off only when ALL wheels are: two wheels on the grass
        /// at an apex is racing, four wheels off is a shortcut.
        ///
        /// Deliberately orthogonal to lap validation — LapTimer's ordered
        /// checkpoints are already the anti-shortcut mechanism, and coupling the
        /// two would double-punish and mean editing a file that non-arcade
        /// sessions share.
        /// </summary>
        private void UpdateTrackLimits(float step)
        {
            if (!IsAuthority || !trackLimits) return;
            // No tile/ribbon surface data (classic oval, diff-drive scene): every
            // hit resolves to Baseline, i.e. "on track" everywhere. Stay silent
            // rather than pretend.
            if (SurfaceMap.Active == null) return;

            foreach (var r in _racers)
            {
                var car = r.car;
                if (car == null) continue;
                int n = car.WheelCount;
                if (n == 0) continue;

                // Being blown into the scenery is punishment enough; don't also
                // hand out an off-track penalty for the flight and the landing.
                if (r.Wrecked || Clock < r.invulnUntil)
                {
                    r.offTrackTime = 0f;
                    r.ungroundedTime = 0f;
                    continue;
                }

                int off = 0, air = 0;
                for (int i = 0; i < n; i++)
                {
                    var wc = car.GetWheel(i);
                    if (wc == null || !wc.GetGroundHit(out WheelHit hit)) { off++; air++; continue; }
                    if (SurfaceMap.At(hit).frictionMult <= ArcadeConfig.OffTrackFrictionThreshold) off++;
                }

                // Airborne reads as off on every wheel, so a jump would otherwise
                // accumulate. Only sustained flight counts.
                bool allAir = air == n;
                r.ungroundedTime = allAir ? r.ungroundedTime + step : 0f;
                bool offNow = off == n && (!allAir || r.ungroundedTime > ArcadeConfig.JumpGraceSeconds);

                r.offTrackTime = offNow
                    ? r.offTrackTime + step
                    : Mathf.Max(0f, r.offTrackTime - step * ArcadeConfig.OffTrackDecayRate);

                if (r.offTrackTime <= 0.01f) r.warned = false;
                if (Clock < r.penaltyCooldownUntil) continue;

                if (r.offTrackTime > ArcadeConfig.OffTrackPenaltySeconds)
                {
                    r.penalized = true;
                    r.warned = false;
                    r.offTrackTime = 0f;
                    r.penaltyUntil = Clock + ArcadeConfig.PenaltyDuration;
                    r.penaltyCooldownUntil = r.penaltyUntil + ArcadeConfig.PenaltyCooldownSeconds;
                    Raise(ArcadeEventKind.OffTrackPenalty, null, r, ItemKind.None, 0,
                        car.transform.position, car.transform.rotation);
                }
                else if (!r.warned && r.offTrackTime > ArcadeConfig.OffTrackWarnSeconds)
                {
                    r.warned = true;
                    Raise(ArcadeEventKind.OffTrackWarning, null, r, ItemKind.None, 0,
                        car.transform.position, car.transform.rotation);
                }
            }
        }

        private void RespawnBoxes()
        {
            if (!IsAuthority) return;
            foreach (var box in _boxes)
                if (box != null && !box.Active && Clock >= box.RespawnAt) box.Respawn();
        }

        private void ExpireBananas()
        {
            if (!IsAuthority) return;
            for (int i = _bananas.Count - 1; i >= 0; i--)
            {
                var b = _bananas[i];
                if (b == null) { _bananas.RemoveAt(i); continue; }
                if (Clock >= b.expiresAt) RemoveBanana(b, true);
            }
        }

        /// <summary>
        /// Live race order from arc length along the spine. Finer than
        /// RaceDirector.Progress(), which quantizes to whole checkpoints — fine for
        /// rubber-banding, useless as a live board on a map with three of them.
        /// That method is deliberately left alone: it drives bot behaviour in
        /// existing non-arcade races.
        /// </summary>
        private void UpdatePositions()
        {
            // Client positions arrive in the arcade sync stream — recomputing them
            // from ghost poses that are ~120 ms behind would disagree with the
            // host's board for no benefit.
            if (!IsAuthority || Spine == null) return;

            foreach (var r in _racers)
            {
                if (r.car == null) continue;
                int hint = r.spineHint;
                float s = Spine.Project(r.car.transform.position, ref hint);
                r.spineHint = hint;
                int laps = 0;
                if (lapTimer != null) laps = lapTimer.GetTracker(r.car).LapCount;
                r.trackProgress = laps * Spine.TotalLength + s;
            }

            _sortBuf.Clear();
            _sortBuf.AddRange(_racers);
            _sortBuf.Sort((a, b) => b.trackProgress.CompareTo(a.trackProgress));
            for (int i = 0; i < _sortBuf.Count; i++) _sortBuf[i].livePosition = i + 1;
        }

        private void Raise(ArcadeEventKind kind, ArcadeRacer src, ArcadeRacer dst,
            ItemKind item, int objId, Vector3 pos, Quaternion rot)
        {
            Event?.Invoke(new ArcadeEvent
            {
                t = Clock,
                kind = kind,
                srcSlot = SlotOf(src),
                dstSlot = SlotOf(dst),
                item = item,
                objId = objId,
                pos = pos,
                rot = rot,
            });
        }
    }
}
