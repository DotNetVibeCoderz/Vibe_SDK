#ifndef SIWARE_DSP_H
#define SIWARE_DSP_H
#include <stddef.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
/* ABI 1. Status: 0 success, 1 invalid argument/allocation, 2 domain/nonfinite.
 * Buffers must be valid for len doubles, aligned, and output must not overlap
 * any input. Caller owns all memory. Input buffers may overlap one another.
 * No concurrent input mutation or output access is allowed during a call.
 * Output remains unchanged on errors. len must be positive. */
uint32_t siware_dsp_abi_version(void);
int32_t siware_dsp_snv(const double *input, double *output, size_t len);
int32_t siware_dsp_absorbance(const double *sample, const double *dark,
    const double *reference, double *output, size_t len);
int32_t siware_dsp_smooth(const double *input, double *output, size_t len, size_t window);
#ifdef __cplusplus
}
#endif
#endif
