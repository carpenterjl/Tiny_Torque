using AIHWSim.Telemetry;

namespace AIHWSim.Tutorial
{
    /// <summary>
    /// The three conditions that ask a question of another system: is a LAN
    /// lobby up, is the control bridge connected, is a telemetry channel alive.
    ///
    /// They live here rather than inline in <see cref="TutorialStepEngine"/> so
    /// the engine stays a state machine over data. Each one is a null-tolerant
    /// read of something that may simply not exist in this session — no LAN, no
    /// bridge running, no car bound — and "not there" always answers false
    /// rather than throwing. A tutorial step that waits forever is a player
    /// pressing skip; a NullReferenceException is a bug report.
    /// </summary>
    public static class TutorialProbes
    {
        /// <summary>A LAN session exists and this machine is the host.</summary>
        public static bool HostingLobby()
        {
            var net = Net.NetSession.Instance;
            return net != null && net.IsHost;
        }

        /// <summary>The IPC bridge is serving and an app is connected to it.</summary>
        public static bool IpcClientConnected() => Ipc.IpcRuntime.ControlConnected;

        /// <summary>
        /// A named telemetry channel exists on the session's hub and has produced
        /// at least one sample.
        ///
        /// The hub is reached through the bound rig rather than a singleton
        /// because there is one per car; a split-screen session has several, and
        /// only the tutorial's own car is the one being taught about.
        /// </summary>
        public static bool TelemetryLive(string channel)
        {
            if (string.IsNullOrEmpty(channel)) return false;
            var hub = Hub;
            if (hub == null) return false;
            foreach (var ch in hub.Channels)
                if (ch.Name == channel) return ch.Count > 0;
            return false;
        }

        /// <summary>The hub of the car being taught. Set by the director when it
        /// binds its rig; null in an overlay tutorial, which has no car.</summary>
        public static TelemetryHub Hub { get; set; }
    }
}
