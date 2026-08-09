<#
.SYNOPSIS
    Smoke-test client for the Tiny Torque named-pipe control bridge.

.DESCRIPTION
    Exercises the Unity side of the IPC bridge without the WPF application:
    handshakes, enumerates vehicles, takes one over, drives a steer pulse,
    subscribes to telemetry, prints the frames, and hands the car back.

    This is the thing to run when the Unity side changes. It speaks the same
    protocol Docs/ipc-protocol.md specifies, so a disagreement between the two
    shows up here rather than in the GUI app later.

    Before running: start the game, turn ON Options - Remote Control - "Allow an
    external app to control this game", and be in a driving session (a free-roam
    map is ideal). The bridge itself is live from the menu, but there are no
    vehicles to drive until a track is loaded.

.PARAMETER Seconds
    How long to drive and stream telemetry for. Default 6.

.PARAMETER VehicleId
    Which vehicle to take over. Default 0 means "the first one listed".

.PARAMETER Channels
    Telemetry channels to subscribe to. Default is a small readable set.

.PARAMETER RateHz
    Telemetry rate. Clamped by the game to the control rate.

.PARAMETER SkipDrive
    Enumerate and stream only; never take the car over. Safe to run against a
    session somebody is actually driving.

.EXAMPLE
    .\ipc-test-client.ps1
.EXAMPLE
    .\ipc-test-client.ps1 -Seconds 20 -Channels veh/speed,veh/yaw_rate,cmd/steer_deg
.EXAMPLE
    .\ipc-test-client.ps1 -SkipDrive
#>
[CmdletBinding()]
param(
    [double]  $Seconds   = 6,
    [int]     $VehicleId = 0,
    [string[]]$Channels  = @('veh/speed', 'veh/yaw_rate', 'cmd/steer_deg'),
    [double]  $RateHz    = 50,
    [switch]  $SkipDrive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Must match IpcProtocol.cs. The handshake is an exact-equality check, so a
# mismatch here is a clean refusal rather than a subtle misparse.
$ProtocolVersion  = 1
$ControlPipeName  = 'TinyTorque.Control'
$TelemetryPipeName = 'TinyTorque.Telemetry'
$FrameMagic       = 0x5454
$FrameHeaderBytes = 13

$script:nextId = 0
function Next-Id { $script:nextId++; return $script:nextId }

# ---- control pipe: newline-delimited UTF-8 JSON ---------------------------

function Send-Msg {
    param([System.IO.Stream]$Pipe, [hashtable]$Msg)
    $json  = $Msg | ConvertTo-Json -Compress -Depth 6
    # No BOM and no CR: the server splits on '\n' and a stray CR would land
    # inside the JSON it then tries to parse.
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json + "`n")
    $Pipe.Write($bytes, 0, $bytes.Length)
    $Pipe.Flush()
}

# Reads one '\n'-terminated line, byte at a time. Slow by design and entirely
# fast enough: control traffic is a handful of messages, and this avoids having
# to hand-manage a buffer that straddles reads.
function Receive-Line {
    param([System.IO.Stream]$Pipe, [int]$TimeoutMs = 5000)
    $buf   = New-Object System.Collections.Generic.List[byte]
    $one   = New-Object byte[] 1
    $until = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)

    while ([DateTime]::UtcNow -lt $until) {
        $ar = $Pipe.BeginRead($one, 0, 1, $null, $null)
        while (-not $ar.IsCompleted) {
            if ([DateTime]::UtcNow -ge $until) { return $null }
            Start-Sleep -Milliseconds 2
        }
        if ($Pipe.EndRead($ar) -le 0) { return $null }      # server closed
        if ($one[0] -eq 10) {                                # '\n'
            if ($buf.Count -eq 0) { continue }
            $text = [System.Text.Encoding]::UTF8.GetString($buf.ToArray())
            return $text.TrimEnd("`r")
        }
        $buf.Add($one[0]) | Out-Null
    }
    return $null
}

# Send a request and wait for the reply carrying the same id. Unsolicited
# events (id 0) are printed and skipped rather than mistaken for the answer.
function Invoke-Request {
    param([System.IO.Stream]$Pipe, [hashtable]$Msg, [int]$TimeoutMs = 5000)
    $id = Next-Id
    $Msg['id'] = $id
    Send-Msg -Pipe $Pipe -Msg $Msg

    $until = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    while ([DateTime]::UtcNow -lt $until) {
        $line = Receive-Line -Pipe $Pipe -TimeoutMs 2000
        if ($null -eq $line) { continue }
        $obj = $line | ConvertFrom-Json
        if ($obj.t -eq 'event') {
            Write-Host ("  [event] {0} vehicle={1} {2}" -f $obj.kind, $obj.vehicleId, $obj.note) -ForegroundColor DarkGray
            continue
        }
        if ($obj.id -eq $id) { return $obj }
        Write-Host ("  [stray id={0}] {1}" -f $obj.id, $line) -ForegroundColor DarkGray
    }
    throw "timed out waiting for a reply to '$($Msg.t)'"
}

function Assert-Ok {
    param($Reply, [string]$What)
    if ($Reply.t -eq 'err') { throw "$What failed: [$($Reply.code)] $($Reply.message)" }
    return $Reply
}

# ---- telemetry pipe: binary frames ----------------------------------------

function Read-Frames {
    param([System.IO.Stream]$Pipe, [System.Collections.Generic.List[byte]]$Acc, [string[]]$Names)

    # Non-blocking top-up: whatever has arrived, without stalling the drive loop.
    $chunk = New-Object byte[] 65536
    $ar = $Pipe.BeginRead($chunk, 0, $chunk.Length, $null, $null)
    $spin = 0
    while (-not $ar.IsCompleted -and $spin -lt 10) { Start-Sleep -Milliseconds 2; $spin++ }
    if ($ar.IsCompleted) {
        $n = $Pipe.EndRead($ar)
        if ($n -gt 0) { $Acc.AddRange($chunk[0..($n - 1)]) }
    } else {
        # Abandoning the read would lose the bytes it is about to receive, so
        # wait it out — the server only stops sending when we unsubscribe.
        $n = $Pipe.EndRead($ar)
        if ($n -gt 0) { $Acc.AddRange($chunk[0..($n - 1)]) }
    }

    $out = @()
    while ($Acc.Count -ge $FrameHeaderBytes) {
        $b = $Acc.ToArray()
        $magic = [BitConverter]::ToUInt16($b, 0)
        if ($magic -ne $FrameMagic) { throw "telemetry stream desynchronised (magic 0x{0:X4})" -f $magic }

        $type       = $b[2]
        $vehicleId  = [BitConverter]::ToUInt16($b, 3)
        $seq        = [BitConverter]::ToUInt32($b, 5)
        $payloadLen = [BitConverter]::ToUInt32($b, 9)
        $total      = $FrameHeaderBytes + $payloadLen
        if ($Acc.Count -lt $total) { break }                 # partial frame; wait for more

        $o = $FrameHeaderBytes
        if ($type -eq 1) {
            $simTime = [BitConverter]::ToSingle($b, $o); $o += 4
            $count   = [int](($payloadLen - 4) / 4)
            $vals    = @()
            for ($i = 0; $i -lt $count; $i++) { $vals += [BitConverter]::ToSingle($b, $o); $o += 4 }
            $out += [pscustomobject]@{
                kind = 'telemetry'; vehicleId = $vehicleId; seq = $seq
                simTime = $simTime; values = $vals; names = $Names
            }
        }
        elseif ($type -eq 2) {
            $simTime = [BitConverter]::ToSingle($b, $o); $o += 4
            $o += 1                                          # sensor ordinal
            $w = [BitConverter]::ToUInt16($b, $o); $o += 2
            $h = [BitConverter]::ToUInt16($b, $o); $o += 2
            $o += 1                                          # format: 0 = gray8
            $out += [pscustomobject]@{
                kind = 'camera'; vehicleId = $vehicleId; seq = $seq
                simTime = $simTime; width = $w; height = $h; bytes = ($payloadLen - 10)
            }
        }

        $Acc.RemoveRange(0, [int]$total)
    }
    return $out
}

# ---- main ------------------------------------------------------------------

$control = $null
$telemetry = $null
$heldVehicle = $null

try {
    Write-Host "Connecting to \\.\pipe\$ControlPipeName ..." -ForegroundColor Cyan
    # Asynchronous, not the default. A synchronous pipe handle serialises every
    # operation on itself, so a pending read blocks a write on the same stream -
    # which is what a client that streams drive commands while listening for
    # replies does constantly. The server opens its end the same way.
    $control = New-Object System.IO.Pipes.NamedPipeClientStream(
        '.', $ControlPipeName, [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous)
    try { $control.Connect(3000) }
    catch {
        throw ("could not connect. Is the game running with Options - Remote Control " +
               "switched on? (A second client also fails here: the bridge serves one at a time.)")
    }

    $welcome = Assert-Ok (Invoke-Request $control @{ t = 'hello'; version = $ProtocolVersion; app = 'ipc-test-client.ps1' }) 'handshake'
    Write-Host ("Connected: {0}, Unity {1}, scene '{2}', session={3}, lan={4}" -f `
        $welcome.game, $welcome.unityVersion, $welcome.scene, $welcome.sessionActive, $welcome.lan) -ForegroundColor Green

    $telemetry = New-Object System.IO.Pipes.NamedPipeClientStream(
        '.', $TelemetryPipeName, [System.IO.Pipes.PipeDirection]::In)
    $telemetry.Connect(3000)
    Write-Host "Telemetry pipe connected." -ForegroundColor Green

    $session = Assert-Ok (Invoke-Request $control @{ t = 'get_session' }) 'get_session'
    Write-Host ("Session: match={0} track='{1}' vehicles={2} physics={3}Hz control={4}Hz" -f `
        $session.match, $session.trackId, $session.vehicleCount, $session.physicsHz, $session.controlHz)

    $list = Assert-Ok (Invoke-Request $control @{ t = 'list_vehicles' }) 'list_vehicles'
    if (-not $list.vehicles -or $list.vehicles.Count -eq 0) {
        throw "no vehicles. Load a track first - the bridge is live in the menu, but there is nothing to drive there."
    }
    foreach ($v in $list.vehicles) {
        Write-Host ("  vehicle {0}: '{1}' design='{2}' bot={3} motors={4} camera={5}" -f `
            $v.id, $v.name, $v.designName, $v.isBot, $v.motors.Count, $v.hasCamera)
    }

    $target = if ($VehicleId -gt 0) { $list.vehicles | Where-Object { $_.id -eq $VehicleId } } else { $list.vehicles[0] }
    if (-not $target) { throw "no vehicle with id $VehicleId" }
    Write-Host ("Target: vehicle {0} ('{1}')" -f $target.id, $target.name) -ForegroundColor Cyan

    $sub = Assert-Ok (Invoke-Request $control @{
        t = 'subscribe'; vehicleId = $target.id; channels = $Channels; rateHz = $RateHz
    }) 'subscribe'
    $names = @($sub.channels)
    Write-Host ("Subscribed at {0} Hz to: {1}" -f $sub.rateHz, ($names -join ', ')) -ForegroundColor Green

    if (-not $SkipDrive) {
        Assert-Ok (Invoke-Request $control @{ t = 'acquire'; vehicleId = $target.id; level = 'drive' }) 'acquire' | Out-Null
        Write-Host "Acquired. Local input to this car is now ignored." -ForegroundColor Yellow
        $heldVehicle = $target.id
    }

    $acc     = New-Object System.Collections.Generic.List[byte]
    $started = [DateTime]::UtcNow
    $frames  = 0
    $cameras = 0
    $lastPrint = [DateTime]::UtcNow

    while (([DateTime]::UtcNow - $started).TotalSeconds -lt $Seconds) {
        $t = ([DateTime]::UtcNow - $started).TotalSeconds

        if (-not $SkipDrive) {
            # A gentle throttle with a slow steer sine: enough to see the car move
            # and the telemetry respond, not enough to launch it off the map.
            Send-Msg -Pipe $control -Msg @{
                t = 'drive'; id = 0; vehicleId = $target.id
                throttle = 0.35; steer = [Math]::Sin($t * 1.5) * 0.5; brake = 0.0
            }
        }

        foreach ($f in (Read-Frames -Pipe $telemetry -Acc $acc -Names $names)) {
            if ($f.kind -eq 'camera') { $cameras++; continue }
            $frames++
            if (([DateTime]::UtcNow - $lastPrint).TotalMilliseconds -ge 500) {
                $lastPrint = [DateTime]::UtcNow
                $pairs = @()
                for ($i = 0; $i -lt $f.values.Count -and $i -lt $names.Count; $i++) {
                    $pairs += ('{0}={1,8:F3}' -f $names[$i], $f.values[$i])
                }
                Write-Host ("  t={0,7:F3}  {1}" -f $f.simTime, ($pairs -join '  '))
            }
        }
    }

    Write-Host ("Received {0} telemetry frames ({1:F1}/s){2}." -f `
        $frames, ($frames / $Seconds), $(if ($cameras) { ", $cameras camera frames" } else { '' })) -ForegroundColor Green

    if ($frames -eq 0) {
        Write-Warning ("No frames arrived. Either the session is paused (the hub commits on " +
                       "control ticks, which stop with the sim) or the channel names do not " +
                       "exist on that vehicle - try list_channels.")
    }

    Assert-Ok (Invoke-Request $control @{ t = 'unsubscribe'; vehicleId = $target.id }) 'unsubscribe' | Out-Null
}
catch {
    Write-Host "FAILED: $_" -ForegroundColor Red
    exit 1
}
finally {
    # Hand the car back even on failure. Without this a crashed client leaves the
    # car under a takeover until the dead-man brakes it and the disconnect is
    # noticed - which works, but is a worse thing to leave behind.
    if ($heldVehicle -and $control -and $control.IsConnected) {
        try { Invoke-Request $control @{ t = 'release'; vehicleId = $heldVehicle } -TimeoutMs 2000 | Out-Null
              Write-Host "Released." -ForegroundColor Yellow } catch { }
    }
    if ($telemetry) { $telemetry.Dispose() }
    if ($control)   { $control.Dispose() }
}

Write-Host "OK" -ForegroundColor Green
