/*
 * tt_log.h — one logging schema for sim and car (FW-09).
 *
 * A firmware describes its log channels ONCE, in an X-macro table:
 *
 *     // my_log.def
 *     TT_LOG(state,  "",    "mission phase")
 *     TT_LOG(odo_m,  "m",   "odometer since arm")
 *
 * and that one table produces all three views of the log:
 *
 *   - the sim's ctrl_get_debug_names() string        (tt_log_names)
 *   - the MCU's binary frame, a TtLogFrame whose v[] is in table order
 *   - the CSV header the host decoder writes for a real-car log
 *     (Tools/tt_log_decode.js reads the same .def file)
 *
 * so a sim CSV and a decoded car log share their dbg/<name> columns and the
 * Telemetry Analyzer overlays them directly.
 *
 * Usage, in exactly one place per firmware:
 *
 *     #define TT_LOG(name, unit, desc) float name;
 *     typedef struct { #include "my_log.def" } MyLog;      (one float each)
 *     #undef TT_LOG
 *
 * then fill the struct each tick and hand it to tt_log_pack().
 */
#ifndef TT_LOG_H
#define TT_LOG_H

#include <stdint.h>
#include <string.h>
#include "tt_types.h"

#ifdef __cplusplus
extern "C" {
#endif

#define TT_LOG_MAX     32
#define TT_LOG_MAGIC   0x47544C54u     /* "TLTG" little-endian */

/* The binary frame. Fixed layout, 32-bit fields only: written raw to SD /
 * USB on the MCU and decoded on the host. */
typedef struct {
    uint32_t magic;
    uint32_t seq;
    tt_us_t  t_us;
    uint32_t n;                        /* channels used in v[]            */
    uint32_t params_hash;              /* tt_params_hash() of the run     */
    float    v[TT_LOG_MAX];
} TtLogFrame;

/* Copy a filled X-macro struct (n floats, table order) into a frame. */
static inline void tt_log_pack(TtLogFrame *f, const void *log_struct, uint32_t n,
                               uint32_t seq, tt_us_t t_us, uint32_t params_hash)
{
    if (n > TT_LOG_MAX) n = TT_LOG_MAX;
    f->magic = TT_LOG_MAGIC;
    f->seq = seq;
    f->t_us = t_us;
    f->n = n;
    f->params_hash = params_hash;
    memcpy(f->v, log_struct, n * sizeof(float));
}

#ifdef __cplusplus
}
#endif

#endif /* TT_LOG_H */
