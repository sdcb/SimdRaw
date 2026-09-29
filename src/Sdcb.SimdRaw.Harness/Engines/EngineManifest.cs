using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sdcb.SimdRaw.Harness.Engines;

/// <summary>Pinned native engine builds, read from the embedded <c>engine-manifest.json</c>.</summary>
public sealed class EngineManifest
{
    public required Dictionary<string, EngineRelease> Engines { get; init; }

    public static EngineManifest LoadEmbedded()
    {
        using Stream stream = typeof(EngineManifest).Assembly.GetManifestResourceStream("engine-manifest.json")
            ?? throw new InvalidOperationException("engine-manifest.json resource is missing.");
        return JsonSerializer.Deserialize(stream, EngineManifestJsonContext.Default.EngineManifest)
            ?? throw new InvalidOperationException("engine-manifest.json is empty.");
    }

    /// <summary>Runtime identifier used as the key of <see cref="EngineRelease.Builds"/>.</summary>
    public static string CurrentRid
    {
        get
        {
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            return $"{os}-{arch}";
        }
    }
}

public sealed class EngineRelease
{
    /// <summary>Cache key and file-name stem, <c>yyyymmdd_commit</c>.</summary>
    public required string Version { get; init; }
    public required string Commit { get; init; }
    public string? Describe { get; init; }
    public string? Source { get; init; }
    public required Dictionary<string, EngineBuild> Builds { get; init; }
}

public sealed class EngineBuild
{
    public required EngineAsset Library { get; init; }
    public required EngineAsset CamerasXml { get; init; }
}

public sealed class EngineAsset
{
    public required string Url { get; init; }
    public required string Sha256 { get; init; }
    public required long Size { get; init; }

    public string FileName => Path.GetFileName(new Uri(Url).AbsolutePath);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(EngineManifest))]
internal sealed partial class EngineManifestJsonContext : JsonSerializerContext;
