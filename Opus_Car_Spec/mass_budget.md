# Mass budget

Total **2131.5 g (2.1315 kg)**. This is the figure used everywhere in
[`derived_parameters.md`](derived_parameters.md).

The simulator computes the total itself from per-part masses
(`Garage/MassProperties.cs`, enabled by `useCompositeMass = true`), so the split below is
not decorative — it determines the centre of mass and the inertia tensor the physics
engine uses.

## Line items

| # | Item | Qty | Each | Total | Tag |
|---|---|---:|---:|---:|---|
| 1 | Rolling chassis — tub, drivetrain, suspension, shocks | 1 | 750 g | 750.0 g | estimated |
| 2 | Drive motor, Castle 1410-3800Kv | 1 | 239 g | 239.0 g | **published** |
| 3 | Wheels and tyres, 66 mm Ø, mounted | 4 | 60 g | 240.0 g | estimated |
| 4 | Battery, 2S 5200 mAh LiPo shorty | 1 | 265 g | 265.0 g | estimated |
| 5 | Printed shell, PETG | 1 | 199 g | 199.0 g | derived (below) |
| 6 | ESC | 1 | 82 g | 82.0 g | estimated |
| 7 | Equipment deck | 1 | 85 g | 85.0 g | estimated |
| 8 | Fasteners, wiring, connectors | 1 | 60 g | 60.0 g | estimated |
| 9 | Raspberry Pi 5, 8 GB | 1 | 46 g | 46.0 g | **published** |
| 10 | Steering servo, Savox SC-1251MG | 1 | 44.5 g | 44.5 g | **published** |
| 11 | Power distribution / UBEC board | 1 | 35 g | 35.0 g | estimated |
| 12 | Pi active cooler | 1 | 25 g | 25.0 g | **published** |
| 13 | Wheel encoders | 4 | 6 g | 24.0 g | estimated |
| 14 | WiFi antennas | 2 | 8 g | 16.0 g | estimated |
| 15 | Camera module + mount | 1 | 12 g | 12.0 g | estimated |
| 16 | VL53L1X ToF breakouts | 3 | 2 g | 6.0 g | estimated |
| 17 | BNO055 IMU breakout | 1 | 3 g | 3.0 g | estimated |
| | | | | **2131.5 g** | |

Published mass covers 354.5 g (17 %); the rest is estimated, dominated by the rolling
chassis and battery — the two items whose real mass a bench scale would settle in
seconds.

## The printed shell — derived, and heavier than you might expect

A 420 × 200 × 90 mm shell, open underneath:

```
area = top (0.42 × 0.20) + 2 sides (0.42 × 0.09) + 2 ends (0.20 × 0.09)
     = 0.0840 + 0.0756 + 0.0360 = 0.1956 m² = 1956 cm²
volume at 0.8 mm wall = 1956 × 0.08 = 156.5 cm³
mass  at PETG 1.27 g/cm³             = 199 g
```

**This is roughly twice a vacuum-formed lexan body** (~90–110 g), and it is an honest
consequence of printing rather than thermoforming: 0.8 mm is about the thinnest wall
that prints reliably at this size, whereas lexan is drawn to 0.5 mm or less. On a
2.1 kg car it is 9 % of the total, sitting high — the single worst place to carry mass.
If the real build wanted the weight back, thermoforming the shell is where to get it.

## How the mass is split for the simulator

`MassProperties.Compute` sums the chassis figure plus every part carrying its own
`massKg`. Parts the simulator models individually are pulled out; everything else is
folded into the chassis number:

| Simulator field | Contents | Mass |
|---|---|---:|
| `WheelSpec.massKg` ×4 | line 3 | 4 × 60 g = 240.0 g |
| `BatterySpec.massKg` | line 4 | 265.0 g |
| `SensorSpec.massKg` — encoders ×4 | line 13 | 4 × 6 g = 24.0 g |
| `SensorSpec.massKg` — ToF ×3 | line 16 | 3 × 2 g = 6.0 g |
| `SensorSpec.massKg` — camera | line 15 | 12.0 g |
| `AntennaSpec.massKg` ×2 | line 14 | 2 × 8 g = 16.0 g |
| | **modelled parts** | **563.0 g** |
| `VehicleDesign.mass` (chassis) | lines 1, 2, 5, 6, 7, 8, 9, 10, 11, 12, 17 | **1568.5 g** |
| | **total** | **2131.5 g** |

The motor, ESC and servo go into the chassis figure rather than into the wheels: the
real car's motor is centrally mounted and drives through a differential, so putting its
239 g at a wheel would misplace both the mass and the yaw inertia. Note this is a
departure from the simulator's default assumption — `MassProperties.PoweredWheelMass` is
190 g precisely because it expects an in-wheel motor — which is why all four wheels
carry an **explicit** 60 g rather than relying on the auto value.

## Mass placement (vehicle frame: +X right, +Y up, +Z forward, origin at chassis centre)

| Item | Position | Rationale |
|---|---|---|
| Battery | (0, −0.020, −0.050) | Low and slightly rearward — the heaviest single item, kept under the deck |
| Camera | (0, +0.090, +0.055) | Forward-looking, above the deck |
| ToF front | (0, +0.030, +0.210) | Nose, on the bumper line |
| ToF left / right | (∓0.060, +0.030, +0.190) | ±32° yaw, at the front corners |
| Encoders | at each wheel hub | Co-located with the wheel they measure |
| Antennas | (±0.055, +0.090, −0.140) | Rear deck, the F1TENTH look |

The chassis point mass sits at (0, −0.030, 0). With the battery low and central, the
resulting composite centre of mass lands slightly below and behind the geometric centre
— which is what keeps the inner front wheel loaded through the mission's corner (see
`derived_parameters.md` §9).
