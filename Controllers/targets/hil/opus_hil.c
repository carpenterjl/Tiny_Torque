/*
 * opus_hil.c — the Opus firmware behind the lockstep link. See opus_hil.h.
 */
#include "opus_hil.h"

#include <string.h>

static int h_init(void *ctx, float rate_hz)
{
    OpusHilCtx *c = (OpusHilCtx *)ctx;
    opus_app_init(c->app, c->params, rate_hz);
    return 0;
}

static void h_configure(void *ctx, const TtCarDesc *car)
{
    opus_app_configure(((OpusHilCtx *)ctx)->app, car);
}

static void h_reset(void *ctx)
{
    opus_app_reset(((OpusHilCtx *)ctx)->app);
}

static void h_step(void *ctx, const TtMeas *m, TtAct *act)
{
    opus_app_step(((OpusHilCtx *)ctx)->app, m, act);
}

static const float *h_log(void *ctx, uint32_t *n)
{
    return opus_app_log(((OpusHilCtx *)ctx)->app, n);
}

static const char *h_names(void *ctx)
{
    (void)ctx;
    return opus_app_log_names();
}

static uint32_t h_hash(void *ctx)
{
    return tt_params_hash(((OpusHilCtx *)ctx)->params);
}

void opus_hil_app(OpusHilCtx *ctx, OpusApp *app, const TtParams *params,
                  const char *name, HilApp *out)
{
    ctx->app = app;
    ctx->params = params;
    memset(out, 0, sizeof(*out));
    out->ctx = ctx;
    out->name = name;
    out->init = h_init;
    out->configure = h_configure;
    out->reset = h_reset;
    out->step = h_step;
    out->log = h_log;
    out->log_names = h_names;
    out->params_hash = h_hash;
}
