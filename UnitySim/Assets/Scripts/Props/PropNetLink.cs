using System.Collections.Generic;
using AIHWSim.Net;
using UnityEngine;

namespace AIHWSim.Props
{
    /// <summary>
    /// LAN glue for world-prop toggle state (button speakers, RF beacons).
    /// Existence needs no sync — scene and Studio props are deterministically
    /// recreated on every machine and matched by position-hash id. Toggles are
    /// EVENTS (client request → host applies + reliable rebroadcast), backed
    /// by a 1 Hz idempotent state list of every prop off its default — the
    /// ArcSync recipe — which heals dropped events and late joiners with no
    /// join hook. Built by TrackBootstrap when a net session exists; installs
    /// itself as the props' ToggleHook so they stay net-ignorant.
    /// </summary>
    public sealed class PropNetLink : MonoBehaviour
    {
        private const float StateInterval = 1f;

        private NetSession _net;
        private float _nextState;

        // Prop lookup by id, rebuilt lazily (props all exist at scene build;
        // a stale cache after a rebuild self-heals on the next miss).
        private readonly Dictionary<int, SpeakerProp> _speakers = new Dictionary<int, SpeakerProp>();
        private readonly Dictionary<int, RfBeaconProp> _beacons = new Dictionary<int, RfBeaconProp>();
        private bool _indexed;

        public static PropNetLink Build()
        {
            return new GameObject("PropNetLink").AddComponent<PropNetLink>();
        }

        private void Awake()
        {
            _net = NetSession.Instance;
            if (_net == null) { Destroy(gameObject); return; }

            _net.PropEventReceived += OnPropEvent;
            _net.PropStateReceived += OnPropState;
            SpeakerProp.ToggleHook = RouteToggle;
            RfBeaconProp.ToggleHook = RouteToggle;
        }

        private void OnDestroy()
        {
            if (_net != null)
            {
                _net.PropEventReceived -= OnPropEvent;
                _net.PropStateReceived -= OnPropState;
            }
            if (SpeakerProp.ToggleHook == RouteToggle) SpeakerProp.ToggleHook = null;
            if (RfBeaconProp.ToggleHook == RouteToggle) RfBeaconProp.ToggleHook = null;
        }

        /// <summary>The hook a prop's local Interact press routes through.
        /// Host: apply + broadcast. Client: request; the state applies when
        /// the broadcast comes back — half a round-trip of lag, and every
        /// machine agrees.</summary>
        private bool RouteToggle(int propId, bool on)
        {
            var m = new PropEvtMsg { propId = propId, on = on };
            if (_net.IsHost)
            {
                Apply(m);
                _net.HostBroadcastPropEvent(m);
            }
            else
            {
                _net.SendPropRequestToHost(m);
            }
            return true;
        }

        private void OnPropEvent(PropEvtMsg m)
        {
            Apply(m);
            // A client's request reached the host: apply, then mirror to all
            // (including the requester, whose local state is still waiting).
            if (_net.IsHost) _net.HostBroadcastPropEvent(m);
        }

        private void OnPropState(PropStateMsg m)
        {
            // Idempotent: everything named is set, everything absent is reset
            // to its authored default. Absence has to mean something, or a
            // prop toggled off-default and back on again would stick for a
            // late joiner that only saw the middle.
            Index();
            var named = new HashSet<int>();
            for (int i = 0; i < m.ids.Length && i < m.on.Length; i++)
            {
                named.Add(m.ids[i]);
                Apply(new PropEvtMsg { propId = m.ids[i], on = m.on[i] });
            }
            foreach (var kv in _speakers)
                if (!named.Contains(kv.Key) && kv.Value != null
                    && kv.Value.config.mode == SpeakerMode.Interact
                    && kv.Value.InteractState != kv.Value.config.startOn)
                    kv.Value.SetPlaying(kv.Value.config.startOn);
            foreach (var kv in _beacons)
                if (!named.Contains(kv.Key) && kv.Value != null
                    && kv.Value.Enabled != kv.Value.startOn)
                    kv.Value.SetEnabled(kv.Value.startOn);
        }

        private void Apply(PropEvtMsg m)
        {
            Index();
            if (_speakers.TryGetValue(m.propId, out var sp) && sp != null)
                sp.SetPlaying(m.on);
            else if (_beacons.TryGetValue(m.propId, out var bc) && bc != null)
                bc.SetEnabled(m.on);
            else
                _indexed = false; // unknown id: re-index next time (late build)
        }

        private void Index()
        {
            if (_indexed) return;
            _indexed = true;
            _speakers.Clear();
            _beacons.Clear();
            foreach (var s in FindObjectsByType<SpeakerProp>(FindObjectsSortMode.None))
                _speakers[s.PropId] = s;
            foreach (var b in FindObjectsByType<RfBeaconProp>(FindObjectsSortMode.None))
                _beacons[b.PropId] = b;
        }

        private void Update()
        {
            if (!_net.IsHost || Time.unscaledTime < _nextState) return;
            _nextState = Time.unscaledTime + StateInterval;

            Index();
            var ids = new List<int>();
            var on = new List<bool>();
            foreach (var kv in _speakers)
            {
                var s = kv.Value;
                if (s != null && s.config.mode == SpeakerMode.Interact
                    && s.InteractState != s.config.startOn)
                { ids.Add(kv.Key); on.Add(s.InteractState); }
            }
            foreach (var kv in _beacons)
            {
                var b = kv.Value;
                if (b != null && b.Enabled != b.startOn)
                { ids.Add(kv.Key); on.Add(b.Enabled); }
            }
            if (ids.Count == 0) return; // nothing off default; silence is the default too
            _net.HostBroadcastPropState(new PropStateMsg
            {
                ids = ids.ToArray(), on = on.ToArray(),
            });
        }

        /// <summary>Built by TrackBootstrap when a session exists.</summary>
        public static void BuildIfLan()
        {
            if (NetSession.Instance != null) Build();
        }
    }
}
