/*
 * tt_icm42688.h — ICM-42688-P FIFO packets -> TtImu (HW_02_MAIN, SEN-01).
 *
 * MAIN reads the IMU's FIFO over SPI (by DMA, on its data-ready interrupt)
 * and hands the bytes here. The FIFO holds 16-byte "packet 3" records:
 *
 *     0      header   0x68 = accel + gyro + ODR timestamp; bit 7 = FIFO empty
 *     1..6   accel X, Y, Z   int16 big-endian
 *     7..12  gyro  X, Y, Z   int16 big-endian
 *     13     temperature     int8: degC = raw / 2.07 + 25
 *     14..15 timestamp       uint16 big-endian, the sample's ODR time
 *
 * Accel-only and gyro-only packets (8 bytes) and 20-bit packets (20 bytes)
 * are skipped by their length, so a mis-set FIFO_CONFIG1 cannot desync the
 * stream. -32768 in an axis marks an invalid sample (the sensor was off).
 * The layout, the defaults (+-16 g, +-2000 dps, big-endian, 1 us timestamp
 * resolution) and the temperature scale are datasheet figures quoted from
 * memory [D]: check them against DS-000347 before bring-up.
 *
 * The chip's axes go to the body (FLU) through `axis`: body axis k is
 * sign(axis[k]) * chip axis |axis[k]| - 1. The twin's IMU part reports in
 * its chip frame and the manifest rotates it, so this is the same step.
 * Accel is specific force: level and at rest reads (0, 0, +9.81).
 *
 * Portable C11, float32, no allocation.
 */
#ifndef TT_ICM42688_H
#define TT_ICM42688_H

#include <stddef.h>
#include <stdint.h>
#include "tt_types.h"

#ifdef __cplusplus
extern "C" {
#endif

#define TT_ICM_PACKET3        16u
#define TT_ICM_HDR_EMPTY      0x80u
#define TT_ICM_HDR_ACCEL      0x40u
#define TT_ICM_HDR_GYRO       0x20u
#define TT_ICM_HDR_20BIT      0x10u
#define TT_ICM_HDR_TS_ODR     0x08u

typedef struct {
    int8_t axis[3];             /* body x, y, z <- +-1..3 chip axis           */
    int8_t pad_;
    float  accel_lsb_per_g;     /* 2048 at +-16 g                            */
    float  gyro_lsb_per_dps;    /* 16.4 at +-2000 dps                        */
    float  ts_us_per_lsb;       /* 1 (TMST_RES = 0) or 16                    */
} TtIcmCfg;

typedef struct {
    float    gyro[3];           /* rad/s, body                               */
    float    accel[3];          /* m/s^2, body, specific force               */
    float    temp_c;
    uint16_t ts;                /* the chip's 16-bit timestamp               */
    uint8_t  ok;                /* accel and gyro both present and valid     */
    uint8_t  pad_;
} TtIcmSample;

/* Chip axes = body axes, +-16 g, +-2000 dps, 1 us. */
void tt_icm_default_cfg(TtIcmCfg *c);

/* Decode a FIFO read. Returns the number of samples written to out (at most
 * max). Stops at an empty-FIFO header, a partial packet, or when out is
 * full; *used = the bytes consumed (keep the rest for the next read). */
int  tt_icm_fifo_decode(const uint8_t *p, size_t n, const TtIcmCfg *c,
                        TtIcmSample *out, int max, size_t *used);

/* When sample `ts` was taken, given that the newest sample (ts_newest) was
 * taken at t_newest on MAIN's clock: the 16-bit difference, wrapped. */
tt_us_t tt_icm_sample_time(tt_us_t t_newest, uint16_t ts_newest, uint16_t ts,
                           const TtIcmCfg *c);

/* A sample -> TtImu (stamp left to the caller). */
void tt_icm_to_imu(const TtIcmSample *s, TtImu *imu);

#ifdef __cplusplus
}
#endif

#endif /* TT_ICM42688_H */
