/*
 * tt_pstore.c — the parameter image (FW-07). See tt_pstore.h.
 */
#include "tt_pstore.h"

#include <string.h>

static void put_u32(uint8_t *b, uint32_t v)
{
    b[0] = (uint8_t)v;
    b[1] = (uint8_t)(v >> 8);
    b[2] = (uint8_t)(v >> 16);
    b[3] = (uint8_t)(v >> 24);
}

static uint32_t get_u32(const uint8_t *b)
{
    return (uint32_t)b[0] | ((uint32_t)b[1] << 8) | ((uint32_t)b[2] << 16) | ((uint32_t)b[3] << 24);
}

size_t tt_pstore_write(uint8_t *buf, size_t cap, const TtParams *p, uint32_t seq)
{
    size_t body = TT_PSTORE_HDR;

    if (buf == NULL || p == NULL || cap < TT_PSTORE_SIZE) return 0;
    if (tt_params_validate(p) != 0) return 0;
    put_u32(buf + 0, TT_PSTORE_MAGIC);
    put_u32(buf + 4, p->version);
    put_u32(buf + 8, (uint32_t)sizeof(*p));
    put_u32(buf + 12, seq);
    memcpy(buf + body, p, sizeof(*p));
    body += sizeof(*p);
    put_u32(buf + body, tt_crc32(buf, body));
    return TT_PSTORE_SIZE;
}

int tt_pstore_read(const uint8_t *buf, size_t n, TtParams *p, uint32_t *seq)
{
    TtParams t;
    uint32_t ver, size;

    if (buf == NULL || p == NULL) return TT_PSTORE_E_SHORT;
    if (n < TT_PSTORE_HDR + 4u) return TT_PSTORE_E_SHORT;
    if (get_u32(buf) != TT_PSTORE_MAGIC) return TT_PSTORE_E_MAGIC;
    ver = get_u32(buf + 4);
    size = get_u32(buf + 8);
    /* The CRC covers the header and a body of the stated size, so an image
     * of another layout is still checked whole before being refused for
     * its version: a damaged version word is damage, not another layout. */
    if (size > n - TT_PSTORE_HDR - 4u) return TT_PSTORE_E_SHORT;
    if (tt_crc32(buf, TT_PSTORE_HDR + size) != get_u32(buf + TT_PSTORE_HDR + size))
        return TT_PSTORE_E_CRC;
    if (ver != TT_PARAMS_VERSION) return TT_PSTORE_E_VERSION;
    if (size != (uint32_t)sizeof(t)) return TT_PSTORE_E_SIZE;
    memcpy(&t, buf + TT_PSTORE_HDR, sizeof(t));
    if (t.version != TT_PARAMS_VERSION) return TT_PSTORE_E_VERSION;
    if (tt_params_validate(&t) != 0) return TT_PSTORE_E_INVALID;
    *p = t;
    if (seq) *seq = get_u32(buf + 12);
    return TT_PSTORE_OK;
}

int tt_pstore_pick(const uint8_t *a, size_t na, const uint8_t *b, size_t nb,
                   TtParams *p, uint32_t *seq)
{
    TtParams pa, pb;
    uint32_t sa = 0, sb = 0;
    int oka = tt_pstore_read(a, na, &pa, &sa) == TT_PSTORE_OK;
    int okb = tt_pstore_read(b, nb, &pb, &sb) == TT_PSTORE_OK;

    if (!oka && !okb) return 0;
    /* Sequence numbers wrap: "newer" is the one half a lap ahead. */
    if (oka && (!okb || (int32_t)(sa - sb) >= 0)) {
        *p = pa;
        if (seq) *seq = sa;
        return 1;
    }
    *p = pb;
    if (seq) *seq = sb;
    return 2;
}
