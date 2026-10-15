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
#define GA_TURN_SETTLE_S   0.50f     /* s past the profile: the looser mark applies */
#define GA_TURN_LOOSE_RAD  0.030f    /* 1.7 deg */

#define GA_HEAD_KP         2.0f      /* 1/s — heading hold on the straights */
#define GA_HEAD_MAX_RATE   0.30f     /* rad/s, clamp on the heading loop's output */

/* ------------------------------------------- the car's own errors (FW-15) --
 * Heading is the gyro less the bias it read while the car stood still to
 * arm. (It used to be 75 % gyro + 25 % front-encoder differential: a 2 %
 * left/right radius mismatch reads as 30 deg/s of yaw at cruise, and the
 * heading loop steered the car round to cancel it. The differential is now
 * only the fallback while the IMU is out, corrected by the left/right scale
 * learned against the gyro below.) */
#define GA_GB_SETTLE_S     0.20f     /* s into the standing check before the bias sum starts */
#define GA_LR_TAU_S        1.0f      /* s, learning the odometry pair's left/right scale */
#define GA_LR_MIN_V        1.0f      /* m/s, below this the differential is too coarse */
#define GA_LR_MAX          0.05f     /* clamp: a bigger mismatch is a fault, not a scale */
/* Steering trim: on a straight the steering that is ACTING (the command a
 * delay ago, through the steering's lag) should give the kinematic yaw rate
 * v*tan(delta)/L. What it gives instead is the servo's centre error, learned
 * here and taken off every command. A +2 deg trim otherwise swings the car
 * 7 deg off line during the launch, faster than the yaw loop's integral can
 * follow, and into the marker cones at the turn. */
#define GA_TRIM_TAU_S      0.25f     /* s */
#define GA_TRIM_MIN_V      0.8f      /* m/s */
#define GA_TRIM_MAX_AY     1.0f      /* m/s^2: above this tyre slip angles count */
#define GA_TRIM_MAX_RAD    0.07f     /* 4 deg */
/* ...but only what lies beyond the servo's own play. Inside its deadband and
 * backlash (R6: ~0.3 deg) the angle acting depends on the way it was last
 * moved, so a small offset is noise to this estimate; the yaw loop's integral
 * already takes care of it. Learning it anyway left a 0.23 deg trim that held
 * the steering inside the deadband after the turn, and the car swung back
 * 1.8 deg. */
#define GA_TRIM_DEADZONE   0.008f    /* rad, 0.46 deg */
/* Steering backlash (steer_backlash_rad, TtParams): the wheels stay put
 * while the horn crosses the play, so a reversal is commanded straight across
 * it. A correction smaller than this holds the horn instead, so noise in the
 * yaw loop cannot flick it from side to side. */
#define GA_LASH_HYST       0.001f    /* rad, 0.06 deg */
#define GA_TRIM_LAG_S      0.05f     /* steering lag to assume when steer_lag_s is 0 */
/* Distance scale: the navigation filter's odometer scale k_w (true metres per
 * odometer metre) is taken once it is known this well, and only in this band.
 * A uniform 2 % radius error is otherwise 290 mm on leg A. */
#define NV_KW_SIGMA_MAX    0.004f
/* The flow sensor's speed goes to the EKF above this quality (squal / 255;
 * the twin reads 0.6-0.8 over the proving ground's asphalt) and below this
 * speed (the PAA5100 loses track at 1.14 m/s over 25 mm) (FW-14). */
#define NV_FLOW_QUALITY    0.4f
#define NV_FLOW_VMAX       3.0f
#define NV_KW_MIN          0.90f
#define NV_KW_MAX          1.10f

/* ------------------------------------------ force gain and grip (FW-16) --
 * Force gain: how much acceleration a newton of commanded wheel force
 * actually buys, against what the parameters promise. Motor constant, mass
 * and wheel radius errors all land here (kt 7.5 % low, the car 10 % heavy:
 * the stop overran by 51 / 26 mm on a speed loop held back by the grip
 * budget). Least squares over the launch, on ticks where no wheel is
 * slip-limited and the force is not stepping; then held. (Measured over the
 * stop as well it reads 2-3 % low on every car, nominal included: the
 * pitching body and the braked wheels' slip are not in the model, and a
 * gain that low over-brakes.) */
#define GA_FG_MIN_N        5.0f      /* N of force acting before a tick counts      */
#define GA_FG_STEP_FRAC    0.15f     /* ...and within this of the tick before's     */
#define GA_FG_MIN_W        50.0f     /* N^2*s of evidence before the gain is used:
                                        ~0.25 s of the launch's 14 N              */
#define GA_FG_MIN          0.75f     /* outside this band the measurement is broken,
                                        not the car: the gain stays 1. On a floor
                                        measured slippery it is set aside too:
                                        the shortfall it saw was grip.       */
#define GA_FG_MAX          1.33f
/* Grip: the floor's friction is only seen at the limit. The driven wheels'
 * slip (the way their torque pushes) is averaged over the launch above
 * GA_MU_MIN_V, and each wheel's slip at cruise (GA_MU_CAL_T0..T1 into leg A,
 * where the drive force is almost nothing) is taken off it: that is the
 * wheel's own radius error, not slip. One second into leg A, if the mean
 * corrected launch slip passes GA_MU_SLIP_FRAC of slip_max, the car was at
 * the friction the floor has: the launch's mean acceleration, over g, over
 * the share of the peak a tyre gives there. Judged once, ~5 s before the
 * stop. (Judged during the launch on the raw slip, per-wheel radius and
 * motor errors raised false alarms on 11 of 74 good floors.)
 *
 * The launch (0.72 g of tyre
 * force) asks about as much as the stop (0.75 g), so a floor too slippery to
 * stop on shows up before the stop; a nominal launch slips 3.5 %. (Under the
 * stop a nominal car's rear pair, unloaded, already runs at -7.5 %: the car
 * is nowhere near its limit there.) The evidence is the wheels' slip, never
 * the limiter's cut: once the estimate is down, the limiter and the budget
 * hold the car back by design, and reading that as the floor spirals the
 * estimate to nothing. */
/* Measured on the FOC twin's launches above 3 m/s: the largest wheel's
 * slip ~4.5 % on a nominal floor (the mean ~4 %), 5-6 % at 0.84x grip,
 * 5-9 % at 0.62-0.75x. A false alarm costs only an early stop that still
 * lands on the mark. */
#define GA_MU_SLIP_FRAC    0.65f
/* A wheel slipping past this share of slip_max keeps its tick out of the
 * force-gain fit: the force it carried is the floor's, not the motor's.
 * At the limiter's own threshold, not below it (FW-23): the launch's slip
 * is read against a speed that runs on the accelerometer and lags 0.05-0.1
 * m/s, and through each wheel's radius error (+-2 %, learned only at
 * cruise), so a nominal launch's 4.5 % reads 6-8 % on many cars. At 0.75
 * the fit starved on 6 of 8 heavy cars, kept a gain of 1, budgeted their
 * stop for the nominal mass and overran it by 50-140 mm. */
#define GA_FG_SLIP_FRAC    1.00f
/* Above this speed only: the firmware's slip is the wheel against v_est,
 * which trails a hard launch (the odometer's slip model over-corrects there)
 * and inflates slip most at low speed. At 1 m/s a nominal launch read past
 * the threshold. */
#define GA_MU_MIN_V        3.0f      /* m/s */
#define GA_MU_CAL_T0       0.5f      /* s into leg A: the launch's transient gone */
#define GA_MU_CAL_T1       1.5f      /* s into leg A: offsets taken, floor judged  */
#define GA_MU_SAT          0.93f     /* force at the limiter's slip / peak          */
#define GA_MU_MIN          0.15f
/* What the stop plans on, of the measured friction: below the peak with a
 * margin for the load estimate (the tyre past its peak falls to a slide). */
#define GA_MU_USE          0.85f
/* The slip limiter's threshold on a slippery floor: this share of the slip
 * at which the tyre peaks (2 mu / C for the brush tyre), never above
 * slip_max. 8 % is past the peak below mu = 0.68. */
#define GA_SLIP_PEAK_FRAC  0.70f

/* ------------------------------------------- the command delay (FW-17) --
 * cmd_delay_s is what the parameters say; the delay the car really has
 * shows on the drives' measured current. At the launch the torque steps
 * from nothing to the launch force in one tick; the drives' current crosses
 * half of it the first tick after the command acts. That tick, less the
 * step's own, less the tick it takes to be sampled, is the delay in whole
 * ticks, rounded down (26 ms reads 20): what the in-flight prediction counts
 * in. (A step through a current loop this fast shows no finer: the current
 * is all there or none of it at a sample. On the car, the drive bus stamps
 * when each command reached its node: core/tt_drvbus.h.) */
#define GA_DLY_MIN_N       2.0f      /* N: a launch step smaller than this is not timed */
#define GA_DLY_FRAC        0.5f      /* the measured force must reach this share of the step */
#define GA_DLY_PRE_FRAC    0.25f     /* ...from below this before it (the drives were idle) */

/* ---------------------------------------------- the friction probe (FW-18) --
 * The launch cannot judge every floor: it asks 0.61 g of tyres that the
 * stop will ask 0.69 g of, a heavy car or weak motors ask less, and a tyre
 * well below its peak slips about the same on any floor. So on leg A, once
 * the floor's launch judge is done, a car that can asks the floor directly:
 * the front axle brakes against the rear, which drives as hard (the car
 * holds its speed), the pair ramping until the front tyres carry
 * GA_PR_A_MAX of their load. Against that force the slip between the axles
 * bends the way a tyre's does on its way to the peak, and how soon it bends
 * says where the peak is: a brush tyre slips 2 mu / C (1 - sqrt(1 - a / mu))
 * at a = force / load, and the fit finds mu and C both (opus_probe_mu).
 * The rear-over-front slip needs no car speed (it cancels), and the forces
 * come from the drives' measured current. The stop is then planned on the
 * mu found, as on a launch judged slippery (FW-16), and the launch's own
 * verdict is kept only for if the probe cannot judge. */
#define GA_PR_T0           1.6f      /* s into leg A: after the launch judge (GA_MU_CAL_T1) */
#define GA_PR_PRE_S        0.05f     /* s of samples before the ramp: its zero      */
#define GA_PR_RAMP_S       0.40f     /* s to ramp the pair to its top               */
#define GA_PR_A_MAX        0.65f     /* front tyres' force / load at the top: near
                                        what the stop will ask (0.69)              */
#define GA_PR_DT_S         0.0095f   /* at most one sample per this (any tick rate) */
#define GA_PR_ABORT_FRAC   0.75f     /* stop the ramp when a wheel slips this share of
                                        slip_max past its cruise offset           */
#define GA_PR_BAD_SLIP     0.20f     /* a wheel this far off the car's speed is not measuring it */
#define GA_PR_MIN_N        12        /* samples the fit needs                       */
#define GA_PR_SETTLE_S     0.05f     /* s past the release (and the delay) the
                                        odometer stays on v_est                    */
/* A sample the fitted tyre misses by this many times the median miss is
 * dropped and the fit run again: as the front axle's torque reverses, the
 * motor crosses its gear lash and the motor-shaft speed the drive reports
 * leaves the wheel's for a tick or two (1 % of slip, against 0.03 % noise). */
#define GA_PR_OUTLIER      5.0f
/* What the stop plans on, of the probe's mu, where FW-16's launch judge
 * uses GA_MU_USE: the brush curve bends sooner than a tyre that holds its
 * slope longer, so a brush fit short of the peak reads the peak high. The
 * twin's tyre reads 7-10 % high at every grip (each axle fitted alone on
 * its true slip: the front 1.35 against 1.15); real tyres are more linear
 * than a brush too. */
#define GA_PR_USE          0.78f
#define GA_PR_MU_MIN       0.30f     /* the fit's range                             */
#define GA_PR_MU_MAX       2.00f

/* ------------------------------------------------- routes (NAV-02..04) --
 * A route's limits (a_lat_max, a_drive, a_brake) come with the route; these
 * are how the mission flies one. */
#define RT_STRAIGHT_K      0.01f     /* 1/m: below this the route counts as straight
                                        (the trim and left/right scale learn there) */
#define RT_SEED_POS_M      0.10f     /* how well the car is put on a route's start  */
#define RT_SEED_PSI_RAD    0.05f     /* ...and pointed along it (3 deg)             */
#define RT_POSE_WAIT_S     2.0f      /* ARMED waits this long for a pose, then FA_NAV */
#define RT_MU_REPLAN       0.03f     /* replan the speeds when mu_est moves this much */
#define RT_V_MIN           0.3f      /* the plan never asks less (it would never get there) */
/* The speed plan's lateral limit, of the route's a_lat_max: the tracker's
 * yaw rate overshoots the route's curvature by a few per cent where a short
 * spiral hands over (3.3 against 3.0 m/s^2 at the hairpin's 0.5 m spirals),
 * and NAV-03 keeps the peak under the route's limit. */
#define RT_LAT_USE         0.9f

/* --------------------------------------------------------------- endgame --*/
#define EN_CREEP_M         0.040f    /* below this remaining distance, switch to creep */
#define EN_CREEP_V         0.30f     /* m/s ceiling while creeping */
#define EN_CREEP_KV        7.5f      /* 1/s, v_ref = KV * s_remaining */
#define EN_DONE_M          0.001f    /* stop tolerance against our own odometer */
#define EN_DONE_V          0.02f     /* m/s */
#define EN_HOLD_S          1.0f      /* zero motion for this long before declaring DONE */
/* A creep this close that has stopped moving is done: inside a few mm the
 * position loop asks for less speed than it takes to break the car free
 * (7 mm/s at 1 mm), and it would wait there for ever. The miss is reported. */
#define EN_STALL_M         0.005f
#define EN_STALL_S         0.5f
#define EN_DEAD_TIME_S     0.020f    /* loop dead time, led out of the braking profile */
/* Speed low-pass for the creep loop and the stop tests: 23.2 ms is the old
 * per-tick 0.35 blend at 100 Hz (FW-05). */
#define EN_VFILT_TAU_S     0.023214f

/* ------------------------------------------------- speed and delay (FW-13) --
 * The loops close on the car's speed, not on its wheels'. Every wheel of the
 * FOC car is driven, the odometry pair included, and a driven wheel answers
 * its own torque within a tick: closed on the wheel, a brake demand near the
 * grip limit slows the front pair, the loop reads the car as slowing and
 * backs off, the wheels spin up, and it brakes again — a 14 Hz cycle that
 * locked the fronts once the command came a period late (TIM-02). So the
 * speed is the accelerometer's integral, pulled toward the odometer with a
 * time constant; while the two disagree by more than tyre slip can explain,
 * the odometer is the one that is wrong, and the pull goes slack. */
#define EN_VOBS_TAU_S      0.10f     /* s, odometer correction of the speed estimate */
#define EN_VOBS_SLIP_TAU_S 2.0f      /* s, the same while the wheels disagree        */
#define EN_VOBS_BAND_MPS   0.15f     /* m/s, + EN_VOBS_BAND_FRAC of speed: tyre slip */
#define EN_VOBS_BAND_FRAC  0.05f
/* The accelerometer reads gravity as well as the car's acceleration: braking
 * pitches the nose down ~1.5 deg, which reads as 0.25 m/s^2 of extra decel —
 * enough to drag the estimate onto the very wheels it should be judging. The
 * pitch is the gyro's integral, leaking away so a gyro bias cannot build up
 * (a steady slope is the odometer correction's business). Not while the car
 * turns: rolled into the turn, the body's y axis takes yaw rate x sin(roll)
 * (0.75 rad/s x 0.7 deg on the oval at 3 m/s, ~0.01 rad/s; a die rolled in
 * its mount adds the same) and the integral builds a pitch that is not there,
 * 0.17 m/s^2 by the end of a half-lap, that leaks out over the next two
 * seconds as a speed error and, with FW-22, as a bias (FW-22). */
#define EN_PITCH_TAU_S     2.0f
#define EN_PITCH_AY_MAX    1.0f      /* m/s^2: the gyro's pitch rate is taken below this lateral */
/* The forward accelerometer's bias (FW-22): the part's own offset, the die
 * pitched in the chassis (2 deg is 0.34 m/s^2 of gravity), the floor's slope.
 * Proportional alone, the pull above leaves the speed bias*tau off, and once
 * it goes slack (a stop's slip) the bias integrates straight into the speed
 * and the odometer the stop is judged on. Read at rest while arming, then
 * kept by the odometer while the car runs steadily: the spot it armed on may
 * tilt where the road does not (the twin's spawn arrow lifts its front axle
 * 1.2 deg), and the pitch above leaks a lasting attitude change away. Held
 * while it launches, brakes, slips or probes — exactly when the speed leans
 * on the accelerometer. */
#define EN_AB_TAU_S        1.0f      /* s, the bias estimate's time constant      */
#define EN_AB_AX_MAX       1.5f      /* m/s^2: learned only below this acceleration */
#define EN_AB_AY_MAX       1.0f      /* m/s^2, and this lateral: in a turn the body's
                                        sideslip puts v*psi_dot*sin(beta) forward */
#define EN_AB_MAX          1.0f      /* m/s^2 clamp (6 deg of mount)              */
/* Load moves onto the front axle as the body pitches, not when the brake
 * goes on: the allocator's grip budget sees the measured acceleration
 * through this lag. */
#define EN_LOAD_TAU_S      0.05f
/* (Commands still in flight: each acts cmd_delay_s, a TtParams field, after
 * its tick. The speed loop, the stop profile and the heading loops work on
 * the state predicted that far ahead from what was already asked for; see
 * predict() in opus_mission.c.) */
/* (EN_REGEN_CAP_N is gone: how hard the motors can brake is the regen_*
 * capability in TtParams — for this car's ESC strong at speed and fading to
 * nothing at rest, which is exactly why the friction brake still finishes
 * the stop.) */

/* --------------------------------------------- the way ahead (NAV-05) --
 * A car with a multizone ranger (core/tt_clear.h) measures how far the
 * path ahead is clear, and the speed loop is held to what that distance
 * allows: v <= sqrt(2 a_brake (clear - stop - the travel over the delay)).
 * Short of GA_CL_STOP_M the car holds, and goes on once the way has opened
 * by GA_CL_HYST_M more. A hold longer than SF_BLOCK_S, or a return within
 * GA_CL_CONTACT_M of the nose, is a fault (FA_OBSTACLE).
 *
 * Held and below GA_CL_STAND_V the car STANDS: the speed loop is dropped
 * and the motors command nothing, so it neither creeps back on the loop
 * settling at zero nor forward on the drag feed-forward. What is in the
 * way may be coming: between two frames the clearance should shrink by
 * the car's own travel, and what it shrinks beyond that is the closing
 * speed, which the stop profile then allows for (the way v <= -v_c +
 * sqrt(v_c^2 + 2 a d)) and which eats the clearance over the next frame
 * and the delay. A clearance that jumps by more than GA_CL_CLOSE_JUMP_M
 * is a different thing in the way, not the same one moving. */
#define GA_CL_STAND_V      0.05f     /* m/s: held and slower than this = stand  */
#define GA_CL_CLOSE_JUMP_M 1.0f      /* a larger change between frames = a new
                                        target, closing speed starts again     */
#define GA_CL_FRAME_MAX_S  0.2f      /* frames further apart carry no closing
                                        speed (a frame was missed)             */
#define GA_CL_MARGIN_W_M   0.08f     /* corridor half width beyond the body's:
                                        the tracker's error and the tyres     */
#define GA_CL_FLOOR_M      0.03f     /* a return within this of the floor is it */
#define GA_CL_ROOF_M       0.15f     /* above this over the origin the car passes
                                        under it (its top is 0.10 up)            */
#define GA_CL_STOP_M       0.15f     /* the nose stops this short of the way   */
#define GA_CL_HYST_M       0.10f     /* ...and goes on when it opens this more */
#define GA_CL_CONTACT_M    0.02f     /* nearer is a touch                      */
#define GA_CL_LOOK_M       5.0f      /* the corridor's reach without a route   */
#define GA_CL_PATH_DS      0.25f     /* m between the corridor's route points  */
#define GA_CL_PATH_N       20        /* ...and how many (5 m)                   */
#define SF_BLOCK_S         20.0f     /* held this long: fault                  */

/* ------------------------------------------------- the detour (NAV-08) --
 * The ranger's frames build a local map (core/tt_grid.h) the planner
 * (core/tt_detour.h) checks the path ahead against; its own settings are
 * the planner's defaults, the disc from the corridor's half width. */
#define GA_DT_CELL_M       0.05f     /* the map's cell                          */
#define GA_DT_AHEAD_M      1.8f      /* the map's window is centred this far ahead:
                                        4.8 m of it from 0.6 m behind the car  */
#define GA_DT_PERIOD_S     0.2f      /* the path ahead is checked at 5 Hz       */
#define GA_DT_SPREAD_M     0.15f     /* the planner's disc beyond the corridor's:
                                        a zone's return lands up to half a zone
                                        (0.16 m at 3 m) from what it is on      */

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
/* An encoder rate this high (~4.5x cruise) is a bad read, not motion. A
 * speed, not a count rate: the same car counts 4096 per wheel turn with the
 * encoder on the wheel and 20 480 with it on the motor shaft (SEN-11). Was
 * 400 000 counts/s, which is this at 4096. */
#define SF_GLITCH_MPS      20.0f
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
#define FA_SAFETY          0x4000    /* the safety layer (FW-08) stopped the car */
#define FA_GRIP_LOW        0x8000    /* warning: the floor cannot stop the car in
                                        MI_BRAKE_M; the stop starts early (FW-16) */
#define FA_NAV             0x10000   /* following a route without a pose, or
                                        the seeded start failed its check (NAV-04) */

#endif /* OPUS_MISSION_CFG_H */
