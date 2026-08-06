using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// Tyre temperature and inflation pressure — the two things a real tyre has
    /// that <see cref="TyreModel"/>'s slip curve does not.
    ///
    /// <b>What it adds.</b> A cold tyre grips worse than a warm one, a warm tyre
    /// runs at a higher pressure than the one you set cold, and both an over- and
    /// an under-inflated tyre grip worse than a correct one. None of that is
    /// authored as a coefficient here: the temperature is INTEGRATED from the
    /// friction power the tyre model is already computing, the running pressure
    /// follows from the temperature by the gas law, and everything downstream is a
    /// function of those two.
    ///
    /// <b>Pure functions, no state.</b> The per-wheel temperature lives on
    /// <c>CarVehicle.Wheel</c> beside the rest of the wheel's state; this file is
    /// the arithmetic, which is what lets <c>[TTBENCH]</c> test it with no scene,
    /// no rigidbody and no play mode.
    ///
    /// <b>Scale.</b> The constants are for a 1/10 RC tyre — a ~50 g corner doing
    /// 5–15 m/s — not for a car. At those numbers a tyre warms with a time
    /// constant of about half a minute, settles around 40 °C in hard cornering,
    /// and reaches the high sixties in a sustained drift. A full-size car would
    /// want different ones, which is why the heat capacity is derived from the
    /// wheel's own unsprung mass rather than being a constant.
    ///
    /// <b>How it stays out of everything that existed before it.</b> A wheel with
    /// <c>pressureKpa</c> 0 — every design that predates this, every preset that
    /// does not opt in, and the Tiguan and the Opus Vector deliberately — takes
    /// none of these branches at all. Not a multiply by one: the call is not made.
    /// That is what makes the physics tests and the Opus mission bit-identical by
    /// construction rather than by tolerance.
    /// </summary>
    public static class TyreThermal
    {
        // ---- environment ------------------------------------------------------------

        /// <summary>Ambient air and track temperature (°C). Also the reference the
        /// garage's cold pressure is understood to be set at, so that a tyre at
        /// rest reads back exactly the pressure that was authored.</summary>
        public const float AmbientC = 25f;

        /// <summary>Absolute zero offset, for the gas law.</summary>
        public const float KelvinOffset = 273.15f;

        // ---- pressure ---------------------------------------------------------------

        /// <summary>The pressure the tyre is happiest at (kPa absolute). Not a
        /// tuning target so much as the origin of the penalty curve: over- and
        /// under-inflation are both measured from here.</summary>
        public const float PressOptKpa = 180f;

        /// <summary>Authored cold pressure is clamped to this band. Below the
        /// floor a tyre would be off the rim; above the ceiling it is a bicycle
        /// tyre, and neither end is a setup anyone is choosing on purpose.</summary>
        public const float PressMinKpa = 80f, PressMaxKpa = 300f;

        // ---- thermal ----------------------------------------------------------------

        /// <summary>Specific heat of the tread shell, J/(kg·K). Rubber is around
        /// 1800; this is lower because the number it multiplies is a mass, and
        /// what is actually being modelled is a tread band bolted to a hub that
        /// soaks heat away from it.</summary>
        public const float CTyreJPerKgK = 750f;

        /// <summary>The fraction of unsprung mass that is thermally active rubber.
        /// The rest is hub, bearing and upright, which heat far more slowly and
        /// are not what grips the road.</summary>
        public const float TreadMassFrac = 0.4f;

        /// <summary>The unsprung mass a wheel with the 0 sentinel means. The SAME
        /// 0.05 kg <c>CarVehicle</c> falls back to — stated here rather than
        /// assumed, because a heat capacity of zero divides.</summary>
        public const float LegacyUnsprungKg = 0.05f;

        /// <summary>Convective cooling at a standstill, W/K.</summary>
        public const float H0WPerK = 0.15f;

        /// <summary>How much airflow adds to that, W/(K·m/s).</summary>
        public const float H1WPerKPerMps = 0.05f;

        /// <summary>The share of rolling-resistance work that heats the RUBBER.
        /// All of it: rolling resistance IS hysteresis in the tread. Brake heat is
        /// deliberately not here — that goes into the disc.</summary>
        public const float RollHeatFrac = 1f;

        // ---- derived quantities -----------------------------------------------------

        /// <summary>Thermal mass of one tyre (J/K). About 15 for a stock RC wheel,
        /// which with the cooling below gives a warm-up time constant of roughly
        /// half a minute at speed.</summary>
        public static float HeatCapacityJPerK(float unsprungMassKg)
        {
            float m = unsprungMassKg > 0f ? unsprungMassKg : LegacyUnsprungKg;
            return TreadMassFrac * m * CTyreJPerKgK;
        }

        /// <summary>Convective loss coefficient (W/K) at a given contact-patch
        /// speed. Linear in speed, which is the standard forced-convection
        /// approximation over the range an RC car covers.</summary>
        public static float CoolingWPerK(float speedMs) =>
            H0WPerK + H1WPerKPerMps * Mathf.Abs(speedMs);

        /// <summary>
        /// One explicit step of the tyre's heat balance.
        ///
        /// <c>dT/dt = (Q − h(v)·(T − ambient)) / C</c>. At 400 Hz the stability
        /// number <c>h·dt/C</c> is around 1e-4, so this is nowhere near the edge —
        /// which is what lets <c>P9</c> re-run a manoeuvre at 200, 400 and 800 Hz
        /// and get the same answer.
        ///
        /// A wheel in the air still cools. That is not an oversight: it is why
        /// landing from a jump does not put a cold tyre on the road.
        /// </summary>
        public static float Step(float tempC, float heatW, float speedMs,
                                 float capacityJPerK, float dt)
        {
            float c = Mathf.Max(1e-6f, capacityJPerK);
            float cooling = CoolingWPerK(speedMs) * (tempC - AmbientC);
            return tempC + (heatW - cooling) / c * dt;
        }

        /// <summary>
        /// Running pressure (kPa) from the cold setting and the current tyre
        /// temperature — the gas law at constant volume.
        ///
        /// Exactly the cold pressure at ambient, and that exactness is the point:
        /// a car sitting in the pits reads back the number that was typed into it,
        /// so the pressure penalty at rest is whatever the setup deserves and not
        /// an artefact of the model warming up.
        /// </summary>
        public static float RunningPressureKpa(float coldKpa, float tempC)
        {
            float cold = Mathf.Clamp(coldKpa, PressMinKpa, PressMaxKpa);
            return cold * ((tempC + KelvinOffset) / (AmbientC + KelvinOffset));
        }

        // ---- what temperature and pressure do -------------------------------------

        /// <summary>
        /// Grip multiplier against tread temperature. Piecewise linear, in the
        /// shape of <c>CarVehicle.CellOcv</c>'s discharge curve and for the same
        /// reason: the interesting part is the corners, and a smooth fit through
        /// them would hide where they are.
        ///
        /// <b>The plateau is exactly 1.00, not 1.05.</b> A fully warm tyre grips
        /// the same as a tyre with no thermal model at all, so opting a design into
        /// pressure is a cold-start penalty and a overheating penalty — never a
        /// free grip bonus over the designs that have not opted in. It also makes
        /// the warm-up test's expected plateau exact rather than approximate.
        /// </summary>
        public static float GripVsTemp(float tempC)
        {
            if (tempC <= 0f) return 0.80f;
            if (tempC < 25f) return Mathf.Lerp(0.80f, 0.92f, tempC / 25f);
            if (tempC < 40f) return Mathf.Lerp(0.92f, 1.00f, (tempC - 25f) / 15f);
            if (tempC <= 70f) return 1.00f;
            if (tempC < 100f) return Mathf.Lerp(1.00f, 0.85f, (tempC - 70f) / 30f);
            if (tempC < 130f) return Mathf.Lerp(0.85f, 0.70f, (tempC - 100f) / 30f);
            return 0.70f;
        }

        /// <summary>
        /// Grip multiplier against running pressure. Quadratic either side of the
        /// optimum and floored, because a badly inflated tyre is worse, not
        /// useless — an under-inflated tyre squirms and an over-inflated one rides
        /// on its crown, and both of those are a few per cent of grip rather than a
        /// cliff.
        ///
        /// Exactly 1 at the optimum, so a correctly set tyre is only ever judged on
        /// its temperature.
        /// </summary>
        public static float GripVsPressure(float runKpa)
        {
            float e = (runKpa - PressOptKpa) / PressOptKpa;
            return Mathf.Max(0.85f, 1f - 0.5f * e * e);
        }

        /// <summary>
        /// Rolling-resistance scale against running pressure. A soft tyre deforms
        /// more per revolution and loses more to hysteresis, which is why an
        /// under-inflated car is slower on the straight and why the effect partly
        /// cancels itself: the extra loss becomes heat, the heat raises the
        /// pressure, and the pressure takes some of the loss back.
        ///
        /// Scales the SURFACE's rolling term rather than replacing it. How much a
        /// surface costs to roll on is a property of the surface; how much this
        /// tyre suffers for it is a property of the tyre.
        /// </summary>
        public static float RollResistScale(float runKpa) =>
            Mathf.Clamp(Mathf.Sqrt(PressOptKpa / Mathf.Max(1e-3f, runKpa)), 0.7f, 1.4f);

        /// <summary>Static rolling-radius scale with pressure — a hard tyre stands
        /// a little taller. Deliberately tiny (±1 % at the clamps): this moves the
        /// odometry every controller reads, and inflation is not a gear change.</summary>
        public static float RadiusScale(float runKpa) =>
            1f + 0.02f * Mathf.Clamp(runKpa / PressOptKpa - 1f, -0.5f, 0.5f);

        /// <summary>
        /// How much of the centrifugal ballooning survives at this pressure.
        ///
        /// Inflation is exactly what resists a tyre growing at speed, so the
        /// existing <c>balloonPct</c> growth is damped by pressure rather than
        /// being independent of it. A hot, hard tyre balloons less than the cold
        /// one that number was measured on.
        /// </summary>
        public static float BalloonDamp(float runKpa) =>
            Mathf.Clamp(PressOptKpa / Mathf.Max(1e-3f, runKpa), 0.5f, 1.5f);
    }
}
