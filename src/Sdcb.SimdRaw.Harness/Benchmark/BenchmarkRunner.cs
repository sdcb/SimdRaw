using System.Diagnostics;
using Sdcb.SimdRaw.Harness.Engines;
using Sdcb.SimdRaw.Harness.Fixtures;
using Sdcb.SimdRaw.Harness.Reporting;

namespace Sdcb.SimdRaw.Harness.Benchmark;

/// <summary>Cold, single pass over the fixture: no warmup, no repetitions.</summary>
public sealed class BenchmarkRunner(IDecodeEngine engine, Fixture fixture, IReadOnlyList<ManifestEntry> entries, IReadOnlyList<KnownDiff> knownDiffs, TextWriter console)
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

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
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
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        long gcDelta = GC.GetTotalMemory(false) - gcBefore;
        long peakAfter = ProcessMemory.PeakWorkingSet();
        long wsAfter = ProcessMemory.CurrentWorkingSet();
        long filePeak = exactPeak || peakAfter > peakBefore ? Math.Max(peakAfter, sampledPeak) : sampledPeak;

        fixture.LibRawGeometry.TryGetValue(entry.File, out LibRawGeometry? libraw);
        (string status, string? note, OracleCheck? oracle) = _classifier.Classify(entry, libraw, result);

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
        };
    }

    private RunSummary Summarize(List<FileRecord> records)
    {
        StatusCounts counts = new();
        foreach (FileRecord r in records) counts.Add(r.Status);
        return new RunSummary
        {
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
    private const string Format = "{0,3}  {1,-58}  {2,-12}  {3,10}  {4,-14}  {5,10}";

    public void WriteHeader()
    {
        w.WriteLine(Format, "#", "file", "status", "decode ms", "dims", "peak ΔMB");
        w.WriteLine(new string('-', 3 + 2 + 58 + 2 + 12 + 2 + 10 + 2 + 14 + 2 + 10));
    }

    public void WriteRow(FileRecord r)
    {
        string dims = r.Layout is { } l ? $"{l.Width}x{l.Height}x{l.Components}" : "-";
        string ms = r.DecodeTimeMs is { } d ? d.ToString("F2") : "-";
        w.WriteLine(Format, r.Index, Truncate(r.File, 58), r.Status, ms, dims, BenchmarkRunner.Mb(r.PeakWorkingSetDeltaBytes).ToString("F1"));
        if (r.Status != FileStatus.Pass && (r.ErrorMessage ?? r.Note) is { } detail)
        {
            w.WriteLine($"     └ {r.ErrorCode ?? r.Status}: {Truncate(detail.ReplaceLineEndings(" "), 110)}");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
