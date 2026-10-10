/*
 * hil_dev.c — the firmware's end of the lockstep link. See hil_dev.h.
 */
#include "hil_dev.h"

#include <string.h>

void hil_dev_init(HilDev *d, const HilApp *app, HilWrite write, void *wctx)
{
    memset(d, 0, sizeof(*d));
    tt_wire_rx_init(&d->rx);
    d->app = app;
    d->write = write;
    d->wctx = wctx;
}

/* Bounded copy that always terminates (strncpy without its warnings). */
static void copy_str(char *dst, size_t cap, const char *src)
{
    size_t i = 0;
    if (src) for (; i + 1u < cap && src[i]; i++) dst[i] = src[i];
    dst[i] = '\0';
}

/* A reply carries the request's tick and time, so the host can tell a late
 * answer from the one it is waiting for. */
static void send(HilDev *d, uint8_t type, uint8_t flags, const TtWireHdr *req,
                 const uint8_t *payload, size_t n)
{
    TtWireHdr h;
    size_t len;
    h.type = type;
    h.flags = flags;
    h.seq = d->tx_seq++;
    h.tick = req ? req->tick : 0u;
    h.time_us = req ? req->time_us : 0u;
    len = tt_wire_frame(&h, payload, n, d->frame, sizeof(d->frame));
    if (len) d->write(d->wctx, d->frame, len);
}

static void ack(HilDev *d, const TtWireHdr *req, int32_t status)
{
    uint8_t b[5];
    b[0] = req->type;
    b[1] = (uint8_t)((uint32_t)status);
    b[2] = (uint8_t)((uint32_t)status >> 8);
    b[3] = (uint8_t)((uint32_t)status >> 16);
    b[4] = (uint8_t)((uint32_t)status >> 24);
    send(d, TT_WIRE_ACK, 0, req, b, sizeof(b));
}

static void fault(HilDev *d, const TtWireHdr *req, uint16_t code, uint32_t detail)
{
    uint8_t b[6];
    b[0] = (uint8_t)code; b[1] = (uint8_t)(code >> 8);
    b[2] = (uint8_t)detail; b[3] = (uint8_t)(detail >> 8);
    b[4] = (uint8_t)(detail >> 16); b[5] = (uint8_t)(detail >> 24);
    send(d, TT_WIRE_FAULT, 0, req, b, sizeof(b));
}

static void on_frame(HilDev *d, const TtWireHdr *h, const uint8_t *p, size_t n)
{
    const HilApp *a = d->app;

    switch (h->type) {

    case TT_WIRE_PING:
        send(d, TT_WIRE_PONG, 0, h, p, n);
        break;

    case TT_WIRE_INIT: {
        TtWireInfo info;
        float rate = 0.0f;
        uint32_t u;
        size_t len;
        int rc;
        if (n != 4) { fault(d, h, TT_WFAULT_BAD_PAYLOAD, h->type); break; }
        u = (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
        memcpy(&rate, &u, 4);
        /* rate 0: the host only asks who this is; nothing is initialised. */
        if (rate > 0.0f) {
            rc = a->init(a->ctx, rate);
            d->inited = rc == 0;
        } else {
            rc = d->inited ? 0 : -1;
        }

        memset(&info, 0, sizeof(info));
        info.version = TT_WIRE_VERSION;
        info.rate_hz = a->rate_hz ? a->rate_hz(a->ctx) : 0.0f;
        info.params_hash = a->params_hash ? a->params_hash(a->ctx) : 0u;
        if (a->log) { uint32_t nl = 0; (void)a->log(a->ctx, &nl); info.n_log = (uint16_t)nl; }
        if (a->name) copy_str(info.name, sizeof(info.name), a->name);
        if (a->log_names) copy_str(info.log_names, sizeof(info.log_names), a->log_names(a->ctx));
        len = tt_wire_put_info(d->payload, sizeof(d->payload), &info);
        send(d, TT_WIRE_INFO, (uint8_t)(rc == 0 ? 0u : TT_WF_NOT_READY), h, d->payload, len);
        break;
    }

    case TT_WIRE_CONFIG: {
        TtCarDesc car;
        if (tt_wire_get_car(p, n, &car) != 0) { ack(d, h, -1); break; }
        a->configure(a->ctx, &car);
        ack(d, h, 0);
        break;
    }

    case TT_WIRE_RESET:
        if (d->inited) a->reset(a->ctx);
        ack(d, h, d->inited ? 0 : -1);
        break;

    case TT_WIRE_SHUTDOWN:
        if (a->shutdown) a->shutdown(a->ctx);
        d->inited = 0;
        ack(d, h, 0);
        break;

    case TT_WIRE_MEAS: {
        TtMeas m;
        TtAct act;
        const float *log = 0;
        uint32_t nl = 0;
        size_t len;
        uint8_t flags = 0;

        memset(&act, 0, sizeof(act));
        if (tt_wire_get_meas(p, n, &m) != 0) {
            d->bad++;
            fault(d, h, TT_WFAULT_BAD_PAYLOAD, h->type);
            flags = TT_WF_NOT_READY;            /* still answer: the host waits */
        } else if (!d->inited) {
            d->not_ready++;
            flags = TT_WF_NOT_READY;
        } else {
            a->step(a->ctx, &m, &act);
            d->steps++;
            if (a->log) log = a->log(a->ctx, &nl);
        }
        len = tt_wire_put_act(d->payload, sizeof(d->payload), &act, log, log ? nl : 0u);
        send(d, TT_WIRE_ACT, flags, h, d->payload, len);
        break;
    }

    default:
        fault(d, h, TT_WFAULT_UNKNOWN_TYPE, h->type);
        break;
    }
}

void hil_dev_feed(HilDev *d, const uint8_t *p, size_t n)
{
    while (n > 0) {
        TtWireHdr h;
        const uint8_t *pl;
        size_t plen, used;
        int got;
        uint32_t errs;

        used = tt_wire_rx_feed(&d->rx, p, n, &got, &h, &pl, &plen);
        p += used;
        n -= used;
        /* Tell the host about damaged frames as they happen: it is waiting
         * for a reply that will never come. */
        errs = d->rx.crc_errors + d->rx.cobs_errors + d->rx.overflows;
        if (errs != d->last_rx_errors) {
            d->last_rx_errors = errs;
            fault(d, 0, TT_WFAULT_RX_ERRORS, errs);
        }
        if (got) on_frame(d, &h, pl, plen);
    }
}
