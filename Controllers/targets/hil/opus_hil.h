/*
 * opus_hil.h — the Opus firmware behind the lockstep link.
 *
 * Fills a HilApp (hil_dev.h) whose calls are the Opus firmware's own
 * (opus_app.h). An MCU image in external-tick mode is then:
 *
 *     static OpusApp app;  static HilApp ha;  static HilDev dev;
 *     opus_hil_app(&app, &tt_params_opus_vector_foc, "opus_foc", &ha);
 *     hil_dev_init(&dev, &ha, usb_cdc_write, 0);
 *     for (;;) { n = usb_cdc_read(buf, sizeof buf); hil_dev_feed(&dev, buf, n); }
 *
 * and targets/hil/pil_main.c is the same thing on a PC, over stdin/stdout.
 */
#ifndef OPUS_HIL_H
#define OPUS_HIL_H

#include "hil_dev.h"
#include "opus_app.h"

typedef struct {
    OpusApp        *app;
    const TtParams *params;
} OpusHilCtx;

/* `ctx` must outlive `out`. */
void opus_hil_app(OpusHilCtx *ctx, OpusApp *app, const TtParams *params,
                  const char *name, HilApp *out);

#endif /* OPUS_HIL_H */
