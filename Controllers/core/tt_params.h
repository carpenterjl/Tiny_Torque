/*
 * tt_params.h — the vehicle parameter store shared by sim and MCU (FW-07).
 *
 * Everything that describes the car rather than the controller lives in one
 * plain struct. Its defaults are GENERATED from a JSON file
 * (Controllers/params/<car>.json -> core/params_<car>.c, by
 * Tools/gen_params.js), which is also what the calibration tools write; on
 * the MCU the same struct is kept in flash behind tt_params_hash() and
 * loaded through the HAL. Every telemetry run records the hash, so a log
 * always says which parameters produced it.
 *
 * All fields are 32-bit, so the struct has no padding and hashes the same
 * on every target. Append only; bump TT_PARAMS_VERSION when the layout
 * changes.
 *
 * These are the FIRMWARE's beliefs about the car. In the sim they are
 * deliberately not overwritten from the manifest: a wrong kt or gear ratio
 * here must show up as a torque error, exactly as it would on the car. The
 * sim adapter only cross-checks the geometry the manifest also states.
 */
#ifndef TT_PARAMS_H
#define TT_PARAMS_H

#include <stdint.h>
#include "tt_types.h"

#ifdef __cplusplus
extern "C" {
#endif

#define TT_PARAMS_VERSION 1u

typedef struct {
    uint32_t version;              /* TT_PARAMS_VERSION                       */

    /* geometry */
    float    wheel_radius_m;
    float    wheelbase_m;
    float    track_front_m;        /* the odometry baseline on a front-odo car */
    float    mass_kg;              /* all-up                                  */
    float    mass_eff_kg;          /* + reflected rotating inertia            */

    /* encoders */
    uint32_t odo_wheel_mask;       /* wheels whose encoders give odometry     */
    float    enc_cpr[TT_MAX_WHEELS];   /* counts per turn of the encoder shaft */
    float    enc_ratio[TT_MAX_WHEELS]; /* encoder shaft turns per wheel turn   */

    /* drive */
    uint32_t driven_mask;          /* wheels with a motor                     */
    float    gear[TT_MAX_WHEELS];  /* motor turns per wheel turn              */
    float    kt[TT_MAX_WHEELS];    /* N*m/A, motor side                       */
    float    eta_drive;            /* gearbox efficiency, motor driving wheel */
    float    eta_back;             /* ... wheel back-driving motor (braking)  */
    float    regen_n_per_ms;       /* motor braking force per m/s at the road,
                                      whole car; 0 = not speed-limited        */
    float    regen_max_n;          /* motor braking ceiling (current limit)   */
    float    regen_grip_cap_n;     /* ceiling from driven-axle grip           */
    float    traction_eff;         /* fraction of driven force reaching road  */
    float    v_rail_nom;           /* nominal pack voltage                    */

    /* chassis */
    float    max_steer_rad;        /* road-wheel angle at full lock           */
    float    servo_slew_rad_s;
    float    brake_max_nm;         /* friction brake per wheel at 1.0; 0 = none */
    float    drag_c0, drag_c1, drag_c2; /* coast drag F = c0 + c1 v + c2 v^2   */

    /* calibration (measured, see Opus_Car_Spec/calibration.md) */
    float    cal_scale;            /* v_ground = v_enc * (1 + cal_scale)     */
    float    cal_brake;            /* extra fractional slip per unit brake    */

    /* sim artefacts, each to be deleted with the sim fix named beside it */
    uint32_t odo_lead_comp;        /* 1 = correct the sim encoder's
                                      left-endpoint integration bias (SEN-02) */
} TtParams;

/* Defaults generated from Controllers/params/opus_vector.json. */
extern const TtParams tt_params_opus_vector;

/* Range checks. Returns 0 when the set is usable, else the number of fields
 * that are not; a target must refuse to arm on a non-zero result. */
int      tt_params_validate(const TtParams *p);

/* CRC-32 (IEEE) over the whole struct: the identity of a parameter set. */
uint32_t tt_params_hash(const TtParams *p);

/* Wheel torque -> motor Iq through this car's believed kt, gear and
 * efficiency. Shared by every target, so the sim exercises exactly the
 * conversion the car will run. Back-driven (t < 0) the losses help: the
 * wheel sees more torque than the motor makes, hence 1/eta_back. */
static inline float tt_torque_to_iq(const TtParams *p, int w, float t_wheel_nm)
{
    float g   = p->gear[w], kt = p->kt[w];
    float eta = (t_wheel_nm >= 0.0f) ? p->eta_drive : 1.0f / p->eta_back;
    float k   = g * kt * eta;
    return k > 0.0f ? t_wheel_nm / k : 0.0f;
}

/* Metres of road per encoder count on wheel w. */
static inline float tt_m_per_count(const TtParams *p, int w)
{
    float c = p->enc_cpr[w] * p->enc_ratio[w];
    return c > 0.0f ? (2.0f * 3.14159265f * p->wheel_radius_m) / c : 0.0f;
}

#ifdef __cplusplus
}
#endif

#endif /* TT_PARAMS_H */
