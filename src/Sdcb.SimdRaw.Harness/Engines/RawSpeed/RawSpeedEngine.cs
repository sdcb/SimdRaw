using System.Diagnostics;

namespace Sdcb.SimdRaw.Harness.Engines.RawSpeed;

/// <summary>RawSpeed reference engine through the rawspeed C ABI shim (P/Invoke).
/// Binaries are pinned in engine-manifest.json and cached locally after the first download.</summary>
public sealed class RawSpeedEngine(EngineRelease release, EngineCache cache, TextWriter log) : IDecodeEngine, IEngineDiagnostics, ISelfOracleEngine
{
    public string OracleColumn => "golden_rawspeed";

    public string OracleHashAlgorithm => HashAlgorithms.Md5LineMd5;

    public const string LibraryOverrideEnvVar = "SIMDRAW_RAWSPEED_LIB";
    public const string CamerasXmlOverrideEnvVar = "SIMDRAW_RAWSPEED_CAMERAS_XML";

    private readonly List<InitPhase> _phases = [];
    private readonly Dictionary<string, string> _properties = [];
    private RawSpeedNative? _native;

    public string Name => "rawspeed";

    public string Version { get; private set; } = release.Version;

    public IReadOnlyList<InitPhase> InitPhases => _phases;

    public IReadOnlyDictionary<string, string> Properties => _properties;

    public async Task InitializeAsync(CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        (string libraryPath, string camerasXmlPath) = await ResolveBinariesAsync(ct);
        _phases.Add(new InitPhase("resolve-binaries", sw.Elapsed.TotalMilliseconds));

        sw.Restart();
        _native = new RawSpeedNative(libraryPath);
        _phases.Add(new InitPhase("load-library", sw.Elapsed.TotalMilliseconds));

        sw.Restart();
        int rc = _native.Init(camerasXmlPath, out string error);
        _phases.Add(new InitPhase("rawspeed_init", sw.Elapsed.TotalMilliseconds));
        if (rc != RawSpeedErrorCode.Ok)
        {
            throw new InvalidOperationException($"rawspeed_init({camerasXmlPath}) failed with {rc}: {error}");
        }

        Version = $"{release.Version} ({_native.Version})";
        _properties["library"] = libraryPath;
        _properties["camerasXml"] = camerasXmlPath;
        _properties["nativeVersion"] = _native.Version;
    }

    private async Task<(string Library, string CamerasXml)> ResolveBinariesAsync(CancellationToken ct)
    {
        string? libOverride = Environment.GetEnvironmentVariable(LibraryOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(libOverride))
        {
            string xml = Environment.GetEnvironmentVariable(CamerasXmlOverrideEnvVar)
                ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(libOverride))!, "cameras.xml");
            _properties["source"] = $"override ({LibraryOverrideEnvVar})";
            return (Path.GetFullPath(libOverride), Path.GetFullPath(xml));
        }

        string rid = EngineManifest.CurrentRid;
        if (!release.Builds.TryGetValue(rid, out EngineBuild? build))
        {
            throw new PlatformNotSupportedException(
                $"No rawspeed build for {rid} in engine-manifest.json (available: {string.Join(", ", release.Builds.Keys)}).");
        }

        using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
        (string lib, bool libDownloaded) = await cache.EnsureAsync(Name, release.Version, build.Library, http, ct);
        (string xmlPath, bool xmlDownloaded) = await cache.EnsureAsync(Name, release.Version, build.CamerasXml, http, ct);
        bool downloaded = libDownloaded || xmlDownloaded;
        _properties["source"] = downloaded ? "downloaded" : "cache";
        _properties["cacheDir"] = Path.GetDirectoryName(lib)!;
        _properties["libraryUrl"] = build.Library.Url;
        _properties["camerasXmlUrl"] = build.CamerasXml.Url;
        log.WriteLine($"[engine] rawspeed {release.Version} {rid}: {(downloaded ? "downloaded" : "cache hit")} ({Path.GetDirectoryName(lib)})");
        return (lib, xmlPath);
    }

    public unsafe Task<DecodeResult> DecodeAsync(string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RawSpeedNative native = _native ?? throw new InvalidOperationException("InitializeAsync was not called.");

        RawSpeedResult r = default;
        try
        {
            int rc = native.Decode(filePath, &r);
            if (rc != RawSpeedErrorCode.Ok)
            {
                string code = rc switch
                {
                    RawSpeedErrorCode.Unsupported => DecodeErrorCodes.Unsupported,
                    RawSpeedErrorCode.Corrupt => DecodeErrorCodes.DecodeError,
                    RawSpeedErrorCode.Io => DecodeErrorCodes.IoError,
                    RawSpeedErrorCode.OutOfMemory => DecodeErrorCodes.OutOfMemory,
                    _ => DecodeErrorCodes.Internal,
                };
                return Task.FromResult(DecodeResult.Failed(code, RawSpeedNative.ReadString(r.ErrorMsg, 256)));
            }

            long bytes = r.PixelsLen * r.BytesPerSample;
            long rowBytes = (long)r.Width * r.Cpp * r.BytesPerSample;
            string sha = MosaicHasher.Sha256(r.Pixels, bytes);
            string lineMd5 = MosaicHasher.Md5OfLineMd5(r.Pixels, rowBytes, r.Height);

            return Task.FromResult(new DecodeResult(true, null, r.DecodeTimeMs, sha, HashAlgorithms.Sha256)
            {
                EngineTotalTimeMs = r.TotalTimeMs,
                ExtraHashes = new Dictionary<string, string> { [HashAlgorithms.Md5LineMd5] = lineMd5 },
                Layout = new MosaicLayout(r.Width, r.Height, r.Cpp, r.DataType == 1 ? "f32" : "u16")
                {
                    CropLeft = r.CropLeft,
                    CropTop = r.CropTop,
                    CropWidth = r.CropWidth,
                    CropHeight = r.CropHeight,
                    BlackLevel = r.BlackLevel < 0 ? null : r.BlackLevel,
                    WhiteLevel = r.WhitePoint < 0 ? null : r.WhitePoint,
                    CfaFilters = r.CfaFilters,
                    Make = RawSpeedNative.ReadString(r.Make, 64),
                    Model = RawSpeedNative.ReadString(r.Model, 64),
                },
            });
        }
        finally
        {
            native.Free(&r);
        }
    }

    public void Dispose()
    {
        _native?.Dispose();
        _native = null;
    }
}
