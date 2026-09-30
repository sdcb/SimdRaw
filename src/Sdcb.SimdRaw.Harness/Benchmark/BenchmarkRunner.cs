using System.Diagnostics;
using Sdcb.SimdRaw.Harness.Engines;
using Sdcb.SimdRaw.Harness.Fixtures;
using Sdcb.SimdRaw.Harness.Reporting;

namespace Sdcb.SimdRaw.Harness.Benchmark;

/// <param name="RunId">Report file stem; PPMs go to <c>&lt;OutDir&gt;/&lt;RunId&gt;-ppm/</c>.</param>
/// <param name="PpmBits">0 (no PPM), 8 or 16.</param>
public sealed record RunSettings(string RunId, string OutDir, bool Develop, int PpmBits)
{
    public string PpmDirName => RunId + "-ppm";
}

/// <summary>Cold, single pass over the fixture: no warmup, no repetitions.</summary>
public sealed class BenchmarkRunner(IDecodeEngine engine, Fixture fixture, IReadOnlyList<ManifestEntry> entries, IReadOnlyList<KnownDiff> knownDiffs,
    RunSettings settings, TextWriter console)
{
    private readonly Classifier _classifier = new(engine as ISelfOracleEngine, knownDiffs, engine.Name);

    public async Task<RunReport> RunAsync(string? gitSha, string? filter, CancellationToken ct)
    {
        Stopwatch initSw = Stopwatch.StartNew();
        await engine.InitializeAsync(ct);
        double initMs = initSw.Elapsed.TotalMilliseconds;
        IEngineDiagnostics? diag = engine as IEngineDiagnostics;
        console.WriteLine($"[engine] {engine.Name} {engine.Version} initialized in {initMs:F1} ms");
        foreach (InitPhase phase in diag?.InitPhases ?? [])
        {
            console.WriteLine($"         {phase.Name,-18} {phase.Ms,10:F2} ms");
        }

        long baseline = ProcessMemory.CurrentWorkingSet();
        console.WriteLine($"[memory] baseline working set {Mb(baseline):F1} MB ({ProcessMemory.PeakMethod})");
        console.WriteLine();

        ConsoleTable table = new(console);
        table.WriteHeader();
        List<FileRecord> records = [];
        using WorkingSetSampler sampler = new();
        for (int i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ManifestEntry entry = entries[i];
            FileRecord record = await RunOneAsync(i + 1, entry, baseline, sampler, ct);
            records.Add(record);
            table.WriteRow(record);
        }
        console.WriteLine();

        return new RunReport
        {
            RunId = settings.RunId,
            Engine = engine.Name,
            EngineVersion = engine.Version,
            GeneratedAt = DateTimeOffset.Now,
            GitSha = gitSha,
            FixtureCommit = fixture.Commit,
            FixtureDownloaded = fixture.Downloaded,
            Filter = filter,
            Environment = EnvironmentProbe.Collect(),
            Init = new InitReport
            {
                TotalMs = initMs,
                Phases = [.. diag?.InitPhases ?? []],
                Properties = diag?.Properties.ToDictionary() ?? [],
            },
            BaselineWorkingSetBytes = baseline,
            PeakMethod = ProcessMemory.PeakMethod,
            Summary = Summarize(records),
            ByDecodePath = GroupByPath(records),
            Files = records,
        };
    }

    private async Task<FileRecord> RunOneAsync(int index, ManifestEntry entry, long baseline, WorkingSetSampler sampler, CancellationToken ct)
    {
        string path = fixture.PathOf(entry);
        bool exactPeak = ProcessMemory.TryResetPeak();
        long peakBefore = ProcessMemory.PeakWorkingSet();
        long gcBefore = GC.GetTotalMemory(false);
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        int collectionsBefore = GC.CollectionCount(0);
        sampler.Begin();
        long start = Stopwatch.GetTimestamp();

        DecodeResult result;
        try
        {
            result = await engine.DecodeAsync(path, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = DecodeResult.Failed(DecodeErrorCodes.Internal, ex.ToString());
        }

        double wallMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long sampledPeak = sampler.End();
        int collections = GC.CollectionCount(0) - collectionsBefore;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        long gcDelta = GC.GetTotalMemory(false) - gcBefore;
        long peakAfter = ProcessMemory.PeakWorkingSet();
        long wsAfter = ProcessMemory.CurrentWorkingSet();
        long filePeak = exactPeak || peakAfter > peakBefore ? Math.Max(peakAfter, sampledPeak) : sampledPeak;

        fixture.LibRawGeometry.TryGetValue(entry.File, out LibRawGeometry? libraw);
        (string status, string? note, OracleCheck? oracle) = _classifier.Classify(entry, libraw, result);

        DevelopRecord? develop = null;
        if (engine is IDevelopEngine developer)
        {
            try
            {
                if (settings.Develop && result.Success) develop = RunDevelop(developer, entry, sampler, ct);
            }
            finally
            {
                developer.ReleaseDecoded();
            }
        }

        return new FileRecord
        {
            Index = index,
            File = entry.File,
            DecodePath = entry.DecodePath,
            SizeBytes = entry.SizeBytes,
            Status = status,
            ErrorCode = result.ErrorCode,
            ErrorMessage = result.ErrorMessage,
            DecodeTimeMs = result.Success ? result.DecodeTimeMs : null,
            DecodeJitMs = result.Success ? result.JitMs : null,
            EngineTotalTimeMs = result.EngineTotalTimeMs,
            WallTimeMs = wallMs,
            HashAlgorithm = result.HashAlgorithm,
            MosaicHash = result.MosaicHash,
            GoldenLibRaw = entry.GoldenLibRaw,
            Layout = result.Layout,
            LibRawGeometry = libraw,
            Note = note,
            EngineOracle = oracle,
            PeakWorkingSetBytes = filePeak,
            PeakWorkingSetDeltaBytes = filePeak - baseline,
            WorkingSetAfterBytes = wsAfter,
            GcTotalMemoryDeltaBytes = gcDelta,
            AllocatedBytes = allocated,
            GcCollections = collections,
            Develop = develop,
        };
    }

    private DevelopRecord RunDevelop(IDevelopEngine developer, ManifestEntry entry, WorkingSetSampler sampler, CancellationToken ct)
    {
        string? ppmRelative = settings.PpmBits > 0 ? Path.Combine(settings.PpmDirName, FixturePaths.Sanitize(entry.File) + ".ppm") : null;
        PpmRequest? ppm = ppmRelative is null ? null : new PpmRequest(Path.Combine(settings.OutDir, ppmRelative), settings.PpmBits);

        bool exactPeak = ProcessMemory.TryResetPeak();
        long before = ProcessMemory.CurrentWorkingSet();
        long peakBefore = ProcessMemory.PeakWorkingSet();
        long gcBefore = GC.GetTotalMemory(false);
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        int collectionsBefore = GC.CollectionCount(0);
        sampler.Begin();
        long start = Stopwatch.GetTimestamp();

        DevelopResult d;
        try
        {
            d = developer.DevelopDecoded(ppm, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            d = DevelopResult.Failed(DevelopStatus.Error, DecodeErrorCodes.Internal, ex.ToString());
        }

        double wallMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long sampledPeak = sampler.End();
        int collections = GC.CollectionCount(0) - collectionsBefore;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        long gcDelta = GC.GetTotalMemory(false) - gcBefore;
        long peakAfter = ProcessMemory.PeakWorkingSet();
        long peak = exactPeak || peakAfter > peakBefore ? Math.Max(peakAfter, sampledPeak) : sampledPeak;
        bool ok = d.Status == DevelopStatus.Ok;
        return new DevelopRecord
        {
            Status = d.Status,
            ErrorCode = d.ErrorCode,
            ErrorMessage = d.ErrorMessage,
            TimeMs = ok ? d.TimeMs : null,
            JitMs = ok ? d.JitMs : null,
            WallTimeMs = wallMs,
            Sha256 = d.Sha256,
            Width = d.Width,
            Height = d.Height,
            PixelFormat = d.PixelFormat,
            Ppm = ok ? ppmRelative : null,
            PeakWorkingSetDeltaBytes = Math.Max(0, peak - before),
            GcTotalMemoryDeltaBytes = gcDelta,
            AllocatedBytes = allocated,
            GcCollections = collections,
        };
    }

    private RunSummary Summarize(List<FileRecord> records)
    {
        StatusCounts counts = new();
        foreach (FileRecord r in records) counts.Add(r.Status);
        List<DevelopRecord> developed = [.. records.Select(r => r.Develop).OfType<DevelopRecord>()];
        return new RunSummary
        {
            DevelopMode = engine is IDevelopEngine ? settings.Develop ? "on" : "off" : "n/a",
            Developed = developed.Count(d => d.Status == DevelopStatus.Ok),
            DevelopUnsupported = developed.Count(d => d.Status == DevelopStatus.Unsupported),
            DevelopErrors = developed.Count(d => d.Status == DevelopStatus.Error),
            DevelopMs = TimeStats.From(developed.Where(d => d.TimeMs.HasValue).Select(d => d.TimeMs!.Value)),
            MaxDevelopPeakWorkingSetDeltaBytes = developed.Count == 0 ? 0 : developed.Max(d => d.PeakWorkingSetDeltaBytes),
            Files = records.Count,
            Status = counts,
            Decoded = records.Count(r => r.DecodeTimeMs.HasValue),
            DecodeMs = TimeStats.From(records.Where(r => r.DecodeTimeMs.HasValue).Select(r => r.DecodeTimeMs!.Value)),
            EngineTotalMs = TimeStats.From(records.Where(r => r.EngineTotalTimeMs.HasValue).Select(r => r.EngineTotalTimeMs!.Value)),
            MaxPeakWorkingSetDeltaBytes = records.Count == 0 ? 0 : records.Max(r => r.PeakWorkingSetDeltaBytes),
            EngineOracleColumn = (engine as ISelfOracleEngine)?.OracleColumn,
            EngineOracleMatch = records.Count(r => r.EngineOracle?.Status == "match"),
            EngineOracleMismatch = records.Count(r => r.EngineOracle?.Status == "mismatch"),
        };
    }

    private static List<PathGroup> GroupByPath(List<FileRecord> records) =>
        [.. records
            .GroupBy(r => r.DecodePath)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                StatusCounts counts = new();
                foreach (FileRecord r in g) counts.Add(r.Status);
                return new PathGroup
                {
                    DecodePath = g.Key,
                    Files = g.Count(),
                    Status = counts,
                    DecodeMs = TimeStats.From(g.Where(r => r.DecodeTimeMs.HasValue).Select(r => r.DecodeTimeMs!.Value)),
                    MaxPeakWorkingSetDeltaBytes = g.Max(r => r.PeakWorkingSetDeltaBytes),
                };
            })];

    internal static double Mb(long bytes) => bytes / 1024.0 / 1024.0;
}

internal sealed class ConsoleTable(TextWriter w)
{
    private const string Format = "{0,3}  {1,-58}  {2,-12}  {3,10}  {4,-14}  {5,10}  {6,11}";

    public void WriteHeader()
    {
        w.WriteLine(Format, "#", "file", "status", "decode ms", "dims", "peak ΔMB", "develop ms");
        w.WriteLine(new string('-', 3 + 2 + 58 + 2 + 12 + 2 + 10 + 2 + 14 + 2 + 10 + 2 + 11));
    }

    public void WriteRow(FileRecord r)
    {
        string dims = r.Layout is { } l ? $"{l.Width}x{l.Height}x{l.Components}" : "-";
        string ms = r.DecodeTimeMs is { } d ? d.ToString("F2") : "-";
        string dev = r.Develop is null ? "-" : r.Develop.TimeMs is { } t ? t.ToString("F2") : r.Develop.Status;
        w.WriteLine(Format, r.Index, Truncate(r.File, 58), r.Status, ms, dims, BenchmarkRunner.Mb(r.PeakWorkingSetDeltaBytes).ToString("F1"), dev);
        if (r.Status != FileStatus.Pass && (r.ErrorMessage ?? r.Note) is { } detail)
        {
            w.WriteLine($"     └ {r.ErrorCode ?? r.Status}: {Truncate(detail.ReplaceLineEndings(" "), 110)}");
        }
        if (r.Develop is { Status: not DevelopStatus.Ok } failed)
        {
            w.WriteLine($"     └ develop {failed.ErrorCode ?? failed.Status}: {Truncate((failed.ErrorMessage ?? "").ReplaceLineEndings(" "), 102)}");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
