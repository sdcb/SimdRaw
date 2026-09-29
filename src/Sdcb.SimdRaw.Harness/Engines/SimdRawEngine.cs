using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Sdcb.SimdRaw.Harness.Engines;

/// <summary>Wraps <see cref="RawDecoder"/>. Decoders are not implemented yet, so every file reports
/// <c>unsupported</c>; the plumbing (timing, hashing, memory) is already the real one.</summary>
public sealed class SimdRawEngine : IDecodeEngine, IEngineDiagnostics
{
    private readonly List<InitPhase> _phases = [];

    public string Name => "simdraw";

    public string Version { get; } =
        typeof(RawDecoder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(RawDecoder).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public IReadOnlyList<InitPhase> InitPhases => _phases;

    public IReadOnlyDictionary<string, string> Properties { get; } = new Dictionary<string, string>
    {
        ["assembly"] = typeof(RawDecoder).Assembly.FullName ?? "Sdcb.SimdRaw",
    };

    public Task InitializeAsync(CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        RuntimeHelpers.RunClassConstructor(typeof(RawDecoder).TypeHandle);
        _phases.Add(new InitPhase("jit-warmup", sw.Elapsed.TotalMilliseconds));
        return Task.CompletedTask;
    }

    public Task<DecodeResult> DecodeAsync(string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using RawFile file = RawDecoder.OpenFile(filePath);
            long start = Stopwatch.GetTimestamp();
            using RawMosaic mosaic = file.DecodeMosaic();
            double decodeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            ReadOnlySpan<ushort> buffer = mosaic.Buffer.Span;
            string hash = MosaicHasher.Sha256OfRows(buffer, mosaic.Width, mosaic.Stride, mosaic.Height * mosaic.PlaneCount);
            return Task.FromResult(new DecodeResult(true, null, decodeMs, hash, HashAlgorithms.Sha256)
            {
                Layout = new MosaicLayout(mosaic.Width, mosaic.Height, mosaic.PlaneCount, "u16")
                {
                    CropLeft = mosaic.ActiveArea.Left,
                    CropTop = mosaic.ActiveArea.Top,
                    CropWidth = mosaic.ActiveArea.Width,
                    CropHeight = mosaic.ActiveArea.Height,
                    BlackLevel = mosaic.BlackLevel,
                    WhiteLevel = mosaic.WhiteLevel,
                },
            });
        }
        catch (RawException ex)
        {
            return Task.FromResult(DecodeResult.Failed(MapError(ex.Code), ex.Message));
        }
        catch (NotImplementedException ex)
        {
            return Task.FromResult(DecodeResult.Failed(DecodeErrorCodes.NotImplemented, ex.Message));
        }
    }

    private static string MapError(RawErrorCode code) => code switch
    {
        RawErrorCode.UnsupportedFormat or RawErrorCode.UnsupportedVariant => DecodeErrorCodes.Unsupported,
        RawErrorCode.CorruptData or RawErrorCode.TruncatedFile => DecodeErrorCodes.DecodeError,
        RawErrorCode.IoError => DecodeErrorCodes.IoError,
        RawErrorCode.OutOfMemory => DecodeErrorCodes.OutOfMemory,
        _ => DecodeErrorCodes.Internal,
    };

    public void Dispose() { }
}
