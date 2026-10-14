/*
 * tt_pstore.h — the parameter image a target keeps (FW-07).
 *
 * TtParams framed for a flash page or a file: a header with the layout
 * version and size, a write sequence number, the set's hash, then the
 * struct, then a CRC-32 over all of it. A target keeps two such slots and
 * loads the newer one whose CRC and version check out, so a write that
 * lost power leaves the previous set, not a half-written one; a slot that
 * reads back wrong in any way is simply not a parameter set. The same
 * bytes come out of Tools/gen_params.js --image, so a set calibrated on
 * the host goes to the car as the image the car reads back.
 *
 * Little-endian on every target (the struct is all 32-bit fields, so it
 * is copied as it lies). The MCU's tt_hal_params_load/store wrap this
 * around its flash driver; the host targets wrap it around a file named
 * by TT_PARAMS_STORE (targets/host_params.h).
 */
#ifndef TT_PSTORE_H
#define TT_PSTORE_H

#include <stddef.h>
#include <stdint.h>
#include "tt_params.h"

#ifdef __cplusplus
extern "C" {
#endif

#define TT_PSTORE_MAGIC   0x53505454u          /* "TTPS" as little-endian bytes */
#define TT_PSTORE_HDR     16u                  /* magic, version, size, seq     */
#define TT_PSTORE_SIZE    (TT_PSTORE_HDR + sizeof(TtParams) + 4u)

enum {
    TT_PSTORE_OK        = 0,
    TT_PSTORE_E_SHORT   = -1,   /* fewer bytes than an image               */
    TT_PSTORE_E_MAGIC   = -2,   /* not an image (blank flash, another file) */
    TT_PSTORE_E_CRC     = -3,   /* damaged                                  */
    TT_PSTORE_E_VERSION = -4,   /* another TtParams layout                  */
    TT_PSTORE_E_SIZE    = -5,   /* the layout's size does not match         */
    TT_PSTORE_E_INVALID = -6    /* intact, but tt_params_validate refuses it */
};

/* Frame `p` into `buf` as write number `seq`. Returns the bytes written
 * (TT_PSTORE_SIZE), or 0 when the buffer is too small or the set does not
 * validate: an image that would be refused on the way back is not made. */
size_t tt_pstore_write(uint8_t *buf, size_t cap, const TtParams *p, uint32_t seq);

/* Read an image. TT_PSTORE_OK with `p` and `seq` filled, else one of the
 * errors and `p` untouched. */
int tt_pstore_read(const uint8_t *buf, size_t n, TtParams *p, uint32_t *seq);

/* Of two slots, the newer valid one (the higher seq; a slot that fails to
 * read is skipped). Returns 1 or 2 for the slot taken, with `p` and `seq`
 * filled, or 0 when neither holds a parameter set. */
int tt_pstore_pick(const uint8_t *a, size_t na, const uint8_t *b, size_t nb,
                   TtParams *p, uint32_t *seq);

#ifdef __cplusplus
}
#endif

#endif /* TT_PSTORE_H */
