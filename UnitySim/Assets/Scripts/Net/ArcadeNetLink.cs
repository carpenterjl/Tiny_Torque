using System.Collections.Generic;
using AIHWSim.Arcade;
using UnityEngine;

namespace AIHWSim.Net
{
    /// <summary>
    /// The only place that knows both the arcade layer and the network layer.
    ///
    /// On the host it reads the director and publishes; on a client it writes
    /// into the director and never decides anything. Keeping it in one component
    /// is what lets <see cref="ArcadeDirector"/> stay ignorant of the network and
    /// <see cref="NetSession"/> stay ignorant of power-ups — neither has to grow
    /// a mode flag for the other.
    ///
    /// Two things are synced very differently on purpose. Inventories, effects
    /// and item boxes are STATE, streamed at 15 Hz and idempotent: a dropped
    /// packet costs 66 ms of staleness and nothing else. Hits, pickups and
    /// launches are EVENTS, sent reliably exactly once, because a missed
    /// explosion is a missing bang and a duplicated one is two.
    /// </summary>
    public sealed class ArcadeNetLink : MonoBehaviour
    {
        public ArcadeDirector director;

        private const float SyncInterval = 1f / 15f;
        /// <summary>How long a streamed effect flag stays "on" after the packet
        /// that set it. Comfortably longer than the sync period, so a single lost
        /// packet cannot make a shield blink.</summary>
        private const float EffectHold = 0.25f;

        private float _accum;
        private NetSession S => NetSession.Instance;

        private readonly List<ArcRacerState> _racers = new List<ArcRacerState>();
        private readonly List<ArcProjState> _projectiles = new List<ArcProjState>();
        private readonly List<byte> _boxMask = new List<byte>();

        // Client: visual-only projectiles, keyed by the host's object id.
        private readonly Dictionary<int, Transform> _proj = new Dictionary<int, Transform>();
        private readonly HashSet<int> _live = new HashSet<int>();
        /// <summary>Projectile kinds this build does not recognise, so a mismatched
        /// host is reported once rather than fifteen times a second.</summary>
        private readonly HashSet<int> _unknownKinds = new HashSet<int>();

        private bool _host;
        /// <summary>Client: the publisher for our own car, so a recovery we
        /// apply locally can announce itself as a teleport.</summary>
        private OwnStateSender _ownSender;

        private void Start()
        {
            if (S == null || director == null) { enabled = false; return; }
            _host = S.IsHost;
            if (!_host) _ownSender = FindFirstObjectByType<OwnStateSender>();

            if (_host)
            {
                director.Event += OnLocalEvent;
                // Since protocol 4 every slot but ours is simulated by its
                // owner, so the director decides what happens to those cars but
                // hands the physics over instead of applying it.
                director.RemoteOwned = slot => slot >= 0 && slot != S.LocalSlot;
                director.EffectDispatch += OnEffectDispatch;
            }
            else
            {
                S.ArcSyncReceived += OnSync;
                S.ArcEventReceived += OnRemoteEvent;
                S.ArcFxReceived += OnArcFx;
            }
        }

        private void OnDestroy()
        {
            if (director != null)
            {
                director.Event -= OnLocalEvent;
                director.EffectDispatch -= OnEffectDispatch;
                director.RemoteOwned = null;
            }
            if (S == null) return;
            S.ArcSyncReceived -= OnSync;
            S.ArcEventReceived -= OnRemoteEvent;
            S.ArcFxReceived -= OnArcFx;
        }

        /// <summary>Host: ship one effect's physics to the machine that owns the car.</summary>
        private void OnEffectDispatch(ArcadeFx fx)
        {
            if (!_host || S == null) return;
            S.SendArcFxTo(fx.slot, new ArcFxMsg
            {
                kind = (int)fx.kind,
                slot = fx.slot,
                impulse = fx.impulse,
                torqueImpulse = fx.torqueImpulse,
                spinTorqueSigned = fx.spinTorqueSigned,
                pos = fx.pos,
                rot = fx.rot,
            });
        }

        // ================= host =================

        private void Update()
        {
            if (!_host || S == null || director == null) return;
            _accum += Time.unscaledDeltaTime;
            if (_accum < SyncInterval) return;
            // Subtract, don't zero: zeroing rounds the period up to a whole
            // frame and silently drops the rate below 15 Hz.
            _accum = Mathf.Min(_accum - SyncInterval, SyncInterval);
            Publish();
        }

        private void Publish()
        {
            _racers.Clear();
            foreach (var r in director.Racers)
            {
                if (r == null || r.netSlot < 0) continue;
                _racers.Add(new ArcRacerState
                {
                    slot = r.netSlot,
                    held = (int)r.held,
                    charges = r.charges,
                    rollFace = (int)r.rollFace,
                    effects = EffectsOf(r),
                    position = r.livePosition,
                    points = r.points,
                    blindLeftDs = NetPack.PackDs(r.blindUntil - ArcadeDirector.Clock),
                });
            }

            _projectiles.Clear();
            foreach (var m in director.Missiles)
            {
                if (m == null) continue;
                _projectiles.Add(new ArcProjState
                {
                    objId = m.objId,
                    kind = NetPack.ProjMissile,
                    pos = m.transform.position,
                    rot = m.transform.rotation,
                });
            }
            foreach (var b in director.Bananas)
            {
                if (b == null) continue;
                _projectiles.Add(new ArcProjState
                {
                    objId = b.objId,
                    kind = NetPack.ProjBanana,
                    pos = b.transform.position,
                    rot = b.transform.rotation,
                });
            }
            // Area hazards go out the same way. A smoke cloud DRIFTS, and its
            // drift is derived from the object id so every machine could animate
            // it identically — but only from the same starting instant, which is
            // exactly what a client cannot know. Streaming the pose costs one
            // more entry in a block that already carries a dozen and removes the
            // question.
            foreach (var h in director.Hazards)
            {
                if (h == null) continue;
                _projectiles.Add(new ArcProjState
                {
                    objId = h.objId,
                    kind = h.kind == ItemKind.OilSlick ? NetPack.ProjSlick : NetPack.ProjSmoke,
                    pos = h.transform.position,
                    rot = h.transform.rotation,
                });
            }

            // One bit per box, in build order. Boxes are laid out from the same
            // track and the same racing line on every machine, so the index is
            // the identity and nothing else has to be sent.
            _boxMask.Clear();
            int n = director.BoxCount;
            for (int i = 0; i < n; i += 8)
            {
                byte b = 0;
                for (int k = 0; k < 8 && i + k < n; k++)
                    if (director.BoxActive(i + k)) b |= (byte)(1 << k);
                _boxMask.Add(b);
            }

            S.HostBroadcastArcSync(_racers, _projectiles, _boxMask);
        }

        private ArcEffect EffectsOf(ArcadeRacer r)
        {
            float clock = ArcadeDirector.Clock;
            var fx = ArcEffect.None;
            if (r.rolling) fx |= ArcEffect.Rolling;
            // Boosting, not boostUntil: a mini-turbo the host awarded to itself or
            // to a bot is a real boost and other machines should see the trail.
            // (A CLIENT's mini-turbo is invisible here by construction — the host
            // does not simulate that car and never learns about its drift. Purely
            // cosmetic, and the alternative is an upward wire field for a spark.)
            if (r.Boosting) fx |= ArcEffect.Boost;
            if (clock < r.shieldUntil) fx |= ArcEffect.Shield;
            if (clock < r.spinUntil) fx |= ArcEffect.Spun;
            if (r.Wrecked) fx |= ArcEffect.Wrecked;
            if (r.penalized) fx |= ArcEffect.Penalized;
            if (r.warned) fx |= ArcEffect.Warned;
            if (director.IncomingMissile(r.car)) fx |= ArcEffect.Incoming;
            if (r.OnSlick) fx |= ArcEffect.Slicked;
            return fx;
        }

        private void OnLocalEvent(ArcadeEvent e)
        {
            if (!_host || S == null) return;

            // A recovery is a teleport. Without the bump the clients lerp the
            // car from where it blew up to where it was set down, which reads as
            // the wreck sliding home rather than being lifted.
            if (e.kind == ArcadeEventKind.Recovered && e.dstSlot >= 0)
                S.BumpCarEpoch(e.dstSlot);

            S.HostBroadcastArcEvent(new ArcEvtMsg
            {
                kind = (int)e.kind,
                src = e.srcSlot,
                dst = e.dstSlot,
                item = (int)e.item,
                objId = e.objId,
                pos = e.pos,
                rot = e.rot,
            });
        }

        // ================= client =================

        private void OnSync()
        {
            if (director == null) return;
            float clock = ArcadeDirector.Clock;

            foreach (var a in S.ArcRacers)
            {
                var r = director.RacerForSlot(a.slot);
                if (r == null) continue;

                r.held = (ItemKind)a.held;
                r.charges = a.charges;
                r.rollFace = (ItemKind)a.rollFace;
                r.livePosition = a.position;
                r.points = a.points;

                // Deadlines rather than booleans, so every consumer on the client
                // reads the same fields it reads on the host. The hold is short
                // enough that a lifted shield disappears promptly and long enough
                // to bridge a lost packet.
                r.rolling = (a.effects & ArcEffect.Rolling) != 0;
                r.boostUntil = Hold(a.effects, ArcEffect.Boost, clock);
                r.shieldUntil = Hold(a.effects, ArcEffect.Shield, clock);
                r.spinUntil = Hold(a.effects, ArcEffect.Spun, clock);
                r.wreckedUntil = Hold(a.effects, ArcEffect.Wrecked, clock);
                // Our own off-track state is decided here, by the wheels that
                // actually touch the road, and sent upward. Taking the host's
                // echo back would overwrite a live verdict with a round-trip-old
                // copy of itself and make the banner stutter.
                if (a.slot != S.LocalSlot)
                {
                    r.penalized = (a.effects & ArcEffect.Penalized) != 0;
                    r.warned = (a.effects & ArcEffect.Warned) != 0;
                }
                r.incomingRemote = (a.effects & ArcEffect.Incoming) != 0;

                // The area hazards are the exact opposite case to the off-track
                // verdict above: they are taken here UNCONDITIONALLY, our own car
                // included. Containment is polled on the host against its own
                // 60 Hz positions, because a client testing its interpolated
                // ghosts (60-120 ms behind) would put the two machines on
                // different sides of the same cloud edge.
                //
                // The slick matters most of all. Since protocol 4 a client
                // simulates its own car, so this flag is the ONLY way the oil
                // reaches the physics of a human player's car — without it a
                // slick would affect the host and the bots and nobody else.
                r.slickUntil = Hold(a.effects, ArcEffect.Slicked, clock);

                // Blindness arrives as a real remaining duration, not a liveness
                // hold, so the tint's ramp-hold-fade envelope is the one the host
                // is drawing rather than a permanent third of it.
                float blindLeft = NetPack.UnpackDs(a.blindLeftDs);
                if (blindLeft > 0f && !r.Blinded) r.blindStartedAt = clock;
                r.blindUntil = blindLeft > 0f ? clock + blindLeft : 0f;
            }

            SyncProjectiles();
            SyncBoxes();
        }

        private static float Hold(ArcEffect fx, ArcEffect bit, float clock) =>
            (fx & bit) != 0 ? clock + EffectHold : 0f;

        private void SyncProjectiles()
        {
            _live.Clear();
            foreach (var p in S.ArcProjectiles)
            {
                _live.Add(p.objId);
                if (!_proj.TryGetValue(p.objId, out var t) || t == null)
                {
                    t = SpawnProjectileViz(p);
                    if (t == null) continue;      // unknown kind: draw nothing
                    _proj[p.objId] = t;
                }
                t.SetPositionAndRotation(p.pos, p.rot);
            }

            // Anything the host stopped sending is gone. Bananas expire and
            // missiles detonate on the host's clock, so this is the only signal
            // a client needs — and it cannot leave one behind.
            List<int> stale = null;
            foreach (var kv in _proj)
            {
                if (_live.Contains(kv.Key)) continue;
                (stale ??= new List<int>()).Add(kv.Key);
            }
            if (stale == null) return;
            foreach (int id in stale)
            {
                if (_proj[id] != null) Destroy(_proj[id].gameObject);
                _proj.Remove(id);
            }
        }

        /// <summary>
        /// Visual only: no trigger, no Missile/Banana/AreaHazard component. A
        /// client must never be able to detonate anything or decide who a cloud
        /// caught.
        ///
        /// The default returns NULL rather than falling back to a banana. An
        /// unknown kind means this build and the host's disagree, and rendering a
        /// convincing hazard that exists on neither machine's rules is the worst
        /// available failure — a player would swerve around nothing, or drive
        /// through what looks like a peel and take a hit they never saw coming.
        /// Drawing nothing is at least honest about not knowing.
        /// </summary>
        private Transform SpawnProjectileViz(in ArcProjState p)
        {
            switch (p.kind)
            {
                case NetPack.ProjMissile:
                {
                    var go = new GameObject("NetMissile");
                    ArcadeVfx.BuildMissile(go.transform);
                    return go.transform;
                }
                case NetPack.ProjBanana:
                {
                    var go = new GameObject("NetBanana");
                    ArcadeVfx.BuildBanana(go.transform);
                    return go.transform;
                }
                case NetPack.ProjSmoke:
                    return SpawnHazardViz(ItemKind.SmokeCloud, p, ArcadeConfig.SmokeLifetime);
                case NetPack.ProjSlick:
                    return SpawnHazardViz(ItemKind.OilSlick, p, ArcadeConfig.SlickLifetime);
                default:
                    if (_unknownKinds.Add(p.kind))
                        Debug.LogWarning($"[ArcadeNetLink] Host sent projectile kind {p.kind}, " +
                                         "which this build does not know. Version mismatch?");
                    return null;
            }
        }

        /// <summary>
        /// Client-side hazard visual. The stream owns everything about it: the
        /// host stops sending when the hazard expires (exactly as for a banana),
        /// and the pose comes down the wire, so the viz must neither self-destruct
        /// nor drift under its own steam — two sources of truth for where a cloud
        /// is would have it wandering off the area that is actually catching
        /// people.
        ///
        /// The fade clock starts at first sight, which is within one sync period
        /// (66 ms) of the drop for anyone already in the session. A client that
        /// joined while a cloud was already up sees it pop rather than fade —
        /// which is the right way round, because the alternative is not seeing it.
        /// </summary>
        private static Transform SpawnHazardViz(ItemKind kind, in ArcProjState p, float lifetime)
        {
            var v = AreaHazardViz.Spawn(kind, p.pos, p.objId, lifetime);
            v.selfDestruct = false;
            v.drift = Vector3.zero;
            return v.transform;
        }

        private void SyncBoxes()
        {
            int n = director.BoxCount;
            var mask = S.ArcBoxMask;
            for (int i = 0; i < n; i++)
            {
                int byteIdx = i >> 3;
                if (byteIdx >= mask.Count) break;
                director.SetBoxActive(i, (mask[byteIdx] & (1 << (i & 7))) != 0);
            }
        }

        /// <summary>
        /// Client: apply one arcade effect the host decided about our car.
        ///
        /// Every value here was rolled on the host — the punt direction, the
        /// tumble, the spin's sign, the recovery pose — because two machines
        /// rolling their own would disagree about the same hit. We only do the
        /// applying.
        /// </summary>
        private void OnArcFx(ArcFxMsg m)
        {
            if (director == null || m == null) return;
            var r = director.RacerForSlot(m.slot);
            var car = r?.car;
            if (car == null) return;

            switch (m.kind)
            {
                case ArcFxMsg.KindSpin:
                    r.spinTorqueSigned = m.spinTorqueSigned;
                    car.ArcadeImpulse(m.impulse, car.transform.position);
                    break;

                case ArcFxMsg.KindWreck:
                    r.spinTorqueSigned = m.spinTorqueSigned;
                    car.ArcadeImpulse(m.impulse, car.transform.position);
                    car.ArcadeTorqueImpulse(m.torqueImpulse);
                    break;

                case ArcFxMsg.KindRecover:
                    car.RestoreState(m.pos, m.rot, Vector3.zero, Vector3.zero);
                    r.RestoreCar();
                    // A teleport we performed: tell everyone to snap us there
                    // rather than drag the wreck across the map to it.
                    _ownSender?.BumpEpoch();
                    break;
            }
        }

        private void OnRemoteEvent(ArcEvtMsg m)
        {
            if (director == null) return;

            director.RaiseRemote(new ArcadeEvent
            {
                t = ArcadeDirector.Clock,
                kind = (ArcadeEventKind)m.kind,
                srcSlot = m.src,
                dstSlot = m.dst,
                item = (ItemKind)m.item,
                objId = m.objId,
                pos = m.pos,
                rot = m.rot,
            });

            // The host runs its banners and bursts inside the hit handlers; the
            // client has no hit handlers, so the same feedback is reconstructed
            // from the event here rather than being a second wire field.
            var victim = m.dst >= 0 ? director.RacerForSlot(m.dst) : null;
            switch ((ArcadeEventKind)m.kind)
            {
                case ArcadeEventKind.Wrecked:
                    ArcadeBurst.Spawn(m.pos, 0.9f, ArcadeConfig.SpinFeedbackColor,
                        ArcadeConfig.ExplosionSeconds);
                    victim?.ShowHit("WRECKED!", ArcadeConfig.WreckFeedbackColor,
                        ArcadeConfig.WreckBannerSeconds, ArcadeConfig.HitFlashSeconds);
                    break;

                case ArcadeEventKind.BananaHit:
                    victim?.ShowHit("SPUN OUT!", ArcadeConfig.SpinFeedbackColor,
                        ArcadeConfig.HitBannerSeconds, ArcadeConfig.HitFlashSeconds);
                    break;

                case ArcadeEventKind.ShieldBlocked:
                    ArcadeBurst.Spawn(m.pos, 0.8f, ArcadeConfig.ShieldFeedbackColor, 0.35f);
                    victim?.ShowHit("SHIELD BLOCKED!", ArcadeConfig.ShieldFeedbackColor,
                        ArcadeConfig.HitBannerSeconds, ArcadeConfig.HitFlashSeconds);
                    break;

                // One kind for both hazards, told apart by the item — the same
                // split OnHazardHit makes on the host, and for the same reason:
                // the lifecycle is identical and only the effect differs.
                case ArcadeEventKind.HazardHit:
                    if ((ItemKind)m.item == ItemKind.OilSlick)
                        victim?.ShowHit("OIL SLICK!", ArcadeConfig.SlickFeedbackColor,
                            ArcadeConfig.HitBannerSeconds, ArcadeConfig.HitFlashSeconds);
                    else
                        victim?.ShowHit("SMOKED!", ArcadeConfig.BlindFeedbackColor,
                            ArcadeConfig.HitBannerSeconds, ArcadeConfig.HitFlashSeconds);
                    break;

                case ArcadeEventKind.MissileExpired:
                    ArcadeBurst.Spawn(m.pos, 0.5f, ArcadeConfig.SpinFeedbackColor,
                        ArcadeConfig.ExplosionSeconds);
                    break;
            }
        }
    }
}
