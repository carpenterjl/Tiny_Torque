/*
 * tt_icm42688.c — ICM-42688-P FIFO decoding. See tt_icm42688.h.
 */
#include "tt_icm42688.h"

#include <string.h>

#define ICM_G      9.80665f
#define ICM_D2R    0.0174532925f

void tt_icm_default_cfg(TtIcmCfg *c)
{
    c->axis[0] = 1;
    c->axis[1] = 2;
    c->axis[2] = 3;
    c->pad_ = 0;
    c->accel_lsb_per_g = 2048.0f;
    c->gyro_lsb_per_dps = 16.4f;
    c->ts_us_per_lsb = 1.0f;
}

static int16_t be16(const uint8_t *p) { return (int16_t)(uint16_t)((uint16_t)(p[0] << 8) | p[1]); }

/* Chip vector -> body vector. */
static void to_body(const int8_t axis[3], const float chip[3], float body[3])
{
    int k;
    for (k = 0; k < 3; k++) {
        int a = axis[k] < 0 ? -axis[k] : axis[k];
        float v = (a >= 1 && a <= 3) ? chip[a - 1] : 0.0f;
        body[k] = axis[k] < 0 ? -v : v;
    }
}

int tt_icm_fifo_decode(const uint8_t *p, size_t n, const TtIcmCfg *c,
                       TtIcmSample *out, int max, size_t *used)
{
    size_t i = 0;
    int got = 0;
    while (i < n && got < max) {
        uint8_t h = p[i];
        size_t len;
        if (h & TT_ICM_HDR_EMPTY) { i = n; break; }        /* nothing after it */
        if (h & TT_ICM_HDR_20BIT) len = 20u;
        else if ((h & TT_ICM_HDR_ACCEL) && (h & TT_ICM_HDR_GYRO)) len = TT_ICM_PACKET3;
        else len = 8u;
        if (i + len > n) break;                             /* partial: keep */
        if (len == TT_ICM_PACKET3) {
            const uint8_t *q = p + i;
            TtIcmSample *s = &out[got++];
            float a[3], g[3];
            int k, bad = 0;
            for (k = 0; k < 3; k++) {
                int16_t ra = be16(q + 1 + 2 * k), rg = be16(q + 7 + 2 * k);
                if (ra == -32768 || rg == -32768) bad = 1;
                a[k] = (float)ra * (ICM_G / c->accel_lsb_per_g);
                g[k] = (float)rg * (ICM_D2R / c->gyro_lsb_per_dps);
            }
            to_body(c->axis, a, s->accel);
            to_body(c->axis, g, s->gyro);
            s->temp_c = (float)(int8_t)q[13] * (1.0f / 2.07f) + 25.0f;
            s->ts = (uint16_t)be16(q + 14);
            s->ok = (uint8_t)!bad;
            s->pad_ = 0u;
        }
        i += len;
    }
    *used = i;
    return got;
}

tt_us_t tt_icm_sample_time(tt_us_t t_newest, uint16_t ts_newest, uint16_t ts,
                           const TtIcmCfg *c)
{
    uint16_t back = (uint16_t)(ts_newest - ts);
    return t_newest - (tt_us_t)((float)back * c->ts_us_per_lsb + 0.5f);
}

void tt_icm_to_imu(const TtIcmSample *s, TtImu *imu)
{
    memcpy(imu->gyro, s->gyro, sizeof imu->gyro);
    memcpy(imu->accel, s->accel, sizeof imu->accel);
}
