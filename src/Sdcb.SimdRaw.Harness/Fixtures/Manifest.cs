namespace Sdcb.SimdRaw.Harness.Fixtures;

/// <summary>One row of Sdcb.LibRaw.TestData/manifest.tsv.</summary>
public sealed record ManifestEntry(
    string File,
    string Sha256,
    long SizeBytes,
    string UnpackFunction,
    string RpuSource,
    string? GoldenLibRaw,
    string? GoldenRawSpeed,
    IReadOnlyDictionary<string, string> Columns)
{
    /// <summary>Raw value of a manifest column ("" when absent).</summary>
    public string Column(string name) => Columns.TryGetValue(name, out string? v) ? v : "";

    /// <summary>LibRaw decode path, e.g. <c>sony_arw2_load_raw</c>.</summary>
    public string DecodePath => UnpackFunction.EndsWith("()", StringComparison.Ordinal) ? UnpackFunction[..^2] : UnpackFunction;

    /// <summary>Path relative to the fixture root, with characters that are invalid on Windows replaced.</summary>
    public string LocalRelativePath => FixturePaths.ToLocal("files/" + File);
}

/// <summary>One row of docs/libraw_goldens.tsv: LibRaw's decoded buffer geometry.</summary>
public sealed record LibRawGeometry(string File, int Width, int Height, int Elements, uint Filters, string Make, string Model)
{
    public override string ToString() => $"{Width}x{Height}x{Elements}";
}

/// <summary>A reviewed engine-vs-LibRaw difference, valid only while the engine still produces
/// exactly <see cref="MosaicSha256"/> for <see cref="File"/>.</summary>
public sealed record KnownDiff(string Engine, string File, string MosaicSha256, string Reason);

public static class ManifestParser
{
    public static IReadOnlyList<ManifestEntry> ParseManifest(string path)
    {
        List<ManifestEntry> entries = [];
        string[] lines = File.ReadAllLines(path);
        if (lines.Length == 0) throw new InvalidDataException($"{path} is empty.");
        Dictionary<string, int> col = Header(lines[0]);
        foreach (string line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] c = line.Split('\t');
            entries.Add(new ManifestEntry(
                File: c[col["file"]],
                Sha256: c[col["sha256"]],
                SizeBytes: long.Parse(c[col["size_bytes"]]),
                UnpackFunction: c[col["unpack_function"]],
                RpuSource: c[col["rpu_source"]],
                GoldenLibRaw: Golden(c[col["golden_libraw"]]),
                GoldenRawSpeed: Golden(c[col["golden_rawspeed"]]),
                Columns: col.ToDictionary(kv => kv.Key, kv => kv.Value < c.Length ? c[kv.Value] : "")));
        }
        return entries;
    }

    /// <summary>Parses docs/libraw_goldens.tsv; rows LibRaw failed to decode are skipped.</summary>
    public static IReadOnlyDictionary<string, LibRawGeometry> ParseLibRawGeometry(string path)
    {
        Dictionary<string, LibRawGeometry> result = [];
        if (!File.Exists(path)) return result;
        string[] lines = File.ReadAllLines(path);
        if (lines.Length == 0) return result;
        Dictionary<string, int> col = Header(lines[0]);
        foreach (string line in lines.Skip(1))
        {
            string[] c = line.Split('\t');
            if (c.Length < col.Count) continue;
            string[] dims = c[col["dims"]].Split('x');
            if (dims.Length != 2 || !int.TryParse(dims[0], out int w) || !int.TryParse(dims[1], out int h)) continue;
            int elems = int.Parse(c[col["elems"]].Split('=')[1]);
            uint filters = uint.Parse(c[col["filters"]].Split('=')[1]);
            result[c[col["file"]]] = new LibRawGeometry(c[col["file"]], w, h, elems, filters, c[col["make"]], c[col["model"]]);
        }
        return result;
    }

    /// <summary>Parses known-diffs.tsv: <c>engine, file, mosaic_sha256, reason</c>.</summary>
    public static IReadOnlyList<KnownDiff> ParseKnownDiffs(TextReader reader)
    {
        List<KnownDiff> result = [];
        string? header = reader.ReadLine();
        if (header is null) return result;
        Dictionary<string, int> col = Header(header);
        for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            string[] c = line.Split('\t');
            result.Add(new KnownDiff(c[col["engine"]], c[col["file"]], c[col["mosaic_sha256"]], c[col["reason"]]));
        }
        return result;
    }

    private static Dictionary<string, int> Header(string line) =>
        line.Split('\t').Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i, StringComparer.Ordinal);

    private static string? Golden(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("unsupported", StringComparison.OrdinalIgnoreCase) ? null : value;
}

public static class FixturePaths
{
    private static readonly char[] s_invalid = ['<', '>', ':', '"', '|', '?', '*', '\\'];

    /// <summary>Maps a repository path ("/"-separated) to a portable local relative path.</summary>
    public static string ToLocal(string repoPath) =>
        Path.Combine(repoPath.Split('/').Select(Sanitize).ToArray());

    public static string Sanitize(string segment)
    {
        Span<char> chars = segment.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(s_invalid, chars[i]) >= 0 || char.IsControl(chars[i])) chars[i] = '_';
        }
        return new string(chars);
    }
}
