/*
 * mission_cfg.h — every tunable for the Opus Vector mission, in one place.
 *
 * Two kinds of constant live here and they must not be confused:
 *
 *   VEHICLE constants describe the hardware. They come from
 *   Opus_Car_Spec/sim_mapping.md and change only when the car changes.
 *
 *   CALIBRATION constants describe the difference between what the sensors say
 *   and what the world does. They are MEASURED, not chosen, and the procedure
 *   is written up in Opus_Car_Spec/calibration.md.
 *
 * Nothing here depends on the simulator. Ported to the real car, the vehicle
 * block is retyped from the same spec sheet and the calibration block is
 * re-measured by the same procedure.
 */
#ifndef OPUS_MISSION_CFG_H
#define OPUS_MISSION_CFG_H

#define OPUS_PI            3.14159265358979323846f
#define OPUS_RAD2DEG       57.29577951308232f
#define OPUS_DEG2RAD       0.017453292519943295f

/* ---------------------------------------------------------------- mission --
 * The manoeuvre, exactly as specified. Distances are path length along the
 * ground, measured at the FRONT AXLE (that is what the odometer natively
 * measures — see odometry notes in opus_mission.c).
 */
#define MI_V_CRUISE        4.5f      /* m/s, held through legs A and B and the turn */
#define MI_LEG_A_M         14.5f     /* m of constant-velocity travel before the turn */
#define MI_TURN_RAD        (OPUS_PI * 0.25f)  /* 45 deg, left */
#define MI_LEG_B_M         7.5f      /* m at cruise after straightening out */
#define MI_BRAKE_M         1.5f      /* m from brake application to standstill */
/* 9.0 m total from the turn exit. Kept as a sum so the two legs can never
 * silently drift apart from the stated total. */
#define MI_STOP_FROM_EXIT  (MI_LEG_B_M + MI_BRAKE_M)

/* ---------------------------------------------------------------- vehicle --
 * From Opus_Car_Spec/sim_mapping.md. Do not edit these to make a run come out
 * right — that is what the calibration block is for.
 */
#define VE_WHEEL_R         0.033f    /* m, 66 mm touring tyre */
#define VE_WHEELBASE       0.300f    /* m */
#define VE_TRACK_FRONT     0.172f    /* m, front axle track — the heading baseline */
#define VE_MASS            2.1315f   /* kg, all-up (Opus_Car_Spec/mass_budget.md) */
#define VE_ENC_CPR         4096.0f   /* counts/rev, 1024-line quadrature on the wheel */
#define VE_ENC_WRAP        65536     /* the sensor's 16-bit tick register */

#define VE_KT              0.0025130f /* N*m/A, = 60/(2*pi*3800 Kv) */
#define VE_GEAR            11.2f      /* final drive */
#define VE_R_MOTOR         0.060f     /* ohm, per SIMULATED motor (2x the real 30 mOhm) */
#define VE_ETA             0.85f      /* drivetrain efficiency */
#define VE_N_MOTORS        2          /* simulated motors standing in for one real one */
#define VE_ESC_DEADBAND_V  0.10f      /* below this the ESC outputs nothing */
#define VE_V_RAIL          7.4f       /* nominal 2S; the live value is read from the pack */

#define VE_MAX_STEER_DEG   28.0f
#define VE_SERVO_SLEW_DPS  600.0f     /* for the servo observer only; not a command limit */
#define VE_MAX_BRAKE_NM    0.8f       /* per wheel, at brake command 1.0 */

/* Referred to the road. Derived, not independent — see
 * Opus_Car_Spec/derived_parameters.md section 5. */
#define VE_BEMF_V_PER_MS   (VE_KT * VE_GEAR / VE_WHEEL_R)           /* 0.8529 V per m/s */
#define VE_FORCE_PER_AMP   (VE_BEMF_V_PER_MS * VE_ETA)              /* 0.7250 N per motor-amp */
#define VE_FORCE_PER_AMP_ALL (VE_FORCE_PER_AMP * (float)VE_N_MOTORS)/* 1.4499 N per per-motor-amp */

/* COAST drag only, F = c0 + c1*v + c2*v^2 — the force that decelerates a car
 * with no torque through the driven wheels. Feed-forward for the speed loop.
 *
 * RE-MEASURED for the iteration-22 brush tyre model (the previous figures were
 * PhysX WheelCollider artifacts: its stylised slip curve dissipated ~4x the
 * physical losses, which is why the old c-values summed to ~13 N at cruise).
 * Under the brush model the analytic budget of derived_parameters.md §6 (motor
 * Coulomb + gearbox viscosity + aero) is close to the whole story.
 * PROVISIONAL pending the calibration rerun — measured values land here. */
/* MEASURED (run R1, 2026-07-25): holding a true 4.41 m/s took 2.91 N of
 * commanded wheel force — the whole coast budget, since driven slip is now
 * ~1 %. c0 = the Coulomb term from derived_parameters §6, c2 = analytic aero;
 * c1 absorbs the measured remainder. */
#define VE_DRAG_C0         0.90f
#define VE_DRAG_C1         0.38f
#define VE_DRAG_C2         0.015f

/* Effective longitudinal inertia. The rotors turn at gear^2 times the wheel's
 * rate, so their inertia is reflected to the road as
 *     m_rot = N * J * gear^2 / r^2 = 2 * 2.5e-6 * 11.2^2 / 0.033^2 = 0.576 kg
 * on top of the 2.1315 kg of actual car. A quarter of the "mass" this
 * controller accelerates and brakes never moves anywhere. */
#define VE_MASS_EFF        2.708f    /* kg */

/* Fraction of the force commanded at the driven wheels that reaches the road.
 * The old PhysX tyre lost 53 % of it to slip (0.47 measured) — a simulator
 * artifact. MEASURED under the brush tyre: ~1 % slip loss at cruise loads. */
#define VE_TRACTION_EFF    0.99f

/* ESC shorted-winding brake, referred to the road. A hobby ESC brakes by
 * shorting the motor: braking force is proportional to BOTH the brake duty and
 * the speed (back-EMF drives the current through R), fading to nothing at rest:
 *   F(duty, v) = duty * N*kt^2*gear^2*eta/(R_motor*r^2) * v
 * current-limited at the ESC's 30 A per motor. This replaces the old smooth
 * signed-voltage "regen" model (EN_REGEN_CAP_N) — the host now runs a real
 * drive/brake/reverse state machine, and a negative command while rolling
 * forward IS a brake duty of |v_cmd|/V_rail. */
#define VE_ESC_BRAKE_N_PER_MS  20.6f  /* = 2*kt^2*gear^2*eta/(R*r^2) */
#define VE_ESC_BRAKE_MAX_N     43.5f  /* at the 30 A/motor ESC current limit */
/* Rear-grip ceiling on the ESC brake — the modern form of the old regen cap,
 * for the same physical reason: the ESC brakes through the REAR tyres only,
 * and braking weight transfer unloads exactly that axle. MEASURED (run R1):
 * asking the rears for 16.6 N saturated them at ~9 N (the whole car decelerated
 * at 4.1 m/s^2 with the friction brake never called). 7 N keeps the rear slip
 * in the linear range; the friction brake — all four wheels — takes the rest. */
#define EN_ESC_BRAKE_CAP_N     7.0f

/* ------------------------------------------------------------ calibration --
 * MEASURED against ground truth. See Opus_Car_Spec/calibration.md.
 *
 * CAL_SCALE is the big one: a free-rolling wheel that carries any drag runs at
 * negative slip, so its encoder under-reads the ground. Everything else in the
 * error budget is a rounding error next to it.
 */
/* Iteration 22: the old 0.116 was a PhysX WheelCollider artifact (its slip
 * curve ran the free-rolling fronts at ~10 % slip). MEASURED (run R1) over the
 * 14.5 m constant-velocity leg per the unchanged calibration.md procedure:
 * d_truth/d_raw − 1 = −0.00011 — the free-rolling fronts carry so little drag
 * in the brush model that their slip is unresolvable. On the physical car this
 * returns to the 1–4 % band (bearing drag + carcass deformation the sim does
 * not model on unpowered wheels); the PROCEDURE, not the value, transfers. */
#define CAL_SCALE          0.0000f   /* v_ground = v_enc * (1 + CAL_SCALE) */
/* MEASURED (run R2): the friction brake now does real work through the FRONT
 * wheels (the ESC brake is rear-grip-capped), so the free-rolling odometer
 * axle finally slips while braking — 115 mm went missing over 1.15 m of
 * braked rolling at brake_cmd ≈ 0.1: k = missing/∫brake·ds ≈ 1.0. FRACTIONAL
 * scale per unit brake command (multiplicative with the rolled distance —
 * slip is proportional to road speed, so it must vanish at rest). Under the
 * old model the brake barely brushed the fronts and this was unmeasurable. */
#define CAL_BRAKE          1.0f      /* extra fractional slip per unit brake cmd */

/* ----------------------------------------------------------------- limits --*/
#define LI_A_LAUNCH        6.0f      /* m/s^2. Below the ~15.7 m/s^2 traction limit on
                                      * purpose: 35 % of the car's effective inertia is
                                      * rotating drivetrain, and a slipping rear axle
                                      * would tell us nothing useful. */
#define LI_A_BRAKE         6.75f     /* m/s^2 = MI_V_CRUISE^2/(2*MI_BRAKE_M); 43 % of grip */
#define LI_A_MAX           12.0f     /* m/s^2, clamp on the commanded acceleration.
                                      * Below the ~15.7 m/s^2 traction limit, but well
                                      * above anything the mission asks for: the margin
                                      * is there so the speed loop can still reach its
                                      * target when the drag feed-forward under-reads. */
#define LI_A_LAT           4.0f      /* m/s^2 through the turn — limited by inner-wheel
                                      * LOAD, not by grip. At 8 the inner front lifts. */
#define LI_TURN_RAMP_S     0.15f     /* steering blend in/out */

/* ------------------------------------------------------------------- gains --*/
#define GA_SPD_KP          12.0f     /* (m/s^2) per (m/s) — 83 ms velocity time constant */
#define GA_SPD_KI          30.0f
/* Trim authority. Sized generously rather than tightly: the analytic drag model
 * accounts for the motor, gearbox and bearing losses it can see, but not the
 * tyre losses inside the physics engine's own wheel model, which measured ~6 N
 * larger at 2 m/s. A tight trim limit turns that shortfall into a speed the car
 * simply never reaches. See Opus_Car_Spec/calibration.md. */
#define GA_SPD_TRIM        12.0f     /* m/s^2 */
/* Yaw-rate loop, in deg of steer per (rad/s) of error.
 *
 * Steer angle maps to yaw rate as a near-static gain, dpsi/ddelta = v/L, which
 * at cruise is 0.26 (rad/s) per degree. A discrete loop closed around a static
 * plant with one sample of delay is unstable once the loop gain passes ~2, so
 * these have to stay small: 1.5 * 0.26 = 0.39 is comfortably damped. (An earlier
 * 40 here — sized as if the plant were an integrator — oscillated at exactly the
 * Nyquist frequency, alternating steer sign every tick.) Feed-forward does the
 * work anyway; this loop only supplies the tyre slip that cannot be predicted. */
#define GA_YAW_KP          1.5f
#define GA_YAW_KI          4.0f
#define GA_YAW_TRIM        8.0f      /* deg */
#define GA_YAW_FILT        0.4f      /* yaw-rate low-pass; the tick-quantised
                                      * differential is coarse at 0.03 rad/s */
/* Heading closure INSIDE the turn. The trapezoid alone is open-loop in heading:
 * whatever the yaw-rate loop fails to deliver is simply lost, and the first run
 * measured 41.4 deg of an intended 45. Adding a proportional term on the
 * integrated command (turn_cmd_rad) versus the measured heading turns the turn
 * into a closed-loop manoeuvre without disturbing the profile's shape — the
 * profile still supplies the feed-forward, this only pays back the shortfall. */
#define GA_TURN_KP         2.5f      /* 1/s */
#define GA_TURN_MAX_TRIM   0.30f     /* rad/s, ceiling on that correction */
#define GA_TURN_CLOSE_RAD  0.010f    /* heading error that counts as "arrived" */
#define GA_TURN_MAX_EXTRA  1.50f     /* s past the profile before giving up */

#define GA_HEAD_KP         2.0f      /* 1/s — heading hold on the straights */
#define GA_HEAD_MAX_RATE   0.30f     /* rad/s, clamp on the heading loop's output */
#define GA_GYRO_ALPHA      0.75f     /* complementary blend toward the gyro */

/* --------------------------------------------------------------- endgame --*/
#define EN_CREEP_M         0.040f    /* below this remaining distance, switch to creep */
#define EN_CREEP_V         0.30f     /* m/s ceiling while creeping */
#define EN_CREEP_KV        7.5f      /* 1/s, v_ref = KV * s_remaining */
#define EN_DONE_M          0.001f    /* stop tolerance against our own odometer */
#define EN_DONE_V          0.02f     /* m/s */
#define EN_HOLD_S          1.0f      /* zero motion for this long before declaring DONE */
#define EN_DEAD_TIME_S     0.020f    /* loop dead time, led out of the braking profile */
/* (EN_REGEN_CAP_N is gone: motor braking is now the ESC shorted-winding model,
 * VE_ESC_BRAKE_* above — strong at speed, fading to nothing at rest, which is
 * exactly why the friction brake still finishes the stop.) */

/* -------------------------------------------------------------- sequencing --*/
#define SQ_ARM_BRAKE_S     1.0f      /* held-brake settling window */
#define SQ_ARM_ROLL_S      2.0f      /* brake released; counters must not move */
#define SQ_ARM_DWELL_S     0.25f     /* ARMED dwell before launching */
#define SQ_SETTLE_S        0.30f     /* at cruise speed before leg A starts counting */
#define SQ_LIVENESS_M      0.50f     /* distance over which encoder liveness is proven */

/* ------------------------------------------------------------------ safety --*/
#define SF_TOF_ABORT_M     0.60f     /* forward range below this aborts */
/* ...but only if it PERSISTS. A single short return is far more likely to be
 * the nose-down attitude under braking bouncing the beam off the road than a
 * real obstacle, and a 1/10 car pitches several degrees under 6.75 m/s^2. Real
 * ToF firmware debounces for exactly this reason. 5 ticks = 50 ms = 22 cm of
 * travel at cruise, still far enough out to stop for something real. */
#define SF_TOF_TICKS       5
/* Impact / teleport detector. 60 m/s^2 (6 g) was far too tight: a 1/10 car on a
 * 400 Hz solver puts single-tick spikes of that size through the IMU whenever a
 * suspension corner loads up quickly — brake application alone tripped it. A
 * real MEMS part on a real RC car sees the same thing, which is why real
 * firmware debounces instead of latching on one sample. 15 g sustained for 3
 * ticks is a crash; anything shorter is the road. */
#define SF_ACCEL_ABORT     150.0f    /* m/s^2 */
#define SF_ACCEL_TICKS     3
#define SF_TICK_GLITCH     4000      /* counts in one tick (~4.5x cruise) = bad read */

/* Fault bits, reported on debug[1]. */
#define FA_NO_MANIFEST     0x0001
#define FA_NO_ENCODERS     0x0002
#define FA_NO_MOTORS       0x0004
#define FA_BATTERY         0x0008
#define FA_DT              0x0010
#define FA_IMU             0x0020
#define FA_ROLLING         0x0040
#define FA_NAN             0x0080
#define FA_ENC_DEAD        0x0100
#define FA_BRAKE_LOCK      0x0200
#define FA_IMPACT          0x0400
#define FA_OBSTACLE        0x0800
#define FA_TICK_GLITCH     0x1000

#endif /* OPUS_MISSION_CFG_H */
