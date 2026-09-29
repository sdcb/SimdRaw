#include "rawspeed_shim.h"
#include "rawspeed_shim_version.h"

#include "RawSpeed-API.h"
#include "io/FileIOException.h"
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <memory>
#include <new>
#include <string>

namespace {

std::unique_ptr<const rawspeed::CameraMetaData> g_metadata;
std::string g_last_error;

using Clock = std::chrono::steady_clock;

double elapsedMs(Clock::time_point start) {
  return std::chrono::duration<double, std::milli>(Clock::now() - start)
      .count();
}

void copyString(char* dst, size_t cap, const char* src) {
  std::snprintf(dst, cap, "%s", src ? src : "");
}

int32_t classify(const rawspeed::RawspeedException& e) {
  if (dynamic_cast<const rawspeed::FileIOException*>(&e))
    return RAWSPEED_ERR_IO;
  const std::string msg = e.what();
  if (msg.find("No decoder found") != std::string::npos ||
      msg.find("not supported") != std::string::npos ||
      msg.find("Not supported") != std::string::npos ||
      msg.find("nsupported") != std::string::npos)
    return RAWSPEED_ERR_UNSUPPORTED;
  return RAWSPEED_ERR_CORRUPT;
}

void fail(RawSpeedResult* out, int32_t code, const char* msg) {
  out->error_code = code;
  copyString(out->error_msg, sizeof(out->error_msg), msg);
}

int32_t decode(const char* path, RawSpeedResult* out) {
  const rawspeed::FileReader reader(path);
  auto [storage, buf] = reader.readFile();

  const auto totalStart = Clock::now();
  rawspeed::RawParser parser(buf);
  auto decoder = parser.getDecoder(g_metadata.get());
  decoder->failOnUnknown = false;
  decoder->checkSupport(g_metadata.get());

  const auto decodeStart = Clock::now();
  decoder->decodeRaw();
  out->decode_time_ms = elapsedMs(decodeStart);

  decoder->decodeMetaData(g_metadata.get());
  out->total_time_ms = elapsedMs(totalStart);

  const rawspeed::RawImage raw = decoder->mRaw;
  const rawspeed::iPoint2D uncropped = raw->getUncroppedDim();
  const rawspeed::iPoint2D cropOffset = raw->getCropOffset();

  out->width = uncropped.x;
  out->height = uncropped.y;
  out->cpp = static_cast<int32_t>(raw->getCpp());
  out->crop_left = cropOffset.x;
  out->crop_top = cropOffset.y;
  out->crop_width = raw->dim.x;
  out->crop_height = raw->dim.y;
  out->data_type = raw->getDataType() == rawspeed::RawImageType::F32
                       ? RAWSPEED_DATA_F32
                       : RAWSPEED_DATA_U16;
  out->bytes_per_sample = static_cast<int32_t>(raw->getBpp() / raw->getCpp());
  out->black_level = raw->blackLevel;
  out->white_point = raw->whitePoint ? *raw->whitePoint : -1;
  out->cfa_filters = raw->isCFA ? raw->cfa.getDcrawFilter() : 0;
  copyString(out->make, sizeof(out->make), raw->metadata.make.c_str());
  copyString(out->model, sizeof(out->model), raw->metadata.model.c_str());

  const rawspeed::Array2DRef<std::byte> img =
      raw->getByteDataAsUncroppedArray2DRef();
  const size_t rowBytes = static_cast<size_t>(img.width());
  const size_t rows = static_cast<size_t>(img.height());
  auto* pixels = static_cast<std::byte*>(std::malloc(rowBytes * rows));
  if (!pixels) {
    fail(out, RAWSPEED_ERR_OUT_OF_MEMORY, "failed to allocate output buffer");
    return out->error_code;
  }
  for (size_t y = 0; y < rows; ++y)
    std::memcpy(pixels + y * rowBytes, &img(static_cast<int>(y), 0), rowBytes);

  out->pixels = pixels;
  out->pixels_len =
      static_cast<int64_t>(rowBytes * rows / out->bytes_per_sample);
  out->error_code = RAWSPEED_OK;
  return RAWSPEED_OK;
}

} // namespace

extern "C" {

const char* rawspeed_version(void) { return RAWSPEED_SHIM_VERSION; }

int32_t rawspeed_init(const char* cameras_xml_path) {
  try {
    g_metadata = std::make_unique<const rawspeed::CameraMetaData>(
        cameras_xml_path);
    g_last_error.clear();
    return RAWSPEED_OK;
  } catch (const rawspeed::RawspeedException& e) {
    g_last_error = e.what();
    return classify(e) == RAWSPEED_ERR_IO ? RAWSPEED_ERR_IO
                                          : RAWSPEED_ERR_CORRUPT;
  } catch (const std::bad_alloc&) {
    g_last_error = "out of memory";
    return RAWSPEED_ERR_OUT_OF_MEMORY;
  } catch (const std::exception& e) {
    g_last_error = e.what();
    return RAWSPEED_ERR_INTERNAL;
  } catch (...) {
    g_last_error = "unknown exception";
    return RAWSPEED_ERR_INTERNAL;
  }
}

const char* rawspeed_last_error(void) { return g_last_error.c_str(); }

int32_t rawspeed_decode(const char* path, RawSpeedResult* out) {
  if (!out)
    return RAWSPEED_ERR_INTERNAL;
  std::memset(out, 0, sizeof(*out));
  out->black_level = -1;
  out->white_point = -1;

  if (!g_metadata) {
    fail(out, RAWSPEED_ERR_NOT_INITIALIZED, "rawspeed_init() was not called");
    return out->error_code;
  }
  if (!path) {
    fail(out, RAWSPEED_ERR_IO, "path is null");
    return out->error_code;
  }

  try {
    return decode(path, out);
  } catch (const rawspeed::RawspeedException& e) {
    fail(out, classify(e), e.what());
  } catch (const std::bad_alloc&) {
    fail(out, RAWSPEED_ERR_OUT_OF_MEMORY, "out of memory");
  } catch (const std::exception& e) {
    fail(out, RAWSPEED_ERR_INTERNAL, e.what());
  } catch (...) {
    fail(out, RAWSPEED_ERR_INTERNAL, "unknown exception");
  }
  std::free(out->pixels);
  out->pixels = nullptr;
  out->pixels_len = 0;
  return out->error_code;
}

void rawspeed_free(RawSpeedResult* result) {
  if (!result)
    return;
  std::free(result->pixels);
  result->pixels = nullptr;
  result->pixels_len = 0;
}

void rawspeed_shutdown(void) { g_metadata.reset(); }

} // extern "C"
