using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Sdcb.SimdRaw.Harness.Engines;

/// <summary>Wraps <see cref="RawDecoder"/>: times <see cref="RawFile.DecodeMosaic"/> and, separately,
/// <see cref="RawFile.Develop(RawMosaic, DevelopOptions?)"/> on the decoded mosaic.</summary>
public sealed class SimdRawEngine : IDecodeEngine, IEngineDiagnostics, IDevelopEngine
{
    private readonly List<InitPhase> _phases = [];
    private RawFile? _file;
    private RawMosaic? _mosaic;

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
        ReleaseDecoded();
        RawFile? file = null;
        try
        {
            file = RawDecoder.OpenFile(filePath);
            TimeSpan jit = System.Runtime.JitInfo.GetCompilationTime(currentThread: true);
            long start = Stopwatch.GetTimestamp();
            RawMosaic mosaic = file.DecodeMosaic();
            double decodeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            double jitMs = (System.Runtime.JitInfo.GetCompilationTime(currentThread: true) - jit).TotalMilliseconds;

            ReadOnlySpan<ushort> buffer = mosaic.Buffer.Span;
            string hash = MosaicHasher.Sha256OfRows(buffer, mosaic.Width, mosaic.Stride, mosaic.Height * mosaic.PlaneCount);
            _file = file;
            _mosaic = mosaic;
            file = null;
            return Task.FromResult(new DecodeResult(true, null, decodeMs, hash, HashAlgorithms.Sha256)
            {
                JitMs = jitMs,
                Layout = new MosaicLayout(mosaic.Width, mosaic.Height, mosaic.PlaneCount, "u16")
                {
                    CropLeft = mosaic.ActiveArea.Left,
                    CropTop = mosaic.ActiveArea.Top,
                    CropWidth = mosaic.ActiveArea.Width,
                    CropHeight = mosaic.ActiveArea.Height,
                    BlackLevel = mosaic.BlackLevel,
                    WhiteLevel = mosaic.WhiteLevel,
                    Make = _file.Make,
                    Model = _file.Model,
                    CfaFilters = Filters(mosaic.CfaPattern),
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
        finally
        {
            file?.Dispose();
        }
    }

    public DevelopResult DevelopDecoded(PpmRequest? ppm, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_file is null || _mosaic is null) throw new InvalidOperationException("No decoded mosaic to develop.");
        try
        {
            TimeSpan jit = System.Runtime.JitInfo.GetCompilationTime(currentThread: true);
            long start = Stopwatch.GetTimestamp();
            using RawBitmap bitmap = _file.Develop(_mosaic);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            double jitMs = (System.Runtime.JitInfo.GetCompilationTime(currentThread: true) - jit).TotalMilliseconds;

            string hash = Convert.ToHexStringLower(SHA256.HashData(bitmap.Buffer.Span));
            if (ppm != null) WritePpm(bitmap, ppm);
            return new DevelopResult(DevelopStatus.Ok, ms, hash)
            {
                Width = bitmap.Width,
                Height = bitmap.Height,
                PixelFormat = bitmap.PixelFormat.ToString(),
                JitMs = jitMs,
            };
        }
        catch (RawException ex)
        {
            string code = MapError(ex.Code);
            return DevelopResult.Failed(DecodeErrorCodes.IsUnsupported(code) ? DevelopStatus.Unsupported : DevelopStatus.Error, code, ex.Message);
        }
    }

    public void ReleaseDecoded()
    {
        _mosaic?.Dispose();
        _file?.Dispose();
        (_mosaic, _file) = (null, null);
    }

    /// <summary>Binary PPM (P6) of an <see cref="RawPixelFormat.Rgb48"/> bitmap; 16-bit samples are big-endian.</summary>
    private static void WritePpm(RawBitmap bitmap, PpmRequest request)
    {
        if (bitmap.PixelFormat != RawPixelFormat.Rgb48) throw new NotSupportedException($"PPM export needs Rgb48, got {bitmap.PixelFormat}.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.Path))!);
        using FileStream fs = new(request.Path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        fs.Write(Encoding.ASCII.GetBytes($"P6\n{bitmap.Width} {bitmap.Height}\n{(request.Bits == 16 ? 65535 : 255)}\n"));
        int samples = bitmap.Width * 3;
        byte[] row = new byte[samples * (request.Bits == 16 ? 2 : 1)];
        for (int y = 0; y < bitmap.Height; y++)
        {
            ReadOnlySpan<byte> src = bitmap.Row(y);
            for (int i = 0; i < samples; i++)
            {
                ushort v = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(2 * i, 2));
                if (request.Bits == 16) BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(2 * i, 2), v);
                else row[i] = (byte)((v * 255 + 32767) / 65535);
            }
            fs.Write(row);
        }
    }

    /// <summary>LibRaw <c>filters</c> encoding of a 2×2 pattern (R=0, G=1, B=2, green of the blue row=3),
    /// repeated over the 8×2 cell; RGGB is 0xB4B4B4B4.</summary>
    private static uint Filters(RawCfaPattern cfa)
    {
        uint f = 0;
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 2; col++)
            {
                RawColor c = cfa.At(col, row);
                uint code = c switch
                {
                    RawColor.Red => 0u,
                    RawColor.Blue => 2u,
                    _ => cfa.At(col ^ 1, row) == RawColor.Red ? 1u : 3u,
                };
                f |= code << ((row << 1 | col) << 1);
            }
        }
        return f;
    }

    private static string MapError(RawErrorCode code) => code switch
    {
        RawErrorCode.UnsupportedFormat or RawErrorCode.UnsupportedVariant => DecodeErrorCodes.Unsupported,
        RawErrorCode.CorruptData or RawErrorCode.TruncatedFile => DecodeErrorCodes.DecodeError,
        RawErrorCode.IoError => DecodeErrorCodes.IoError,
        RawErrorCode.OutOfMemory => DecodeErrorCodes.OutOfMemory,
        _ => DecodeErrorCodes.Internal,
    };

    public void Dispose() => ReleaseDecoded();
}
