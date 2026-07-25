/*
 * diffdrive_control.h — portable closed-loop controller for a two-wheel
 * differential-drive robot.
 *
 * This is firmware-grade logic: it includes only pid.h and the C standard
 * library, and knows nothing about Unity or the sim ABI. The sim target (and
 * later, a real MCU target) adapts I/O to this interface.
 *
 * Command convention (setpoints): linear velocity (m/s) and yaw rate (rad/s).
 * The controller mixes these into per-wheel angular-velocity targets and runs
 * an independent PID per wheel.
 */
#ifndef DIFFDRIVE_CONTROL_H
#define DIFFDRIVE_CONTROL_H

#include "pid.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct DiffDriveConfig {
    float wheel_radius_m;   /* wheel radius (m)                         */
    float track_width_m;    /* distance between left and right wheels   */
    float kp, ki, kd;       /* wheel-speed PID gains                    */
} DiffDriveConfig;

typedef struct DiffDriveController {
    DiffDriveConfig cfg;
    Pid pid_left;
    Pid pid_right;

    /* Telemetry snapshot from the last update(). */
    float target_wl, target_wr;   /* wheel target angular vel (rad/s) */
    float cmd_left, cmd_right;     /* actuator commands [-1, 1]        */
} DiffDriveController;

void diffdrive_init(DiffDriveController* c, const DiffDriveConfig* cfg);

/*
 * Run one control step.
 *   lin_mps  : commanded forward velocity (m/s)
 *   yaw_rps  : commanded yaw rate (rad/s)
 *   meas_wl  : measured left  wheel angular velocity (rad/s)
 *   meas_wr  : measured right wheel angular velocity (rad/s)
 *   dt_s     : control period (s)
 *   out_cmd  : [2] normalized commands, left then right, each in [-1, 1]
 */
void diffdrive_update(DiffDriveController* c,
                      float lin_mps, float yaw_rps,
                      float meas_wl, float meas_wr,
                      float dt_s,
                      float out_cmd[2]);

#ifdef __cplusplus
}
#endif

#endif /* DIFFDRIVE_CONTROL_H */
