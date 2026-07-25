# Bill of materials — published specifications

Every figure below is copied from a manufacturer datasheet or product page. Where a
parameter the simulator needs is *not* published, that is stated explicitly rather than
filled in — see [`derived_parameters.md`](derived_parameters.md) for how those gaps are
closed.

Accessed 2026-07-24.

---

## 1. Drive motor — Castle Creations 1410-3800Kv, 4-pole sensored

Source: <https://www.castlecreations.com/en/1410-3800kv-sensored-motor-5mm-060-0066-00>

| Parameter | Published value |
|---|---|
| Kv | 3800 rpm/V |
| Configuration | 4-pole, 12-slot, sensored |
| Maximum speed | 100 000 rpm |
| Can length | 52.7 mm |
| Can diameter | 36 mm |
| Shaft | 5 mm Ø × 21 mm |
| Mass (with wires) | 239 g (8.4 oz) |
| Input voltage | 2S–3S LiPo |
| Mounting | M3 on 25 mm centres |
| Connectors | 4 mm Castle bullet |

**Not published:** winding resistance, no-load current, rotor inertia, continuous or
peak current rating, efficiency. Castle publishes no engineering datasheet for this
motor; the product page and retail listings carry only the table above. All four missing
electrical parameters are handled in `derived_parameters.md`.

---

## 2. Electronic speed controller — 1/10-class sensored brushless, 2–3S

No specific part is pinned, because the mission never approaches the ESC's limits (peak
demand is ~20 A against a 60 A class rating — see `derived_parameters.md` §6). Any
1/10-class sensored ESC in the Castle Sidewinder / Hobbywing QuicRun family satisfies
the requirement. The simulator is configured with:

| Parameter | Value | Tag |
|---|---|---|
| Continuous current limit | 60 A (30 A per simulated motor) | estimated — class-typical; non-binding at mission power |
| Mass | 82 g | estimated — class-typical with wires and fan |
| PWM resolution | 1024 steps | estimated — class-typical 10-bit |
| Command deadband | 0.10 V | estimated — models the neutral band every RC ESC implements |
| Command time constant | 5 ms | estimated — models ESC input filtering and commutation update |

These five rows are the weakest evidence in the whole document. They are also the least
consequential: §6 of `derived_parameters.md` shows the mission demands about a third of
the current limit, and the deadband and lag terms are small enough that the mission
controller's feed-forward absorbs them.

---

## 3. Steering servo — Savox SC-1251MG low-profile digital

Source: <https://www.savoxusa.com/products/savsc1251mg-low-profile-digital-servo>

| Parameter | Published value |
|---|---|
| Speed @ 4.8 V | 0.10 s / 60° |
| Speed @ 6.0 V | 0.09 s / 60° |
| Torque @ 4.8 V | 7.0 kg·cm (97.2 oz·in) |
| Torque @ 6.0 V | 9.0 kg·cm (125.0 oz·in) |
| Mass | 44.5 g |
| Dimensions | 40.3 × 20.2 × 25.4 mm |
| Resolution | 12-bit (4096 steps) |
| Gear train | Metal, coreless motor, aluminium case |

---

## 4. Battery — 2S 7.4 V LiPo shorty pack

| Parameter | Value | Tag |
|---|---|---|
| Nominal voltage | 7.4 V (2 cells × 3.7 V) | published — LiPo chemistry |
| Capacity | 5200 mAh | estimated — typical 1/10 shorty pack |
| Mass | 265 g | estimated — typical 2S 5200 mAh hard-case shorty |
| Internal resistance | 20 mΩ total | estimated — ~10 mΩ per cell for a healthy high-C pack, plus wiring |

Pack internal resistance is rarely published and degrades with cycle count; 20 mΩ is a
fresh-pack figure. Its only effect in the simulator is voltage sag under load, which at
this mission's peak current (~20 A) is 0.4 V out of 7.4 V — visible in telemetry, never
limiting.

---

## 5. Range sensors ×3 — STMicroelectronics VL53L1X

Source: <https://www.st.com/en/imaging-and-photonics-solutions/vl53l1x.html>
Datasheet: [`datasheets/VL53L1X-datasheet.pdf`](datasheets/VL53L1X-datasheet.pdf)

| Parameter | Published value |
|---|---|
| Maximum ranging distance | 4 m (400 cm) |
| Ranging frequency | up to 50 Hz |
| Field of view | 27° (programmable ROI) |
| Ranging accuracy | ±25 mm (±20 mm in the dark) |
| Emitter | 940 nm invisible, Class 1 eye-safe |
| Interface | I²C |
| Package | 4.9 × 2.5 × 1.56 mm |

Mounted forward and at ±32° yaw. Note that the ±25 mm figure is an accuracy *bound*, not
a standard deviation — see `derived_parameters.md` §7 for the conversion used.

---

## 6. IMU — Bosch Sensortec BNO055

Source: <https://www.bosch-sensortec.com/products/smart-sensor-systems/bno055/>
Datasheet: [`datasheets/BST-BNO055-DS000.pdf`](datasheets/BST-BNO055-DS000.pdf)

| Parameter | Published value |
|---|---|
| Configuration | Triaxial 14-bit accelerometer, triaxial 16-bit gyroscope, triaxial magnetometer, Cortex-M0+ running Bosch sensor fusion |
| Gyroscope range | ±2000 °/s |
| Gyroscope noise density | 0.014 °/s/√Hz |
| Gyroscope output noise | 0.3 °/s (47 Hz bandwidth) |
| Interface | I²C / UART |

The gyroscope's yaw channel is what the mission controller fuses for heading. At the
100 Hz control rate the relevant figure is the 0.3 °/s output noise, which integrates to
well under a degree across the 1 s turn — the dominant heading error is chassis
vibration, not sensor noise.

---

## 7. Wheel encoders ×4 — magnetic quadrature, 1024 lines

| Parameter | Value | Tag |
|---|---|---|
| Resolution | 1024 lines → 4096 counts/rev in ×4 quadrature | estimated — AS5047P / AMT102-V class |
| Mounting | On the wheel/hub shaft, ratio 1:1 | design choice |
| Mass | 6 g each (sensor board + diametric magnet) | estimated |
| Read-out | Synchronous hardware counter (no polling latency) | design choice |

At a 33 mm wheel radius, 4096 counts/rev is **0.0506 mm of travel per count** — see
`derived_parameters.md` §8. The two *front* encoders are the odometry source, because
the front wheels are unpowered and therefore cannot spin up under drive torque.

---

## 8. Camera — Raspberry Pi Camera Module 3

| Parameter | Value | Tag |
|---|---|---|
| Sensor | Sony IMX708, 11.9 MP | published |
| Field of view | 66° horizontal (standard lens) | published |
| Mass with ribbon and mount | 12 g | estimated |

Streamed to the controller as a downsampled 64 × 48 grayscale frame at 10 Hz, which is
what a real perception loop would consume for lane-keeping at this scale.

---

## 9. Compute — Raspberry Pi 5, 8 GB

| Parameter | Value | Tag |
|---|---|---|
| Mass, board | 46 g | published |
| Mass, active cooler | 25 g | published |
| Supply | 5 V via UBEC from the traction pack | design choice |

---

## 10. Structure

| Item | Value | Tag |
|---|---|---|
| Rolling chassis (tub, drivetrain, suspension, shocks, less wheels and electronics) | 750 g | estimated |
| Wheels and tyres, 66 mm Ø, mounted | 60 g each | estimated |
| Equipment deck (3 mm carbon or printed) | 85 g | estimated |
| Printed shell, PETG | 199 g | derived from printed volume — see `mass_budget.md` |
| Fasteners, wiring, connectors | 60 g | estimated |
| Wheelbase | 300 mm | design choice |
| Track width | 172 mm | design choice |
| Wheel radius | 33 mm | design choice — 66 mm Ø, 1/10 touring |
