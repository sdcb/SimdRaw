using Sdcb.SimdRaw.Harness.Engines;
using Sdcb.SimdRaw.Harness.Fixtures;
using Sdcb.SimdRaw.Harness.Reporting;

namespace Sdcb.SimdRaw.Harness.Benchmark;

/// <summary>Self-oracle comparison for one file: <c>match</c>, <c>mismatch</c> or <c>unsupported</c>.</summary>
public sealed record OracleCheck(string Column, string Status, string? EngineHash, string? GoldenValue);

internal sealed class Classifier(ISelfOracleEngine? oracle, IReadOnlyList<KnownDiff> knownDiffs, string engineName)
{
    public (string Status, string? Note, OracleCheck? Oracle) Classify(ManifestEntry entry, LibRawGeometry? libraw, DecodeResult result)
    {
        OracleCheck? check = CheckOracle(entry, result);

        if (!result.Success)
        {
            if (DecodeErrorCodes.IsUnsupported(result.ErrorCode)) return (FileStatus.Unsupported, null, check);
            if (check?.Status == "unsupported")
            {
                return (FileStatus.Unsupported, $"{check.Column} also marks this file unsupported", check);
            }
            return (FileStatus.Error, null, check);
        }

        if (entry.GoldenLibRaw is null) return (FileStatus.NoGolden, null, check);
        if (result.HashAlgorithm == HashAlgorithms.Sha256 &&
            string.Equals(result.MosaicHash, entry.GoldenLibRaw, StringComparison.OrdinalIgnoreCase))
        {
            return (FileStatus.Pass, null, check);
        }

        List<string> diffs = LayoutDiffs(result.Layout, libraw);
        if (diffs.Count > 0) return (FileStatus.LayoutDiff, string.Join("; ", diffs), check);

        if (check?.Status == "match")
        {
            return (FileStatus.EngineDiff, $"output identical to the engine's own golden ({check.Column}); LibRaw's buffer differs in value semantics", check);
        }

        KnownDiff? known = knownDiffs.FirstOrDefault(k => k.Engine == engineName && k.File == entry.File);
        if (known != null)
        {
            if (string.Equals(known.MosaicSha256, result.MosaicHash, StringComparison.OrdinalIgnoreCase))
            {
                return (FileStatus.EngineDiff, $"known-diffs.tsv: {known.Reason}", check);
            }
            return (FileStatus.Fail, $"output changed since known-diffs.tsv was reviewed (pinned {known.MosaicSha256[..12]}…)", check);
        }

        return (FileStatus.Fail, libraw is null ? "LibRaw geometry unknown" : "same geometry as LibRaw, content differs", check);
    }

    /// <summary>Compares the engine buffer with LibRaw's (raw_image: 1 u16 per pixel, color4_image: 4).</summary>
    public static List<string> LayoutDiffs(MosaicLayout? engine, LibRawGeometry? libraw)
    {
        List<string> diffs = [];
        if (engine is null || libraw is null) return diffs;
        if (engine.Width != libraw.Width) diffs.Add($"width {engine.Width} vs LibRaw {libraw.Width}");
        if (engine.Height != libraw.Height) diffs.Add($"height {engine.Height} vs LibRaw {libraw.Height}");
        if (engine.Components != libraw.Elements) diffs.Add($"components {engine.Components} vs LibRaw {libraw.Elements}");
        if (engine.SampleType != "u16") diffs.Add($"{engine.SampleType} samples vs LibRaw u16");
        return diffs;
    }

    private OracleCheck? CheckOracle(ManifestEntry entry, DecodeResult result)
    {
        if (oracle is null) return null;
        string golden = entry.Column(oracle.OracleColumn);
        if (golden.Length == 0) return null;
        if (golden.Equals("unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return new OracleCheck(oracle.OracleColumn, "unsupported", null, golden);
        }
        if (!result.Success || result.ExtraHashes is null ||
            !result.ExtraHashes.TryGetValue(oracle.OracleHashAlgorithm, out string? hash))
        {
            return null;
        }
        string status = string.Equals(hash, golden, StringComparison.OrdinalIgnoreCase) ? "match" : "mismatch";
        return new OracleCheck(oracle.OracleColumn, status, hash, golden);
    }
}
