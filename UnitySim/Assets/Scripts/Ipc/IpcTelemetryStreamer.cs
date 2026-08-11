using System;
using System.Collections.Generic;
using AIHWSim.Sensors;
using AIHWSim.Telemetry;
using UnityEngine;

namespace AIHWSim.Ipc
{
    /// <summary>
    /// Turns telemetry-hub channels and sensor-camera frames into binary frames
    /// on the telemetry pipe.
    ///
    /// <b>Subscriptions, not a firehose.</b> Nothing is sent until a client asks
    /// for it, and it asks per vehicle for a named list of channels at a named
    /// rate. An unsubscribed session costs one null check per frame; a client
    /// watching two channels pays for two floats, not for the ninety a car
    /// publishes.
    ///
    /// <b>Driven by the hub, not by Update.</b> Values are packed from the
    /// <c>FrameCommitted</c> event, which fires once per CONTROL tick — the only
    /// moment new numbers exist. Sampling from <c>Update</c> instead would alias
    /// against the render rate and hand a client duplicated or skipped samples
    /// with correct-looking timestamps. Camera frames are the exception: they are
    /// polled from <c>Tick</c>, because a sensor camera runs at its own low rate
    /// (10 Hz by default) and a frame counter is cheaper to watch than an event
    /// per capture.
    /// </summary>
    internal sealed class IpcTelemetryStreamer
    {
        private sealed class Sub
        {
            public IpcVehicle vehicle;
            public TelemetryHub hub;
            public TelemetryHub.Channel[] channels;
            public string[] names;
            public float rateHz;
            public float minInterval;      // seconds of sim time between frames
            public float lastSentAt = float.NegativeInfinity;
            public uint seq;
            public Action<float> handler;  // held so it can be unsubscribed
        }

        private sealed class CamSub
        {
            public IpcVehicle vehicle;
            public CameraSensor camera;
            public int lastFrameIndex = -1;
            public uint seq;
        }

        private readonly IpcRuntime _owner;
        private readonly Dictionary<int, Sub> _subs = new Dictionary<int, Sub>();
        private readonly Dictionary<int, CamSub> _camSubs = new Dictionary<int, CamSub>();

        // The single world-stream subscription (vehicle null; frames go out
        // under IpcProtocol.WorldStreamId). One per client, like a vehicle sub.
        private Sub _worldSub;

        public IpcTelemetryStreamer(IpcRuntime owner) => _owner = owner;

        // ---- channel subscriptions -------------------------------------------

        /// <summary>
        /// Subscribe (or re-subscribe, which replaces) a vehicle's channel stream.
        /// Returns the resolved channel names — unknown names are dropped rather
        /// than failing the whole request, and an empty request expands to every
        /// channel the vehicle publishes. The returned order is the binary layout
        /// the client must decode against, which is why it goes back in the ack.
        /// </summary>
        public string[] Subscribe(IpcVehicle v, string[] requested, float rateHz, out string error)
        {
            error = null;
            var runner = v.rig != null ? v.rig.runner : null;
            var hub = runner != null ? runner.Hub : null;
            if (hub == null)
            {
                error = $"vehicle {v.id} has no telemetry hub yet";
                return null;
            }

            var chans = new List<TelemetryHub.Channel>();
            var names = new List<string>();

            if (requested == null || requested.Length == 0)
            {
                foreach (var c in hub.Channels) { chans.Add(c); names.Add(c.Name); }
            }
            else
            {
                foreach (var n in requested)
                {
                    if (string.IsNullOrEmpty(n)) continue;
                    if (!hub.TryGetChannel(n, out var c)) continue;
                    chans.Add(c);
                    names.Add(n);
                }
            }

            if (chans.Count == 0)
            {
                error = "none of the requested channels exist on that vehicle; "
                        + "call list_channels to see what it publishes";
                return null;
            }

            Unsubscribe(v.id);

            // The hub commits once per control tick and there is nothing between
            // ticks to report, so a request above the control rate is silently the
            // control rate. 0 means "as fast as it comes".
            float control = runner.controlRateHz > 0 ? runner.controlRateHz : 100f;
            float rate = rateHz <= 0f ? control : Mathf.Min(rateHz, control);

            var sub = new Sub
            {
                vehicle = v,
                hub = hub,
                channels = chans.ToArray(),
                names = names.ToArray(),
                rateHz = rate,
                // A hair under the true interval: sim time advances in exact control
                // steps, and asking for exactly one step of separation would drop
                // every other frame to floating-point noise.
                minInterval = rate > 0f ? (1f / rate) * 0.999f : 0f,
            };
            sub.handler = _ => PackAndSend(sub);
            hub.FrameCommitted += sub.handler;
            _subs[v.id] = sub;
            return sub.names;
        }

        public bool Unsubscribe(int vehicleId)
        {
            if (!_subs.TryGetValue(vehicleId, out var sub)) return false;
            if (sub.hub != null && sub.handler != null) sub.hub.FrameCommitted -= sub.handler;
            _subs.Remove(vehicleId);
            return true;
        }

        // ---- world stream (2026-08 additive) ---------------------------------

        /// <summary>
        /// Subscribe (or replace) the world sensor stream: same resolution rules
        /// as a vehicle subscribe, against the world hub, framed under
        /// <see cref="IpcProtocol.WorldStreamId"/>. Payload layout is identical
        /// to vehicle telemetry, so the ack's channel order is the decode key.
        /// </summary>
        public string[] SubscribeWorld(string[] requested, float rateHz, out string error)
        {
            error = null;
            var hub = WorldTelemetry.Hub;
            if (hub == null)
            {
                error = "no world hub — no track is loaded";
                return null;
            }

            var chans = new List<TelemetryHub.Channel>();
            var names = new List<string>();
            if (requested == null || requested.Length == 0)
            {
                foreach (var c in hub.Channels) { chans.Add(c); names.Add(c.Name); }
            }
            else
            {
                foreach (var n in requested)
                {
                    if (string.IsNullOrEmpty(n)) continue;
                    if (!hub.TryGetChannel(n, out var c)) continue;
                    chans.Add(c);
                    names.Add(n);
                }
            }

            if (chans.Count == 0)
            {
                error = "none of the requested channels exist on the world hub; "
                        + "call list_world_sensors to see what it publishes";
                return null;
            }

            UnsubscribeWorld();

            float rate = rateHz <= 0f ? WorldTelemetry.WorldRateHz
                                      : Mathf.Min(rateHz, WorldTelemetry.WorldRateHz);
            var sub = new Sub
            {
                vehicle = null,     // world stream — WorldStreamId on the wire
                hub = hub,
                channels = chans.ToArray(),
                names = names.ToArray(),
                rateHz = rate,
                minInterval = rate > 0f ? (1f / rate) * 0.999f : 0f,
            };
            sub.handler = _ => PackAndSend(sub);
            hub.FrameCommitted += sub.handler;
            _worldSub = sub;
            return sub.names;
        }

        public bool UnsubscribeWorld()
        {
            if (_worldSub == null) return false;
            if (_worldSub.hub != null && _worldSub.handler != null)
                _worldSub.hub.FrameCommitted -= _worldSub.handler;
            _worldSub = null;
            return true;
        }

        private void PackAndSend(Sub sub)
        {
            var service = _owner.Service;
            if (service == null || !service.TelemetryConnected) return;

            float now = sub.hub.LatestTime;
            if (now - sub.lastSentAt < sub.minInterval) return;
            sub.lastSentAt = now;

            int n = sub.channels.Length;
            int payload = 4 + 4 * n;
            var buf = service.RentFrame(IpcProtocol.FrameHeaderBytes + payload);

            ushort frameId = sub.vehicle != null ? (ushort)sub.vehicle.id
                                                 : IpcProtocol.WorldStreamId;
            int o = IpcProtocol.WriteHeader(buf, IpcProtocol.FrameTelemetry,
                                            frameId, sub.seq++, (uint)payload);
            o += IpcProtocol.WriteF32(buf, o, now);
            for (int i = 0; i < n; i++)
                o += IpcProtocol.WriteF32(buf, o, sub.channels[i].Latest);

            service.SendFrame(buf, o);
        }

        // ---- camera subscriptions --------------------------------------------

        public bool SubscribeCamera(IpcVehicle v, string sensorName, out string error)
        {
            error = null;
            var rig = v.rig != null ? v.rig.sensorRig : null;
            if (rig == null) { error = $"vehicle {v.id} has no sensor rig"; return false; }

            CameraSensor cam = null;
            if (string.IsNullOrEmpty(sensorName))
            {
                cam = rig.PrimaryCamera;
            }
            else
            {
                foreach (var s in rig.Sensors)
                    if (s is CameraSensor c && c.sensorName == sensorName) { cam = c; break; }
            }

            if (cam == null)
            {
                error = string.IsNullOrEmpty(sensorName)
                    ? $"vehicle {v.id} has no camera sensor"
                    : $"vehicle {v.id} has no camera sensor named '{sensorName}'";
                return false;
            }

            _camSubs[v.id] = new CamSub { vehicle = v, camera = cam, lastFrameIndex = cam.FrameIndex };
            return true;
        }

        public bool UnsubscribeCamera(int vehicleId) => _camSubs.Remove(vehicleId);

        /// <summary>Poll the camera subscriptions. Called once per frame from
        /// <c>IpcRuntime.Update</c>.</summary>
        public void Tick()
        {
            if (_camSubs.Count == 0) return;

            var service = _owner.Service;
            if (service == null || !service.TelemetryConnected) return;

            foreach (var kv in _camSubs)
            {
                var cs = kv.Value;
                var cam = cs.camera;
                if (cam == null) continue;

                // The counter, not the pixel contents: a camera that renders the
                // same view twice still produces a new frame, and a client asking
                // for a feed wants to know time passed.
                if (cam.FrameIndex == cs.lastFrameIndex) continue;
                cs.lastFrameIndex = cam.FrameIndex;

                var pixels = cam.Pixels;
                if (pixels == null || pixels.Length == 0) continue;

                int payload = 4 + 1 + 2 + 2 + 1 + pixels.Length;
                var buf = service.RentFrame(IpcProtocol.FrameHeaderBytes + payload);

                int o = IpcProtocol.WriteHeader(buf, IpcProtocol.FrameCamera,
                                                (ushort)cs.vehicle.id, cs.seq++, (uint)payload);
                o += IpcProtocol.WriteF32(buf, o, cam.LastCaptureTime);
                buf[o++] = 0;                                     // sensor ordinal, reserved for multi-camera
                o += IpcProtocol.WriteU16(buf, o, (ushort)cam.Width);
                o += IpcProtocol.WriteU16(buf, o, (ushort)cam.Height);
                buf[o++] = IpcProtocol.CamFormatGray8;
                Buffer.BlockCopy(pixels, 0, buf, o, pixels.Length);
                o += pixels.Length;

                service.SendFrame(buf, o);
            }
        }

        // ---- teardown --------------------------------------------------------

        /// <summary>Drop every subscription. A client that reconnects starts from
        /// nothing, and a scene load invalidates every hub reference held here.</summary>
        public void ClearAll()
        {
            foreach (var kv in _subs)
            {
                var sub = kv.Value;
                if (sub.hub != null && sub.handler != null) sub.hub.FrameCommitted -= sub.handler;
            }
            _subs.Clear();
            _camSubs.Clear();
            UnsubscribeWorld();
        }

        public bool IsSubscribed(int vehicleId) => _subs.ContainsKey(vehicleId);
    }
}
