using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace AIHWSim.Net
{
    /// <summary>
    /// LAN game discovery over plain UDP broadcast (System.Net only — no Unity
    /// APIs off the main thread). Hosts announce once a second on port 47777;
    /// the Join page listens on a background thread and drains discovered games
    /// on the main thread. Broadcast loops back locally, so same-machine
    /// editor+build testing works (127.0.0.1 manual entry is the fallback).
    /// </summary>
    public static class LanDiscovery
    {
        public const int Port = 47777;

        [Serializable]
        public class Announce
        {
            public int ver = NetSession.ProtocolVersion;
            public string gameName = "";
            public ushort port = NetSession.DefaultPort;
            public int players;
            public int maxPlayers = NetSession.MaxPlayers;
            public string trackName = "";
        }

        public sealed class DiscoveredGame
        {
            public string address = "";
            public Announce info;
            public float lastSeen;   // Time.unscaledTime, stamped on drain
        }

        // ---- host broadcast ---------------------------------------------------

        private static UdpClient _sender;
        private static Announce _current;

        /// <summary>Start (or update) the 1 Hz host announcement.</summary>
        public static void SetAnnounce(Announce a)
        {
            _current = a;
            if (_sender == null)
            {
                _sender = new UdpClient { EnableBroadcast = true };
                _broadcastThread = new Thread(BroadcastLoop) { IsBackground = true };
                _broadcastThread.Start();
            }
        }

        private static Thread _broadcastThread;
        private static volatile bool _stopBroadcast;

        private static void BroadcastLoop()
        {
            var target = new IPEndPoint(IPAddress.Broadcast, Port);
            while (!_stopBroadcast)
            {
                try
                {
                    var a = _current;
                    if (a != null)
                    {
                        byte[] data = Encoding.UTF8.GetBytes(JsonUtility.ToJson(a));
                        _sender?.Send(data, data.Length, target);
                    }
                }
                catch { /* transient socket errors are fine for a beacon */ }
                Thread.Sleep(1000);
            }
        }

        public static void StopBroadcast()
        {
            _stopBroadcast = true;
            _broadcastThread = null;
            try { _sender?.Close(); } catch { }
            _sender = null;
            _current = null;
            _stopBroadcast = false;
        }

        // ---- join-page listener -------------------------------------------------

        private static UdpClient _listener;
        private static Thread _listenThread;
        private static volatile bool _stopListen;
        private static readonly ConcurrentQueue<(string addr, Announce info)> _found =
            new ConcurrentQueue<(string, Announce)>();

        public static void StartListen()
        {
            if (_listener != null) return;
            try
            {
                _listener = new UdpClient();
                _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                _stopListen = false;
                _listenThread = new Thread(ListenLoop) { IsBackground = true };
                _listenThread.Start();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LanDiscovery] Listen failed: {e.Message}");
                _listener = null;
            }
        }

        private static void ListenLoop()
        {
            while (!_stopListen)
            {
                try
                {
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _listener.Receive(ref from); // blocking; unblocked by Close()
                    var info = JsonUtility.FromJson<Announce>(Encoding.UTF8.GetString(data));
                    if (info != null && info.ver == NetSession.ProtocolVersion)
                        _found.Enqueue((from.Address.ToString(), info));
                }
                catch { if (_stopListen) return; }
            }
        }

        public static void StopListen()
        {
            _stopListen = true;
            try { _listener?.Close(); } catch { }
            _listener = null;
            _listenThread = null;
            while (_found.TryDequeue(out _)) { }
        }

        /// <summary>
        /// Main-thread: merge newly received announcements into a live list
        /// (call every frame while the Join page is open). Entries expire after 4 s.
        /// </summary>
        public static void Drain(List<DiscoveredGame> games)
        {
            while (_found.TryDequeue(out var item))
            {
                var existing = games.Find(g => g.address == item.addr);
                if (existing == null)
                {
                    existing = new DiscoveredGame { address = item.addr };
                    games.Add(existing);
                }
                existing.info = item.info;
                existing.lastSeen = Time.unscaledTime;
            }
            games.RemoveAll(g => Time.unscaledTime - g.lastSeen > 4f);
        }
    }
}
