using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Sdcb.SimdRaw.Harness.Fixtures;

public enum FixtureVerifyMode
{
    /// <summary>Every file must exist with the manifest size.</summary>
    Size,
    /// <summary><see cref="Size"/> plus SHA-256 of a fixed sample of files.</summary>
    Sample,
    /// <summary><see cref="Size"/> plus SHA-256 of every file.</summary>
    Full,
}

public sealed class Fixture(string root, IReadOnlyList<ManifestEntry> entries, IReadOnlyDictionary<string, LibRawGeometry> geometry, string? commit)
{
    public string Root { get; } = root;
    public IReadOnlyList<ManifestEntry> Entries { get; } = entries;
    public IReadOnlyDictionary<string, LibRawGeometry> LibRawGeometry { get; } = geometry;
    public string? Commit { get; } = commit;
    public bool Downloaded { get; init; }

    public string PathOf(ManifestEntry entry) => Path.Combine(Root, entry.LocalRelativePath);
}

/// <summary>Makes sure the Sdcb.LibRaw.TestData fixture is present and intact.
/// The data repo contains a file name with ':' which cannot be checked out on Windows, so instead of
/// <c>git checkout</c> blobs are streamed out of a shallow clone and written under sanitized names.</summary>
public sealed class FixtureManager(string root, FixtureVerifyMode verify, TextWriter log)
{
    public const string RepoUrl = "https://github.com/sdcb/Sdcb.LibRaw.TestData";
    private const string CommitFile = ".fixture-commit";
    private const string TempCloneDir = ".fixture-clone.git";
    private const int SampleStride = 12;

    public string Root { get; } = Path.GetFullPath(root);

    public async Task<Fixture> EnsureAsync(CancellationToken ct)
    {
        (Fixture? fixture, string? problem) = await ValidateAsync(ct);
        if (fixture != null)
        {
            log.WriteLine($"[fixture] {Root}: {fixture.Entries.Count} files ok (verify={verify}), no download");
            return fixture;
        }

        log.WriteLine($"[fixture] {Root}: {problem}");
        await MaterializeAsync(ct);

        (fixture, problem) = await ValidateAsync(ct);
        if (fixture == null) throw new InvalidDataException($"Fixture at {Root} is still invalid after fetching: {problem}");
        log.WriteLine($"[fixture] {Root}: {fixture.Entries.Count} files ok (verify={verify}) after fetch");
        return new Fixture(fixture.Root, fixture.Entries, fixture.LibRawGeometry, fixture.Commit) { Downloaded = true };
    }

    private async Task<(Fixture?, string?)> ValidateAsync(CancellationToken ct)
    {
        string manifestPath = Path.Combine(Root, "manifest.tsv");
        if (!File.Exists(manifestPath)) return (null, "manifest.tsv not found");

        IReadOnlyList<ManifestEntry> entries = ManifestParser.ParseManifest(manifestPath);
        List<string> bad = [];
        foreach (ManifestEntry e in entries)
        {
            FileInfo fi = new(Path.Combine(Root, e.LocalRelativePath));
            if (!fi.Exists) bad.Add($"{e.File} missing");
            else if (fi.Length != e.SizeBytes) bad.Add($"{e.File} size {fi.Length} != {e.SizeBytes}");
        }
        if (bad.Count > 0) return (null, $"{bad.Count}/{entries.Count} files missing or wrong size (first: {bad[0]})");

        IEnumerable<ManifestEntry> toHash = verify switch
        {
            FixtureVerifyMode.Full => entries,
            FixtureVerifyMode.Sample => entries.Where((_, i) => i % SampleStride == 0),
            _ => [],
        };
        foreach (ManifestEntry e in toHash)
        {
            await using FileStream fs = File.OpenRead(Path.Combine(Root, e.LocalRelativePath));
            string sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
            if (!sha.Equals(e.Sha256, StringComparison.OrdinalIgnoreCase)) return (null, $"{e.File} sha256 mismatch");
        }

        IReadOnlyDictionary<string, LibRawGeometry> geometry = ManifestParser.ParseLibRawGeometry(Path.Combine(Root, "docs", "libraw_goldens.tsv"));
        return (new Fixture(Root, entries, geometry, ReadCommit()), null);
    }

    private string? ReadCommit()
    {
        string path = Path.Combine(Root, CommitFile);
        if (File.Exists(path)) return File.ReadAllText(path).Trim();
        string head = Path.Combine(Root, ".git", "HEAD");
        return File.Exists(head) ? TryGitCapture(Path.Combine(Root, ".git"), "rev-parse", "HEAD") : null;
    }

    private async Task MaterializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        string existingGit = Path.Combine(Root, ".git");
        string gitDir;
        bool ownsGitDir;
        if (Directory.Exists(existingGit))
        {
            gitDir = existingGit;
            ownsGitDir = false;
            log.WriteLine("[fixture] extracting from existing clone (.git)");
        }
        else
        {
            gitDir = Path.Combine(Root, TempCloneDir);
            if (Directory.Exists(gitDir)) DeleteDirectory(gitDir);
            log.WriteLine($"[fixture] git clone --depth 1 {RepoUrl}");
            await RunGitAsync(null, ct, "clone", "--bare", "--depth", "1", "--single-branch", RepoUrl, gitDir);
            ownsGitDir = true;
        }

        string commit = GitCapture(gitDir, "rev-parse", "HEAD");
        List<(string Sha, string Path, long Size)> blobs = ListBlobs(gitDir);
        int written = await ExtractBlobsAsync(gitDir, blobs, ct);
        await File.WriteAllTextAsync(Path.Combine(Root, CommitFile), commit + "\n", ct);
        log.WriteLine($"[fixture] extracted {written}/{blobs.Count} files at {commit[..Math.Min(12, commit.Length)]}");

        if (ownsGitDir) DeleteDirectory(gitDir);
    }

    private static List<(string Sha, string Path, long Size)> ListBlobs(string gitDir)
    {
        string output = GitCapture(gitDir, "ls-tree", "-r", "-l", "-z", "HEAD");
        List<(string, string, long)> result = [];
        foreach (string record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<mode> SP <type> SP <object> SP+ <size> TAB <path>"
            int tab = record.IndexOf('\t');
            string[] meta = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta[1] != "blob") continue;
            result.Add((meta[2], record[(tab + 1)..], long.Parse(meta[3])));
        }
        return result;
    }

    private async Task<int> ExtractBlobsAsync(string gitDir, List<(string Sha, string Path, long Size)> blobs, CancellationToken ct)
    {
        ProcessStartInfo psi = GitStartInfo(gitDir, "cat-file", "--batch");
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        using Process git = Process.Start(psi) ?? throw new InvalidOperationException("failed to start git");
        Stream stdout = git.StandardOutput.BaseStream;
        StreamWriter stdin = git.StandardInput;
        stdin.NewLine = "\n";

        int written = 0;
        byte[] buffer = new byte[1 << 20];
        foreach ((string sha, string repoPath, long size) in blobs)
        {
            string target = Path.Combine(Root, FixturePaths.ToLocal(repoPath));
            bool skip = new FileInfo(target).Exists && new FileInfo(target).Length == size;

            await stdin.WriteLineAsync(sha);
            await stdin.FlushAsync(ct);
            string header = ReadLine(stdout);
            string[] parts = header.Split(' ');
            if (parts.Length != 3 || parts[1] != "blob") throw new InvalidDataException($"git cat-file: unexpected '{header}' for {repoPath}");
            long remaining = long.Parse(parts[2]);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string tmp = target + ".tmp";
            await using (FileStream? fs = skip ? null : File.Create(tmp))
            {
                while (remaining > 0)
                {
                    int n = await stdout.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                    if (n == 0) throw new EndOfStreamException($"git cat-file ended while reading {repoPath}");
                    if (fs != null) await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                    remaining -= n;
                }
            }
            if (stdout.ReadByte() != '\n') throw new InvalidDataException("git cat-file: missing record terminator");
            if (skip) continue;
            File.Move(tmp, target, overwrite: true);
            written++;
        }

        stdin.Close();
        await git.WaitForExitAsync(ct);
        return written;
    }

    private static string ReadLine(Stream s)
    {
        StringBuilder sb = new();
        for (int b = s.ReadByte(); b != '\n'; b = s.ReadByte())
        {
            if (b < 0) throw new EndOfStreamException("git cat-file closed its output");
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static ProcessStartInfo GitStartInfo(string? gitDir, params string[] args)
    {
        ProcessStartInfo psi = new("git") { UseShellExecute = false };
        if (gitDir != null)
        {
            psi.ArgumentList.Add("--git-dir");
            psi.ArgumentList.Add(gitDir);
        }
        foreach (string a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static async Task RunGitAsync(string? gitDir, CancellationToken ct, params string[] args)
    {
        using Process git = Process.Start(GitStartInfo(gitDir, args)) ?? throw new InvalidOperationException("failed to start git");
        await git.WaitForExitAsync(ct);
        if (git.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} exited with {git.ExitCode}");
    }

    private static string GitCapture(string gitDir, params string[] args) =>
        TryGitCapture(gitDir, args) ?? throw new InvalidOperationException($"git {string.Join(' ', args)} failed");

    private static string? TryGitCapture(string gitDir, params string[] args)
    {
        ProcessStartInfo psi = GitStartInfo(gitDir, args);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        try
        {
            using Process git = Process.Start(psi)!;
            Task<string> stderr = git.StandardError.ReadToEndAsync();
            string output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            _ = stderr.Result;
            return git.ExitCode == 0 ? output.Trim('\n', '\r', ' ') : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void DeleteDirectory(string path)
    {
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }
}
