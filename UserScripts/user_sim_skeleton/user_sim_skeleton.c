/*
 * user_sim_skeleton.c — the smallest controller that does anything.
 *
 * Everything here is plumbing: the five functions the game looks up, a copy of
 * the car's parts list so the motors can be found, and one number you can edit.
 * There is no control law, because that is the part you are here to write.
 *
 * Build it (Single Player → Simulate Controller → Build & Reload), pick
 * user_sim_skeleton in the Controller row, press Run controller ▶, and the car
 * pulls away at whatever DRIVE_VOLTS says. Change the number, press Build &
 * Reload again — the car does not even stop.
 *
 * If you want a worked example instead of a blank page, read
 * ../MyController/my_controller.c: same five functions, with a speed loop and
 * obstacle braking already in them.
 *
 * One warning, and it is the only one: this is a real DLL loaded into the
 * running game. A bad pointer takes the process down with it, editor included.
 * The tt_* helpers all bounds-check and return a fallback for exactly that
 * reason — reach past them and you are on your own.
 */

#include "tt_controller.h"


/* ═══════════════════════════ YOUR ONE KNOB ════════════════════════════════
 *
 * Volts sent to every drive motor, every tick. Positive is forwards. The game
 * clamps this to the motor's own limit, so a silly number is safe — it just
 * gives you the same answer as the largest sensible one.
 *
 * 0.0 is a BRAKE, not neutral, and it is worth knowing that before you wonder
 * why the car stops so fast when you let go: a motor commanded to zero volts
 * has both terminals at the same potential, which is a short across the
 * windings, and the drag from that dwarfs aerodynamic drag at any speed you
 * will reach here. There is no "coast" to ask for — the sim's own coastdown
 * tests have to open the circuit from the physics side to get one.
 */
static const float DRIVE_VOLTS = 3.0f;


/* ═══════════════════════════════ STATE ════════════════════════════════════
 *
 * `static` at file scope, because the DLL is loaded once and stepped forever.
 * There is no per-tick place to keep anything.
 */

static TtCar g_car;     /* what the game told us this car is made of */
static int   g_ready;   /* 0 until ctrl_init has run                 */

/* Debug channels: graphed live in the game and written to the telemetry CSV.
 * Keep this enum and ctrl_get_debug_names() in the same order — the game pairs
 * them up by position, not by name. */
enum {
    DBG_VOLTS = 0,
    DBG_SPEED,
    DBG_COUNT
};


/* ════════════════════ WHICH CAR (optional, ABI v5) ════════════════════════
 *
 * Return one of the CTRL_VEHICLE_* values and the Simulate Controller screen
 * builds THAT car for you, ignoring its own Vehicle picker. Useful when a
 * controller is written for one specific machine and driving it in anything
 * else is just confusing.
 *
 *   CTRL_VEHICLE_MENU          whatever the menu picked  ← the default
 *   CTRL_VEHICLE_STOCK         Stock Default
 *   CTRL_VEHICLE_REAL_TWIN     Real Twin 1/10
 *   CTRL_VEHICLE_TT_COUPE      TT Coupe
 *   CTRL_VEHICLE_TT_BAJA       TT Baja
 *   CTRL_VEHICLE_TT_PATROL     TT Patrol
 *   CTRL_VEHICLE_TT_RATTLETRAP TT Rattletrap
 *   CTRL_VEHICLE_TT_REDLINE    TT Redline
 *   CTRL_VEHICLE_TT_HIGHWING   TT Highwing
 *   CTRL_VEHICLE_TT_AUTOPIA    TT Autopia
 *   CTRL_VEHICLE_OPUS_VECTOR   Opus Vector
 *
 * A car you designed and saved yourself has no number — it did not exist when
 * this file was compiled. Pick those in the menu and leave this alone.
 *
 * The game asks BEFORE the car is built, so this runs before ctrl_init and must
 * answer from a constant: there is nothing to consult yet. If it names a car
 * you have not unlocked the menu's pick stands and the console says why. Delete
 * the whole function if you never want it — it is optional, and a controller
 * without it behaves exactly as controllers did before it existed.
 */
CTRL_EXPORT int ctrl_get_vehicle(void) {
    return CTRL_VEHICLE_MENU;
}


/* ═══════════════════════════════ SETUP ════════════════════════════════════ */

/*
 * Once, when the car is created — and again after every Build & Reload.
 * Return 0 for "ready"; anything else and the game drives the car open-loop.
 *
 * control_rate_hz is how often ctrl_step will be called (100 Hz). You do not
 * need it: every tick is handed its own exact dt.
 */
CTRL_EXPORT int ctrl_init(float control_rate_hz) {
    TT_UNUSED(control_rate_hz);
    g_ready = 1;
    return 0;
}

/*
 * Once, right after ctrl_init: this car's parts list. How many motors, which
 * actuator slot each one answers to, how many volts it will take, what sensors
 * are bolted on and where their readings land.
 *
 * Cars here are assembled from parts, so none of that is something to assume —
 * change a wheel in the garage and the answers change. The pointer is valid
 * only for this call; tt_car_configure copies what it needs into g_car.
 */
CTRL_EXPORT void ctrl_configure(const SensorInfo* sensors, int count) {
    tt_car_configure(&g_car, sensors, count);
}

/*
 * Once at the end, and before every hot reload. Free anything you allocated;
 * the reload path is init → configure → step, so what you leave behind leaks
 * once per Build & Reload.
 */
CTRL_EXPORT void ctrl_shutdown(void) {
    g_ready = 0;
}

/* Labels for debug[], comma-separated, in the enum's order. */
CTRL_EXPORT const char* ctrl_get_debug_names(void) {
    return "volts,speed";
}


/* ═════════════════════════════ THE LOOP ═══════════════════════════════════
 *
 * 100 times a second. `in` is everything the car can sense; `out` is everything
 * it can do. Your controller is whatever you put between them.
 *
 * What is in `in`, in short — the full list with units is in
 * Controllers/hal/controller_api.h, which is the actual contract:
 *
 *   in->dt_s          seconds since the last tick
 *   in->time_s        seconds since the sim started
 *   in->gyro[3]       body angular rate, rad/s
 *   in->accel[3]      body specific force, m/s²
 *   in->wheel_vel[4]  wheel angular velocity, rad/s   (tt_speed turns this into m/s)
 *   in->setpoint[]    what the person at the keyboard is asking for:
 *                     [TT_SP_SPEED] m/s, [TT_SP_STEER] -1..+1. Ignore them and
 *                     you have written an autopilot instead of a driver's aid.
 *   in->sensor_data   every fitted sensor's readings, laid out by the manifest.
 *                     Read it through the helpers — tt_tof(&g_car, in, "tof_front",
 *                     4.0f), tt_sensor_value(...) — which bounds-check and hand
 *                     back your fallback rather than reading past the end.
 *   in->cam_pixels    grayscale frame if a camera is fitted, else NULL.
 *                     Row 0 is the TOP. tt_cam_pixel / tt_cam_balance.
 *
 * And `out`: out->actuator[] through tt_drive_volts / tt_steer / tt_brake, and
 * out->debug[] for anything you want to see on a graph.
 */
CTRL_EXPORT void ctrl_step(const CtrlInputs* in, CtrlOutputs* out) {
    /* Clear the outputs first, every tick. They are not zeroed for you, so a
     * struct you only half fill in silently repeats last tick's steering. */
    memset(out, 0, sizeof(*out));
    if (!g_ready || in == 0) return;

    /* Reading a sensor. tt_speed averages the four wheel encoders and converts
     * rad/s into m/s; nothing below uses it, it is here so the graph has
     * something on it and so you can see where a measurement comes from. */
    float speed = tt_speed(in);

    /* Driving. Same voltage to every drive motor, clamped per motor to its own
     * limit, and the applied value handed back so the graph shows what the car
     * got rather than what you asked for. */
    float applied = tt_drive_volts(&g_car, out, DRIVE_VOLTS);

    /* Steering, -1 (full left) .. +1 (full right), and the friction brake,
     * 0 .. 1. Straight ahead and brakes off — this is where a real controller
     * would have something to say.
     *
     *   tt_steer(out, 0.0f);
     *   tt_brake(out, 0.0f);
     *
     * Both are already zero from the memset, so the calls are commented out
     * rather than written: the point is that you know where they are.
     */

    out->debug[DBG_VOLTS] = applied;
    out->debug[DBG_SPEED] = speed;
}
