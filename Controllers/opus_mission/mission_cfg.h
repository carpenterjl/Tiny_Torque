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
 *
 * (Since FW-07 both of those live in the parameter store instead — see the
 * note where the vehicle block used to be. This file keeps the mission.)
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

/* ------------------------------------------------- vehicle & calibration --
 * Moved to the parameter store (FW-07): Controllers/params/opus_vector.json,
 * generated into core/params_opus_vector.c as TtParams. That covers geometry,
 * encoders, motor constants, drag, the friction brake, the motor-braking
 * capability and the measured calibration (CAL_SCALE / CAL_BRAKE). What
 * stays here is how the MISSION behaves, not what the car is.
 *
 * Gone for good: VE_N_MOTORS and the doubled R (the core now commands torque
 * per driven wheel, so it never needed to know the sim splits one real motor
 * into two), and every ESC detail (deadband, duty, rail) — those belong to
 * the drive, which in the sim is the adapter's virtual driver.
 */

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
/* Yaw-rate low-pass time constant; the tick-quantised differential is coarse
 * at 0.03 rad/s. 19.6 ms is the old per-tick 0.4 blend at 100 Hz, now
 * rate-independent (FW-05). */
#define GA_YAW_TAU_S       0.019576f
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
/* Speed low-pass for the creep loop and the stop tests: 23.2 ms is the old
 * per-tick 0.35 blend at 100 Hz (FW-05). */
#define EN_VFILT_TAU_S     0.023214f
/* (EN_REGEN_CAP_N is gone: how hard the motors can brake is the regen_*
 * capability in TtParams — for this car's ESC strong at speed and fading to
 * nothing at rest, which is exactly why the friction brake still finishes
 * the stop.) */

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
 * ToF firmware debounces for exactly this reason. 50 ms = 22 cm of travel at
 * cruise, still far enough out to stop for something real. (Was 5 ticks:
 * thresholds are times now, so the loop rate cannot change them. FW-05) */
#define SF_TOF_S           0.050f
/* Impact / teleport detector. 60 m/s^2 (6 g) was far too tight: a 1/10 car on a
 * 400 Hz solver puts single-tick spikes of that size through the IMU whenever a
 * suspension corner loads up quickly — brake application alone tripped it. A
 * real MEMS part on a real RC car sees the same thing, which is why real
 * firmware debounces instead of latching on one sample. 15 g sustained for 3
 * ticks is a crash; anything shorter is the road. */
#define SF_ACCEL_ABORT     150.0f    /* m/s^2 */
#define SF_ACCEL_S         0.030f    /* sustained this long (was 3 ticks) */
/* An encoder rate this high (~4.5x cruise) is a bad read, not motion. Was
 * 4000 counts per 10 ms tick. */
#define SF_GLITCH_CPS      400000.0f
/* The loop period may wander this far from the rate the host promised at
 * init before the standing check refuses to arm. */
#define SF_DT_TOL          0.20f

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
#define FA_PARAMS          0x2000    /* parameter set invalid, or contradicts the manifest */

#endif /* OPUS_MISSION_CFG_H */
