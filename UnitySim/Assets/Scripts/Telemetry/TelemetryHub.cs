using System;
using System.Collections.Generic;

namespace AIHWSim.Telemetry
{
    /// <summary>
    /// Central telemetry store. Producers register named channels and stage a
    /// value each control step; <see cref="Commit"/> snapshots the staged
    /// values into per-channel ring buffers (for live graphing) and raises
    /// <see cref="FrameCommitted"/> (consumed by the CSV logger).
    ///
    /// Channels must be registered before the first Commit so column layout and
    /// graph pickers are stable.
    /// </summary>
    public sealed class TelemetryHub
    {
        public sealed class Channel
        {
            public readonly string Name;
            public readonly float[] Ring;
            public int Head;      // index of the next write
            public int Count;     // number of valid samples (<= capacity)
            public float Latest;

            public Channel(string name, int capacity)
            {
                Name = name;
                Ring = new float[capacity];
            }
        }

        private readonly int _capacity;
        private readonly List<Channel> _channels = new List<Channel>();
        private readonly Dictionary<string, Channel> _byName = new Dictionary<string, Channel>();

        public float[] TimeRing { get; }
        public int TimeHead { get; private set; }
        public int SampleCount { get; private set; }
        public float LatestTime { get; private set; }

        /// <summary>Raised after each Commit with the just-committed timestamp.</summary>
        public event Action<float> FrameCommitted;

        public IReadOnlyList<Channel> Channels => _channels;

        public TelemetryHub(int capacity = 8192)
        {
            _capacity = capacity;
            TimeRing = new float[capacity];
        }

        public Channel RegisterChannel(string name)
        {
            if (_byName.TryGetValue(name, out var existing))
                return existing;
            var ch = new Channel(name, _capacity);
            _channels.Add(ch);
            _byName[name] = ch;
            return ch;
        }

        public bool TryGetChannel(string name, out Channel channel) => _byName.TryGetValue(name, out channel);

        /// <summary>Stage a value for the current (not-yet-committed) frame.</summary>
        public void SetValue(string name, float value)
        {
            if (!_byName.TryGetValue(name, out var ch))
                ch = RegisterChannel(name);
            ch.Latest = value;
        }

        /// <summary>Snapshot all staged values into the ring buffers.</summary>
        public void Commit(float time)
        {
            LatestTime = time;
            TimeRing[TimeHead] = time;
            TimeHead = (TimeHead + 1) % _capacity;

            for (int i = 0; i < _channels.Count; i++)
            {
                var ch = _channels[i];
                ch.Ring[ch.Head] = ch.Latest;
                ch.Head = (ch.Head + 1) % _capacity;
                if (ch.Count < _capacity) ch.Count++;
            }

            if (SampleCount < _capacity) SampleCount++;
            FrameCommitted?.Invoke(time);
        }

        public void Clear()
        {
            TimeHead = 0;
            SampleCount = 0;
            LatestTime = 0f;
            foreach (var ch in _channels)
            {
                ch.Head = 0;
                ch.Count = 0;
                ch.Latest = 0f;
            }
        }
    }
}
