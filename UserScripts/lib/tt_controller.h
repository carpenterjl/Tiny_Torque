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

/* ──────────────────────── kept frames (2D, buffered) ─────────────────────── */

/*
 * Everything above reads the LIVE frame — in->cam_pixels, which is the game's
 * own buffer handed to you by pointer and dead the instant ctrl_step returns.
 * That is fine for "which side is brighter". It is no good at all for anything
 * that has to compare this frame with the last one, or scan an image over more
 * than one tick, or keep a picture of the moment something went wrong.
 *
 * TtCamera is the fix: your own storage, on your side of the boundary, holding
 * the last few frames as plain 2D arrays.
 *
 *     static TtCamera g_cam;                  // ~48 KB of static, see below
 *
 *     CTRL_EXPORT int ctrl_init(float hz) { tt_cam_init(&g_cam); ... }
 *
 *     CTRL_EXPORT void ctrl_step(const CtrlInputs* in, CtrlOutputs* out) {
 *         if (tt_cam_update(&g_cam, in)) {
 *             const TtFrame* f = tt_cam_newest(&g_cam);
 *             int p = f->px[10][32];          // row 10 from the TOP, column 32
 *             ...                             // your perception, once per frame
 *         }
 *     }
 *
 * ─── why the `if` ─────────────────────────────────────────────────────────
 *
 * ctrl_step runs at 100 Hz. The camera captures at its own rate — 10 Hz on the
 * Opus Vector — so you are handed the SAME picture about ten times in a row,
 * and the ABI carries no frame counter to tell you which reads are new.
 * tt_cam_update works it out by comparing the bytes, and returns 1 only when
 * the image actually changed. Putting your image processing behind that test
 * is the difference between running it 10 times a second and 100.
 *
 * The honest edge of that: two identical captures are indistinguishable from
 * one capture. Park facing a blank wall and the sequence number stops
 * advancing, because nothing about the picture is new. For a controller that
 * reacts to what it sees, that is the right answer; if you need to know the
 * camera is still alive, watch in->time_s, not this.
 *
 * ─── cost ─────────────────────────────────────────────────────────────────
 *
 * A TtFrame is a fixed 128x96 block whatever the camera's real size, so the
 * [y][x] indexing needs no arithmetic and no allocation: 12 KB each, times
 * TT_CAM_FRAMES. The default 4 is about 48 KB of static memory. Define
 * TT_CAM_FRAMES before including this header to keep more (a longer history)
 * or fewer (less memory). 1 is legal and means "the current frame only".
 *
 * There is no malloc anywhere in here, deliberately. The DLL is torn down and
 * rebuilt on every Build & Reload, and a buffer that has to be freed is a leak
 * per build waiting to be forgotten.
 */

/* The camera's own clamps, from CameraSensor: width 8..128, height 8..96. A
 * frame larger than this cannot arrive from the game — but it is checked
 * anyway rather than trusted, because a silently cropped image is a bug you
 * find by wondering why the right-hand side of the world is missing. */
#define TT_CAM_MAX_W 128
#define TT_CAM_MAX_H 96

#ifndef TT_CAM_FRAMES
#define TT_CAM_FRAMES 4
#endif

/*
 * One kept frame. `px` is the picture: px[y][x], 0 (black) .. 255 (white),
 * ROW 0 IS THE TOP exactly as the live buffer is.
 *
 * Only [0..height) x [0..width) is the image. The rest of the block is zero
 * and stays zero — it is padding that exists so the row stride is a constant.
 * Read px[y][x] outside the real image and you get black, not garbage, but you
 * are still reading something that is not a picture: use tt_frame_px when x
 * and y come from a calculation rather than from a loop you wrote the bounds
 * of yourself.
 */
typedef struct TtFrame {
    unsigned char px[TT_CAM_MAX_H][TT_CAM_MAX_W];
    int   width, height;   /* the real image size inside px                  */
    float time_s;          /* in->time_s when this frame was first seen      */
    unsigned long seq;     /* 1 for the first frame kept, then 2, 3, ...      */
} TtFrame;

/*
 * A ring of the last TT_CAM_FRAMES distinct frames. Declare one `static` at
 * file scope and initialise it in ctrl_init.
 */
typedef struct TtCamera {
    TtFrame frames[TT_CAM_FRAMES];
    int  newest;           /* ring index of the newest frame                 */
    int  held;             /* how many frames are valid (0..TT_CAM_FRAMES)   */
    unsigned long seq;     /* distinct frames seen since tt_cam_init         */
    int  oversize;         /* set if a frame arrived too big to keep         */
} TtCamera;

/* Zeroes the ring and forgets everything. Call from ctrl_init — the struct is
 * static, so on a hot reload it is fresh anyway, but a controller that is ever
 * reset mid-session must not carry a stale history across it. */
static inline void tt_cam_init(TtCamera* c) {
    if (c == 0) return;
    memset(c, 0, sizeof(*c));
    c->newest = -1;
}

/* How many frames are being held right now, 0 .. TT_CAM_FRAMES. */
static inline int tt_cam_held(const TtCamera* c) { return c == 0 ? 0 : c->held; }

/*
 * A kept frame by age: 0 is the newest, 1 the one before it, and so on.
 * Returns NULL when that many frames have not been seen yet, or when age is
 * outside the ring — so `const TtFrame* prev = tt_cam_frame(&g_cam, 1);
 * if (prev) ...` is the shape of every comparison against the past.
 */
static inline const TtFrame* tt_cam_frame(const TtCamera* c, int age) {
    if (c == 0 || age < 0 || age >= c->held) return 0;
    int i = c->newest - age;
    while (i < 0) i += TT_CAM_FRAMES;
    return &c->frames[i];
}

/* The newest kept frame, or NULL before the first one arrives. */
static inline const TtFrame* tt_cam_newest(const TtCamera* c) {
    return tt_cam_frame(c, 0);
}

/*
 * Take a copy of the live frame if it is one you have not seen.
 *
 * Returns 1 when a new frame was stored (this is the tick to do your image
 * work on), 0 when the picture is unchanged, when there is no camera fitted,
 * or when the frame is too large for TtFrame to hold — that last case also
 * sets c->oversize, which stays set, so it is worth reporting on a debug
 * channel once rather than testing every tick.
 */
static inline int tt_cam_update(TtCamera* c, const CtrlInputs* in) {
    if (c == 0 || in == 0 || in->cam_pixels == 0) return 0;

    int w = in->cam_width, h = in->cam_height;
    if (w <= 0 || h <= 0) return 0;
    if (w > TT_CAM_MAX_W || h > TT_CAM_MAX_H) { c->oversize = 1; return 0; }

    /* Same size and same bytes as the newest kept frame? Then the camera has
     * not captured since — the game hands the identical buffer over and over
     * between captures, and there is no flag in the ABI that says so. */
    const TtFrame* last = tt_cam_newest(c);
    if (last != 0 && last->width == w && last->height == h) {
        int same = 1;
        for (int y = 0; y < h; y++)
            if (memcmp(last->px[y], in->cam_pixels + (long)y * w, (size_t)w) != 0) {
                same = 0;
                break;
            }
        if (same) return 0;
    }

    int i = c->newest + 1;
    if (i >= TT_CAM_FRAMES) i = 0;
    TtFrame* f = &c->frames[i];

    /* Clear only the region the last tenant used but this frame will not, so a
     * camera that shrinks cannot leave the old image showing through the
     * padding. The common case — same size every frame — copies and no more. */
    if (f->width > w || f->height > h) memset(f->px, 0, sizeof(f->px));

    for (int y = 0; y < h; y++)
        memcpy(f->px[y], in->cam_pixels + (long)y * w, (size_t)w);

    f->width  = w;
    f->height = h;
    f->time_s = in->time_s;
    f->seq    = ++c->seq;

    c->newest = i;
    if (c->held < TT_CAM_FRAMES) c->held++;
    return 1;
}

/* One pixel of a kept frame, bounds-checked against the REAL image rather than
 * the padded block: 0 for off-frame, for a NULL frame, and for black. Use
 * f->px[y][x] directly when you control the loop bounds and want the speed. */
static inline int tt_frame_px(const TtFrame* f, int x, int y) {
    if (f == 0) return 0;
    if (x < 0 || y < 0 || x >= f->width || y >= f->height) return 0;
    return f->px[y][x];
}

/*
 * Mean brightness (0..255) of a rectangle of a kept frame, clipped to the
 * image. -1 when the frame is NULL or the rectangle misses it entirely — the
 * same "not there" / "very dark" distinction tt_cam_brightness makes on the
 * live buffer, because the mistake it prevents is the same one.
 */
static inline float tt_frame_mean(const TtFrame* f,
                                  int x0, int y0, int x1, int y1) {
    if (f == 0 || f->width <= 0 || f->height <= 0) return -1.0f;
    if (x0 > x1) { int t = x0; x0 = x1; x1 = t; }
    if (y0 > y1) { int t = y0; y0 = y1; y1 = t; }
    if (x0 < 0) x0 = 0;
    if (y0 < 0) y0 = 0;
    if (x1 > f->width  - 1) x1 = f->width  - 1;
    if (y1 > f->height - 1) y1 = f->height - 1;
    if (x0 > x1 || y0 > y1) return -1.0f;

    long sum = 0, n = 0;
    for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++) { sum += f->px[y][x]; n++; }
    return n > 0 ? (float)sum / (float)n : -1.0f;
}

/*
 * Seconds since the newest kept frame was captured, or -1 with nothing kept.
 *
 * Worth watching once: at a 10 Hz camera and a 100 Hz control loop this swings
 * between 0 and 0.1 s every frame, and a controller that steers from an image
 * is steering from something up to a tenth of a second out of date. That is
 * not a bug in the buffering, it is the camera, and it is the same lag a real
 * one has.
 */
static inline float tt_cam_age(const TtCamera* c, const CtrlInputs* in) {
    const TtFrame* f = tt_cam_newest(c);
    if (f == 0 || in == 0) return -1.0f;
    return in->time_s - f->time_s;
}

#endif /* TT_CONTROLLER_H */
