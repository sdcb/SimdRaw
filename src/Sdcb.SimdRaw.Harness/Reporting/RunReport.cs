using System.Text.Json.Serialization;
using Sdcb.SimdRaw.Harness.Benchmark;
using Sdcb.SimdRaw.Harness.Engines;
using Sdcb.SimdRaw.Harness.Fixtures;

namespace Sdcb.SimdRaw.Harness.Reporting;

public static class FileStatus
{
    /// <summary>SHA-256 of the mosaic equals <c>golden_libraw</c>.</summary>
    public const string Pass = "pass";
    /// <summary>Same geometry as LibRaw's buffer but different content. Gates CI.</summary>
    public const string Fail = "fail";
    /// <summary>Hash differs and the buffer geometry/sample type differs from LibRaw's (e.g. crop or
    /// sRaw component semantics) - a known engine-semantics difference, reported but not gating.</summary>
    public const string LayoutDiff = "layout-diff";
    /// <summary>Same geometry, different content, but explained: the output is identical to the engine's own
    /// golden (e.g. rstest) or matches a reviewed, hash-pinned entry in known-diffs.tsv. Not gating.</summary>
    public const string EngineDiff = "engine-diff";
    /// <summary>The engine itself does not support the file.</summary>
    public const string Unsupported = "unsupported";
    /// <summary>Decoded, but the manifest has no <c>golden_libraw</c> to compare with.</summary>
    public const string NoGolden = "no-golden";
    /// <summary>IO / internal / crash-like failure. Gates CI.</summary>
    public const string Error = "error";

    public static readonly string[] All = [Pass, Fail, LayoutDiff, EngineDiff, Unsupported, NoGolden, Error];

    public static bool IsGating(string status) => status is Fail or Error;
}

public sealed class RunReport
{
    public int SchemaVersion { get; init; } = 1;
    public required string Engine { get; init; }
    public required string EngineVersion { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public string? GitSha { get; init; }
    public string? FixtureCommit { get; init; }
    public bool FixtureDownloaded { get; init; }
    public string? Filter { get; init; }
    public required EnvironmentInfo Environment { get; init; }
    public required InitReport Init { get; init; }
    public required long BaselineWorkingSetBytes { get; init; }
    public required string PeakMethod { get; init; }
    public required RunSummary Summary { get; init; }
    public required List<PathGroup> ByDecodePath { get; init; }
    public required List<FileRecord> Files { get; init; }
}

public sealed class EnvironmentInfo
{
    public required string Rid { get; init; }
    public required string Os { get; init; }
    public required string Cpu { get; init; }
    public required int LogicalCores { get; init; }
    public required string Runtime { get; init; }
    public required string EffectiveIsa { get; init; }
    public required int VectorBits { get; init; }
    public required Dictionary<string, bool> Isa { get; init; }
    public required Dictionary<string, string> DotnetEnv { get; init; }
}

public sealed class InitReport
{
    /// <summary>Wall time of <see cref="IDecodeEngine.InitializeAsync"/> including downloads.</summary>
    public required double TotalMs { get; init; }
    public required List<InitPhase> Phases { get; init; }
    public required Dictionary<string, string> Properties { get; init; }
}

public sealed class FileRecord
{
    public required int Index { get; init; }
    public required string File { get; init; }
    public required string DecodePath { get; init; }
    public required long SizeBytes { get; init; }
    public required string Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public double? DecodeTimeMs { get; init; }
    public double? EngineTotalTimeMs { get; init; }
    public required double WallTimeMs { get; init; }
    public required string HashAlgorithm { get; init; }
    public string? MosaicHash { get; init; }
    public string? GoldenLibRaw { get; init; }
    public MosaicLayout? Layout { get; init; }
    public LibRawGeometry? LibRawGeometry { get; init; }
    public string? Note { get; init; }
    /// <summary>Comparison with the engine's own golden column (RawSpeed: rstest), null when not applicable.</summary>
    public OracleCheck? EngineOracle { get; init; }
    public required long PeakWorkingSetBytes { get; init; }
    public required long PeakWorkingSetDeltaBytes { get; init; }
    public required long WorkingSetAfterBytes { get; init; }
    public required long GcTotalMemoryDeltaBytes { get; init; }
    public required long AllocatedBytes { get; init; }
}

public sealed class TimeStats
{
    public required int Count { get; init; }
    public required double Mean { get; init; }
    public required double Median { get; init; }
    public required double P95 { get; init; }
    public required double Min { get; init; }
    public required double Max { get; init; }
    public required double Total { get; init; }

    public static TimeStats? From(IEnumerable<double> values)
    {
        double[] v = values.Order().ToArray();
        if (v.Length == 0) return null;
        double median = v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
        int p95Index = Math.Clamp((int)Math.Ceiling(0.95 * v.Length) - 1, 0, v.Length - 1);
        return new TimeStats
        {
            Count = v.Length,
            Mean = v.Average(),
            Median = median,
            P95 = v[p95Index],
            Min = v[0],
            Max = v[^1],
            Total = v.Sum(),
        };
    }
}

public sealed class StatusCounts
{
    public int Pass { get; set; }
    public int Fail { get; set; }
    public int LayoutDiff { get; set; }
    public int EngineDiff { get; set; }
    public int Unsupported { get; set; }
    public int NoGolden { get; set; }
    public int Error { get; set; }

    public void Add(string status)
    {
        switch (status)
        {
            case FileStatus.Pass: Pass++; break;
            case FileStatus.Fail: Fail++; break;
            case FileStatus.LayoutDiff: LayoutDiff++; break;
            case FileStatus.EngineDiff: EngineDiff++; break;
            case FileStatus.Unsupported: Unsupported++; break;
            case FileStatus.NoGolden: NoGolden++; break;
            default: Error++; break;
        }
    }
}

public sealed class RunSummary
{
    public required int Files { get; init; }
    public required StatusCounts Status { get; init; }
    public required int Decoded { get; init; }
    public TimeStats? DecodeMs { get; init; }
    public TimeStats? EngineTotalMs { get; init; }
    public required long MaxPeakWorkingSetDeltaBytes { get; init; }
    public string? EngineOracleColumn { get; init; }
    public required int EngineOracleMatch { get; init; }
    public required int EngineOracleMismatch { get; init; }
}

public sealed class PathGroup
{
    public required string DecodePath { get; init; }
    public required int Files { get; init; }
    public required StatusCounts Status { get; init; }
    public TimeStats? DecodeMs { get; init; }
    public required long MaxPeakWorkingSetDeltaBytes { get; init; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunReport))]
internal sealed partial class ReportJsonContext : JsonSerializerContext;
