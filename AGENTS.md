# Agent notes for SimdRaw

## Harness: never trigger a GC

The harness measures the library the way an application uses it, and applications do not collect garbage on behalf
of a library. So `src/Sdcb.SimdRaw.Harness` must not call `GC.Collect`, `GC.WaitForPendingFinalizers`,
`GC.TryStartNoGCRegion`, `GCSettings.LargeObjectHeapCompactionMode`, or anything else that forces, defers or tunes a
collection, not even outside the timed region (for example between files or before taking the baseline). Read-only
counters (`GC.GetTotalMemory(false)`, `GC.GetAllocatedBytesForCurrentThread`, `GC.CollectionCount`) are fine.

If memory numbers look inflated by garbage from earlier files, fix it in the library, not in the harness: large
buffers (file bytes, mosaics, developed images, big scratch buffers) are owned native memory
(`Sdcb.SimdRaw.NativeBuffer<T>`), freed deterministically by `Dispose` on `RawFile` / `RawMosaic` / `RawBitmap`.
`NativeBuffer<T>` intentionally has no finalizer (CA2015: a span does not keep its owner alive, so a finalizer could
free memory still in use); undisposed objects leak, so the harness must dispose everything it opens. Keep per-call
managed allocations small and short-lived.

## Other conventions

- Harness runs are cold and single-pass: no warmup, no repetitions.
- ISA tiers are selected only with runtime knobs (`DOTNET_EnableAVX2=0`, `DOTNET_EnableHWIntrinsic=0`, ...); the
  library exposes no ISA switch. Every SIMD kernel must be bit-identical to its scalar path
  (`tests/Sdcb.SimdRaw.Tests`, `--isa-matrix`).
- Decode correctness is `golden_libraw` (SHA-256 of the uncropped mosaic). Develop has no external golden; its hash
  must only match across ISA tiers.
