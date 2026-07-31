/*
 * my_controller.c — your car's firmware. Edit this file.
 *
 * It already works: build it, run it, and the car holds the speed you ask for,
 * steers where you point it, and stops before it hits things. Change a number,
 * press Build & Reload in the game, and watch what that did — you do not have
 * to leave the game or open a terminal at any point.
 *
 * ─── the five functions ───────────────────────────────────────────────────
 *
 *   ctrl_init()             once, when the car is created. Set up your state.
 *   ctrl_configure()        once, right after init. The game hands you this
 *                           car's parts list: how many motors, which slots they
 *                           answer to, what sensors are bolted on.
 *   ctrl_step()             every control tick (100 Hz). Read inputs, write
 *                           outputs. This is the one you will spend your time in.
 *   ctrl_shutdown()         once, at the end. Release anything you allocated.
 *   ctrl_get_debug_names()  names for your debug channels, so the graphs in the
 *                           game are labelled instead of "debug[3]".
 *
 * All five must exist. The game loads this DLL by looking those names up: if
 * one is missing the car quietly coasts, because there is nothing to ask.
 *
 * ─── who is driving ───────────────────────────────────────────────────────
 *
 * By default your code is a DRIVER'S AID, not an autopilot. `in->setpoint[]`
 * carries what the person at the keyboard is asking for — a target speed and a
 * steering angle — and your job is to deliver it: how hard to push the motors,
 * when to brake, how much of that steering input to actually use.
 *
 * To write a real autopilot instead, ignore setpoint[] and decide from sensors.
 * There is a worked example at the bottom of this file.
 *
 * ─── the one thing that will bite you ─────────────────────────────────────
 *
 * This is real C compiled into a real DLL loaded into the running game. A bad
 * pointer here takes the whole process down with it — in the Unity editor, that
 * means the editor. There is no sandbox. Check your indices; that is why the
 * helpers in tt_controller.h return a fallback instead of reading past an array.
 */

#include "tt_controller.h"


/* ════════════════════════════ TUNING ══════════════════════════════════════
 *
 * Start here. Change a number, Build & Reload, drive. These are the knobs
 * worth having opinions about; everything below is the machinery that uses them.
 */

/* Speed loop. Higher KP responds harder to being off-target; too high and the
 * car surges. KI removes the steady error that KP alone leaves behind (a car
 * holding 9.7 m/s when you asked for 10). KD damps the response. */
static const float SPEED_KP = 6.0f;    /* volts per (m/s) of error */
static const float SPEED_KI = 3.0f;
static const float SPEED_KD = 0.0f;

/* How much of the driver's steering input reaches the wheels. 1.0 is direct.
 * Below 1 is calmer and slower to react; above 1 is twitchy. */
static const float STEER_GAIN = 1.0f;

/* Obstacle braking, from the front ToF sensor. Inside this distance the
 * controller starts braking and stops driving; set it to 0 to switch the whole
 * behaviour off. The braking ramps in — full brakes the moment something is
 * seen would be no fun to drive. */
static const float BRAKE_DISTANCE_M = 6.0f;

/* Name of the front range sensor, as it is called in the garage. If your car
 * has no sensor by that name, obstacle braking simply never triggers. */
static const char* FRONT_SENSOR = "tof_front";


/* ════════════════════════════ STATE ═══════════════════════════════════════
 *
 * Anything that has to survive from one tick to the next lives here. `static`
 * at file scope, because the DLL is loaded once and stepped forever — there is
 * no per-tick place to keep things.
 */

static TtCar g_car;      /* what the game told us about this car's parts */
static TtPid g_speed;    /* the speed loop's memory (integrator, last value) */
static int   g_ready;    /* 0 until ctrl_init has run                       */

/* Debug channels. These are graphed live in the game and written to the
 * telemetry CSV, and they are the fastest way to find out why your controller
 * is doing something strange. Keep this enum and ctrl_get_debug_names() in the
 * same order — the game pairs them up by position. */
enum {
    DBG_TARGET_SPEED = 0,
    DBG_SPEED,
    DBG_VOLTS,
    DBG_STEER,
    DBG_BRAKE,
    DBG_FRONT_DIST,
    DBG_COUNT
};


/* ═══════════════════════════ LIFECYCLE ════════════════════════════════════ */

/*
 * Called once when the car is built, and again on every hot reload.
 *
 * control_rate_hz is how often ctrl_step will be called (100 Hz today). Use it
 * if you need a filter time constant in ticks; you do not need it for anything
 * here, because ctrl_step is handed the exact dt of every tick.
 *
 * Return 0 for "ready". Anything else tells the game you could not start, and
 * the car runs open-loop.
 */
CTRL_EXPORT int ctrl_init(float control_rate_hz) {
    TT_UNUSED(control_rate_hz);

    tt_pid_init(&g_speed, SPEED_KP, SPEED_KI, SPEED_KD);
    /* Clamp the integrator in the units the PID outputs — volts, here. Without
     * this, holding the throttle against a wall winds it up for as long as you
     * are stuck, and the car launches when it comes free. */
    tt_pid_limits(&g_speed, -24.0f, 24.0f);

    g_ready = 1;
    return 0;
}

/*
 * Called once, right after ctrl_init, with this car's parts list.
 *
 * This is optional in the ABI — a controller that does not export it still runs
 * — but without it you would have to guess which actuator slot each motor
 * answers to, and cars in this game are assembled from parts, so guessing is
 * wrong as soon as someone changes a wheel.
 *
 * The pointer is only valid for the duration of this call. tt_car_configure
 * copies what it needs.
 */
CTRL_EXPORT void ctrl_configure(const SensorInfo* sensors, int count) {
    tt_car_configure(&g_car, sensors, count);
}

/*
 * Called once when the car is destroyed, and before every hot reload.
 *
 * If you allocated anything, free it here. The reload path is init → configure
 * → step, so anything you leave behind leaks once per Build & Reload.
 */
CTRL_EXPORT void ctrl_shutdown(void) {
    g_ready = 0;
}

/*
 * Names for your debug[] channels, comma-separated, in the same order as the
 * enum above. The game splits this to label its graphs.
 */
CTRL_EXPORT const char* ctrl_get_debug_names(void) {
    return "target_speed,speed,volts,steer,brake,front_dist";
}


/* ════════════════════════════ THE LOOP ════════════════════════════════════
 *
 * Called 100 times a second. `in` is everything the car can sense; `out` is
 * everything it can do. Between them is your control law.
 *
 * Read the guide (UserScripts/guide.html) for the full list of what is in each,
 * or open Controllers/hal/controller_api.h — it is the actual contract and it
 * is commented.
 */
CTRL_EXPORT void ctrl_step(const CtrlInputs* in, CtrlOutputs* out) {
    /* Always clear the outputs first. They are not zeroed for you, and a struct
     * you only half fill in leaves whatever was in it last tick. */
    memset(out, 0, sizeof(*out));
    if (!g_ready || in == 0) return;

    /* ── 1. What is the driver asking for? ─────────────────────────────── */
    float target_speed = in->setpoint[TT_SP_SPEED];    /* m/s   */
    float driver_steer = in->setpoint[TT_SP_STEER];    /* -1..1 */

    /* ── 2. What is the car actually doing? ────────────────────────────── */
    float speed = tt_speed(in);        /* m/s, from the wheel encoders */

    /* ── 3. Is there something in the way? ─────────────────────────────── */
    /* -1 means "no such sensor", which is not the same as "nothing ahead" —
     * a ToF that sees nothing reports its maximum range, not zero. */
    float front = tt_tof(&g_car, in, FRONT_SENSOR, -1.0f);

    float brake = 0.0f;
    int   blocked = 0;
    if (front >= 0.0f && BRAKE_DISTANCE_M > 0.0f && front < BRAKE_DISTANCE_M) {
        /* Ramp: gentle at the edge of the zone, hard up close. */
        brake = 1.0f - (front / BRAKE_DISTANCE_M);
        brake = tt_clamp(brake, 0.0f, 1.0f);
        blocked = 1;
    }

    /* ── 4. Decide the motor voltage. ──────────────────────────────────── */
    /* The PID answers "how many volts to hold that speed?". Braking overrides
     * it: driving into your own brakes is a good way to cook a motor. */
    float volts = tt_pid_update(&g_speed, target_speed, speed, in->dt_s);
    if (blocked) {
        volts = 0.0f;
        tt_pid_reset(&g_speed);   /* do not accumulate error while stopped */
    }

    /* ── 5. Write the outputs. ─────────────────────────────────────────── */
    float applied = tt_drive_volts(&g_car, out, volts);
    float steer   = tt_clamp1(driver_steer * STEER_GAIN);
    tt_steer(out, steer);
    tt_brake(out, brake);

    /* ── 6. Say what you did, so the graphs can show it. ───────────────── */
    out->debug[DBG_TARGET_SPEED] = target_speed;
    out->debug[DBG_SPEED]        = speed;
    out->debug[DBG_VOLTS]        = applied;
    out->debug[DBG_STEER]        = steer;
    out->debug[DBG_BRAKE]        = brake;
    out->debug[DBG_FRONT_DIST]   = front;
}


/* ═══════════════════════ WHERE TO GO NEXT ═════════════════════════════════
 *
 * Things to try, roughly in order of how much they will teach you:
 *
 * 1. Set SPEED_KI to 0 and drive. The car now settles slightly below the speed
 *    you asked for and stays there. That gap is exactly what the I term exists
 *    to remove — watch target_speed and speed on the graph.
 *
 * 2. Set SPEED_KP to 30. The car surges and hunts. Add SPEED_KD (try 0.5) and
 *    watch it settle down. This is the whole of PID tuning, felt rather than
 *    read.
 *
 * 3. Make STEER_GAIN depend on speed — full authority when parking, much less
 *    at 30 m/s:
 *
 *        float g = tt_lerp(1.0f, 0.35f, speed / 30.0f);
 *        tt_steer(out, tt_clamp1(driver_steer * g));
 *
 * 4. Take the driver out of it entirely. Below is a complete autopilot: it
 *    holds a fixed speed and steers toward the brighter side of the camera
 *    image, which follows a pale road across dark ground. Replace the body of
 *    ctrl_step with this to try it (your car needs a camera fitted — add one in
 *    the garage).
 *
 *        memset(out, 0, sizeof(*out));
 *        if (!g_ready || in == 0) return;
 *
 *        float speed = tt_speed(in);
 *        float volts = tt_pid_update(&g_speed, 8.0f, speed, in->dt_s);
 *
 *        // -1 = left half brighter, +1 = right half brighter. Only the bottom
 *        // 20 rows: that is the road just ahead, not the horizon.
 *        float bias  = tt_cam_balance(in, 20);
 *
 *        tt_drive_volts(&g_car, out, volts);
 *        tt_steer(out, tt_clamp1(bias * 2.0f));
 *
 *        out->debug[DBG_SPEED] = speed;
 *        out->debug[DBG_STEER] = bias;
 *
 * 5. Make a second controller: copy this whole folder, rename the copy, and it
 *    appears in the game's Build list on its own. The folder name is the DLL
 *    name — nothing else to edit, no CMake to touch.
 */
