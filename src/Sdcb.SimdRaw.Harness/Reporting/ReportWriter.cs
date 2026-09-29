using System.Globalization;
using System.Text;
using System.Text.Json;
using Sdcb.SimdRaw.Harness.Engines;

namespace Sdcb.SimdRaw.Harness.Reporting;

public static class ReportWriter
{
    public static (string Json, string Markdown) Write(RunReport report, string outDir)
    {
        Directory.CreateDirectory(outDir);
        string stem = $"report-{report.Engine}-{report.GeneratedAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}";
        string json = Path.Combine(outDir, stem + ".json");
        string md = Path.Combine(outDir, stem + ".md");
        File.WriteAllText(json, JsonSerializer.Serialize(report, ReportJsonContext.Default.RunReport));
        File.WriteAllText(md, ToMarkdown(report));
        return (json, md);
    }

    public static string ToMarkdown(RunReport r)
    {
        StringBuilder sb = new();
        RunSummary s = r.Summary;
        StatusCounts c = s.Status;

        sb.AppendLine($"# Sdcb.SimdRaw harness report · {r.Engine}");
        sb.AppendLine();
        sb.AppendLine($"git `{r.GitSha ?? "unknown"}` · generated {r.GeneratedAt:yyyy-MM-dd HH:mm zzz} · {s.Files} files · cold single run, no warmup"
            + $" · fixture `{Short(r.FixtureCommit)}`{(r.Filter is null ? "" : $" · filter `{r.Filter}`")}");
        sb.AppendLine();

        sb.AppendLine("## Environment");
        sb.AppendLine();
        sb.AppendLine("| RID | OS | CPU | cores | runtime | effective ISA | vector bits | engine version |");
        sb.AppendLine("| --- | --- | --- | ---: | --- | --- | ---: | --- |");
        EnvironmentInfo e = r.Environment;
        sb.AppendLine($"| {e.Rid} | {Esc(e.Os)} | {Esc(e.Cpu)} | {e.LogicalCores} | {Esc(e.Runtime)} | {e.EffectiveIsa} | {e.VectorBits} | {Esc(r.EngineVersion)} |");
        sb.AppendLine();
        sb.AppendLine(e.DotnetEnv.Count == 0
            ? "- DOTNET ISA knobs: none set"
            : $"- DOTNET ISA knobs: {string.Join(", ", e.DotnetEnv.Select(kv => $"`{kv.Key}={kv.Value}`"))}");
        sb.AppendLine();

        sb.AppendLine("## Engine initialization");
        sb.AppendLine();
        sb.AppendLine("| phase | ms |");
        sb.AppendLine("| --- | ---: |");
        foreach (InitPhase p in r.Init.Phases) sb.AppendLine($"| {p.Name} | {F(p.Ms)} |");
        sb.AppendLine($"| **total** | **{F(r.Init.TotalMs)}** |");
        sb.AppendLine();
        foreach ((string k, string v) in r.Init.Properties.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"- {k}: `{Esc(v)}`");
        }
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| files | pass | fail | layout-diff | engine-diff | unsupported | no-golden | error | decoded | mean ms | median ms | P95 ms | total ms | max peak ΔWS MB |");
        sb.AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        sb.AppendLine($"| {s.Files} | {c.Pass} | {c.Fail} | {c.LayoutDiff} | {c.EngineDiff} | {c.Unsupported} | {c.NoGolden} | {c.Error} | {s.Decoded} "
            + $"| {F(s.DecodeMs?.Mean)} | {F(s.DecodeMs?.Median)} | {F(s.DecodeMs?.P95)} | {F(s.DecodeMs?.Total)} | {Mb(s.MaxPeakWorkingSetDeltaBytes)} |");
        sb.AppendLine();
        int comparable = c.Pass + c.Fail + c.LayoutDiff + c.EngineDiff;
        sb.AppendLine($"- Accuracy vs `golden_libraw` (SHA-256 of the uncropped mosaic): **{c.Pass}/{comparable}** decoded files with a golden are byte-exact"
            + (c.LayoutDiff > 0 ? $"; {c.LayoutDiff} differ in buffer geometry" : "")
            + (c.EngineDiff > 0 ? $"; {c.EngineDiff} differ in value semantics (explained, see *Engine differences*)" : ""));
        if (s.EngineOracleColumn is { } oracleColumn)
        {
            sb.AppendLine($"- Engine self-check vs `{oracleColumn}`: {s.EngineOracleMatch} match / {s.EngineOracleMismatch} mismatch");
        }
        sb.AppendLine($"- Gate: {(c.Fail + c.Error == 0 ? "**PASS** (no `fail` / `error`)" : $"**FAIL** ({c.Fail} fail, {c.Error} error)")}");
        sb.AppendLine($"- Decode time is engine-reported (pure decode, no IO / init); ΔWS is per-file peak working set minus the post-init baseline {Mb(r.BaselineWorkingSetBytes)} MB ({r.PeakMethod})");
        if (s.EngineTotalMs is { } t)
        {
            sb.AppendLine($"- Engine total (parse + decode + metadata): mean {F(t.Mean)} ms, median {F(t.Median)} ms, P95 {F(t.P95)} ms");
        }
        sb.AppendLine();

        sb.AppendLine("## By decode path");
        sb.AppendLine();
        sb.AppendLine("| decode path | n | status | median ms | max ms | max peak ΔWS MB |");
        sb.AppendLine("| --- | ---: | --- | ---: | ---: | ---: |");
        foreach (PathGroup g in r.ByDecodePath)
        {
            sb.AppendLine($"| `{g.DecodePath}` | {g.Files} | {StatusText(g.Status)} | {F(g.DecodeMs?.Median)} | {F(g.DecodeMs?.Max)} | {Mb(g.MaxPeakWorkingSetDeltaBytes)} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Per file");
        sb.AppendLine();
        sb.AppendLine("| # | file | status | decode ms | engine buffer | LibRaw buffer | peak ΔWS MB | GC Δ MB | engine golden |");
        sb.AppendLine("| ---: | --- | --- | ---: | --- | --- | ---: | ---: | --- |");
        foreach (FileRecord f in r.Files)
        {
            sb.AppendLine($"| {f.Index} | `{f.File}` | {f.Status} | {F(f.DecodeTimeMs)} | {Layout(f.Layout)} | {f.LibRawGeometry?.ToString() ?? "—"} "
                + $"| {Mb(f.PeakWorkingSetDeltaBytes)} | {Mb(f.GcTotalMemoryDeltaBytes)} | {f.EngineOracle?.Status ?? "—"} |");
        }
        sb.AppendLine();

        List<FileRecord> gating = [.. r.Files.Where(f => FileStatus.IsGating(f.Status))];
        sb.AppendLine("## Failures");
        sb.AppendLine();
        if (gating.Count == 0) sb.AppendLine("None.");
        foreach (FileRecord f in gating)
        {
            sb.AppendLine($"- `{f.File}` ({f.DecodePath}) **{f.Status}**: {Esc(f.Note ?? f.ErrorMessage ?? f.ErrorCode ?? "")}"
                + (f.MosaicHash is null ? "" : $" — got `{f.MosaicHash}`, golden `{f.GoldenLibRaw}`"));
        }
        sb.AppendLine();

        List<FileRecord> layout = [.. r.Files.Where(f => f.Status == FileStatus.LayoutDiff)];
        if (layout.Count > 0)
        {
            sb.AppendLine("## Layout differences vs LibRaw");
            sb.AppendLine();
            sb.AppendLine("`golden_libraw` hashes LibRaw's `raw_image` (1 u16/pixel, incl. masked edges) or `color4_image` (4 u16/pixel). "
                + "The engine buffer below has a different geometry or sample type, so a byte-exact match is impossible by construction; "
                + "these rows are reported, not gated.");
            sb.AppendLine();
            sb.AppendLine("| file | engine buffer | engine crop (l,t,w,h) | LibRaw buffer | difference | engine golden |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (FileRecord f in layout)
            {
                MosaicLayout? l = f.Layout;
                string crop = l is null ? "—" : $"{l.CropLeft},{l.CropTop},{l.CropWidth},{l.CropHeight}";
                sb.AppendLine($"| `{f.File}` | {Layout(l)} | {crop} | {f.LibRawGeometry?.ToString() ?? "—"} | {Esc(f.Note ?? "")} | {f.EngineOracle?.Status ?? "—"} |");
            }
            sb.AppendLine();
        }

        List<FileRecord> engineDiff = [.. r.Files.Where(f => f.Status == FileStatus.EngineDiff)];
        if (engineDiff.Count > 0)
        {
            sb.AppendLine("## Engine differences");
            sb.AppendLine();
            sb.AppendLine("Same buffer geometry as LibRaw but different sample values (typically LibRaw applying a tone curve, "
                + "bit shift or black/flat-field correction inside `load_raw`). Each row is explained either by the engine's own golden "
                + "or by a reviewed, hash-pinned entry in `known-diffs.tsv`; not gated.");
            sb.AppendLine();
            sb.AppendLine("| file | decode path | engine buffer | reason |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (FileRecord f in engineDiff)
            {
                sb.AppendLine($"| `{f.File}` | `{f.DecodePath}` | {Layout(f.Layout)} | {Esc(f.Note ?? "")} |");
            }
            sb.AppendLine();
        }

        List<FileRecord> unsupported = [.. r.Files.Where(f => f.Status == FileStatus.Unsupported)];
        if (unsupported.Count > 0)
        {
            sb.AppendLine("## Unsupported by engine");
            sb.AppendLine();
            sb.AppendLine("| file | decode path | code | message | note |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (FileRecord f in unsupported)
            {
                sb.AppendLine($"| `{f.File}` | `{f.DecodePath}` | {f.ErrorCode} | {Esc(f.ErrorMessage ?? "")} | {Esc(f.Note ?? "")} |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string StatusText(StatusCounts c)
    {
        List<string> parts = [];
        if (c.Pass > 0) parts.Add($"{c.Pass} pass");
        if (c.Fail > 0) parts.Add($"{c.Fail} fail");
        if (c.LayoutDiff > 0) parts.Add($"{c.LayoutDiff} layout-diff");
        if (c.EngineDiff > 0) parts.Add($"{c.EngineDiff} engine-diff");
        if (c.Unsupported > 0) parts.Add($"{c.Unsupported} unsupported");
        if (c.NoGolden > 0) parts.Add($"{c.NoGolden} no-golden");
        if (c.Error > 0) parts.Add($"{c.Error} error");
        return string.Join(", ", parts);
    }

    private static string Layout(MosaicLayout? l) => l is null ? "—" : $"{l.Width}x{l.Height}x{l.Components}{(l.SampleType == "u16" ? "" : " " + l.SampleType)}";

    private static string F(double? v) => v is { } d ? d.ToString(d >= 100 ? "F1" : "F2", CultureInfo.InvariantCulture) : "—";

    private static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture);

    private static string Short(string? sha) => sha is null ? "unknown" : sha[..Math.Min(8, sha.Length)];

    private static string Esc(string s) => s.ReplaceLineEndings(" ").Replace("|", "\\|");
}
