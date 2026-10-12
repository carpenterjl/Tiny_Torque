/*
 * tt_drvbus.c — MAIN's end of the drive bus. See tt_drvbus.h.
 */
#include "tt_drvbus.h"
#include "tt_safety.h"

#include <string.h>

void tt_drvbus_init(TtDrvBus *b, float tx_latency_us)
{
    memset(b, 0, sizeof *b);
    b->tx_latency_us = tx_latency_us > 0.0f ? tx_latency_us : 0.0f;
}

static void remember(TtDrvBus *b, uint16_t seq, tt_us_t t)
{
    uint32_t k = seq % TT_DRVBUS_HIST;
    b->hist_seq[k] = seq;
    b->hist_us[k] = t;
    b->hist_ok[k] = 1u;
}

void tt_drvbus_cmd(TtDrvBus *b, const float iq_a[TT_CAN_NODES], uint8_t enable,
                   uint8_t flags, tt_us_t now_us, TtCanFrame *f)
{
    TtCanCmd c;
    int n;
    c.seq = b->seq++;
    c.enable = enable;
    c.flags = flags;
    for (n = 0; n < TT_CAN_NODES; n++) c.iq_a[n] = iq_a ? iq_a[n] : 0.0f;
    c.t_us = now_us;
    tt_can_pack_cmd(f, &c);
    remember(b, c.seq, now_us);
}

void tt_drvbus_sent(TtDrvBus *b, uint16_t seq, tt_us_t tx_us)
{
    uint32_t k = seq % TT_DRVBUS_HIST;
    /* Only for a command still remembered. FDCAN stamps a transmission at
     * its start of frame and the node a reception at its end, so
     * tx_latency_us still applies. */
    if (b->hist_ok[k] && b->hist_seq[k] == seq) b->hist_us[k] = tx_us;
}

void tt_drvbus_estop(uint32_t reason, TtCanFrame *f)
{
    TtCanEstop e;
    e.reason = reason;
    tt_can_pack_estop(f, &e);
}

/* When a STATUS was sampled, on MAIN's clock. */
static tt_us_t sample_time(const TtDrvBus *b, const TtCanStatus *s, tt_us_t rx_us)
{
    uint32_t k = s->cmd_seq % TT_DRVBUS_HIST;
    tt_us_t t;
    if (s->age_us == 0xFFFFu || !b->hist_ok[k] || b->hist_seq[k] != s->cmd_seq) return rx_us;
    t = b->hist_us[k] + (tt_us_t)(b->tx_latency_us + 0.5f) + s->age_us;
    /* Never later than it arrived (a stale history slot, a clock glitch). */
    return (int32_t)(t - rx_us) > 0 ? rx_us : t;
}

static void fill_meas(TtMeas *m, uint8_t n, const TtCanStatus *s, tt_us_t t)
{
    TtDriveFb *d = &m->drv[n];
    TtEnc *e = &m->enc[n];
    d->st.t_us = t;
    d->st.seq++;
    d->st.valid = 1u;
    d->iq_a = s->iq_a;
    d->omega_m = s->omega_m;
    d->vbus_v = s->vbus_v;
    /* The winding is what the safety layer's limit is for; a node without a
     * winding sensor reports its FET temperature instead. */
    d->temp_c = (s->t_wind_c == s->t_wind_c) ? s->t_wind_c : s->t_fet_c;
    d->fault = s->fault;
    e->st.t_us = t;
    e->st.seq++;
    e->st.valid = 1u;
    e->count = s->enc_count;
}

int tt_drvbus_rx(TtDrvBus *b, const TtCanFrame *f, tt_us_t rx_us, TtMeas *m)
{
    uint8_t n;
    int kind = tt_can_kind(f->id, &n);
    TtDrvNode *nd = &b->node[n];

    switch (kind) {
    case TT_CAN_K_STATUS: {
        TtCanStatus s;
        if (tt_can_get_status(f, &s) != 0) { b->rx_bad++; return -1; }
        if (nd->have_status) {
            uint8_t step = (uint8_t)(s.seq - nd->last_seq);
            if (step == 0u) return kind;          /* a repeat: nothing new */
            nd->seq_gaps += (uint32_t)(step - 1u);
        }
        nd->have_status = 1u;
        nd->last_seq = s.seq;
        nd->status = s;
        nd->last_rx_us = rx_us;
        nd->n_status++;
        if (m) fill_meas(m, n, &s, sample_time(b, &s, rx_us));
        return kind;
    }
    case TT_CAN_K_BOOT:
        if (tt_can_get_boot(f, &nd->boot) != 0) { b->rx_bad++; return -1; }
        nd->booted = 1u;
        nd->n_boot++;
        return kind;
    case TT_CAN_K_CFG_RSP:
        if (tt_can_get_cfg(f, &nd->cfg_rsp) != 0) { b->rx_bad++; return -1; }
        nd->cfg_new = 1u;
        return kind;
    default:
        b->rx_other++;
        return kind;
    }
}

uint8_t tt_drvbus_not_ready(const TtDrvBus *b, uint8_t mask, tt_us_t now_us,
                            uint32_t stale_us)
{
    uint8_t bad = 0u, n;
    for (n = 0; n < TT_CAN_NODES; n++) {
        const TtDrvNode *nd = &b->node[n];
        int ok;
        if (!(mask & (1u << n))) continue;
        /* Signed: a frame stamped by the receive interrupt just after the
         * caller read its clock is new, not 71 minutes old. */
        ok = nd->booted && nd->boot.proto == TT_CAN_PROTO_VERSION &&
             (b->expect_cfg_hash[n] == 0u || nd->boot.cfg_hash == b->expect_cfg_hash[n]) &&
             nd->have_status && (int32_t)(now_us - nd->last_rx_us) <= (int32_t)stale_us &&
             (nd->status.state == TT_NODE_IDLE || nd->status.state == TT_NODE_RUN) &&
             !(nd->status.fault & (TT_DRV_NOT_CAL | TT_DRV_CONFIG));
        if (!ok) bad |= (uint8_t)(1u << n);
    }
    return bad;
}
