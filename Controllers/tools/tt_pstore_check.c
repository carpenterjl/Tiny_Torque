/*
 * tt_pstore_check.c — read a parameter image with the firmware's own store
 * code (FW-07) and say what it holds.
 *
 *   tt_pstore_check <image.ttp> [opus_vector|opus_vector_foc]
 *
 * Prints the image's write number and parameter hash. With a set name, the
 * hash must equal the built-in set's: the check CI runs on the image
 * Tools/gen_params.js --image writes, so the JavaScript and the C agree on
 * every byte of the struct. Exit 0 when the image reads (and matches).
 */
#define _CRT_SECURE_NO_WARNINGS
#include "tt_pstore.h"

#include <stdio.h>
#include <string.h>

int main(int argc, char **argv)
{
    static unsigned char buf[4096];
    TtParams p;
    uint32_t seq = 0, want = 0;
    FILE *f;
    size_t n;
    int rc;

    if (argc < 2) { fprintf(stderr, "usage: tt_pstore_check <image.ttp> [set name]\n"); return 2; }
    f = fopen(argv[1], "rb");
    if (!f) { fprintf(stderr, "cannot open %s\n", argv[1]); return 2; }
    n = fread(buf, 1, sizeof(buf), f);
    fclose(f);
    rc = tt_pstore_read(buf, n, &p, &seq);
    if (rc != TT_PSTORE_OK) { printf("%s: not a usable image (%d)\n", argv[1], rc); return 1; }
    printf("%s: %u bytes, TtParams v%u, write %lu, hash %08lX\n", argv[1], (unsigned)n,
           (unsigned)p.version, (unsigned long)seq, (unsigned long)tt_params_hash(&p));
    if (argc > 2) {
        if (!strcmp(argv[2], "opus_vector")) want = tt_params_hash(&tt_params_opus_vector);
        else if (!strcmp(argv[2], "opus_vector_foc")) want = tt_params_hash(&tt_params_opus_vector_foc);
        else { fprintf(stderr, "unknown set %s\n", argv[2]); return 2; }
        if (tt_params_hash(&p) != want) {
            printf("MISMATCH: the built-in %s hashes %08lX\n", argv[2], (unsigned long)want);
            return 1;
        }
        printf("matches the built-in %s\n", argv[2]);
    }
    return 0;
}
