# Tiny Torque IPC protocol — v1

The contract between the game and an external control application over Windows
named pipes. The Unity side lives in `UnitySim/Assets/Scripts/Ipc/`;
`IpcProtocol.cs` is the authority for every constant named here, and the `[IPC]`
validator checks the two agree.

**Enable it first.** The bridge is off by default. Turn on
*Options → Remote Control → "Allow an external app to control this game"*, or the
same toggle in the in-race pause *Settings* panel. Flipping it takes effect
immediately — no restart. While it is off, no pipe exists and no thread runs.

---

## Transport

Two pipes. Both are created with a **single server instance**, so a second
client's connect attempt fails with `ERROR_PIPE_BUSY` rather than being accepted
and then refused. Two applications both holding takeover of one car has no
sensible meaning, so this is the intended answer.

| Pipe | Name | Direction | Payload |
|---|---|---|---|
| Control | `\\.\pipe\TinyTorque.Control` | duplex | newline-delimited UTF-8 JSON |
| Telemetry | `\\.\pipe\TinyTorque.Telemetry` | server → client | binary frames |

Connect the control pipe first, handshake, then connect telemetry. Frames
enqueued while the telemetry pipe is unconnected are **dropped, not buffered**.

> **Open the control pipe with `PipeOptions.Asynchronous`.** This is not a tuning
> choice. A synchronous pipe handle serialises every operation on itself, so a
> pending read blocks a write on the same stream — and a client that streams
> `drive` commands while listening for replies does both constantly. Without it
> the connection deadlocks the first time a read is outstanding when you try to
> send. The server opens its end the same way. `.NET`:
> `new NamedPipeClientStream(".", "TinyTorque.Control", PipeDirection.InOut, PipeOptions.Asynchronous)`.

Why two formats: control traffic is low-rate and wants to be versionable and
readable in a log; telemetry at 100 Hz across several vehicles does not. This is
the same split `Net/NetMessages.cs` documents for the LAN layer.

### Control framing

One message per line, UTF-8, terminated by `\n`. **Do not send CR.** The server
tolerates a trailing `\r`, but nothing else about CRLF is supported. Lines over
8 MB drop the connection.

Every message is a flat JSON object with:

| Field | Type | Meaning |
|---|---|---|
| `t` | string | message type |
| `id` | int | client-chosen request id, echoed on the reply |

Server messages carry the request's `id`. Unsolicited events use `id: 0`.

Replies are `ack`, `err`, or a type-specific reply object. `drive` and
`actuate` are **not acked** — they are the hot path, and a client sending them at
100 Hz does not want 100 replies a second. Errors still come back for both.

### Partial updates

`set_settings`, `set_solver`, `set_session_config` and `load_track` are partial:
send only the fields you want changed. The server prefills the message object
with the current values and applies your JSON over the top with
`JsonUtility.FromJsonOverwrite`, so an omitted key round-trips to itself. Sending
`{"t":"set_settings","id":1,"bloom":false}` changes bloom and nothing else.

---

## Handshake

Send `hello` before anything else; every other message gets
`err not_handshaken` until you do.

```json
{"t":"hello","id":1,"version":1,"app":"MyControlApp"}
```

Reply:

```json
{"t":"welcome","id":1,"version":1,"game":"Tiny Torque","unityVersion":"6000.1.15f1",
 "scene":"TTA_Sandbox","sessionActive":true,"lan":false}
```

The version check is **exact equality** — a near-miss version is more dangerous
than none, because the field names still parse and the meanings have moved. A
mismatch returns `err version_mismatch` and the client should disconnect.

Handshake state resets on every connect and disconnect. On disconnect the server
releases every takeover and drops every subscription, so a client that dies
never leaves a car under the control of a process that is gone.

---

## Message reference

### Enumeration

| Type | Body | Reply |
|---|---|---|
| `list_vehicles` | — | `vehicles` |
| `list_tracks` | — | `tracks` |
| `list_presets` | — | `presets` |
| `list_channels` | `vehicleId` | `channels` |
| `get_session` | — | `session` |
| `get_tunables` | `vehicleId` | `tunables` |
| `get_settings` | — | `settings` |

**`vehicles`** — `vehicles[]` of:

| Field | Meaning |
|---|---|
| `id` | stable while the car lives; use it everywhere else |
| `name`, `designName` | display strings |
| `isBot`, `control` | `Human` / `BotAI` / `Firmware` |
| `netSlot` | LAN roster slot, −1 in local sessions |
| `acquired`, `level` | whether this client holds it, and at which level |
| `wheelCount` | |
| `motors[]` | `name`, `actuatorIndex`, `maxVoltage` — the actuator layout for `actuate` |
| `sensors[]` | `name`, `kind`, `channels[]`, `camWidth`, `camHeight` |
| `hasCamera` | whether `subscribe_camera` will work |

**`tracks`** — `tracks[]` of `id`, `displayName`, `kind` (`scene` / `tilemap` /
`oval`), `hasFinish`. Pass `id` to `load_track`; `""` is the classic procedural
oval.

**`session`** — `active`, `scene`, `trackId`, `match`, `vehicleCount`, `simTime`,
`lan`, `paused`, `physicsHz`, `controlHz`.

### Takeover and control

A vehicle must be **acquired** before it can be commanded. While held, local
gamepad and keyboard input to that car is ignored; other vehicles stay locally
driven.

```json
{"t":"acquire","id":2,"vehicleId":1,"level":"drive","deadManMs":500}
```

| Level | What you send | What happens |
|---|---|---|
| `drive` | `drive` messages: normalized throttle/steer/brake | Goes through `CarInput`, so assists, arcade handling and the steering-rate limit all apply — identical to a gamepad. |
| `raw` | `actuate` messages: the float[8] actuator vector | Written straight into the runner. Nothing shapes it. What a firmware-style loop wants. |

`deadManMs` is milliseconds of silence after which **the car brakes itself**. 0
takes the default (500 ms). Negative disables it — only sane for a client that
parks the car before it stops talking. The dead-man exists because a control
application is a separate process that can be paused in a debugger or killed,
and a car left latched at full throttle keeps going.

| Type | Body |
|---|---|
| `release` | `vehicleId` |
| `drive` | `vehicleId`, `throttle` 0–1, `steer` −1–1, `brake` 0–1, `handbrake`, `respawn`, `useItem`, `jump`, `horn`, `boost` |
| `actuate` | `vehicleId`, `actuators[]` (≤8 floats), `setpoints[]` (≤4, logged only), `handbrake` |
| `reset_vehicle` | `vehicleId` — back to the spawn point |
| `teleport` | `vehicleId`, `pos`, `euler`, `vel`, `angVel`, `keepMomentum` |
| `set_mode` | `vehicleId`, `mode`: `manual` or `autonomous` |

`respawn`, `useItem` and `jump` are **edges**: latched until consumed, so one
message produces exactly one respawn.

Actuator vector layout: index `0..N-1` are motor volts at each motor's
`actuatorIndex` (from `list_vehicles`), `[6]` is steer −1..1, `[7]` is brake
0..1. Handbrake is not in the vector — it is a field on the message.

Vectors are `{"x":0,"y":0,"z":0}`. `euler` is degrees. Omitting `euler` keeps the
current rotation; omitting `vel`/`angVel` means zero unless `keepMomentum` is set.

### Telemetry

```json
{"t":"subscribe","id":3,"vehicleId":1,"channels":["veh/speed","veh/yaw_rate"],"rateHz":50}
```

Reply:

```json
{"t":"subscribed","id":3,"vehicleId":1,"channels":["veh/speed","veh/yaw_rate"],"rateHz":50}
```

**Read the returned `channels` array — it is the binary layout.** Unknown names
are dropped rather than failing the request, and an empty or absent `channels`
expands to every channel the vehicle publishes, so the order you get back is not
necessarily the order you asked for.

`rateHz` is clamped to the control rate: the telemetry hub commits once per
control tick and there is nothing between ticks to send. 0 means "as fast as it
comes". Re-subscribing replaces the previous subscription.

Channel names come from the telemetry hub — the same set the CSV logger writes.
`list_channels` returns the live list for a vehicle. Common ones:
`veh/speed`, `veh/speed_kmh`, `veh/yaw_rate`, `veh/pos_x`, `veh/pos_z`,
`veh/yaw_deg`, `cmd/steer_deg`, `cmd/brake`, `cmd/<motor>/volt`,
`sens/<sensor>/<field>`, and — when `PhysicsDebugTelemetry` is bound —
`veh/tyre_temp_<i>`, `veh/slip_<i>`, `veh/fz_<i>`.

| Type | Body |
|---|---|
| `unsubscribe` | `vehicleId` |
| `subscribe_camera` | `vehicleId`, `sensor` (empty = the primary camera) |
| `unsubscribe_camera` | `vehicleId` |

### Binary frame format

Little-endian throughout. 13-byte header:

| Offset | Size | Field |
|---|---|---|
| 0 | u16 | magic `0x5454` |
| 2 | u8 | frame type: 1 telemetry, 2 camera |
| 3 | u16 | vehicleId |
| 5 | u32 | seq — per-stream, for gap detection |
| 9 | u32 | payloadLen |

A frame not starting with the magic means the stream has desynchronised. There
is no resynchronisation scheme; drop the connection.

**Telemetry payload** — `f32 simTime`, then one `f32` per subscribed channel in
the acked order.

**Camera payload** — `f32 simTime`, `u8 sensorOrdinal`, `u16 width`,
`u16 height`, `u8 format` (0 = gray8), then `width × height` bytes.
**Row 0 is the TOP of the image** — `CameraSensor.Pixels` verbatim, the usual
vision convention. A client that draws it bottom-up gets an upside-down picture
and no error. Sensor cameras are ≤128×128 and run at their own rate (10 Hz by
default), independent of the control rate.

If a client cannot keep up, the server drops the **oldest** frames — 256 deep,
about 2.5 s at 100 Hz — and logs a warning naming the count. Watch `seq` for
gaps.

### Tuning, physics and settings

| Type | Body | Notes |
|---|---|---|
| `set_tunable` | `vehicleId`, `name`, `value` | Names and ranges from `get_tunables`. Clamped to the range, not rejected. |
| `set_assists` | `vehicleId`, `steer`, `stability`, `traction`, `abs`, `launch` | 0–1 each. |
| `set_session_config` | partial: `targetLaps`, `targetScore`, `timeLimitSec`, `rubberBand`, `arcade`, `trackLimits`, `arcadeHandling`, `arcadeTyreThermal` | Live within a frame. |
| `set_mode_tuning` | `name`, `value` | One field on the scene's `ModeConfigOverride`, by name. |
| `set_arcade_tuning` | `name`, `value` | Same for `ArcadeConfigOverride`. |
| `set_solver` | partial: `defaultContactOffset`, `defaultSolverIterations`, `defaultSolverVelocityIterations`, `defaultMaxDepenetrationVelocity`, `maximumDeltaTime` | Written straight to the engine; the scene's asset file is not edited. |
| `set_rates` | `physicsHz`, `controlHz` (0 = leave alone) | Applied to **every** runner in the session. |
| `set_settings` | partial, see below | Saved to `settings.json` and applied. |

`set_settings` covers `masterVolume`, `sfxVolume`, `engineVolume`, `musicVolume`,
`bloom`, `vSync`, `fullscreen`, `qualityLevel`, `logTelemetry`, `noiseSeed`,
`actuationDelayTicks`, `spArcadeHandling`, `spArcadeTyreThermal`.
`noiseSeed` and `actuationDelayTicks` apply to the **next** session — the seed is
read once per process and the delay once per runner at build time. The ack says
so.

**Assists and the arcade floor.** With arcade handling on, `HandlingFloor`
re-asserts a per-channel maximum every frame, so an assist set *below* the floor
snaps back. That is not a bug in the bridge. Turn `arcadeHandling` off via
`set_session_config` if you want full authority over the assists.

**Changing the physics rate re-times everything.** Every dt-dependent term in a
controller — integrators, derivatives, odometry — is derived from the control
period. `set_rates` re-derives it properly across all runners, but a controller
already mid-run does not know its period changed.

### Design and lifecycle

```json
{"t":"push_design","id":9,"vehicleId":1,"designJson":"{...}","liveOnly":false}
```

`designJson` is a whole `VehicleDesign` — the same text the garage saves. The
live-safe fields are applied immediately: `steerRate`, `servoStallNm`,
`ackermannPct`, `maxBrakeTorque`, `handbrakeTorque`, `brakeProportioning`,
`antiRoll`, `stickyPhantomNm`, `dragCd`, `frontalAreaM2`.

Everything else — suspension, wheel positions and count, mass and inertia, motor
parameters, batteries, aero parts, sensors — needs the car rebuilt. Set
`liveOnly: true` to apply only the live fields and skip the rebuild, which is
what a client sweeping a parameter wants: no car disappearing underneath it.

A rebuild is **refused** in a LAN session, on a bot, in arcade or match modes, on
a rig running a native controller DLL. The refusal comes back as
`err rebuild_refused` with the reason verbatim from `CarRebuilder.CanRebuild`.

A rebuild drops the vehicle's telemetry subscription (the old hub's channels
closed over the old car) and re-establishes an existing takeover.

| Type | Body | Notes |
|---|---|---|
| `load_track` | partial: `trackId`, `match`, `laps`, `bots`, `difficulty`, `arcade`, `arcadeHandling`, `trackLimits`, `countdown`, `vehicle` | Acked *before* the scene load; `session_changed` follows. |
| `end_session` | — | Back to the menu. |
| `restart_run` | `vehicleId` | |
| `spawn_vehicle` | `preset` or `designJson`, `name`, `pos`, `euler`, `acquire` | |
| `despawn_vehicle` | `vehicleId` | Only vehicles from `spawn_vehicle`. |

`match` is `Race`, `Derby`, `Ctf`, `Soccer` or `FreeRoam`.

Lifecycle commands are refused during a LAN session with `err lan_session`: the
host owns the match and every client is watching it.

**A spawned vehicle is a free agent** — drivable, streamable and tunable, but not
entered into the match: no lap timing, no scoring. This is deliberate. A
`MatchDirector` aliases `TrackBootstrap`'s rig list, so appending to it mid-race
would hand the director a rig with no `MatchRacer` and no lap tracker. Spawned
cars adopt the session's physics and control rates.

Session vehicles cannot be despawned — destroying a car `TrackBootstrap` built
would leave the bootstrap, the HUD, the pause menu and any match director holding
a rig whose car is gone.

### Events

Unsolicited, `id: 0`:

| `kind` | Meaning |
|---|---|
| `session_changed` | A scene loaded; `note` is the scene name. Every subscription was dropped and every vehicle id is stale. Re-enumerate. |
| `vehicles_changed` | The vehicle list changed (spawn, despawn, LAN join/leave). |

---

## Error codes

Branch on `code`; the `message` is for humans and is not stable.

| Code | Meaning |
|---|---|
| `version_mismatch` | Protocol versions differ. Disconnect. |
| `not_handshaken` | Send `hello` first. |
| `bad_json` | The line, or an embedded `designJson`, did not parse. |
| `unknown_message` | Unrecognised `t`. |
| `busy` | Reserved. A second *connection* fails at the pipe, not here. |
| `no_session` | Nothing is loaded that the command needs. |
| `no_vehicle` | No such id, or its car is gone. |
| `not_acquired` | Acquire before commanding. |
| `already_acquired` | Already held; release first. |
| `wrong_level` | `drive` on a `raw` hold, or the reverse. |
| `lan_session` | Refused while LAN is running. |
| `rebuild_refused` | `message` carries the reason verbatim. |
| `bad_argument` | A value was out of range or unrecognised. |
| `unknown_channel` | None of the requested channels exist. |
| `not_supported` | The vehicle or scene cannot do this. |
| `internal` | A handler threw. The connection survives; check the game log. |

---

## Client checklist

1. Connect control → `hello` → check `welcome.version`.
2. Connect telemetry.
3. `list_vehicles`; keep the ids.
4. `acquire` before driving. Always `release` — including on your own crash path.
5. Send `drive`/`actuate` faster than your `deadManMs`, or the car brakes.
6. Decode telemetry against the **acked** channel order, not your request order.
7. Handle `session_changed` by re-enumerating: every id is stale.
8. Watch `seq` for dropped frames; lower `rateHz` or narrow the channel list.

`Tools/ipc-test-client.ps1` does all of this and is the reference implementation.

---

## Versioning

Message types, error codes and frame layouts are **append-only**. Anything that
changes the meaning of an existing field is a `ProtocolVersion` bump plus a
changelog entry in `IpcProtocol.cs`.

| Version | Changes |
|---|---|
| 1 | Initial: handshake, enumeration, per-vehicle takeover, telemetry and camera subscriptions, tuning/physics/settings control, session lifecycle, design push with rebuild refusals. |
