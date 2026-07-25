# AI Hardware Control Sim — Sharing & LAN Play

## Building the shareable installer

1. **Build the release player** (in Unity, editor closed for a clean build isn't required here):
   - `Tools ▸ AIHWSim ▸ Build Standalone (Release)`
   - or headless:
     ```
     "E:\Unity Hub\Editor\6000.1.15f1\Editor\Unity.exe" -batchmode -quit ^
       -projectPath "E:\EE Projects\AI Hardware Control Sim (Unity)\UnitySim" ^
       -executeMethod AIHWSim.EditorTools.BuildMenu.BuildRelease
     ```
   - Output: `UnitySim\Builds\Release\` (boots into the **main menu**).
2. **Install Inno Setup** (free): https://jrsoftware.org/isdl.php
3. **Compile the installer**:
   ```
   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" Installer\AIHWSim.iss
   ```
   - Output: `UnitySim\Builds\Installer\AI-Hardware-Control-Sim-Setup.exe` — this is the single file to share.

> Prefer no installer? The `Builds\Release\` folder is a self-contained portable game.
> Zip it and share; friends unzip anywhere and run the `.exe`. (Saves go to per-user
> AppData either way — see below.)

## Where saves live

Vehicles, tracks, telemetry logs, settings, and profiles are written to the per-user
folder:

```
%USERPROFILE%\AppData\LocalLow\AIHWSim\AI Hardware Control Sim\
    Vehicles\  Tracks\  Saves\  TelemetryLogs\
```

This is always writable, so installing under `Program Files` is fine. Built-in cars
and maps (the ★ presets) are compiled into the game and always available.

## Playing on a LAN

1. Everyone installs the **same version** of the game (a version mismatch is rejected
   at connect — protocol version 2).
2. All PCs on the **same local network / subnet** (same Wi‑Fi or switch).
3. **Host:** Main Menu ▸ Multiplayer ▸ **Host LAN Game** ▸ pick car/track ▸ start.
   - When Windows Firewall prompts on first host, **Allow access on Private networks.**
4. **Others:** Main Menu ▸ Multiplayer ▸ **Join LAN Game**.
   - The host appears automatically in the discovery list — click it.
   - Or type the host's IP manually (`ipconfig` on the host → IPv4 address).
5. Up to **4 players**. Joiners drop into free‑roam on the host's map; the host controls
   the session (change map, **Start Race**, kick).

### Firewall / ports

If auto‑discovery or connect fails, allow these **inbound UDP** ports for the game on
the **Private** network profile (host especially):

| Port (UDP) | Purpose               |
|-----------:|-----------------------|
| **7777**   | Game transport        |
| **47777**  | LAN discovery beacon   |

Pre‑add a rule from an elevated PowerShell if needed:
```powershell
New-NetFirewallRule -DisplayName "AIHWSim UDP" -Direction Inbound `
  -Protocol UDP -LocalPort 7777,47777 -Action Allow -Profile Private
```

### Playing over the internet (optional)

Port‑forward **UDP 7777** on the host's router to the host PC, and have friends use the
host's public IP with the **manual IP** join field. (Discovery on 47777 is LAN‑only.)

## Autonomous (C firmware) mode in shared builds

The shared build ships **without** a controller DLL, so **Autonomous (C firmware)** falls
back to open‑loop. **Manual**, **Autonomous (Bot AI)**, split‑screen, and LAN all work
fully. To enable firmware mode later, drop a 64‑bit `car_controller.dll` into:

```
<install dir>\AI Hardware Control Sim_Data\Plugins\x86_64\car_controller.dll
```
