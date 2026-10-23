#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct ufx_capture ufx_capture;

typedef enum ufx_capture_kind {
    UFX_CAPTURE_KIND_NONE = 0,
    UFX_CAPTURE_KIND_DMABUF = 1,
    UFX_CAPTURE_KIND_MEMFD = 2,
} ufx_capture_kind;

typedef struct ufx_capture_frame {
    ufx_capture_kind kind;
    int fd; /* dmabuf or memfd; -1 if unused */
    uint32_t width;
    uint32_t height;
    uint32_t stride;
    uint32_t fourcc; /* DRM fourcc, 0 if unknown */
    uint64_t modifier;
    void* mapped; /* CPU pointer for memfd/staging; NULL for dmabuf-only */
    size_t mapped_size;
} ufx_capture_frame;

/* Opens a portal ScreenCast session (pops the compositor picker) and starts
 * a PipeWire consumer. Returns 0 on success. On hosts without a portal,
 * returns non-zero and writes a short reason into err. */
int ufx_capture_open(ufx_capture** out, char* err, size_t errlen);

int ufx_capture_poll_frame(ufx_capture* cap, ufx_capture_frame* frame, int timeout_ms);

void ufx_capture_release_frame(ufx_capture* cap, ufx_capture_frame* frame);

void ufx_capture_close(ufx_capture* cap);

#ifdef __cplusplus
}
#endif
