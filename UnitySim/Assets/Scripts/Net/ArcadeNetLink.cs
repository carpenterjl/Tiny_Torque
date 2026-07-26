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

        private bool _host;

        private void Start()
        {
            if (S == null || director == null) { enabled = false; return; }
            _host = S.IsHost;

            if (_host)
            {
                director.Event += OnLocalEvent;
            }
            else
            {
                S.ArcSyncReceived += OnSync;
                S.ArcEventReceived += OnRemoteEvent;
            }
        }

        private void OnDestroy()
        {
            if (director != null) director.Event -= OnLocalEvent;
            if (S == null) return;
            S.ArcSyncReceived -= OnSync;
            S.ArcEventReceived -= OnRemoteEvent;
        }

        // ================= host =================

        private void Update()
        {
            if (!_host || S == null || director == null) return;
            _accum += Time.unscaledDeltaTime;
            if (_accum < SyncInterval) return;
            _accum = 0f;
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
            if (clock < r.boostUntil) fx |= ArcEffect.Boost;
            if (clock < r.shieldUntil) fx |= ArcEffect.Shield;
            if (clock < r.spinUntil) fx |= ArcEffect.Spun;
            if (r.Wrecked) fx |= ArcEffect.Wrecked;
            if (r.penalized) fx |= ArcEffect.Penalized;
            if (r.warned) fx |= ArcEffect.Warned;
            if (director.IncomingMissile(r.car)) fx |= ArcEffect.Incoming;
            return fx;
        }

        private void OnLocalEvent(ArcadeEvent e)
        {
            if (!_host || S == null) return;
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
                r.penalized = (a.effects & ArcEffect.Penalized) != 0;
                r.warned = (a.effects & ArcEffect.Warned) != 0;
                r.incomingRemote = (a.effects & ArcEffect.Incoming) != 0;
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
                    t = SpawnProjectileViz(p.kind);
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

        /// <summary>Visual only: no trigger, no Missile/Banana component. A client
        /// must never be able to detonate anything.</summary>
        private Transform SpawnProjectileViz(int kind)
        {
            var go = new GameObject(kind == NetPack.ProjMissile ? "NetMissile" : "NetBanana");
            if (kind == NetPack.ProjMissile) ArcadeVfx.BuildMissile(go.transform);
            else ArcadeVfx.BuildBanana(go.transform);
            return go.transform;
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

                case ArcadeEventKind.MissileExpired:
                    ArcadeBurst.Spawn(m.pos, 0.5f, ArcadeConfig.SpinFeedbackColor,
                        ArcadeConfig.ExplosionSeconds);
                    break;
            }
        }
    }
}
