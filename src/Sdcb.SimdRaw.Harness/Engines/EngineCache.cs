using System.Security.Cryptography;

namespace Sdcb.SimdRaw.Harness.Engines;

/// <summary>Content-addressed local cache for downloaded engine binaries.</summary>
public sealed class EngineCache(string root, TextWriter log)
{
    public const string CacheEnvVar = "SIMDRAW_ENGINE_CACHE";

    public string Root { get; } = root;

    public static EngineCache CreateDefault(TextWriter log) => new(DefaultRoot(), log);

    public static string DefaultRoot()
    {
        string? overridden = Environment.GetEnvironmentVariable(CacheEnvVar);
        if (!string.IsNullOrWhiteSpace(overridden)) return Path.GetFullPath(overridden);

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "simdraw-engines");
        }

        string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        string baseDir = !string.IsNullOrWhiteSpace(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(baseDir, "simdraw-engines");
    }

    /// <summary>Returns the local path of <paramref name="asset"/>, downloading it only when the cached copy
    /// is missing or does not match the pinned size + SHA-256.</summary>
    public async Task<(string Path, bool Downloaded)> EnsureAsync(string engine, string version, EngineAsset asset, HttpClient http, CancellationToken ct)
    {
        string dir = Path.Combine(Root, engine, version);
        string path = Path.Combine(dir, asset.FileName);
        if (await MatchesAsync(path, asset, ct)) return (path, false);

        Directory.CreateDirectory(dir);
        string tmp = path + $".{Environment.ProcessId}.tmp";
        log.WriteLine($"[engine] downloading {asset.Url}");
        using (HttpResponseMessage response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using FileStream fs = File.Create(tmp);
            await response.Content.CopyToAsync(fs, ct);
        }

        if (!await MatchesAsync(tmp, asset, ct))
        {
            File.Delete(tmp);
            throw new InvalidDataException($"Downloaded {asset.Url} does not match the pinned sha256 {asset.Sha256}.");
        }
        File.Move(tmp, path, overwrite: true);
        return (path, true);
    }

    private static async Task<bool> MatchesAsync(string path, EngineAsset asset, CancellationToken ct)
    {
        FileInfo fi = new(path);
        if (!fi.Exists || fi.Length != asset.Size) return false;
        await using FileStream fs = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexStringLower(hash).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
