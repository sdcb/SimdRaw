using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Sdcb.SimdRaw.Harness.Benchmark;
using Sdcb.SimdRaw.Harness.Engines;
using Sdcb.SimdRaw.Harness.Engines.RawSpeed;
using Sdcb.SimdRaw.Harness.Fixtures;
using Sdcb.SimdRaw.Harness.Reporting;

namespace Sdcb.SimdRaw.Harness;

public static class Program
{
    private const int ExitOk = 0;
    private const int ExitGateFailed = 1;
    private const int ExitUsage = 2;
    private const int ExitSetupFailed = 3;

    private const string Usage = """
        Usage: Sdcb.SimdRaw.Harness --engine <simdraw|rawspeed> [options]

          --engine <name>     simdraw | rawspeed
          --data <dir>        fixture directory (default: <repo>/testdata); cloned from
                              https://github.com/sdcb/Sdcb.LibRaw.TestData when missing/incomplete
          --out <dir>         report directory (default: <repo>/artifacts/reports)
          --filter <glob>     only files whose name or decode path matches (e.g. "sony_*", "*.NEF")
          --verify <mode>     fixture check: size | sample (default) | full

        Environment:
          SIMDRAW_ENGINE_CACHE           engine binary cache (default %LOCALAPPDATA%/simdraw-engines, ~/.cache/simdraw-engines)
          SIMDRAW_RAWSPEED_LIB           use a local rawspeed shim instead of the pinned download
          SIMDRAW_RAWSPEED_CAMERAS_XML   cameras.xml for SIMDRAW_RAWSPEED_LIB (default: next to the library)

        Exit codes: 0 ok, 1 any fail/error file, 2 usage, 3 setup (fixture/engine) failure.
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!TryParse(args, out Options? options, out string? error))
        {
            if (error != null) Console.Error.WriteLine(error);
            Console.WriteLine(Usage);
            return error == null ? ExitOk : ExitUsage;
        }

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        TextWriter log = Console.Out;

        Fixture fixture;
        try
        {
            fixture = await new FixtureManager(options.DataDir, options.Verify, log).EnsureAsync(cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[fixture] setup failed: {ex.Message}");
            return ExitSetupFailed;
        }

        IReadOnlyList<ManifestEntry> entries = Filter(fixture.Entries, options.Filter);
        if (entries.Count == 0)
        {
            Console.Error.WriteLine($"No fixture file matches --filter '{options.Filter}'.");
            return ExitUsage;
        }

        using IDecodeEngine engine = CreateEngine(options.Engine, log);
        RunReport report;
        try
        {
            report = await new BenchmarkRunner(engine, fixture, entries, LoadKnownDiffs(), log).RunAsync(GitSha(), options.Filter, cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[engine] {options.Engine} failed: {ex}");
            return ExitSetupFailed;
        }

        (string json, string md) = ReportWriter.Write(report, options.OutDir);
        StatusCounts c = report.Summary.Status;
        log.WriteLine($"pass {c.Pass} · fail {c.Fail} · layout-diff {c.LayoutDiff} · engine-diff {c.EngineDiff} · unsupported {c.Unsupported} · no-golden {c.NoGolden} · error {c.Error}");
        if (report.Summary.DecodeMs is { } t)
        {
            log.WriteLine($"decode ms: mean {t.Mean:F2} · median {t.Median:F2} · P95 {t.P95:F2} · total {t.Total:F1} · init {report.Init.TotalMs:F1}");
        }
        log.WriteLine($"report: {json}");
        log.WriteLine($"report: {md}");
        return c.Fail + c.Error > 0 ? ExitGateFailed : ExitOk;
    }

    private static IDecodeEngine CreateEngine(string name, TextWriter log) => name switch
    {
        "simdraw" => new SimdRawEngine(),
        "rawspeed" => new RawSpeedEngine(EngineManifest.LoadEmbedded().Engines["rawspeed"], EngineCache.CreateDefault(log), log),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static IReadOnlyList<KnownDiff> LoadKnownDiffs()
    {
        using Stream stream = typeof(Program).Assembly.GetManifestResourceStream("known-diffs.tsv")
            ?? throw new InvalidOperationException("known-diffs.tsv resource is missing.");
        using StreamReader reader = new(stream);
        return ManifestParser.ParseKnownDiffs(reader);
    }

    private static List<ManifestEntry> Filter(IReadOnlyList<ManifestEntry> entries, string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob)) return [.. entries];
        Regex re = new("^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return [.. entries.Where(e => re.IsMatch(e.File) || re.IsMatch(e.DecodePath))];
    }

    private sealed record Options(string Engine, string DataDir, string OutDir, string? Filter, FixtureVerifyMode Verify);

    private static bool TryParse(string[] args, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Options? options, out string? error)
    {
        options = null;
        error = null;
        string? engine = null, data = null, output = null, filter = null;
        FixtureVerifyMode verify = FixtureVerifyMode.Sample;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-h" or "--help" or "-?") return false;
            if (i + 1 >= args.Length)
            {
                error = $"Missing value for {arg}.";
                return false;
            }
            string value = args[++i];
            switch (arg)
            {
                case "--engine": engine = value.ToLowerInvariant(); break;
                case "--data": data = value; break;
                case "--out": output = value; break;
                case "--filter": filter = value; break;
                case "--verify":
                    if (!Enum.TryParse(value, ignoreCase: true, out verify))
                    {
                        error = $"Unknown --verify mode '{value}'.";
                        return false;
                    }
                    break;
                default:
                    error = $"Unknown option {arg}.";
                    return false;
            }
        }

        if (engine is not ("simdraw" or "rawspeed"))
        {
            error = engine is null ? "--engine is required." : $"Unknown engine '{engine}'.";
            return false;
        }

        string root = RepoRoot();
        options = new Options(
            engine,
            Path.GetFullPath(data ?? Path.Combine(root, "testdata")),
            Path.GetFullPath(output ?? Path.Combine(root, "artifacts", "reports")),
            filter,
            verify);
        return true;
    }

    private static string RepoRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (DirectoryInfo? d = new(start); d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "SimdRaw.slnx"))) return d.FullName;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    private static string? GitSha()
    {
        string? sha = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(sha)) return sha[..Math.Min(7, sha.Length)];
        try
        {
            ProcessStartInfo psi = new("git", "rev-parse --short HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = RepoRoot(),
            };
            using Process p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
