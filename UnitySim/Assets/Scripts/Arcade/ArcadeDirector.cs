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

        /// <summary>
        /// "This slot's car is simulated by somebody else." Set by the LAN link
        /// on the host; null everywhere else, which is why solo, split-screen and
        /// bot races are untouched by any of the owner-authority handling below.
        ///
        /// The director still DECIDES what happens to a remote-owned car — it
        /// rolls the spin direction, the tumble, the recovery pose — but it
        /// cannot apply the result to a body it does not simulate, so it hands
        /// the numbers over through <see cref="EffectDispatch"/> instead.
        /// </summary>
        public Func<int, bool> RemoteOwned;

        /// <summary>One arcade effect's physics, for the machine that owns the
        /// car. Consumed by the LAN link, which is the only thing here that
        /// knows what a network is.</summary>
        public event Action<ArcadeFx> EffectDispatch;

        private bool Remote(ArcadeRacer r) =>
            RemoteOwned != null && r != null && r.netSlot >= 0 && RemoteOwned(r.netSlot);

        /// <summary>True when THIS machine simulates the physics of r's car.</summary>
        private bool DrivesPhysics(ArcadeRacer r) =>
            IsAuthority ? !Remote(r) : (r != null && r.slotIsLocal);

        private readonly List<ArcadeRacer> _racers = new List<ArcadeRacer>();
        private readonly List<ArcadeItemBox> _boxes = new List<ArcadeItemBox>();
        private readonly List<Banana> _bananas = new List<Banana>();
        private readonly List<Missile> _missiles = new List<Missile>();
        private readonly List<AreaHazard> _hazards = new List<AreaHazard>();
        private readonly List<ArcadeRacer> _sortBuf = new List<ArcadeRacer>();

        /// <summary>Clearance above the surface for a recovered car. The wheels sit
        /// 0.078 m below the body origin, so anything less drops it into the
        /// ribbon and lets the depenetration solver fling it.</summary>
        private const float RecoverRideHeight = 0.10f;

        /// <summary>Shield orb orbit rate. Fast enough to read as active at a
        /// glance from another car, slow enough not to strobe.</summary>
        private const float ShieldOrbDegPerSec = 180f;

        /// <summary>Boost flame flicker rate — fast enough to read as fire,
        /// below anything that strobes.</summary>
        private const float BoostFlamePulseHz = 12f;
        /// <summary>Boost flame length pulse, as a fraction of the authored
        /// plume. Length only: a flame breathes along its axis.</summary>
        private const float BoostFlamePulseAmp = 0.30f;

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
        public IReadOnlyList<AreaHazard> Hazards => _hazards;
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
            // The rig carries a runner exactly when this machine simulates the
            // car; ghosts and host-side followers have none.
            racer.slotIsLocal = rig.runner != null;
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
            foreach (var h in _hazards) if (h != null) Destroy(h.gameObject);
            _missiles.Clear();
            _bananas.Clear();
            _hazards.Clear();
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

                case ItemKind.SmokeCloud:
                case ItemKind.OilSlick:
                    DropHazard(racer, kind);
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

        /// <summary>
        /// Lay a smoke cloud or an oil slick behind the car.
        ///
        /// The <see cref="AreaHazard"/> and its <see cref="AreaHazardViz"/> share
        /// one GameObject deliberately: a smoke cloud drifts, and if the effect
        /// area lived on a separate object it would stay behind on the tarmac
        /// while the visible cloud wandered off — an invisible trap plus a
        /// harmless decoration. Sharing the transform makes that impossible, and
        /// copying the radius off the viz means what catches you is by
        /// construction what you can see.
        /// </summary>
        private void DropHazard(ArcadeRacer racer, ItemKind kind)
        {
            var car = racer.car;
            Vector3 pos = car.transform.position - car.transform.forward * ArcadeConfig.HazardDropOffset;
            if (DropToSurface(pos, out var onSurface))
                pos = onSurface + Vector3.up * ArcadeConfig.HazardHeight;

            int id = _nextObjId++;
            float life = kind == ItemKind.OilSlick ? ArcadeConfig.SlickLifetime
                                                   : ArcadeConfig.SmokeLifetime;

            var viz = AreaHazardViz.Spawn(kind, pos, id, life);
            viz.selfDestruct = false;          // the director owns the lifetime

            var h = viz.gameObject.AddComponent<AreaHazard>();
            h.objId = id;
            h.ownerSlot = SlotOf(racer);
            h.ownerCar = car;
            h.kind = kind;
            h.droppedAt = Clock;
            h.expiresAt = Clock + life;
            h.fullRadius = viz.radius;
            h.startRadius = viz.startRadius;
            h.growSeconds = viz.growSeconds;
            _hazards.Add(h);

            // Cap per player, exactly as the banana does — one player holding two
            // items in a long race should not be able to fog a whole sector.
            int mine = 0;
            for (int i = _hazards.Count - 1; i >= 0; i--)
            {
                if (_hazards[i] == null || _hazards[i].ownerSlot != h.ownerSlot) continue;
                if (++mine > ArcadeConfig.MaxHazardsPerPlayer) RemoveHazard(_hazards[i], false);
            }

            Raise(ArcadeEventKind.HazardDropped, racer, null, kind, id, pos, Quaternion.identity);
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
        /// A car is inside a hazard this frame. Called from the containment poll
        /// once per racer per hazard, so it has to be safe to call every frame —
        /// the deadline is simply re-armed, and only the RISING edge (the frame
        /// the racer was not already affected) raises an event or a banner.
        ///
        /// Re-arming rather than setting once is what makes "parked in a cloud"
        /// work without being a special case: it is LEAVING that starts the
        /// recovery, so sitting in it keeps you in it.
        ///
        /// Shields deliberately do NOT block either hazard. A shield absorbs one
        /// HIT, and losing your sight or your grip is not a hit — which is the
        /// role these two items have that Shield does not already cover.
        /// </summary>
        public void OnHazardHit(AreaHazard h, ArcadeRacer victim)
        {
            if (!IsAuthority || h == null || victim == null) return;
            if (Clock < victim.invulnUntil) return;

            bool rising;
            if (h.kind == ItemKind.OilSlick)
            {
                rising = !victim.OnSlick;
                victim.slickUntil = Clock + ArcadeConfig.SlickLingerSeconds;
                if (!rising) return;
                victim.ShowHit("OIL SLICK!", ArcadeConfig.SlickFeedbackColor,
                    ArcadeConfig.HitBannerSeconds, ArcadeConfig.HitFlashSeconds);
            }
            else
            {
                rising = !victim.Blinded;
                victim.blindUntil = Clock + ArcadeConfig.BlindSeconds;
                if (!rising) return;
                victim.blindStartedAt = Clock;
                victim.ShowHit("SMOKED!", ArcadeConfig.BlindFeedbackColor,
                    ArcadeConfig.HitBannerSeconds, ArcadeConfig.HitFlashSeconds);
            }

            var car = victim.car;
            Raise(ArcadeEventKind.HazardHit, null, victim, h.kind, h.objId,
                car != null ? car.transform.position : h.transform.position,
                car != null ? car.transform.rotation : Quaternion.identity);
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
            Vector3 punt = dir * ArcadeConfig.HitImpulseFwd + Vector3.up * ArcadeConfig.HitImpulseUp;

            if (Remote(victim))
                DispatchFx(ArcadeFx.Kind.Spin, victim, punt, Vector3.zero);
            else
                car.ArcadeImpulse(punt, car.transform.position);

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
            victim.driftBoostUntil = 0f;        // a mini-turbo does not survive a missile

            dir = Flat(dir, car.transform.forward);
            Vector3 punt = dir * ArcadeConfig.WreckImpulseFwd
                           + Vector3.up * ArcadeConfig.WreckImpulseUp;

            // One torque impulse rather than a sustained torque: the car should
            // tumble ballistically and land however it lands, not be driven round
            // like the banana spin. The draws happen here whoever owns the car —
            // the host is the only place that decides.
            float sign = _rng.NextDouble() < 0.5 ? -1f : 1f;
            var tumble = new Vector3(
                (float)(_rng.NextDouble() - 0.5) * ArcadeConfig.WreckTorque,
                sign * ArcadeConfig.WreckTorque,
                (float)(_rng.NextDouble() - 0.5) * ArcadeConfig.WreckTorque);

            if (Remote(victim))
            {
                DispatchFx(ArcadeFx.Kind.Wreck, victim, punt, tumble);
            }
            else
            {
                car.ArcadeImpulse(punt, car.transform.position);
                car.ArcadeTorqueImpulse(tumble);
            }

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
            var landing = Quaternion.LookRotation(fwd, Vector3.up);

            // The host picks the spot — it has the spine and the surface — but
            // only the owner can put the car there.
            if (Remote(r))
                DispatchFx(ArcadeFx.Kind.Recover, r, Vector3.zero, Vector3.zero, pos, landing);
            else
                car.RestoreState(pos, landing, Vector3.zero, Vector3.zero);

            r.spinUntil = 0f;
            r.wreckedUntil = 0f;
            r.invulnUntil = Clock + ArcadeConfig.InvulnSeconds;
            r.RestoreCar();

            Raise(ArcadeEventKind.Recovered, null, r, ItemKind.None, 0,
                pos, car.transform.rotation);
        }

        private void DispatchFx(ArcadeFx.Kind kind, ArcadeRacer victim,
            Vector3 impulse, Vector3 torque, Vector3 pos = default, Quaternion rot = default)
        {
            EffectDispatch?.Invoke(new ArcadeFx
            {
                kind = kind,
                slot = victim.netSlot,
                impulse = impulse,
                torqueImpulse = torque,
                spinTorqueSigned = victim.spinTorqueSigned,
                pos = pos,
                rot = rot == default ? Quaternion.identity : rot,
            });
        }

        /// <summary>
        /// A client reported its own off-track verdict. Its car is kinematic
        /// here, so its wheels never touch the surface map and this machine
        /// cannot see it leave the road.
        ///
        /// The events are raised on the rising edge only, which is what keeps
        /// the shared board, the banners and the audio identical to a
        /// host-simulated car's.
        /// </summary>
        public void NotifyRemoteTrackLimit(int slot, bool penalized, bool warned)
        {
            if (!IsAuthority) return;
            var r = RacerForSlot(slot);
            if (r == null) return;

            var car = r.car;
            Vector3 pos = car != null ? car.transform.position : Vector3.zero;
            Quaternion rot = car != null ? car.transform.rotation : Quaternion.identity;

            if (penalized && !r.penalized)
                Raise(ArcadeEventKind.OffTrackPenalty, null, r, ItemKind.None, 0, pos, rot);
            else if (warned && !r.warned && !penalized)
                Raise(ArcadeEventKind.OffTrackWarning, null, r, ItemKind.None, 0, pos, rot);

            r.penalized = penalized;
            r.warned = warned;
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

        private void RemoveHazard(AreaHazard h, bool expired)
        {
            _hazards.Remove(h);
            if (h == null) return;
            if (expired && IsAuthority)
                Raise(ArcadeEventKind.HazardExpired, null, null, h.kind, h.objId,
                    h.transform.position, Quaternion.identity);
            Destroy(h.gameObject);
        }

        /// <summary>
        /// Expire hazards, and on the authority poll every racer against every
        /// live one. This is the whole hit path for the area items — see
        /// <see cref="AreaHazard"/> for why it is a poll and not a trigger.
        ///
        /// Runs unconditionally rather than behind an authority gate, because a
        /// LAN client still has to tear its own copies down; the CONTAINMENT half
        /// is host-only, since the client would be testing its own interpolated
        /// ghosts (60–120 ms behind) and the two machines would then disagree
        /// about when a screen goes green.
        /// </summary>
        private void UpdateAreaHazards()
        {
            for (int i = _hazards.Count - 1; i >= 0; i--)
            {
                var h = _hazards[i];
                if (h == null) { _hazards.RemoveAt(i); continue; }
                if (Clock >= h.expiresAt) { RemoveHazard(h, true); continue; }
                if (!IsAuthority) continue;

                float rad = h.Radius;
                for (int k = 0; k < _racers.Count; k++)
                {
                    var r = _racers[k];
                    var car = r.car;
                    if (car == null || r.Wrecked) continue;
                    // The dropper gets a grace, then the hazard catches them too —
                    // otherwise parking on your own smoke is a free rear shield.
                    if (car == h.ownerCar && Clock - h.droppedAt < ArcadeConfig.HazardOwnerGrace)
                        continue;
                    if (!h.Contains(car.transform.position, rad)) continue;
                    OnHazardHit(h, r);
                }
            }
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
            UpdateAreaHazards();

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
                UpdateDraft();
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
                case ItemKind.SmokeCloud:
                case ItemKind.OilSlick:
                    // All three are rear-facing area denial: worth nothing at all
                    // unless somebody is close enough behind to drive into it.
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
                int face = 1 + (int)(Clock * ArcadeConfig.RouletteFaceHz)
                                   % ArcadeConfig.RouletteFaceCount;
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
                car.arcadeBoostAccel = (!wrecked && r.Boosting)
                    ? ArcadeConfig.BoostAccel * Mathf.Clamp01(
                        (ArcadeConfig.BoostTopSpeed - car.ForwardSpeed) / ArcadeConfig.BoostFadeBand)
                    : 0f;

                // The channels below are physics, so they belong to whichever
                // machine simulates this car — the host for its own and the
                // bots', each client for the one car it owns. Everywhere else
                // this is a ghost or a kinematic follower with nothing to write
                // to, and only the visuals matter.
                //
                // The deadlines themselves (spinUntil, wreckedUntil) arrive
                // through the sync stream, so an owner reads exactly the state
                // the host decided.
                if (!DrivesPhysics(r)) { UpdateGhostVfx(r, dt); continue; }

                // Bot blindness is PUSHED, where the human tint is PULLED by the
                // HUD at draw time. Control only exists on the machine that
                // simulates the car — which is exactly what the gate above just
                // established — whereas a tint only needs the deadline, which
                // every machine has.
                if (r.isBot && r.rig != null && r.rig.input != null &&
                    r.rig.input.source is BotDriver bot)
                    bot.SetBlind(r.blindUntil - Clock,
                        ArcadeConfig.BlindBotSteerRelease, ArcadeConfig.BlindBotThrottle);

                // Re-asserted here with the rest of them so the drift is the only
                // thing that can ever turn the slip-killing assists down, and only
                // for the frames it is actually holding a slide. UpdateDrift runs
                // below and overwrites this when it wants to.
                car.arcadeAssistMult = 1f;
                car.arcadeHandbrakeMult = 1f;

                if (wrecked)
                {
                    // Limp: no drive, no grip, no held torque — it is tumbling
                    // from the impulse the hit already gave it. The stability
                    // boost stands down here and while spun: a hit must
                    // out-rotate anything that is normally helping you.
                    car.arcadeGripMult = ArcadeConfig.SpinGripMult * r.gripBase;
                    car.arcadeYawTorque = 0f;
                    car.arcadeDriveMult = 0f;
                    car.arcadeStabilityMult = 1f;
                }
                else if (spun)
                {
                    car.arcadeGripMult = ArcadeConfig.SpinGripMult * r.gripBase;
                    car.arcadeYawTorque = r.spinTorqueSigned;
                    car.arcadeDriveMult = ArcadeConfig.SpinDriveMult * r.driveBase;
                    car.arcadeStabilityMult = 1f;
                }
                else
                {
                    // The slick multiplies INTO this write rather than being set
                    // once when the car enters: ApplyEffects re-asserts every
                    // arcade channel every frame, so anything written outside
                    // this block is stomped on the following one.
                    car.arcadeGripMult = r.gripBase *
                        (r.OnSlick ? ArcadeConfig.SlickGripMult : 1f);
                    car.arcadeYawTorque = 0f;
                    car.arcadeDriveMult = r.driveBase;
                    car.arcadeStabilityMult = r.stabilityBase;
                }

                UpdateShieldViz(r, dt);
                UpdateBoostViz(r);
                UpdateDrift(r, car, dt, wrecked || spun);

                // Slipstream, maxed into the same channel the item boost uses
                // rather than added: drafting should close a gap, not stack on
                // top of a mushroom and launch you past the field.
                if (!wrecked && r.draftAccel > 0f)
                    car.arcadeBoostAccel = Mathf.Max(car.arcadeBoostAccel, r.draftAccel);

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

        // ================= drift boost + slipstream =================

        /// <summary>
        /// The drift: commit to a slide, hold it as a steerable arc, straighten
        /// out of it and pay a mini-turbo proportional to how long it was held.
        ///
        /// This is LATCHED rather than detected. The first version watched for
        /// handbrake + speed + slip angle and paid out if it happened to see all
        /// three, which made the drift something the physics might grant you.
        /// Pulling the handbrake while turned now COMMITS the car — direction
        /// latched from the steering command at that instant — and only releasing
        /// it ends the slide. Three consequences worth stating, because each one
        /// is the difference between a mechanic and an accident:
        ///
        ///   * <b>Entry</b> kicks. The same yaw controller that later maintains
        ///     the angle runs with a much larger clamp for a fifth of a second,
        ///     so the car sets itself into the slide instead of easing in. Using
        ///     a torque rather than an angular impulse keeps it independent of
        ///     whatever inertia tensor the garage handed this design.
        ///   * <b>Sustain</b> is an arc you steer, and the stick does two jobs at
        ///     once: it picks the held slip angle between <c>DriftAngleMin/MaxDeg</c>
        ///     AND sets the charge rate, so leaning into the slide both tightens
        ///     the line and pays better than nursing it on counter-steer. The car
        ///     keeps its speed through all of this because the handbrake stops
        ///     being a brake (<c>CarVehicle.arcadeHandbrakeMult</c>) and
        ///     <c>DriftCarryAccel</c> pushes along the nose, which is also what
        ///     rotates the velocity vector onto the heading. Both slip-killing
        ///     assists are turned down — see <c>CarVehicle.arcadeAssistMult</c>,
        ///     without which a Standard-assist car simply refuses to drift.
        ///   * <b>Exit</b> straightens and pops. Releasing aims the same controller
        ///     at zero slip and fires a per-tier impulse along the nose, so the
        ///     mini-turbo lands as an event pointing down the road rather than as a
        ///     gradual recovery pointing sideways.
        ///
        /// The payout goes to <see cref="ArcadeRacer.driftBoostUntil"/>, not to
        /// <c>boostUntil</c>: see that field for why a LAN client needs its own.
        /// </summary>
        private void UpdateDrift(ArcadeRacer r, CarVehicle car, float dt, bool disabled)
        {
            if (disabled)
            {
                // Spun or wrecked: drop everything without paying. Being hit
                // mid-drift should cost you the turbo, not hand you one.
                if (r.Drifting || r.driftCharge > 0f) EndDrift(r, car, false);
                r.driftStraightenUntil = 0f;
                return;
            }

            float v = car.ForwardSpeed;
            // Signed body slip: positive when the nose points RIGHT of where the
            // car is actually travelling, i.e. a right-hand drift. LateralSpeed is
            // positive to the right, so it carries the opposite sign — the
            // negation here is the whole convention and is easy to lose.
            float slipSigned = -Mathf.Atan2(car.LateralSpeed, Mathf.Max(0.5f, Mathf.Abs(v)))
                               * Mathf.Rad2Deg;

            if (r.Drifting)
            {
                if (car.Handbraking && v > ArcadeConfig.DriftHoldSpeed)
                {
                    SustainDrift(r, car, dt, slipSigned);
                    return;
                }
                EndDrift(r, car, true);
                return;
            }

            // Not drifting. Commit if the handbrake is down, we are moving, and
            // the wheel is actually turned — the turn is the request, the
            // handbrake is the commitment.
            float steer = car.SteerCommand;
            if (car.Handbraking && v > ArcadeConfig.DriftMinSpeed &&
                Mathf.Abs(steer) >= ArcadeConfig.DriftEntrySteer)
            {
                BeginDrift(r, car, steer);
                SustainDrift(r, car, dt, slipSigned);
                return;
            }

            // Still unwinding the last one.
            if (Clock < r.driftStraightenUntil) StraightenDrift(r, car, slipSigned);
        }

        private void BeginDrift(ArcadeRacer r, CarVehicle car, float steer)
        {
            r.driftDir = steer > 0f ? 1 : -1;
            r.driftCharge = 0f;
            r.driftTier = 0;
            r.driftKickUntil = Clock + ArcadeConfig.DriftKickSeconds;
            r.driftStraightenUntil = 0f;

            // The "jump". Mostly so the commitment is visible, but it also
            // unloads the tyres for a moment, which is what makes the slide start
            // crisply rather than washing in. Applied at the body centre so it
            // lifts rather than pitches.
            car.ArcadeImpulse(Vector3.up * ArcadeConfig.DriftHopImpulse, car.transform.position);
            ShowDriftSparks(r);

            // No event is raised here on purpose. The only kind that fits is
            // BoostStarted, and the audio layer would answer it with the boost
            // whoosh — announcing a reward at the moment the player has merely
            // asked for one. The hop, the tyres and the meter are the entry
            // feedback; the whoosh belongs to the payout.
        }

        /// <summary>One frame of a committed slide: hold the angle, keep the
        /// speed, bank the charge.</summary>
        private void SustainDrift(ArcadeRacer r, CarVehicle car, float dt, float slipSigned)
        {
            int dir = r.driftDir;
            float slipIn = slipSigned * dir;          // + = sliding the way we asked

            // The stick picks a point on the angle band: full lock into the slide
            // opens it out, counter-steer closes it down. This is the "arc you
            // steer" — without it the drift is one fixed radius and the player is
            // a passenger.
            float steerIn = Mathf.Clamp(car.SteerCommand * dir, -1f, 1f);
            float target = Mathf.Lerp(ArcadeConfig.DriftAngleMinDeg, ArcadeConfig.DriftAngleMaxDeg,
                (steerIn + 1f) * 0.5f);

            float clamp = Clock < r.driftKickUntil
                ? ArcadeConfig.DriftYawKick : ArcadeConfig.DriftYawHold;
            float torque = Mathf.Clamp((target - slipIn) * ArcadeConfig.DriftYawGain, -clamp, clamp);
            car.arcadeYawTorque = torque * dir;

            // Multiplied INTO whatever ApplyEffects just wrote (the slick lives in
            // the same product), never assigned over it — a drift on oil should be
            // both, not the more recent of the two.
            car.arcadeGripMult *= ArcadeConfig.DriftGripMult;
            car.arcadeAssistMult = ArcadeConfig.DriftAssistMult;
            // The arcade ESC boost stands all the way down: a drift is the
            // player asking for yaw, and a damper strong enough to stop a
            // spin-out is strong enough to iron the slide flat. The residual
            // fifth of sim-strength stability (assist mult above) is the only
            // safety net the slide keeps.
            car.arcadeStabilityMult = 1f;

            // Stop the drift button being a brake. This is the difference between
            // an arc and a handbrake turn: a locked rear axle bleeds the speed the
            // rest of this method is trying to preserve, and it bleeds it faster
            // than DriftCarryAccel can put it back.
            car.arcadeHandbrakeMult = ArcadeConfig.DriftHandbrakeMult;

            // Pay back the lateral scrub so the arc carries its momentum. Maxed
            // into the boost channel rather than added, for the same reason the
            // draft is: this must never stack into a second top speed.
            car.arcadeBoostAccel = Mathf.Max(car.arcadeBoostAccel,
                ArcadeConfig.DriftCarryAccel * Mathf.Clamp01(
                    (ArcadeConfig.DriftCarryTopSpeed - car.ForwardSpeed) /
                    ArcadeConfig.DriftCarryFadeBand));

            // Charge follows COMMITMENT, not the clock: the same stick position
            // that just chose the angle also sets the rate. Holding a lazy
            // counter-steered slide is now worth roughly a quarter of what leaning
            // on it is, which is what makes the drift something you are doing
            // rather than something you are waiting through.
            r.driftCharge += dt * Mathf.Lerp(ArcadeConfig.DriftChargeOut,
                ArcadeConfig.DriftChargeInto, (steerIn + 1f) * 0.5f);

            int tier = r.driftCharge >= ArcadeConfig.DriftTier3Seconds ? 3
                     : r.driftCharge >= ArcadeConfig.DriftTier2Seconds ? 2
                     : r.driftCharge >= ArcadeConfig.DriftTier1Seconds ? 1 : 0;
            if (tier != r.driftTier)
            {
                r.driftTier = tier;
                ShowDriftSparks(r);
            }

            // Tire smoke in the car's own colour. Attached lazily (most cars
            // never drift — the bots don't handbrake at all), then only the
            // flag is touched: the component owns its puffs' lives.
            if (r.driftSmoke == null) r.driftSmoke = DriftSmoke.Attach(car);
            if (r.driftSmoke != null) r.driftSmoke.emitting = true;
        }

        /// <summary>The exit arc: same controller, target zero. Grip and the
        /// assists are already back to normal by here — those are what was holding
        /// the slide open — so this only has to finish the rotation.</summary>
        private void StraightenDrift(ArcadeRacer r, CarVehicle car, float slipSigned)
        {
            float slipIn = slipSigned * r.driftDirLast;
            if (slipIn <= ArcadeConfig.DriftStraightenDoneDeg)
            {
                r.driftStraightenUntil = 0f;
                return;
            }
            float torque = Mathf.Clamp(-slipIn * ArcadeConfig.DriftYawGain,
                -ArcadeConfig.DriftYawStraighten, ArcadeConfig.DriftYawStraighten);
            car.arcadeYawTorque = torque * r.driftDirLast;
        }

        /// <summary>Sparks appear the moment the drift is COMMITTED, not when the
        /// first tier lands — they are the in-world half of the charge meter, and
        /// a mechanic you latched into should show something immediately. Tier 0
        /// wears the neutral colour and the three tiers repaint the same geometry
        /// through a property block.</summary>
        private void ShowDriftSparks(ArcadeRacer r)
        {
            if (!r.Drifting || r.car == null) { r.HideDriftSparks(); return; }
            if (r.driftSparks == null) r.driftSparks = ArcadeVfx.BuildDriftSparks(r.car.transform);
            TintDriftSparks(r, r.driftTier);
        }

        /// <summary>One colour table for the sparks, two callers: the owner's
        /// tier machine above, and the ghost path below painting the tier the
        /// sync stream reported.</summary>
        private static void TintDriftSparks(ArcadeRacer r, int tier)
        {
            var c = tier <= 0
                ? ArcadeConfig.DriftChargeColor
                : ArcadeConfig.DriftTierColors[Mathf.Clamp(tier - 1, 0,
                    ArcadeConfig.DriftTierColors.Length - 1)];
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor("_Color", c);
            mpb.SetColor("_EmissionColor", c * 2f);
            foreach (var rend in r.driftSparks.GetComponentsInChildren<Renderer>(true))
                rend.SetPropertyBlock(mpb);
        }

        private void EndDrift(ArcadeRacer r, CarVehicle car, bool award)
        {
            int tier = r.driftTier;
            if (r.driftDir != 0) r.driftDirLast = r.driftDir;
            r.driftDir = 0;
            r.driftCharge = 0f;
            r.driftTier = 0;
            r.driftKickUntil = 0f;
            r.HideDriftSparks();
            if (r.driftSmoke != null) r.driftSmoke.emitting = false;

            // Straighten on any voluntary exit, tier or no tier: a slide you drop
            // out of at half a second still leaves the car pointing sideways, and
            // finishing that rotation is the half of the mechanic that has nothing
            // to do with the reward. Forfeits (spun, wrecked) get nothing — the
            // car is supposed to be out of control.
            if (!award) { r.driftStraightenUntil = 0f; return; }
            r.driftStraightenUntil = Clock + ArcadeConfig.DriftStraightenSeconds;
            if (tier <= 0) return;

            // Owner-local, deliberately not boostUntil — see the field.
            r.driftBoostUntil = Mathf.Max(r.driftBoostUntil,
                Clock + ArcadeConfig.DriftBoostSeconds * tier);

            // Fired along the NOSE, not along the velocity: the car is still
            // sideways at this instant and the straighten below is about to bring
            // the two together, so pushing on the heading is what makes the exit
            // hook up. Through the CoM — the positioned overload would somersault
            // it (see CarVehicle.ArcadeImpulse).
            car.ArcadeImpulse(car.transform.forward *
                (ArcadeConfig.DriftExitImpulse * tier));

            // The meter vanishes on release, so this is what tells the player what
            // the slide was actually worth. It reuses the hit banner's channel
            // (ShowHit is the general "say something over this player's viewport"
            // path, not a hit-only one) with a zero-length flash, because a
            // full-viewport colour wash is a punishment and this is a reward.
            r.ShowHit(tier >= 3 ? "MINI-TURBO ×3" : tier == 2 ? "MINI-TURBO ×2" : "MINI-TURBO",
                ArcadeConfig.DriftTierColors[tier - 1], 0.9f, 0f);

            // Raised as a BoostStarted so the audio layer needs no new case — it
            // IS a boost starting, and the item field says which flavour.
            Raise(ArcadeEventKind.BoostStarted, r, null, ItemKind.Boost, 0,
                car.transform.position, car.transform.rotation);
        }

        /// <summary>
        /// Slipstream: a car tucked in behind another, inside a heading cone, gets
        /// a modest tow. Systemic catch-up that costs no item and no pickup, so
        /// closing a gap on a straight is something you can do by driving.
        ///
        /// Computed at the 5 Hz position tick rather than per frame — it is an
        /// O(n²) search over the field, and at RC speeds a 200 ms update is
        /// indistinguishable from a per-frame one.
        /// </summary>
        private void UpdateDraft()
        {
            for (int i = 0; i < _racers.Count; i++) _racers[i].draftAccel = 0f;
            if (!IsAuthority) return;

            float cos = Mathf.Cos(ArcadeConfig.DraftConeDeg * Mathf.Deg2Rad);
            float rangeSq = ArcadeConfig.DraftRange * ArcadeConfig.DraftRange;

            for (int i = 0; i < _racers.Count; i++)
            {
                var r = _racers[i];
                var car = r.car;
                if (car == null || r.Wrecked) continue;

                float speed = car.ForwardSpeed;
                if (speed <= 0.5f) continue;      // no tow from a standstill

                Vector3 fwd = car.transform.forward;
                for (int k = 0; k < _racers.Count; k++)
                {
                    if (k == i) continue;
                    var o = _racers[k];
                    if (o.car == null || o.Wrecked) continue;

                    Vector3 d = o.car.transform.position - car.transform.position;
                    float dsq = d.sqrMagnitude;
                    if (dsq > rangeSq || dsq < 1e-4f) continue;
                    if (Vector3.Dot(d / Mathf.Sqrt(dsq), fwd) < cos) continue;   // not ahead

                    // Fades out as the tow approaches its own ceiling, exactly
                    // like the item boost — a draft must not be a second top speed.
                    r.draftAccel = ArcadeConfig.DraftAccel *
                        Mathf.Clamp01((ArcadeConfig.DraftTopSpeed - speed) / 2f);
                    break;
                }
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

        /// <summary>
        /// Rear flame from <c>r.Boosting</c> alone, the shield-viz way: every
        /// path a boost can end — expiry, wreck, respawn, restart — is covered
        /// by the one state check. Works unchanged on a ghost, because the sync
        /// stream Hold-arms its <c>boostUntil</c>.
        /// </summary>
        private void UpdateBoostViz(ArcadeRacer r)
        {
            bool on = r.car != null && Clock >= r.wreckedUntil && r.Boosting;
            if (!on) { r.HideBoostFlame(); return; }

            if (r.boostViz == null) r.boostViz = ArcadeVfx.BuildBoostFlame(r.car.transform);
            float pulse = 1f + BoostFlamePulseAmp *
                Mathf.Sin(Clock * BoostFlamePulseHz * 2f * Mathf.PI);
            r.boostViz.localScale = new Vector3(1f, 1f, pulse);
        }

        /// <summary>
        /// Everything a non-simulated car shows: the visuals-only half of
        /// ApplyEffects, run for LAN ghosts and the host's follower copies.
        /// Shield and boost read the same deadlines the owned path reads
        /// (Hold-armed by the sync stream); smoke and sparks read the protocol-6
        /// remote-drift mirror. Sparks are tinted only when the reported tier
        /// actually changes — TintDriftSparks allocates a property block, and
        /// 60 Hz of those for a 15 Hz signal is pure garbage.
        /// </summary>
        private void UpdateGhostVfx(ArcadeRacer r, float dt)
        {
            UpdateShieldViz(r, dt);
            UpdateBoostViz(r);

            bool drifting = r.car != null && r.RemoteDrifting;
            if (r.driftSmoke == null && drifting) r.driftSmoke = DriftSmoke.Attach(r.car);
            if (r.driftSmoke != null) r.driftSmoke.emitting = drifting;

            if (!drifting) { r.HideDriftSparks(); return; }
            if (r.driftSparks == null || r.remoteDriftTier != r.remoteDriftTierShown)
            {
                if (r.driftSparks == null)
                    r.driftSparks = ArcadeVfx.BuildDriftSparks(r.car.transform);
                TintDriftSparks(r, r.remoteDriftTier);
                r.remoteDriftTierShown = r.remoteDriftTier;
            }
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
            if (!trackLimits) return;
            // No tile/ribbon surface data (classic oval, diff-drive scene): every
            // hit resolves to Baseline, i.e. "on track" everywhere. Stay silent
            // rather than pretend.
            if (SurfaceMap.Active == null) return;

            foreach (var r in _racers)
            {
                // Judged by whoever has the wheels. A kinematic follower or ghost
                // never runs its WheelColliders, so GetGroundHit would report
                // every wheel airborne and hand out a penalty for standing still.
                // The owner tests its own car and sends the verdict up.
                if (!DrivesPhysics(r)) continue;

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
                    // An owning client detects its own penalty but does not
                    // announce it: the flag rides up with its pose, the host
                    // raises the event, and the broadcast reaches every machine
                    // including this one. Announcing here too would double the
                    // banner and the sound on the one screen that matters most.
                    if (IsAuthority)
                        Raise(ArcadeEventKind.OffTrackPenalty, null, r, ItemKind.None, 0,
                            car.transform.position, car.transform.rotation);
                }
                else if (!r.warned && r.offTrackTime > ArcadeConfig.OffTrackWarnSeconds)
                {
                    r.warned = true;
                    if (IsAuthority)
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
