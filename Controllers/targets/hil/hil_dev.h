/*
 * hil_dev.h — the firmware's end of the lockstep link (HIL-03, "external
 * tick" mode).
 *
 * In this mode the board does not read its sensors or keep its own time:
 * every control tick is a MEAS frame from the host, and the firmware's
 * answer is the ACT frame it would otherwise have written to its drives and
 * servo. The scheduler is the frame's arrival. Everything between the two is
 * the normal firmware (opus_app_step for the Opus), so the board in the loop
 * runs the code it will run on the car, on the inputs the DLL saw.
 *
 * The transport is the target's business: hand every received byte to
 * hil_dev_feed() (from the USB CDC receive callback's buffer, a UART DMA
 * ring, or stdin on the host), and the device writes its replies through the
 * `write` callback you gave it. One reply per request, in order.
 *
 * On an MCU, call hil_dev_feed() from the main loop, not from the USB
 * interrupt: a MEAS runs a whole firmware tick before it returns.
 *
 * Portable C11, no allocation.
 */
#ifndef HIL_DEV_H
#define HIL_DEV_H

#include <stddef.h>
#include <stdint.h>
#include "tt_wire.h"

/* The firmware, as the link sees it. Any of the optional calls may be NULL. */
typedef struct {
    void        *ctx;
    const char  *name;                                   /* short, for INFO */
    int        (*init)(void *ctx, float rate_hz);        /* 0 = ok          */
    void       (*configure)(void *ctx, const TtCarDesc *car);
    void       (*reset)(void *ctx);
    void       (*shutdown)(void *ctx);                   /* optional        */
    void       (*step)(void *ctx, const TtMeas *m, TtAct *act);
    const float *(*log)(void *ctx, uint32_t *n);         /* optional        */
    const char *(*log_names)(void *ctx);                 /* optional        */
    float      (*rate_hz)(void *ctx);                    /* optional: wanted tick */
    uint32_t   (*params_hash)(void *ctx);                /* optional        */
} HilApp;

typedef void (*HilWrite)(void *wctx, const uint8_t *p, size_t n);

typedef struct {
    TtWireRx       rx;
    const HilApp  *app;
    HilWrite       write;
    void          *wctx;
    uint16_t       tx_seq;
    int            inited;
    uint32_t       steps, not_ready, bad;
    uint32_t       last_rx_errors;
    uint8_t        payload[TT_WIRE_MAX_PAYLOAD];
    uint8_t        frame[TT_WIRE_MAX_FRAME];
} HilDev;

void hil_dev_init(HilDev *d, const HilApp *app, HilWrite write, void *wctx);

/* Bytes in, in any chunks; replies go out through `write`. */
void hil_dev_feed(HilDev *d, const uint8_t *p, size_t n);

#endif /* HIL_DEV_H */
