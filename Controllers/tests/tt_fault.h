/*
 * tt_fault.h — fault injection on the firmware's own frames (VAL-11, host).
 *
 * The same fault catalogue as the sim's FaultInjector (UnitySim
 * Scripts/Sensors/FaultInjector.cs), applied to TtMeas / TtCmd so CTest can
 * check the safety layer (core/tt_safety.h) against every fault without
 * Unity. The sim injects the same faults at their physical source (a sensor
 * part, a driver, the pack, the receiver, the scheduler) and checks the
 * whole car end to end; this checks the firmware's response to what those
 * faults look like by the time they reach it.
 *
 * Host only: it is test code, not firmware.
 */
#ifndef TT_FAULT_H
#define TT_FAULT_H

#include "tt_types.h"

typedef enum {
    TT_FAULT_NONE = 0,
    TT_FAULT_STALE,        /* a sensor stops updating: value and stamp held (I2C NACK)  */
    TT_FAULT_STUCK,        /* a sensor's value freezes; its stamps keep coming           */
    TT_FAULT_NAN,          /* a sensor reads NaN                                         */
    TT_FAULT_ENC_GLITCH,   /* an encoder jumps by `mag` counts, every `period` s          */
    TT_FAULT_DRV_TRIP,     /* a driver reports trip bits `mag` (TT_DRV_*)                 */
    TT_FAULT_DRV_HOT,      /* a driver's winding reads `mag` deg C                        */
    TT_FAULT_BROWNOUT,     /* the pack reads `mag` V                                      */
    TT_FAULT_UWB_LOSS,     /* anchor `index` stops replying (255 = every anchor)          */
    TT_FAULT_RC_LINK,      /* RC frames lost; the receiver declares failsafe after `mag` s */
    TT_FAULT_RC_KILL,      /* the kill switch is thrown                                   */
    TT_FAULT_RC_DISARM,    /* the arm switch is turned off                                */
    TT_FAULT_CORE_STALL,   /* the core stops producing commands                           */
    TT_FAULT_CORE_NAN,     /* the core commands NaN torque                                */
    TT_FAULT_OVERRUN,      /* one tick arrives `mag` s late                               */
    TT_FAULT_TIPOVER       /* the car lies on its side (specific force along body y)      */
} TtFaultKind;

/* Which sensor a STALE / STUCK / NAN fault hits. */
typedef enum {
    TT_FS_ENC = 0, TT_FS_DRV, TT_FS_IMU, TT_FS_BATT, TT_FS_RC, TT_FS_UWB
} TtFaultSensor;

typedef struct {
    TtFaultKind kind;
    int   sensor;          /* TtFaultSensor for STALE / STUCK / NAN          */
    int   index;           /* wheel, or UWB anchor id                        */
    float t_start_s;
    float dur_s;           /* 0 = until the end                              */
    float mag;
    float period_s;        /* ENC_GLITCH repeat; 0 = once                    */
} TtFault;

typedef struct {
    TtFault  f;
    TtMeas   held;         /* the frame at the fault's start (STALE, STUCK)  */
    int      has_held;
    float    next_glitch_s;
    int32_t  glitch_offset;
    TtCmd    last_cmd;     /* CORE_STALL repeats it                          */
    int      has_cmd;
    int      overrun_done;
} TtFaultInj;

void tt_fault_init(TtFaultInj *j, const TtFault *f);

/* True while the fault acts at time t. */
int  tt_fault_active(const TtFaultInj *j, float t);

/* Apply to the frame the firmware is about to see, in place. */
void tt_fault_apply_meas(TtFaultInj *j, float t, TtMeas *m);

/* Apply to the core's command before the safety layer sees it. */
void tt_fault_apply_cmd(TtFaultInj *j, float t, TtCmd *c);

#endif /* TT_FAULT_H */
