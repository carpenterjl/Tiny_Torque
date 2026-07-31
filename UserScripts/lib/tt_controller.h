/*
 * tt_controller.h — the Tiny Torque controller helper library.
 *
 * This sits on top of controller_api.h (the raw ABI the game and your DLL agree
 * on) and does the fiddly parts for you: finding the motors, converting wheel
 * speeds into m/s, reading a named sensor, clamping a steering command.
 *
 * You do not have to use any of it. Everything here is a thin, readable wrapper
 * over fields that are already in CtrlInputs / CtrlOutputs, and a controller
 * that ignores this header entirely is just as valid — car_sensors.c in the
 * game's own Controllers/ folder is written that way on purpose.
 *
 * HEADER-ONLY, on purpose: every function is `static inline`, so there is
 * nothing to link and nothing to keep in sync. Include it and go.
 *
 *   #include "tt_controller.h"
 *
 * You should not need to edit this file. If you do, remember it is shared by
 * every folder under UserScripts/ — a change here changes all of them.
 */
#ifndef TT_CONTROLLER_H
#define TT_CONTROLLER_H

#include "controller_api.h"

#include <string.h>
#include <math.h>

/* Silence "unused parameter" without deleting the parameter. */
#define TT_UNUSED(x) ((void)(x))

/* Wheel radius of the stock car, in metres. Wheel angular velocity arrives in
 * rad/s, so ground speed = rad/s * this. Matches CarVehicle.wheelRadius on the
 * Unity side; if you build a car with different wheels, change it here. */
#define TT_WHEEL_RADIUS_M 0.35f

#define TT_MAX_SENSORS 32
#define TT_MAX_MOTORS  8

/* Setpoint slots. These are the OPERATOR's commands — what the person holding
 * the controller is asking for — not what your code has decided to do. See the
 * guide: in a Simulate Controller drive you still hold the throttle, and your
 * code decides how to deliver it. A fully autonomous controller ignores these
 * and works from sensors alone. */
#define TT_SP_SPEED 0   /* requested forward speed, m/s   */
#define TT_SP_STEER 1   /* requested steering, -1 .. +1   */

/* ─────────────────────────────── small math ─────────────────────────────── */

static inline float tt_clamp(float v, float lo, float hi) {
    return v < lo ? lo : (v > hi ? hi : v);
}

static inline float tt_clamp1(float v) { return tt_clamp(v, -1.0f, 1.0f); }

/* Blend from a to b. t is clamped, so tt_lerp(a,b,5) is just b. */
static inline float tt_lerp(float a, float b, float t) {
    t = tt_clamp(t, 0.0f, 1.0f);
    return a + (b - a) * t;
}

/* Zero out small values — useful on a noisy stick or a jittery error term. */
static inline float tt_deadzone(float v, float width) {
    return (v > -width && v < width) ? 0.0f : v;
}

/* ──────────────────────────────── PID ───────────────────────────────────── */

/*
 * A textbook PID with two things beginners usually find out the hard way
 * already handled: the integrator is clamped (so it cannot wind up while the
 * car is stuck against a wall and then slam the throttle when it comes free),
 * and the derivative is taken on the MEASUREMENT rather than the error (so a
 * step change in your target does not produce a spike).
 */
typedef struct TtPid {
    float kp, ki, kd;
    float i_min, i_max;   /* integrator clamp; set both to 0 to disable I */
    float integral;
    float prev_meas;
    int   primed;         /* first update has no valid previous measurement */
} TtPid;

static inline void tt_pid_init(TtPid* p, float kp, float ki, float kd) {
    memset(p, 0, sizeof(*p));
    p->kp = kp; p->ki = ki; p->kd = kd;
    p->i_min = -1.0f; p->i_max = 1.0f;
}

/* Set the integrator limits. Units are "output", so if your PID drives volts,
 * clamp it in volts. */
static inline void tt_pid_limits(TtPid* p, float lo, float hi) {
    p->i_min = lo; p->i_max = hi;
}

static inline void tt_pid_reset(TtPid* p) {
    p->integral = 0.0f;
    p->primed = 0;
}

static inline float tt_pid_update(TtPid* p, float target, float measured, float dt) {
    float err = target - measured;
    if (dt <= 0.0f) return p->kp * err;

    p->integral += err * dt;
    p->integral = tt_clamp(p->integral, p->i_min, p->i_max);

    float deriv = 0.0f;
    if (p->primed) deriv = -(measured - p->prev_meas) / dt;   /* on measurement */
    p->prev_meas = measured;
    p->primed = 1;

    return p->kp * err + p->ki * p->integral + p->kd * deriv;
}

/* ──────────────────────────── the car context ───────────────────────────── */

/*
 * What the game told us about this particular car, unpacked once so ctrl_step
 * does not have to search the manifest every tick.
 *
 * The manifest is the whole point of ctrl_configure: cars in this game are
 * assembled from parts, so the number of motors, which actuator slot each one
 * answers to, how many volts it will take and what sensors are bolted on are
 * all per-car facts your code is TOLD rather than facts it can assume.
 */
typedef struct TtCar {
    SensorInfo sensors[TT_MAX_SENSORS];
    int        sensor_count;

    int   motor_actuator[TT_MAX_MOTORS];  /* actuator[] slot for each motor  */
    float motor_vmax[TT_MAX_MOTORS];      /* its +max voltage                */
    int   motor_count;

    int   configured;   /* 0 until ctrl_configure ran — see tt_car_configure */
} TtCar;

/*
 * Call this from ctrl_configure. Safe to call with count 0 or a NULL array:
 * a car with no configurable sensors is a legitimate car, and the helpers below
 * all degrade to sensible answers rather than reading past the end of anything.
 */
static inline void tt_car_configure(TtCar* car, const SensorInfo* sensors, int count) {
    memset(car, 0, sizeof(*car));
    if (sensors == 0 || count <= 0) { car->configured = 1; return; }
    if (count > TT_MAX_SENSORS) count = TT_MAX_SENSORS;

    car->sensor_count = count;
    for (int i = 0; i < count; i++) {
        car->sensors[i] = sensors[i];
        if (sensors[i].type == SENSOR_MOTOR && car->motor_count < TT_MAX_MOTORS) {
            car->motor_actuator[car->motor_count] = sensors[i].actuator_index;
            /* range_max is that motor's +maxVoltage. The 24 V fallback matches
             * the stock pack, for a manifest that somehow reported nothing. */
            car->motor_vmax[car->motor_count] =
                sensors[i].range_max > 0.1f ? sensors[i].range_max : 24.0f;
            car->motor_count++;
        }
    }
    car->configured = 1;
}

/* Index of a sensor by name (as typed in the garage), or -1. */
static inline int tt_sensor_index(const TtCar* car, const char* name) {
    if (name == 0) return -1;
    for (int i = 0; i < car->sensor_count; i++)
        if (strncmp(car->sensors[i].name, name, sizeof(car->sensors[i].name)) == 0)
            return i;
    return -1;
}

/* First sensor of a given SENSOR_* type, or -1. Handy when you do not care what
 * the part is called, only that there is one. */
static inline int tt_sensor_of_type(const TtCar* car, int type) {
    for (int i = 0; i < car->sensor_count; i++)
        if (car->sensors[i].type == type) return i;
    return -1;
}

/*
 * One float from a sensor's slice of the flat data array. `slot` is the offset
 * within that sensor (0 for the first value it publishes); see the per-type
 * layout table in controller_api.h.
 *
 * Returns `fallback` for anything missing — no sensor, no data this tick, index
 * out of range. Sensor reads are the most common place a controller reads
 * garbage and then drives into a wall, so every path out of here is a value you
 * chose.
 */
static inline float tt_sensor_value(const TtCar* car, const CtrlInputs* in,
                                    int sensor_index, int slot, float fallback) {
    if (in == 0 || in->sensor_data == 0) return fallback;
    if (sensor_index < 0 || sensor_index >= car->sensor_count) return fallback;
    const SensorInfo* s = &car->sensors[sensor_index];
    if (slot < 0 || slot >= s->data_count) return fallback;
    int idx = s->data_offset + slot;
    if (idx < 0 || idx >= in->sensor_data_len) return fallback;
    return in->sensor_data[idx];
}

/*
 * Distance in metres from a named time-of-flight sensor, or `fallback` if there
 * is no such sensor. A ToF that sees nothing reports its configured range_max,
 * NOT zero — "far away" and "broken" must not look the same to your code.
 */
static inline float tt_tof(const TtCar* car, const CtrlInputs* in,
                           const char* name, float fallback) {
    int i = tt_sensor_index(car, name);
    if (i < 0 || car->sensors[i].type != SENSOR_TOF) return fallback;
    return tt_sensor_value(car, in, i, 0, fallback);
}

/* Measured ground speed in m/s, averaged over the four wheels. Signed: negative
 * means the wheels are turning backwards. */
static inline float tt_speed(const CtrlInputs* in) {
    if (in == 0) return 0.0f;
    float avg = 0.25f * (in->wheel_vel[0] + in->wheel_vel[1] +
                         in->wheel_vel[2] + in->wheel_vel[3]);
    return avg * TT_WHEEL_RADIUS_M;
}

/* ─────────────────────────────── outputs ────────────────────────────────── */

/*
 * Send the same voltage to every drive motor, clamped per motor to its own
 * limit. Positive drives forward, negative reverses.
 *
 * Returns the voltage actually applied to the last motor — useful as a debug
 * channel, because it is what the car got rather than what you asked for.
 */
static inline float tt_drive_volts(const TtCar* car, CtrlOutputs* out, float volts) {
    float applied = 0.0f;
    for (int m = 0; m < car->motor_count; m++) {
        int slot = car->motor_actuator[m];
        if (slot < 0 || slot >= 8) continue;      /* a manifest we don't trust */
        applied = tt_clamp(volts, -car->motor_vmax[m], car->motor_vmax[m]);
        out->actuator[slot] = applied;
    }
    return applied;
}

/*
 * Same, but as a fraction of each motor's maximum: -1 is full reverse, +1 is
 * full forward. Convenient when your control law produces a normalised effort
 * and you would rather not think in volts.
 */
static inline float tt_drive(const TtCar* car, CtrlOutputs* out, float effort) {
    effort = tt_clamp1(effort);
    float applied = 0.0f;
    for (int m = 0; m < car->motor_count; m++) {
        int slot = car->motor_actuator[m];
        if (slot < 0 || slot >= 8) continue;
        applied = effort * car->motor_vmax[m];
        out->actuator[slot] = applied;
    }
    return applied;
}

/* Front-wheel steering, -1 (full left) .. +1 (full right). */
static inline void tt_steer(CtrlOutputs* out, float steer) {
    out->actuator[CTRL_STEER_ACTUATOR] = tt_clamp1(steer);
}

/* Friction brake, 0 (off) .. 1 (full). Independent of motor voltage: you can
 * brake and drive at once, and the car will not thank you for it. */
static inline void tt_brake(CtrlOutputs* out, float brake) {
    out->actuator[CTRL_BRAKE_ACTUATOR] = tt_clamp(brake, 0.0f, 1.0f);
}

/* ─────────────────────────────── camera ─────────────────────────────────── */

/*
 * One grayscale pixel, 0 (black) .. 255 (white), or 0 if there is no camera or
 * the coordinates are off-frame.
 *
 * ROW 0 IS THE TOP of the image (ABI v4). y counts DOWN from the top edge, the
 * way a screen coordinate does — so the road ahead is at large y, and the sky
 * is at small y.
 */
static inline int tt_cam_pixel(const CtrlInputs* in, int x, int y) {
    if (in == 0 || in->cam_pixels == 0) return 0;
    if (x < 0 || y < 0 || x >= in->cam_width || y >= in->cam_height) return 0;
    return in->cam_pixels[(long)y * in->cam_width + x];
}

/*
 * Mean brightness (0..255) of a rectangle, in pixels, clipped to the frame.
 * Returns -1 when there is no camera or the rectangle misses the frame
 * entirely — again, distinguishable from "it is very dark".
 */
static inline float tt_cam_brightness(const CtrlInputs* in,
                                      int x0, int y0, int x1, int y1) {
    if (in == 0 || in->cam_pixels == 0 || in->cam_width <= 0 || in->cam_height <= 0)
        return -1.0f;
    if (x0 > x1) { int t = x0; x0 = x1; x1 = t; }
    if (y0 > y1) { int t = y0; y0 = y1; y1 = t; }
    if (x0 < 0) x0 = 0;
    if (y0 < 0) y0 = 0;
    if (x1 > in->cam_width  - 1) x1 = in->cam_width  - 1;
    if (y1 > in->cam_height - 1) y1 = in->cam_height - 1;
    if (x0 > x1 || y0 > y1) return -1.0f;

    long sum = 0, n = 0;
    for (int y = y0; y <= y1; y++) {
        const unsigned char* row = in->cam_pixels + (long)y * in->cam_width;
        for (int x = x0; x <= x1; x++) { sum += row[x]; n++; }
    }
    return n > 0 ? (float)sum / (float)n : -1.0f;
}

/*
 * "Which way is the bright side?" over the bottom `rows` rows of the frame —
 * the part looking at the road just ahead rather than at the horizon.
 *
 * Returns roughly -1 .. +1: negative when the left half is brighter, positive
 * when the right is, 0 when they match or there is no camera. Steering toward
 * the brighter side follows a pale road across dark ground; negate it to follow
 * a dark line across pale ground.
 */
static inline float tt_cam_balance(const CtrlInputs* in, int rows) {
    if (in == 0 || in->cam_pixels == 0 || in->cam_width < 2 || in->cam_height <= 0)
        return 0.0f;
    if (rows <= 0 || rows > in->cam_height) rows = in->cam_height;

    int y0 = in->cam_height - rows;          /* row 0 is the TOP, so the road */
    int y1 = in->cam_height - 1;             /* ahead is at the BOTTOM        */
    int half = in->cam_width / 2;

    float l = tt_cam_brightness(in, 0, y0, half - 1, y1);
    float r = tt_cam_brightness(in, half, y0, in->cam_width - 1, y1);
    if (l < 0.0f || r < 0.0f) return 0.0f;
    return (r - l) / 255.0f;
}

#endif /* TT_CONTROLLER_H */
