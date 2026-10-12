/*
 * route_env.h — which route a host-built firmware follows (NAV-04).
 *
 * The host targets (the sim DLL, the PIL executable behind the lockstep
 * bridge, the replay tool) take the route from the environment variable
 * TT_ROUTE: a name built into core/routes_gen.c, optionally "@<m/s>" for a
 * lower top speed ("oval@2"); unset or empty = the fixed manoeuvre. The sim
 * sets it before it loads the firmware (-opusRoute), and the bridge's child
 * process inherits it. A board build chooses its route in its own config.
 *
 * Read live on every call: in the sim DLL the C runtime's copy of the
 * environment was taken when the DLL loaded, and Unity sets the variable
 * later, so on Windows this asks the process itself.
 */
#ifndef ROUTE_ENV_H
#define ROUTE_ENV_H

#include <stddef.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
__declspec(dllimport) unsigned long __stdcall GetEnvironmentVariableA(const char *name,
                                                                      char *buf,
                                                                      unsigned long size);
#endif

/* The route spec, "" when there is none. `buf` holds it. */
static inline const char *tt_route_env(char *buf, size_t cap)
{
#ifdef _WIN32
    unsigned long n = GetEnvironmentVariableA("TT_ROUTE", buf, (unsigned long)cap);
    if (n == 0 || n >= cap) buf[0] = '\0';
#else
    const char *e = getenv("TT_ROUTE");
    size_t n = e ? strlen(e) : 0;
    if (n >= cap) n = 0;
    if (n) memcpy(buf, e, n);
    buf[n] = '\0';
#endif
    return buf;
}

#endif /* ROUTE_ENV_H */
