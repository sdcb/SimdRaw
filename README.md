# SimdRaw

`Sdcb.SimdRaw` aims to be a pure .NET, zero C/C++ dependency, high-performance RAW decoder with hand-written
multi-ISA kernels (avx512 → avx2 → advsimd → `Vector<T>` → scalar, dispatched automatically; ISA tiers are tested
with `DOTNET_EnableAVX512F=0`, `DOTNET_EnableHWIntrinsic=0`, ...). The reference point is RawSpeed.

**Status:** the evaluation harness is in place; `Sdcb.SimdRaw` only contains the public API shape
(`RawDecoder.OpenFile/Open → RawFile.DecodeMosaic() → RawMosaic`) with stub decoders.

```
src/Sdcb.SimdRaw/            public API (stubs)
src/Sdcb.SimdRaw.Harness/    benchmark + correctness harness (net10.0 console)
native/rawspeed-shim/        extern "C" shim over RawSpeed + build scripts for the reference engine
.github/workflows/           benchmark.yml (ubuntu-latest + windows-latest)
```

## Harness

```
dotnet run -c Release --project src/Sdcb.SimdRaw.Harness -- --engine <simdraw|rawspeed> [options]

  --data <dir>      fixture directory (default <repo>/testdata)
  --out <dir>       report directory (default <repo>/artifacts/reports)
  --filter <glob>   match file name or decode path, e.g. "sony_*", "*.NEF", "panasonic*"
  --verify <mode>   fixture check: size | sample (default: size of all + sha256 of every 12th file) | full
```

Exit codes: `0` ok, `1` at least one `fail`/`error` file, `2` usage, `3` setup (fixture/engine) failure.

Each run is **cold and single-pass** (no warmup, no repetitions) over the 48 files of
[Sdcb.LibRaw.TestData](https://github.com/sdcb/Sdcb.LibRaw.TestData) and records, per engine:

- **engine initialization time** (`InitializeAsync`: RawSpeed = download-if-needed + library load + `rawspeed_init(cameras.xml)`,
  SimdRaw = JIT warmup), with a per-phase breakdown;
- per file: engine-reported **pure decode time** (fractional ms), **accuracy** vs `manifest.golden_libraw`,
  **peak working set delta** vs the post-init baseline, `GC.GetTotalMemory` delta and bytes allocated on the decode thread.

Output: a console table, `report-<engine>-<utc timestamp>.json` (all raw data) and `.md` (summary with mean/median/P95,
per-decode-path table, failure list, layout/engine differences).

### Fixture

If `--data` does not contain a valid fixture (manifest present, every file with the manifest size, sampled/full sha256),
the harness shallow-clones `https://github.com/sdcb/Sdcb.LibRaw.TestData` and extracts the blobs itself: the data repo
contains `…_4:3.RW2`, which `git checkout` cannot create on Windows, so files are written with `<>:"|?*\` replaced by `_`.
The temporary clone is deleted afterwards (the fixture occupies ~692 MB once). An existing `.git` in the directory is
reused instead of downloading. A valid fixture is never re-downloaded.

### Engines

| engine | implementation |
| --- | --- |
| `simdraw` | `Sdcb.SimdRaw.RawDecoder`; times `RawFile.DecodeMosaic()` only (file read excluded). Stub today: every file is `unsupported`. |
| `rawspeed` | RawSpeed via the C ABI shim, loaded with `NativeLibrary` + function pointers (no reflection, AOT-friendly). |

RawSpeed binaries are pinned (URL + size + sha256) in `src/Sdcb.SimdRaw.Harness/engine-manifest.json` and downloaded
once into `%LOCALAPPDATA%\simdraw-engines` / `~/.cache/simdraw-engines` (override with `SIMDRAW_ENGINE_CACHE`).
A cached file is only reused if its hash matches. To try a local build, set `SIMDRAW_RAWSPEED_LIB=<path to rawspeed.dll|so>`
(`cameras.xml` is taken from the same directory, or `SIMDRAW_RAWSPEED_CAMERAS_XML`).

The harness computes SHA-256 over the engine's uncropped mosaic (rows packed, little-endian samples) and compares it with
`golden_libraw`. For RawSpeed it additionally computes rstest's "md5 of per-line md5s" and checks it against
`golden_rawspeed` (the engine's own golden) to prove the shim reproduces RawSpeed exactly.

### Status per file

| status | meaning | gates CI |
| --- | --- | --- |
| `pass` | SHA-256 equals `golden_libraw` | |
| `fail` | same buffer geometry as LibRaw, different content, unexplained | yes |
| `layout-diff` | geometry / sample type differs from LibRaw's buffer (`raw_image` = 1 u16/px incl. masked edges, `color4_image` = 4 u16/px); dims reported | no |
| `engine-diff` | same geometry, different values, but identical to the engine's own golden, or listed with its exact hash in `known-diffs.tsv` | no |
| `unsupported` | engine reports unsupported, or fails on a file its own golden marks `unsupported` | no |
| `no-golden` | decoded, manifest has no `golden_libraw` | no |
| `error` | any other failure (IO, crash, decode error on a file the engine should handle) | yes |

`known-diffs.tsv` entries pin the mosaic hash, so any change in engine output turns the file back into `fail`.

### Memory

The baseline working set is taken once after engine initialization. On Linux the kernel peak (`VmHWM`) is reset before
every file via `/proc/self/clear_refs`, giving an exact per-file peak. On Windows the OS peak is monotonic, so the per-file
peak is the OS peak when it rose during the file, otherwise the maximum of a 1 ms background sampler (allocation-free
`K32GetProcessMemoryInfo`).

## RawSpeed reference engine

Current build: RawSpeed `c835b05a` (`v3.5-2998-gc835b05a`), published as

| RID | library | cameras.xml |
| --- | --- | --- |
| win-x64 | https://cv-public.sdcb.ai/2026/rawspeed/20260929_c835b05a.dll | https://cv-public.sdcb.ai/2026/rawspeed/20260929_c835b05a.cameras.xml |
| linux-x64 | https://cv-public.sdcb.ai/2026/rawspeed/20260929_c835b05a.so | same file |

- **win-x64** `rawspeed.dll`: RawSpeed does not compile with `cl.exe`, so it is built with clang++ (LLVM 23) targeting the
  MSVC ABI inside a VS developer shell, static CRT (`/MT`), pugixml/zlib/libjpeg-turbo from vcpkg `x64-windows-static`.
  Only dependency: `KERNEL32.dll`.
- **linux-x64** `rawspeed.so`: GCC 12 on ubuntu:22.04, vcpkg static PIC deps, static libstdc++/libgcc, all non-shim
  symbols hidden. Dependencies: `libc`, `libm`, `ld-linux` (GLIBC ≥ 2.35).
- Both: `BINARY_PACKAGE_BUILD=ON` (portable codegen, no `-march=native`), `WITH_OPENMP=OFF` (single-threaded decode;
  LLVM for Windows only ships a DLL OpenMP runtime), `decode_time_ms` wraps `RawDecoder::decodeRaw()` only.

The binaries statically contain RawSpeed (LGPL-2.1+), pugixml (MIT), zlib and libjpeg-turbo (BSD-style); the shim
source and build scripts in this repo rebuild them from any RawSpeed checkout.

Shim API (`native/rawspeed-shim/rawspeed_shim.h`): `rawspeed_version`, `rawspeed_init(cameras_xml)`, `rawspeed_last_error`,
`rawspeed_decode(path, RawSpeedResult*)`, `rawspeed_free`, `rawspeed_shutdown`. RawSpeed exceptions never cross the ABI;
they become `error_code` + `error_msg`.

### Rebuilding / publishing

```powershell
# win-x64 (needs VS 2026 C++ tools, LLVM, vcpkg; see parameters in the script)
pwsh native/rawspeed-shim/build-win-x64.ps1 -RawSpeedSrc C:\path\to\rawspeed

# linux-x64 (Docker)
docker run --rm -v ${PWD}:/simdraw:ro -v C:\path\to\rawspeed:/rawspeed:ro -v ${PWD}\artifacts\linux:/out `
  ubuntu:22.04 bash /simdraw/native/rawspeed-shim/build-linux-x64.sh

# upload (key = <yyyymmdd>_<rawspeed short sha>)
npx wrangler r2 object put cv-public/2026/rawspeed/<key>.dll --file rawspeed.dll --remote
npx wrangler r2 object put cv-public/2026/rawspeed/<key>.so --file rawspeed.so --remote
npx wrangler r2 object put cv-public/2026/rawspeed/<key>.cameras.xml --file cameras.xml --remote
```

Then update `engine-manifest.json` (version, URLs, sizes, sha256). The CI engine cache key is derived from the version,
so a new version is downloaded exactly once per runner OS.

## CI

`.github/workflows/benchmark.yml` runs on `workflow_dispatch`, daily schedule and pushes to `main`, on `ubuntu-latest`
and `windows-latest`: restore fixture cache (key = hash of the data repo's `manifest.tsv`) → restore engine cache
(key = engine package name) → `--engine rawspeed` (gating) → `--engine simdraw` (non-gating while decoders are stubs) →
job summary + report artifacts.
