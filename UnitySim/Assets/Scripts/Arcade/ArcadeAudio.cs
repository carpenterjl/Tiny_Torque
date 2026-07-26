using AIHWSim.Audio;
using AIHWSim.Core;
using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// Sound for the arcade layer, driven off <see cref="ArcadeDirector.Event"/>.
    ///
    /// That event has existed since the layer was built — raised from fourteen
    /// call sites for the benefit of the LAN publisher and the future replay
    /// recorder — and until now had no subscribers at all. Hanging audio off it
    /// means no new plumbing in the director and no risk of a sound and a
    /// gameplay effect drifting out of step: they are literally the same signal.
    ///
    /// Events about the local player play flat (2D) so they are heard regardless
    /// of camera; everyone else's play positionally at the event location. Two
    /// sounds are continuous and owned here rather than raised as events — the
    /// boost loop and the incoming-missile alarm — because both are states, not
    /// moments.
    /// </summary>
    public sealed class ArcadeAudio : MonoBehaviour
    {
        public ArcadeDirector director;
        public PlayerRig localRig;

        private SfxPlayer _sfx;
        private AudioSource _boostLoop;
        private int _localSlot = -1;
        private bool _wasRolling;
        private float _nextWarnBeep;

        private ArcadeRacer Me => localRig != null ? localRig.arcade : null;

        private void Start()
        {
            _sfx = SfxPlayer.Ensure();
            if (director != null)
            {
                // Subscribed in Start, not OnEnable: OnEnable runs inside
                // AddComponent, before the caller has assigned `director`, so
                // subscribing there would silently attach to nothing.
                director.Event += OnArcadeEvent;
                if (Me != null) _localSlot = director.SlotOf(Me);
            }

            var clip = ProceduralAudio.Get(ProceduralAudio.BoostLoop);
            if (clip != null)
            {
                var go = new GameObject("boostLoop");
                go.transform.SetParent(transform, false);
                _boostLoop = SfxPlayer.Configure(go.AddComponent<AudioSource>(), spatial: false);
                _boostLoop.clip = clip;
                _boostLoop.loop = true;
                _boostLoop.volume = 0f;
                _boostLoop.Play();
            }
        }

        private void OnDestroy()
        {
            if (director != null) director.Event -= OnArcadeEvent;
        }

        // ================= discrete events =================

        private void OnArcadeEvent(ArcadeEvent e)
        {
            if (_sfx == null) return;

            bool mine = (e.srcSlot >= 0 && e.srcSlot == _localSlot) ||
                        (e.dstSlot >= 0 && e.dstSlot == _localSlot);

            switch (e.kind)
            {
                case ArcadeEventKind.ItemGranted:
                    if (mine) _sfx.Play2D(ProceduralAudio.Reveal, 0.7f);
                    break;

                case ArcadeEventKind.ItemUsed:
                    // Boost, missile and banana each raise their own event below;
                    // the shield does not, so this is its only cue.
                    if (e.item == ItemKind.Shield) Play(mine, ProceduralAudio.ShieldUp, e.pos, 0.8f);
                    break;

                case ArcadeEventKind.BoostStarted:
                    Play(mine, ProceduralAudio.Boost, e.pos, 0.9f);
                    break;

                case ArcadeEventKind.MissileFired:
                    Play(mine, ProceduralAudio.MissileFire, e.pos, 0.85f);
                    break;

                case ArcadeEventKind.BananaDropped:
                    Play(mine, ProceduralAudio.BananaDrop, e.pos, 0.7f);
                    break;

                case ArcadeEventKind.BananaHit:
                    Play(mine, ProceduralAudio.Spin, e.pos, 0.9f);
                    break;

                // Wrecked is raised by ApplyWreck and MissileHit by the hit
                // handler, for the same impact — take one, or every hit
                // double-fires.
                case ArcadeEventKind.Wrecked:
                    Play(mine, ProceduralAudio.Explosion, e.pos, 1f);
                    break;

                case ArcadeEventKind.MissileExpired:
                    // Burnt out rather than hit: same sound, quieter and duller.
                    _sfx.PlayAt(ProceduralAudio.Explosion, e.pos, 0.45f, 0.85f);
                    break;

                case ArcadeEventKind.ShieldBlocked:
                    Play(mine, ProceduralAudio.ShieldBlock, e.pos, 0.9f);
                    break;

                case ArcadeEventKind.Recovered:
                    if (mine) _sfx.Play2D(ProceduralAudio.Reveal, 0.5f, 0.8f);
                    break;

                case ArcadeEventKind.OffTrackWarning:
                    if (mine) _sfx.Play2D(ProceduralAudio.WarnBeep, 0.5f);
                    break;

                case ArcadeEventKind.OffTrackPenalty:
                    if (mine) _sfx.Play2D(ProceduralAudio.WarnBeep, 0.7f, 0.7f);
                    break;

                case ArcadeEventKind.Finished:
                    if (mine) _sfx.Play2D(ProceduralAudio.Reveal, 0.9f);
                    break;
            }
        }

        /// <summary>Flat if it happened to me, positional if it happened out
        /// there.</summary>
        private void Play(bool mine, string key, Vector3 pos, float volume)
        {
            if (mine) _sfx.Play2D(key, volume);
            else _sfx.PlayAt(key, pos, volume);
        }

        // ================= continuous states =================

        private void Update()
        {
            var me = Me;
            if (me == null || _sfx == null) return;

            // Roulette start. Not an event — the box grants on touch and the item
            // is only decided 0.9 s later — so it is polled off the state that
            // the HUD already shows.
            if (me.rolling && !_wasRolling) _sfx.Play2D(ProceduralAudio.Pickup, 0.8f);
            _wasRolling = me.rolling;

            if (_boostLoop != null)
            {
                bool boosting = ArcadeDirector.Clock < me.boostUntil && Time.timeScale > 0f;
                float target = boosting ? 0.55f * SfxPlayer.SfxGain : 0f;
                _boostLoop.volume = Mathf.MoveTowards(_boostLoop.volume, target, Time.deltaTime * 3f);
            }

            // Incoming-missile alarm. A missile is only fairly dodgeable if you
            // know it is there, so this is the audio half of the HUD warning.
            if (director != null && me.car != null && director.IncomingMissile(me.car))
            {
                if (Time.time >= _nextWarnBeep)
                {
                    _nextWarnBeep = Time.time + 0.35f;
                    _sfx.Play2D(ProceduralAudio.WarnBeep, 0.45f, 1.3f);
                }
            }
            else
            {
                _nextWarnBeep = 0f;
            }
        }
    }
}
