namespace Sdcb.SimdRaw.Harness.Engines;

public interface IDecodeEngine : IDisposable
{
    string Name { get; }

    /// <summary>Engine version / commit.</summary>
    string Version { get; }

    /// <summary>Timed as the engine initialization metric (download, library load, JIT warmup, ...).</summary>
    Task InitializeAsync(CancellationToken ct);

    Task<DecodeResult> DecodeAsync(string filePath, CancellationToken ct);
}

/// <summary>Optional details an engine can expose for the report.</summary>
public interface IEngineDiagnostics
{
    IReadOnlyList<InitPhase> InitPhases { get; }

    IReadOnlyDictionary<string, string> Properties { get; }
}

public sealed record InitPhase(string Name, double Ms);

/// <summary>Engines that have their own golden column in manifest.tsv (RawSpeed: rstest hashes in
/// <c>golden_rawspeed</c>). A value of <c>unsupported</c> there means the engine is known not to decode the file.</summary>
public interface ISelfOracleEngine
{
    string OracleColumn { get; }

    /// <summary>Key into <see cref="DecodeResult.ExtraHashes"/> comparable with <see cref="OracleColumn"/>.</summary>
    string OracleHashAlgorithm { get; }
}

/// <param name="DecodeTimeMs">Engine-reported pure decode time (no IO / initialization). Fractional
/// milliseconds: many fixtures decode in under 1 ms.</param>
/// <param name="MosaicHash">Hash of the uncropped mosaic, rows packed without padding.</param>
/// <param name="HashAlgorithm"><c>sha256</c> or <c>md5-line-md5</c>.</param>
public sealed record DecodeResult(
    bool Success,
    string? ErrorCode,
    double DecodeTimeMs,
    string? MosaicHash,
    string HashAlgorithm)
{
    public string? ErrorMessage { get; init; }

    public MosaicLayout? Layout { get; init; }

    /// <summary>Secondary hashes keyed by algorithm, e.g. <c>md5-line-md5</c> for rstest cross-reference.</summary>
    public IReadOnlyDictionary<string, string>? ExtraHashes { get; init; }

    /// <summary>Engine-side time including container parsing and metadata, when reported.</summary>
    public double? EngineTotalTimeMs { get; init; }

    public static DecodeResult Failed(string errorCode, string message) =>
        new(false, errorCode, 0, null, HashAlgorithms.Sha256) { ErrorMessage = message };
}

public sealed record MosaicLayout(int Width, int Height, int Components, string SampleType)
{
    public int CropLeft { get; init; }
    public int CropTop { get; init; }
    public int CropWidth { get; init; }
    public int CropHeight { get; init; }
    public int? BlackLevel { get; init; }
    public int? WhiteLevel { get; init; }
    public uint CfaFilters { get; init; }
    public string? Make { get; init; }
    public string? Model { get; init; }

    public override string ToString() => $"{Width}x{Height}x{Components} {SampleType}";
}

public static class HashAlgorithms
{
    public const string Sha256 = "sha256";
    public const string Md5LineMd5 = "md5-line-md5";
}

public static class DecodeErrorCodes
{
    public const string Unsupported = "unsupported";
    public const string NotImplemented = "not-implemented";
    public const string DecodeError = "decode-error";
    public const string IoError = "io-error";
    public const string OutOfMemory = "out-of-memory";
    public const string Internal = "internal-error";

    /// <summary>Codes that mean "the engine cannot handle this input" rather than a harness/engine defect.</summary>
    public static bool IsUnsupported(string? code) => code is Unsupported or NotImplemented;
}
