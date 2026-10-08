/*
 * test_tt_controller.c — unit checks for UserScripts/lib/tt_controller.h.
 *
 * Registered with CTest as `tt_controller_unit`. Covers the parts of the
 * helper header whose numbers end up on a real motor: the PID's clamps, the
 * wheel-radius speed conversion and the manifest voltage fallback.
 */
#include "tt_controller.h"

#include <math.h>
#include <stdio.h>

static int g_failures = 0;

#define CHECK(name, cond) do { \
        if (cond) printf("  ok    %s\n", name); \
        else { printf("  FAIL  %s\n", name); g_failures++; } \
    } while (0)

static int near(float a, float b, float tol) { return fabsf(a - b) <= tol; }

static void test_pid(void) {
    TtPid p;
    float u = 0.0f;

    printf("PID\n");

    /* The I clamp is in OUTPUT units: ki * integral may not exceed i_max. */
    tt_pid_init(&p, 0.0f, 10.0f, 0.0f);
    tt_pid_limits(&p, -2.0f, 2.0f);
    for (int i = 0; i < 1000; i++) u = tt_pid_update(&p, 1.0f, 0.0f, 0.01f);
    CHECK("I term clamps at i_max in output units", near(u, 2.0f, 1e-5f));

    /* No output clamp by default. */
    tt_pid_init(&p, 100.0f, 0.0f, 0.0f);
    u = tt_pid_update(&p, 1.0f, 0.0f, 0.01f);
    CHECK("output unclamped by default", near(u, 100.0f, 1e-4f));

    /* Output clamp holds P + I + D together. */
    tt_pid_output_limits(&p, -7.4f, 7.4f);
    u = tt_pid_update(&p, 1.0f, 0.0f, 0.01f);
    CHECK("output clamp limits P+I+D", near(u, 7.4f, 1e-5f));
    u = tt_pid_update(&p, 0.0f, 1.0f, 0.0f);
    CHECK("output clamp also applies when dt <= 0", near(u, -7.4f, 1e-5f));

    /* Anti-windup: pinned at the output limit, the I term must not keep
     * growing, so the output leaves saturation as soon as the error flips. */
    tt_pid_init(&p, 1.0f, 5.0f, 0.0f);
    tt_pid_limits(&p, -100.0f, 100.0f);
    tt_pid_output_limits(&p, -1.0f, 1.0f);
    for (int i = 0; i < 500; i++) tt_pid_update(&p, 10.0f, 0.0f, 0.01f);
    CHECK("I term frozen while saturated", p.integral < 1.0f);
    u = tt_pid_update(&p, 0.0f, 0.5f, 0.01f);
    CHECK("recovers from saturation on the first reversed tick", u < 1.0f);

    /* Derivative on measurement: a setpoint step gives no kick. */
    tt_pid_init(&p, 2.0f, 0.0f, 1.0f);
    tt_pid_update(&p, 0.0f, 0.0f, 0.01f);
    u = tt_pid_update(&p, 5.0f, 0.0f, 0.01f);
    CHECK("setpoint step gives P only", near(u, 10.0f, 1e-5f));

    /* Zero limits disable I. */
    tt_pid_init(&p, 0.0f, 10.0f, 0.0f);
    tt_pid_limits(&p, 0.0f, 0.0f);
    for (int i = 0; i < 10; i++) u = tt_pid_update(&p, 1.0f, 0.0f, 0.01f);
    CHECK("i_min = i_max = 0 disables I", near(u, 0.0f, 1e-6f));
}

static void test_speed(void) {
    CtrlInputs in;
    printf("speed\n");
    memset(&in, 0, sizeof(in));
    /* 66 mm tyre at 100 rad/s = 3.3 m/s. */
    for (int i = 0; i < 4; i++) in.wheel_vel[i] = 100.0f;
    CHECK("TT_WHEEL_RADIUS_M is the 66 mm stock tyre", near(TT_WHEEL_RADIUS_M, 0.033f, 1e-6f));
    CHECK("tt_speed converts rad/s to m/s", near(tt_speed(&in), 3.3f, 1e-4f));
}

static void test_manifest(void) {
    TtCar car;
    SensorInfo s[2];
    printf("manifest\n");
    memset(s, 0, sizeof(s));
    s[0].type = SENSOR_MOTOR; s[0].actuator_index = 0; s[0].range_max = 8.4f;
    s[1].type = SENSOR_MOTOR; s[1].actuator_index = 1; s[1].range_max = 0.0f;
    tt_car_configure(&car, s, 2);
    CHECK("two motors found", car.motor_count == 2);
    CHECK("reported vmax kept", near(car.motor_vmax[0], 8.4f, 1e-6f));
    CHECK("missing vmax falls back to the 7.4 V pack, not 24 V",
          near(car.motor_vmax[1], TT_FALLBACK_VMAX, 1e-6f) && TT_FALLBACK_VMAX < 9.0f);
}

int main(void) {
    test_pid();
    test_speed();
    test_manifest();
    printf(g_failures ? "%d check(s) FAILED\n" : "all checks passed\n", g_failures);
    return g_failures ? 1 : 0;
}
