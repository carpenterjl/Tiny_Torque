# Tiny Torque — LAN play, sharing, and testing

Everything you need to get a game running with friends on your own network, and
to test it by yourself first.

> **Naming note.** The repo is *Tiny Torque*, but the Unity project's product name
> is still `AI Hardware Control Sim`. That name is what you'll see on the executable,
> the install folder, the firewall prompt, and the save path. Renaming it later moves
> the save folder, so anything you've created will look like it vanished until you
> copy it across.

---

## Wi-Fi or Ethernet?

**Wi-Fi is fine.** Use it.

The game is host-authoritative: the host simulates every car, clients send inputs and
render what the host reports. That means the traffic is tiny and steady rather than
bursty:

| Direction | Contents | Rate | Bandwidth |
|---|---|---|---|
| Client → host | throttle/steer/brake + flags (13 bytes) | 30 Hz | ~0.4 KB/s |
| Host → client | pose/velocity/steer/wheel-speed for every car (~200 bytes at 4 cars) | 30 Hz | ~6 KB/s |

That is roughly a thousandth of what streaming video needs, so bandwidth is never the
constraint. What actually affects feel is **latency and jitter**, and clients already
render 120 ms behind the host to absorb exactly that. Ordinary home Wi-Fi jitter
disappears into that buffer.

A few things genuinely matter more than the cable:

- **Everyone on the same network.** Same router, same subnet. 2.4 GHz and 5 GHz bands
  on one router are the same network — mixing them is fine.
- **Not the guest network.** Guest Wi-Fi usually enables client isolation, which blocks
  PCs from talking to each other at all. This is the single most common reason LAN play
  fails, and it looks identical to a firewall problem.
- **Host on Ethernet if it's convenient.** Not required, but the host's link quality is
  the one everybody feels — a client with a bad connection only hurts themselves.
- **Mesh systems are fine** as long as all nodes form one network (most do).

If you can only have one machine wired, wire the host.

---

## Start here: test it alone on one PC

You do not need a second computer or another person to check that LAN works. UDP
broadcast loops back locally, so the editor can host and a build can join on the same
machine.

1. **Make a build** (see below) — you need one, because two Unity editors can't both run.
2. **Run the built game**, and separately **press Play in the editor** on the Menu scene.
3. One of them: Multiplayer ▸ **Host LAN Game** ▸ pick car and track ▸ **Start Hosting ▶**.
4. The other: Multiplayer ▸ **Join LAN Game**. The host should appear in the list within
   a second or two. If not, type `127.0.0.1` in the IP box and hit **Connect**.
5. Drive both. You'll be controlling both cars with the same keyboard, which is useless
   for racing but perfect for confirming that the ghost car moves smoothly, laps count,
   and the session menu works.

Windows Firewall will prompt **twice** over the course of this — once for `Unity.exe`
and once for the built `AI Hardware Control Sim.exe`. Allow **Private networks** both
times. If you dismiss a prompt, see Troubleshooting.

Once that works, the only new variable on a real LAN is the network itself.

---

## Building the game to share

Friends need a build; they can't run your editor.

1. **Build the release player:**
   - In Unity: `Tools ▸ AIHWSim ▸ Build Standalone (Release)`
   - Or headless, with the editor closed:
     ```
     "E:\Unity Hub\Editor\6000.1.15f1\Editor\Unity.exe" -batchmode -quit -projectPath "E:\EE Projects\Tiny_Torque\UnitySim" -executeMethod AIHWSim.EditorTools.BuildMenu.BuildRelease
     ```
   - Output: `UnitySim\Builds\Release\` — boots straight into the main menu.

2. **Share it.** Either zip `Builds\Release\` (it's self-contained — unzip anywhere and
   run the exe), or compile the installer:
   - Install [Inno Setup](https://jrsoftware.org/isdl.php) (free), then:
     ```
     "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" Installer\AIHWSim.iss
     ```
   - Output: `UnitySim\Builds\Installer\AI-Hardware-Control-Sim-Setup.exe` — one file to send.

**Everyone must run the same build.** The connect handshake checks a protocol version
and rejects mismatches, but that check only catches deliberate protocol changes — it
will happily let two builds with different physics connect. Rebuild and redistribute
after any change to the sim, and don't mix an old copy with a new one.

### Where saves live

```
%USERPROFILE%\AppData\LocalLow\AIHWSim\AI Hardware Control Sim\
    Vehicles\  Tracks\  Saves\  TelemetryLogs\
```

Per-user and always writable, so installing under `Program Files` is fine. The ★ preset
cars and maps are compiled into the game, so everyone has starter content immediately.
Custom cars don't need sharing — a joiner's car design is sent to the host automatically.

---

## Playing with friends

1. Everyone installs the **same build**, on the **same network** (not guest Wi-Fi).
2. **Host:** Main Menu ▸ Multiplayer ▸ **Host LAN Game** ▸ set your name, pick your car
   and the map ▸ **Start Hosting ▶**. Allow the firewall prompt on **Private networks**.
   You play too — it's a listen server, not a dedicated one.
3. **Everyone else:** Main Menu ▸ Multiplayer ▸ **Join LAN Game** ▸ set name and car ▸
   click the host in the **Games on your network** list.
   - Nothing listed? Run `ipconfig` on the host, read its IPv4 address, and type that in
     the **IP** box instead. Discovery is a convenience; the manual field is the real path.
4. Up to **4 players** including the host. Joiners land in free roam on the host's map.

### Running the session (host, in-game)

Press **Esc** in-game for the session menu:

- **Start Race ▶** — teleports everyone to a grid behind the start line, runs a 3-second
  countdown with inputs frozen, then races first-to-N laps. Set the lap count first.
  Needs a map with a finish line; it's disabled otherwise.
- **Change Map ▶** — everyone reloads onto the new map together.
- **Kick** — next to each player.
- **Leave Session** — as host this ends the session and returns everyone to their menu.

Someone joining mid-race free-roams as a spectator and gets included in the next one.

---

## Firewall and ports

| Port (UDP) | Purpose |
|---:|---|
| **7777** | Game transport |
| **47777** | LAN discovery beacon |

Allowing the prompt when it appears is normally all that's needed. To pre-add the rule
instead, from an **elevated** PowerShell:

```powershell
New-NetFirewallRule -DisplayName "Tiny Torque UDP" -Direction Inbound -Protocol UDP -LocalPort 7777,47777 -Action Allow -Profile Private
```

The host needs both. Clients strictly only need outbound (allowed by default), but
allowing them too costs nothing and avoids surprises.

---

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Host doesn't appear in the join list, but manual IP works | UDP broadcast is being dropped — common on Wi-Fi APs and across subnets. Harmless; use the IP box. |
| Manual IP fails too, "Connection timed out" | Client isolation (guest network), a firewall rule, or different subnets. Confirm both IPv4 addresses share the first three octets, e.g. `192.168.1.x`. |
| "Failed to start hosting (port in use?)" | Something already holds UDP 7777 — usually another copy of the game still running. Close it. |
| Firewall prompt never appeared and nothing connects | It was dismissed once and remembered. Add the rule manually with the command above. |
| Joiner connects but sees no cars / falls through the map | The host changed maps mid-join. Leave and rejoin. |
| Ghost cars stutter or rubber-band | Wi-Fi congestion or a distant client. Move the host to Ethernet, or the client closer to the router. |
| Version mismatch on connect | Mixed builds. Redistribute the current one to everyone. |

Both roles log to the Unity player log, which is the fastest way to see what actually
happened (`[NetSession]` lines show hosting, connecting, and disconnect reasons):

```
%USERPROFILE%\AppData\LocalLow\AIHWSim\AI Hardware Control Sim\Player.log
```

---

## Over the internet (optional)

Port-forward **UDP 7777** on the host's router to the host PC, and have friends use the
host's public IP in the manual **IP** field. Discovery on 47777 is LAN-only and won't
help here. Expect a worse experience than LAN — the 120 ms interpolation buffer absorbs
household jitter, not internet jitter.

---

## Autonomous (C firmware) mode in shared builds

The shared build ships **without** a controller DLL, so **Autonomous (C firmware)** falls
back to open-loop. **Manual**, **Autonomous (Bot AI)**, split-screen, and LAN all work
fully. To enable firmware mode, drop a 64-bit `car_controller.dll` into:

```
<install dir>\AI Hardware Control Sim_Data\Plugins\x86_64\car_controller.dll
```

Note that LAN is host-authoritative, so a client running firmware locally has no effect —
the host simulates every car. Firmware work belongs in single-player.
