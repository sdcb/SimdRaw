/*
 * Minimal C ABI over RawSpeed (https://github.com/darktable-org/rawspeed),
 * consumed by Sdcb.SimdRaw.Harness via P/Invoke.
 *
 * Linked statically with RawSpeed (LGPL-2.1+), pugixml, zlib and libjpeg.
 */
#ifndef RAWSPEED_SHIM_H
#define RAWSPEED_SHIM_H

#include <stdint.h>

#if defined(_WIN32)
#define RAWSPEED_SHIM_API __declspec(dllexport)
#else
#define RAWSPEED_SHIM_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum RawSpeedErrorCode {
  RAWSPEED_OK = 0,
  RAWSPEED_ERR_UNSUPPORTED = 1,  /* no decoder / camera or variant not supported */
  RAWSPEED_ERR_CORRUPT = 2,      /* decoder/parser rejected the data */
  RAWSPEED_ERR_IO = 3,           /* file could not be opened or read */
  RAWSPEED_ERR_NOT_INITIALIZED = 4,
  RAWSPEED_ERR_OUT_OF_MEMORY = 5,
  RAWSPEED_ERR_INTERNAL = 6,
};

enum RawSpeedDataType {
  RAWSPEED_DATA_U16 = 0,
  RAWSPEED_DATA_F32 = 1,
};

/* Layout is mirrored by RawSpeedNative.RawSpeedResult in the harness; keep in sync. */
typedef struct RawSpeedResult {
  int32_t error_code;
  int32_t width, height;      /* uncropped sensor dims, in pixels */
  int32_t cpp;                /* components per pixel (1 = CFA mosaic, 3 = sRaw/linear) */
  int32_t crop_left, crop_top, crop_width, crop_height;
  int32_t data_type;          /* RawSpeedDataType */
  int32_t bytes_per_sample;   /* 2 (u16) or 4 (f32) */
  int32_t black_level;        /* -1 when unknown */
  int32_t white_point;        /* -1 when unknown */
  uint32_t cfa_filters;       /* dcraw-style filters word, 0 when not CFA */
  int32_t reserved;
  double decode_time_ms;      /* RawDecoder::decodeRaw() only */
  double total_time_ms;       /* parse + checkSupport + decodeRaw + decodeMetaData, no file IO */
  void* pixels;               /* width*cpp samples per row, height rows, no padding; owned by shim */
  int64_t pixels_len;         /* number of samples */
  char make[64];
  char model[64];
  char error_msg[256];
} RawSpeedResult;

/* Build identification, e.g. "rawspeed c835b05a (v3.5-2998-gc835b05a)". */
RAWSPEED_SHIM_API const char* rawspeed_version(void);

/* Loads cameras.xml (UTF-8 path). Returns 0 on success; call again to reload. */
RAWSPEED_SHIM_API int32_t rawspeed_init(const char* cameras_xml_path);

/* Message of the last rawspeed_init() failure, or "" if none. */
RAWSPEED_SHIM_API const char* rawspeed_last_error(void);

/* Decodes one file (UTF-8 path). Always fills *out; release it with rawspeed_free(). */
RAWSPEED_SHIM_API int32_t rawspeed_decode(const char* path, RawSpeedResult* out);

RAWSPEED_SHIM_API void rawspeed_free(RawSpeedResult* result);

/* Releases cameras.xml metadata. */
RAWSPEED_SHIM_API void rawspeed_shutdown(void);

#ifdef __cplusplus
}
#endif

#endif /* RAWSPEED_SHIM_H */
