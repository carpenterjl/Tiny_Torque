/*
 * host_params.h — the parameter store on a host target (FW-07).
 *
 * On the car the firmware's parameters live in flash behind
 * tt_hal_params_load() / tt_hal_params_store() (hal/tt_hal.h), as the image
 * core/tt_pstore.h frames. The host builds (the sim DLL, the PIL executable
 * behind the lockstep bridge) keep the same image in a file instead, named
 * by the environment variable TT_PARAMS_STORE; unset, or naming no readable
 * image, the firmware runs on its compiled-in set exactly as before. So a
 * set calibrated on the host (Tools/gen_params.js --image, or the
 * Calibration Companion's export) is tried in the sim through the path the
 * car will use, and every run's sidecar names the set by its hash either
 * way.
 *
 * Read live, as route_env.h reads TT_ROUTE: on Windows the DLL's C runtime
 * took its copy of the environment when the DLL loaded.
 */
#ifndef HOST_PARAMS_H
#define HOST_PARAMS_H

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "tt_pstore.h"

#ifdef _WIN32
__declspec(dllimport) unsigned long __stdcall GetEnvironmentVariableA(const char *name,
                                                                      char *buf,
                                                                      unsigned long size);
#endif

static inline const char *tt_host_params_path(char *buf, size_t cap)
{
#ifdef _WIN32
    unsigned long n = GetEnvironmentVariableA("TT_PARAMS_STORE", buf, (unsigned long)cap);
    if (n == 0 || n >= cap) buf[0] = '\0';
#else
    const char *e = getenv("TT_PARAMS_STORE");
    size_t n = e ? strlen(e) : 0;
    if (n >= cap) n = 0;
    if (n) memcpy(buf, e, n);
    buf[n] = '\0';
#endif
    return buf;
}

/* The stored set into `p`: 0 = loaded (and `seq` its write number), 1 = no
 * store named, or one of tt_pstore.h's errors (negative) for a file that
 * is not a usable image. `p` is untouched unless 0. */
static inline int tt_host_params_load(TtParams *p, uint32_t *seq)
{
    char path[512];
    uint8_t img[TT_PSTORE_SIZE + 16u];
    FILE *f;
    size_t n;
    int rc;

    if (tt_host_params_path(path, sizeof(path))[0] == '\0') return 1;
    f = fopen(path, "rb");
    if (f == NULL) return TT_PSTORE_E_SHORT;
    n = fread(img, 1, sizeof(img), f);
    fclose(f);
    rc = tt_pstore_read(img, n, p, seq);
    return rc;
}

/* Write `p` to the store as write number `seq`: 0 = done, 1 = no store
 * named, -1 = the file could not be written or the set does not validate. */
static inline int tt_host_params_store(const TtParams *p, uint32_t seq)
{
    char path[512];
    uint8_t img[TT_PSTORE_SIZE];
    FILE *f;
    size_t n = tt_pstore_write(img, sizeof(img), p, seq);

    if (tt_host_params_path(path, sizeof(path))[0] == '\0') return 1;
    if (n == 0) return -1;
    f = fopen(path, "wb");
    if (f == NULL) return -1;
    if (fwrite(img, 1, n, f) != n) { fclose(f); return -1; }
    fclose(f);
    return 0;
}

#endif /* HOST_PARAMS_H */
