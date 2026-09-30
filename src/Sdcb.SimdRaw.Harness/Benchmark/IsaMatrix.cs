using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Sdcb.SimdRaw.Harness.Reporting;

namespace Sdcb.SimdRaw.Harness.Benchmark;

/// <summary>An ISA tier = a set of runtime knobs; the kernels pick their implementation from what the runtime reports.</summary>
public sealed record IsaTier(string Name, IReadOnlyDictionary<string, string> Env)
{
    public static readonly IReadOnlyList<IsaTier> Known =
    [
        new("default", new Dictionary<string, string>()),
        new("no-avx512", new Dictionary<string, string> { ["DOTNET_EnableAVX512F"] = "0" }),
        new("no-avx2", new Dictionary<string, string> { ["DOTNET_EnableAVX2"] = "0" }),
        new("no-hwintrinsic", new Dictionary<string, string> { ["DOTNET_EnableHWIntrinsic"] = "0" }),
    ];

    public string EnvText => Env.Count == 0 ? "—" : string.Join(" ", Env.Select(kv => $"`{kv.Key}={kv.Value}`"));

    public static bool TryParseList(string value, out List<IsaTier> tiers, out string? error)
    {
        tiers = [];
        error = null;
        foreach (string name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            IsaTier? tier = Known.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (tier is null)
            {
                error = $"Unknown ISA tier '{name}' (known: {string.Join(", ", Known.Select(t => t.Name))}).";
                return false;
            }
            if (!tiers.Contains(tier)) tiers.Add(tier);
        }
        if (tiers.Count < 2) error = "--isa-matrix needs at least two tiers.";
        return error is null;
    }
}

/// <summary>Runs the harness once per ISA tier, each in a fresh (cold) child process, then checks that every tier
/// produced the same mosaic and develop hashes.</summary>
internal static class IsaMatrix
{
    public static int Run(IReadOnlyList<IsaTier> tiers, IReadOnlyList<string> childArgs, string outDir, string? filter, int ppmBits, string? gitSha, TextWriter log)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        List<TierRun> runs = [];
        for (int i = 0; i < tiers.Count; i++)
        {
            IsaTier tier = tiers[i];
            string runId = $"report-simdraw-{stamp}-{tier.Name}";
            List<string> args = [.. childArgs, "--run-id", runId, "--ppm", i == 0 && ppmBits > 0 ? ppmBits.ToString(CultureInfo.InvariantCulture) : "off"];
            log.WriteLine($"[isa-matrix] tier {tier.Name} ({(tier.Env.Count == 0 ? "no knobs" : string.Join(" ", tier.Env.Select(kv => $"{kv.Key}={kv.Value}")))})");
            int exit = RunChild(tier, args);
            string json = Path.Combine(outDir, runId + ".json");
            RunReport? report = File.Exists(json) ? JsonSerializer.Deserialize(File.ReadAllText(json), ReportJsonContext.Default.RunReport) : null;
            log.WriteLine($"[isa-matrix] tier {tier.Name} exit {exit}{(report is null ? ", no report" : "")}");
            log.WriteLine();
            runs.Add(new TierRun(tier, runId, exit, report));
        }

        Comparison cmp = Compare(runs);
        string md = Path.Combine(outDir, $"isa-matrix-{stamp}.md");
        File.WriteAllText(md, ToMarkdown(runs, cmp, stamp, filter, gitSha));
        log.WriteLine($"[isa-matrix] {cmp.Rows.Count} decoded files, {cmp.MosaicMismatches} mosaic hash mismatches, {cmp.DevelopMismatches} develop hash mismatches across {runs.Count} tiers");
        log.WriteLine($"report: {md}");
        bool ok = runs.All(r => r.Exit == 0 && r.Report != null) && cmp.MosaicMismatches == 0 && cmp.DevelopMismatches == 0;
        return ok ? 0 : 1;
    }

    private static int RunChild(IsaTier tier, IReadOnlyList<string> args)
    {
        string host = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the harness executable.");
        ProcessStartInfo psi = new(host) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Sdcb.SimdRaw.Harness.dll"));
        }
        foreach (string a in args) psi.ArgumentList.Add(a);
        foreach (string key in psi.Environment.Keys.Where(k => k.StartsWith("DOTNET_Enable", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            psi.Environment.Remove(key);
        }
        foreach ((string k, string v) in tier.Env) psi.Environment[k] = v;
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"Cannot start {host}.");
        p.WaitForExit();
        return p.ExitCode;
    }

    private sealed record TierRun(IsaTier Tier, string RunId, int Exit, RunReport? Report);

    private sealed record Cell(FileRecord? File)
    {
        public string Decode => File?.DecodeTimeMs is { } ms ? $"{F(ms)}{(File.DecodeJitMs is { } j ? $" ({F(j)})" : "")}" : File?.Status ?? "—";
        public string Develop => File?.Develop?.TimeMs is { } ms ? $"{F(ms)}{(File.Develop.JitMs is { } j ? $" ({F(j)})" : "")}" : File?.Develop?.Status ?? "—";
    }

    private sealed record Row(string File, string DecodePath, List<Cell> Cells, string? MosaicHash, bool MosaicSame, string? DevelopHash, bool DevelopSame, bool Developed);

    private sealed record Comparison(List<Row> Rows, int MosaicMismatches, int DevelopMismatches);

    private static Comparison Compare(List<TierRun> runs)
    {
        List<Row> rows = [];
        IEnumerable<string> files = runs.Where(r => r.Report != null).SelectMany(r => r.Report!.Files).Select(f => f.File).Distinct();
        foreach (string file in files)
        {
            List<Cell> cells = [.. runs.Select(r => new Cell(r.Report?.Files.FirstOrDefault(f => f.File == file)))];
            if (cells.All(c => c.File?.MosaicHash is null)) continue;
            bool mosaicSame = cells.Select(c => (c.File?.Status, c.File?.MosaicHash)).Distinct().Count() == 1;
            bool developed = cells.Any(c => c.File?.Develop is not null);
            bool developSame = cells.Select(c => (c.File?.Develop?.Status, c.File?.Develop?.Sha256)).Distinct().Count() == 1;
            FileRecord first = cells.First(c => c.File != null).File!;
            rows.Add(new Row(file, first.DecodePath, cells, first.MosaicHash, mosaicSame, first.Develop?.Sha256, developSame, developed));
        }
        return new Comparison(rows, rows.Count(r => !r.MosaicSame), rows.Count(r => r.Developed && !r.DevelopSame));
    }

    private static string ToMarkdown(List<TierRun> runs, Comparison cmp, string stamp, string? filter, string? gitSha)
    {
        StringBuilder sb = new();
        RunReport? any = runs.Select(r => r.Report).FirstOrDefault(r => r != null);
        sb.AppendLine("# Sdcb.SimdRaw ISA matrix");
        sb.AppendLine();
        sb.AppendLine($"git `{gitSha ?? "unknown"}` · {stamp} · {runs.Count} tiers, each a separate cold process · "
            + $"{any?.Environment.Cpu ?? "unknown CPU"} · {any?.Environment.Runtime ?? ""}{(filter is null ? "" : $" · filter `{filter}`")}");
        sb.AppendLine();
        sb.AppendLine("| tier | knobs | effective ISA | Vector<T> bits | exit | pass | fail | unsupported | error | develop ok / error | report |");
        sb.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |");
        foreach (TierRun r in runs)
        {
            RunReport? rep = r.Report;
            StatusCounts? c = rep?.Summary.Status;
            sb.AppendLine($"| {r.Tier.Name} | {r.Tier.EnvText} | {rep?.Environment.EffectiveIsa ?? "—"} | {rep?.Environment.VectorBits.ToString(CultureInfo.InvariantCulture) ?? "—"} "
                + $"| {r.Exit} | {c?.Pass} | {c?.Fail} | {c?.Unsupported} | {c?.Error} | {rep?.Summary.Developed} / {rep?.Summary.DevelopErrors} | [{r.RunId}.md]({r.RunId}.md) |");
        }
        sb.AppendLine();

        sb.AppendLine("## Decoded files");
        sb.AppendLine();
        sb.AppendLine("Cold single run per tier; `ms (JIT ms)` = engine-reported time with the JIT compilation it contains.");
        sb.AppendLine();
        StringBuilder head = new("| file |"), sep = new("| --- |");
        foreach (TierRun r in runs) { head.Append($" decode {r.Tier.Name} |"); sep.Append(" ---: |"); }
        foreach (TierRun r in runs) { head.Append($" develop {r.Tier.Name} |"); sep.Append(" ---: |"); }
        head.Append(" mosaic identical | develop identical |");
        sep.Append(" --- | --- |");
        sb.AppendLine(head.ToString());
        sb.AppendLine(sep.ToString());
        foreach (Row row in cmp.Rows)
        {
            sb.Append($"| `{row.File}` |");
            foreach (Cell c in row.Cells) sb.Append($" {c.Decode} |");
            foreach (Cell c in row.Cells) sb.Append($" {c.Develop} |");
            sb.AppendLine($" {(row.MosaicSame ? "yes" : "**NO**")} | {(row.Developed ? row.DevelopSame ? "yes" : "**NO**" : "—")} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Hashes");
        sb.AppendLine();
        sb.AppendLine("| file | decode path | mosaic sha256 | develop sha256 (Rgb48) |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (Row row in cmp.Rows)
        {
            sb.AppendLine($"| `{row.File}` | `{row.DecodePath}` | `{row.MosaicHash}` | {(row.DevelopHash is null ? "—" : $"`{row.DevelopHash}`")} |");
        }
        sb.AppendLine();
        bool ok = runs.All(r => r.Exit == 0 && r.Report != null) && cmp.MosaicMismatches == 0 && cmp.DevelopMismatches == 0;
        sb.AppendLine($"Verdict: {(ok ? "**PASS**" : "**FAIL**")} — {cmp.MosaicMismatches} mosaic / {cmp.DevelopMismatches} develop hash mismatches, "
            + $"{runs.Count(r => r.Exit != 0)} tier(s) with a non-zero exit.");
        return sb.ToString();
    }

    private static string F(double v) => v.ToString(v >= 100 ? "F1" : "F2", CultureInfo.InvariantCulture);
}
