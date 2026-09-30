# SimdRaw

`Sdcb.SimdRaw` aims to be a pure .NET, zero C/C++ dependency, high-performance RAW decoder with hand-written
multi-ISA kernels (avx512 → avx2 → advsimd → `Vector<T>` → scalar, dispatched automatically; ISA tiers are tested
with `DOTNET_EnableAVX512F=0`, `DOTNET_EnableHWIntrinsic=0`, ...). The reference point is RawSpeed.

**Status (M1):** two decode paths are implemented and byte-exact with LibRaw, and a develop stage turns their mosaic
into an sRGB image. Every other fixture file reports `unsupported`.

```
src/Sdcb.SimdRaw/            library: TIFF/maker-note parsing, decoders, develop pipeline
src/Sdcb.SimdRaw.Harness/    benchmark + correctness harness (net10.0 console)
tests/Sdcb.SimdRaw.Tests/    kernel equivalence tests (AVX2 / Vector<T> vs scalar, random input) + parser fuzzing
native/rawspeed-shim/        extern "C" shim over RawSpeed + build scripts for the reference engine
.github/workflows/           benchmark.yml (ubuntu-latest + windows-latest)
```

```csharp
using RawFile file = RawDecoder.OpenFile("DSC04126.ARW");   // parses metadata; never throws for unknown formats
using RawMosaic mosaic = file.DecodeMosaic();               // u16 mosaic incl. masked edges, == LibRaw raw_image
using RawBitmap rgb = file.Develop(mosaic);                 // sRGB, Rgb48 by default (file.Develop() decodes itself)
```

## Supported decode paths

| LibRaw decode path | fixture file | camera | detection | output |
| --- | --- | --- | --- | --- |
| `sony_arw2_load_raw` | `sony_arw2_load_raw_DSC04126.ARW` | Sony ILCE-7S | raw IFD with compression 32767 and `StripByteCounts == width·height` | `curve[pix << 1]`, 14-bit range, 2816×1872 |
| `nikon_14bit_load_raw` | `nikon_14bit_load_raw_D06_0312.NEF` | Nikon D6 | Nikon, compression 34713, 14 bps, CFA, `StripByteCounts == height · ceil16(width·7/4)` | 14-bit samples, 5584×3728 |

- **Sony ARW2** ("compressed RAW"): each row is `raw_width` bytes of 16-byte blocks; a block carries an 11-bit max/min,
  their 4-bit slot indices and 14 × 7-bit deltas (shifted by 0…4) for 16 same-colour pixels, two blocks cover 32 columns.
  Samples go through the Sony tone curve from tag 0x7010. A7R II / A7R III in-camera compressed RAW use the same path.
  The output is byte-exact with `golden_libraw`; RawSpeed's buffer for this file differs (it matches RawSpeed's own golden,
  hence `engine-diff` in the RawSpeed report).
- **Nikon 14-bit packed** (NEF "uncompressed" 14-bit): rows padded to 16 bytes, every 7 bytes are four 14-bit samples
  of a little-endian 56-bit word.
- Everything else, including `nikon_load_padded_packed_raw_D06_0317.NEF` from the same D6, is `unsupported`
  (`RawException` with `RawErrorCode.UnsupportedFormat`, raised by `DecodeMosaic`/`Develop`, not by `Open`).

### Kernels and ISA selection

Each hot loop has a hand-written AVX2 kernel, a portable `Vector<T>` kernel and a scalar path that is the verbatim
LibRaw algorithm. The scalar path also processes the tail of every row, and it runs everything when hardware intrinsics are off. Dispatch is internal
(`Avx2.IsSupported` → AVX2, else `Vector.IsHardwareAccelerated` → `Vector<T>`, else scalar); there is no public ISA switch.
Tiers are selected with runtime knobs: `DOTNET_EnableAVX2=0` gives the 128-bit `Vector<T>` kernels,
`DOTNET_EnableHWIntrinsic=0` the scalar control. There is no separately tuned scalar tier.

| kernel | AVX2 | `Vector<T>` |
| --- | --- | --- |
| ARW2 blocks | one block pair per iteration: `pshufb` gathers the deltas into 16-bit lanes pre-aligned for 0/1/2 preceding special slots, `pmullw`+`psrlw` aligns them, compares against imax/imin select, `unpack`+`vperm2i128` interleave the even/odd blocks | lanes = blocks: two rounds of `Vector.Narrow` de-interleave the block words, header/deltas/slot selection are lane-wise, stride-2 scatter is scalar |
| Sony tone curve | evaluated arithmetically: `curve[j] = j + Σ max(0, j − knee_m)·2^(m−1)` (`psubusw`), valid for ordered knees, otherwise the whole file uses the table path | same formula |
| NEF 14-bit | 4 groups (28 B → 16 samples) per 32-byte load: `vpermd` + `pshufb` + `vpsrlvd` + `packusdw` | lanes = 7-byte groups: lane j of `load(p − j)` starts at `p + 7j`, N loads merged with lane masks, then shifts/masks inside the 64-bit lane |
| develop: black/WB, bilinear, matrix | 16 px per iteration, `vpmulld` / `pavgw` / `vpblendvb` / `packusdw` | `Widen`/`Narrow`, `(a\|b) − ((a^b)>>1)` as exact `pavgw` |

`tests/Sdcb.SimdRaw.Tests` checks every SIMD kernel against the scalar path on random bytes, which produce corrupt blocks,
`min > max`, `imax == imin` (scalar fallback), all shift values and row widths that are not a multiple of the block size.
It also feeds 3000 random TIFF-like buffers to the parser and requires `RawException` only (`dotnet test tests/Sdcb.SimdRaw.Tests`).

## Develop

Decode is deterministic and compared with LibRaw byte for byte. Develop is not compared with LibRaw's or RawSpeed's
rendered pixels: implementations may differ visually. What the harness does test is that it runs for every decoded file,
how fast and with how much memory, and that its output is **bit-identical across ISA tiers**.

Pipeline (`RawFile.Develop`, one output row at a time with a three-row ring buffer, so only O(width) memory is used besides the output):

1. crop to the active area: Nikon maker-note `CropArea` (D6: 8,8,5568×3712); Sony: right-edge trim from the model table
   (A7S: 32 columns, as in RawSpeed's cameras.xml);
2. black level and white balance, scaled to 16 bit and clipped at the white level; the smallest WB multiplier is 1, so a clipped
   pixel saturates in every channel and stays neutral. Sources: Sony = encrypted SR2 sub-IFD (`sony_decrypt`): BlackLevel 0x7310, WhiteLevel 0x787F,
   WB_RGGBLevels 0x7313; Nikon = maker note BlackLevel 0x3D and WB_RBLevels 0x0C, white level 15520 from the model table;
3. bilinear demosaic (Bayer, mirrored borders);
4. camera → linear sRGB (D65): Adobe DNG ColorMatrix2 (the same numbers as in dcraw/LibRaw/RawSpeed) for ILCE-7S, ILCE-7RM2,
   ILCE-7RM3 and D6, turned into `rgb_cam` as in dcraw's `cam_xyz_coeff`; identity for unknown models;
5. sRGB transfer curve (65536-entry table) → `Rgb48` (also `Rgba64`, `Rgb96F`).

### Memory ownership

File bytes (`OpenFile` / seekable `Open(Stream)` / `CopyInput`), mosaics, developed bitmaps and develop scratch rows are
native memory, not GC heap, so `Dispose` returns them to the OS right away and the library does not rely on the
application (or the GC) to reclaim tens of MB per image. `RawFile`, `RawMosaic` and `RawBitmap` must therefore be
disposed: there is intentionally no finalizer (a `Span` does not keep its owner alive, so finalizer-based freeing could
release memory that is still being read; see CA2015). An undisposed object leaks its buffer, and spans taken from
`Buffer` / `Row` must not be used after `Dispose`.

All per-pixel math is integer: a Q12 gain, `pavgw`-style rounding averages and a Q12 matrix. As a result the AVX2, `Vector<T>` and scalar paths agree by construction,
not just within a tolerance. There is no auto-exposure or highlight reconstruction (the D6 sample is a dim indoor shot and looks dark).

## Results on the reference machine

AMD Ryzen 7 5800X (AVX2, no AVX-512), Windows 11 26200, .NET 10.0.11, single-threaded. Cold single run per tier (each tier is its own process,
`--isa-matrix default,no-avx2,no-hwintrinsic`), median of three matrix runs, in ms. Parentheses show the JIT time included in the number.
The first supported file decoded/developed (the NEF, fixture #18) pays for compiling the shared code.

| file | stage | AVX2 (default) | `Vector<T>` 128-bit (`EnableAVX2=0`) | scalar (`EnableHWIntrinsic=0`) | RawSpeed `c835b05a` |
| --- | --- | ---: | ---: | ---: | ---: |
| `sony_arw2_load_raw_DSC04126.ARW` (2816×1872) | decode | 7.28 (2.8) | 12.15 (3.2) | 22.31 (2.2) | 21.5 |
| | develop → 2784×1872 | 14.65 (0.0) | 20.75 (0.0) | 61.74 (0.0) | — |
| `nikon_14bit_load_raw_D06_0312.NEF` (5584×3728) | decode | 17.69 (6.2) | 20.15 (5.1) | 25.18 (4.5) | 34.3 |
| | develop → 5568×3712 | 72.68 (11.5) | 99.70 (14.7) | 225.8 (9.9) | — |

- Mosaic SHA-256 = `golden_libraw` for both files in all three tiers; develop SHA-256 identical across the three tiers.
- The NEF unpack reads 34.8 MiB and writes 39.7 MiB, so AVX2 and `Vector<T>` are close. Its cold time is dominated by JIT and
  first-touch page faults of the fresh mosaic, not by the kernel (this share varies between runs; 14–31 ms was seen for AVX2).
- Develop of the NEF writes a 118.5 MiB `Rgb48` image; the first touch of that buffer is a large part of the cold develop time.
- Peak working set (MiB): decode NEF +78 (35.7 file bytes + 39.7 mosaic), ARW +21.5; develop NEF +121, ARW +30 on top
  of the resident mosaic (≈ the output size; row buffers are negligible). No garbage collection happened during any
  decode or develop of the whole 48-file run; managed allocation per develop is 0.07–0.21 MiB.
- RawSpeed column: `--engine rawspeed`, same machine, median of three runs (RawSpeed is built without OpenMP, i.e. also single-threaded).

## Harness

```
dotnet run -c Release --project src/Sdcb.SimdRaw.Harness -- --engine <simdraw|rawspeed> [options]

  --data <dir>          fixture directory (default <repo>/testdata)
  --out <dir>           report directory (default <repo>/artifacts/reports)
  --filter <glob>       match file name or decode path, e.g. "sony_*", "*.NEF", "panasonic*"
  --verify <mode>       fixture check: size | sample (default: size of all + sha256 of every 12th file) | full
  --develop <on|off>    develop every decoded file (default on; engines without a develop stage skip it)
  --ppm <8|16|off>      write developed images as binary PPM next to the report (default 8)
  --isa-matrix <tiers>  simdraw only: one cold child process per tier (default, no-avx512, no-avx2, no-hwintrinsic),
                        then require identical mosaic and develop hashes across tiers
```

Exit codes: `0` ok, `1` at least one `fail`/`error` file, develop error, or (with `--isa-matrix`) a cross-tier hash
difference, `2` usage, `3` setup (fixture/engine) failure.

Each run is **cold and single-pass** (no warmup, no repetitions) over the 48 files of
[Sdcb.LibRaw.TestData](https://github.com/sdcb/Sdcb.LibRaw.TestData) and records, per engine:

- **engine initialization time** (`InitializeAsync`: RawSpeed = download-if-needed + library load + `rawspeed_init(cameras.xml)`,
  SimdRaw = JIT warmup), with a per-phase breakdown;
- per file: engine-reported **pure decode time** (fractional ms) and, for SimdRaw, the JIT time inside it (`JitInfo`),
  **accuracy** vs `manifest.golden_libraw`, **peak working set delta** vs the post-init baseline, `GC.GetTotalMemory`
  delta, managed bytes allocated on the decode thread and the number of garbage collections that occurred during it;
- per decoded file, when the engine has a develop stage: a separate **develop** record: time (+ JIT), SHA-256 of the
  `Rgb48` output, output size, peak working set relative to right before develop (mosaic already resident), managed
  allocated bytes, GC count, and the PPM path.

The harness never triggers, defers or tunes a garbage collection ([AGENTS.md](AGENTS.md)): applications don't collect
on behalf of a library either. Per-file peaks stay clean because the library frees its large buffers on `Dispose`
(see *Memory ownership*), and the engine disposes everything it opens. Develop errors gate; `unsupported` does not. RawSpeed has no develop stage, so it is skipped there.

Output: a console table, `report-<engine>-<utc timestamp>.json` (all raw data), `.md` (summary with mean/median/P95,
per-decode-path table, develop table, failure list, layout/engine differences) and `report-…-ppm/*.ppm`.
`--isa-matrix` writes one report per tier (`report-simdraw-<stamp>-<tier>.*`, PPMs only for the first tier) plus
`isa-matrix-<stamp>.md` with per-tier times and the hash comparison.

### Fixture

If `--data` does not contain a valid fixture (manifest present, every file with the manifest size, sampled/full sha256),
the harness shallow-clones `https://github.com/sdcb/Sdcb.LibRaw.TestData` and extracts the blobs itself: the data repo
contains `…_4:3.RW2`, which `git checkout` cannot create on Windows, so files are written with `<>:"|?*\` replaced by `_`.
The temporary clone is deleted afterwards (the fixture occupies ~692 MB once). An existing `.git` in the directory is
reused instead of downloading. A valid fixture is never re-downloaded.

### Engines

| engine | implementation |
| --- | --- |
| `simdraw` | `Sdcb.SimdRaw.RawDecoder`; times `RawFile.DecodeMosaic()` (file read and metadata parsing excluded), then `RawFile.Develop(mosaic)` separately. |
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
`K32GetProcessMemoryInfo`). Develop uses the same method with the working set right before develop as its baseline.

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
(key = engine package name) → build + kernel tests → `--engine rawspeed` (gating) →
`--engine simdraw --isa-matrix default,no-avx2,no-hwintrinsic --ppm off` (gating: fail/error files, develop errors and
cross-tier hash differences) → job summary + report artifacts.
