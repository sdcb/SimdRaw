using Sdcb.SimdRaw.Containers;
using Sdcb.SimdRaw.Decoders;

namespace Sdcb.SimdRaw.Formats;

internal enum RawCodec
{
    SonyArw2,
    Nikon14Bit,
}

/// <summary>Develop-time metadata. Black levels are indexed by 2×2 CFA position <c>(y &amp; 1) · 2 + (x &amp; 1)</c> in
/// mosaic coordinates; all levels are in decoded-mosaic units.</summary>
internal sealed class DevelopMetadata
{
    public required RawCfaPattern Cfa { get; init; }
    public required int[] Black { get; init; }
    public required int White { get; init; }
    public required RawRect ActiveArea { get; init; }
    /// <summary>As-shot white balance multipliers (R, G, B), null when the file has none.</summary>
    public double[]? AsShotWb { get; init; }
    /// <summary>XYZ → camera ×10000, null for unknown models.</summary>
    public short[]? CamXyz { get; init; }
}

internal sealed class RawSource
{
    public required RawCodec Codec { get; init; }
    public required string Make { get; init; }
    public required string Model { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required long DataOffset { get; init; }
    public SonyCurve Curve { get; init; } = SonyCurve.Identity;
    public required DevelopMetadata Develop { get; init; }
}

internal static class RawIdentifier
{
    private const ushort TagMake = 0x010F, TagModel = 0x0110;
    private const ushort TagWidth = 0x0100, TagHeight = 0x0101, TagBps = 0x0102, TagCompression = 0x0103;
    private const ushort TagPhotometric = 0x0106, TagStripOffsets = 0x0111, TagStripByteCounts = 0x0117;
    private const ushort TagCfaRepeatDim = 0x828D, TagCfaPattern = 0x828E, TagMakerNote = 0x927C;
    private const ushort TagSonyCurve = 0x7010;
    private const uint PhotometricCfa = 32803;

    /// <summary>Throws <see cref="RawException"/> with <see cref="RawErrorCode.UnsupportedFormat"/> for anything that is
    /// not one of the implemented decode paths.</summary>
    public static RawSource Identify(ReadOnlyMemory<byte> data)
    {
        TiffReader r;
        List<TiffIfd> all;
        try
        {
            r = new TiffReader(data);
            all = [.. TiffReader.Flatten(r.ParseChain(r.FirstIfdOffset))];
        }
        catch (TiffFormatException ex)
        {
            throw Unsupported($"not a supported RAW container ({ex.Message})");
        }
        if (all.Count == 0) throw Unsupported("TIFF container without IFDs");

        string make = FirstString(r, all, TagMake);
        string model = FirstString(r, all, TagModel);
        try
        {
            foreach (TiffIfd ifd in all)
            {
                if (!TryImage(r, ifd, out ImageIfd img)) continue;
                if (img.Compression == 32767 && img.Bytes == (long)img.Width * img.Height)
                {
                    return SonyArw2(r, data, all, ifd, img, make, model);
                }
                if (make.StartsWith("NIKON", StringComparison.OrdinalIgnoreCase) && img.Compression == 34713 && img.Bps == 14 &&
                    img.Photometric == PhotometricCfa && img.Bytes == (long)Nikon14BitDecoder.LineLength(img.Width) * img.Height)
                {
                    return Nikon14Bit(r, all, ifd, img, make, model);
                }
            }
        }
        catch (TiffFormatException ex)
        {
            throw new RawException(RawErrorCode.CorruptData, $"{make} {model}: malformed metadata ({ex.Message})");
        }
        throw Unsupported($"{(make.Length == 0 ? "unknown make" : make)} {model}: decode path not implemented");
    }

    private static RawSource SonyArw2(TiffReader r, ReadOnlyMemory<byte> data, List<TiffIfd> all, TiffIfd raw, ImageIfd img, string make, string model)
    {
        SonyCurve curve = SonyCurve.Identity;
        if (raw.TryGet(TagSonyCurve, out TiffEntry c) && c.Count >= 4)
        {
            ushort Knee(int i) => (ushort)(r.UInt(c, i) >> 2 & 0xfff);
            curve = new SonyCurve(Knee(0), Knee(1), Knee(2), Knee(3));
        }

        int[] black = [512, 512, 512, 512];
        int white = curve.Lut[^1];
        double[]? wb = null;
        if (SonySr2.Open(r, data, all[0]) is ({ } sr, { } sub))
        {
            if (sub.TryGet(0x7310, out TiffEntry bl) && bl.Count >= 4)
            {
                // Tag order R, G1, G2, B.
                black = CfaLevels(img.Cfa, [(int)sr.UInt(bl, 0), (int)sr.UInt(bl, 1), (int)sr.UInt(bl, 2), (int)sr.UInt(bl, 3)]);
            }
            if (sub.TryGet(0x787F, out TiffEntry wl) && wl.Count >= 1) white = (int)sr.UInt(wl, 0);
            if (sub.TryGet(0x7313, out TiffEntry rggb) && rggb.Count >= 4)
            {
                wb = [sr.UInt(rggb, 0), (sr.UInt(rggb, 1) + sr.UInt(rggb, 2)) / 2.0, sr.UInt(rggb, 3)];
            }
            else if (sub.TryGet(0x7303, out TiffEntry grbg) && grbg.Count >= 4)
            {
                wb = [sr.UInt(grbg, 1), (sr.UInt(grbg, 0) + sr.UInt(grbg, 3)) / 2.0, sr.UInt(grbg, 2)];
            }
        }

        CameraProfile? profile = CameraProfiles.Find(make, model);
        return new RawSource
        {
            Codec = RawCodec.SonyArw2,
            Make = make,
            Model = model,
            Width = img.Width,
            Height = img.Height,
            DataOffset = img.Offset,
            Curve = curve,
            Develop = new DevelopMetadata
            {
                Cfa = img.Cfa,
                Black = black,
                White = white,
                ActiveArea = new RawRect(0, 0, img.Width - Math.Clamp(profile?.CropRight ?? 0, 0, img.Width - 2), img.Height),
                AsShotWb = wb,
                CamXyz = profile?.CamXyz,
            },
        };
    }

    private static RawSource Nikon14Bit(TiffReader r, List<TiffIfd> all, TiffIfd raw, ImageIfd img, string make, string model)
    {
        int[] black = [0, 0, 0, 0];
        double[]? wb = null;
        RawRect active = new(0, 0, img.Width, img.Height);
        if (NikonMakerNote(r, all) is ({ } mr, { } mn))
        {
            if (mn.TryGet(0x003D, out TiffEntry bl) && bl.Count >= 4)
            {
                black = CfaLevels(img.Cfa, [(int)mr.UInt(bl, 0), (int)mr.UInt(bl, 1), (int)mr.UInt(bl, 2), (int)mr.UInt(bl, 3)]);
            }
            if (mn.TryGet(0x000C, out TiffEntry rb) && rb.Count >= 2 && mr.Real(rb, 0) > 0 && mr.Real(rb, 1) > 0)
            {
                wb = [mr.Real(rb, 0), 1.0, mr.Real(rb, 1)];
            }
            if (mn.TryGet(0x0045, out TiffEntry crop) && crop.Count >= 4)
            {
                RawRect c = new((int)mr.UInt(crop, 0), (int)mr.UInt(crop, 1), (int)mr.UInt(crop, 2), (int)mr.UInt(crop, 3));
                if (c.Left >= 0 && c.Top >= 0 && c.Width >= 2 && c.Height >= 2 && c.Left + c.Width <= img.Width && c.Top + c.Height <= img.Height)
                {
                    active = c;
                }
            }
        }

        CameraProfile? profile = CameraProfiles.Find(make, model);
        return new RawSource
        {
            Codec = RawCodec.Nikon14Bit,
            Make = make,
            Model = model,
            Width = img.Width,
            Height = img.Height,
            DataOffset = img.Offset,
            Develop = new DevelopMetadata
            {
                Cfa = img.Cfa,
                Black = black,
                White = profile?.White ?? (1 << 14) - 1,
                ActiveArea = active,
                AsShotWb = wb,
                CamXyz = profile?.CamXyz,
            },
        };
    }

    /// <summary>Nikon type-3 maker note: <c>"Nikon\0"</c>, version, then an embedded TIFF header at +10.</summary>
    private static (TiffReader, TiffIfd)? NikonMakerNote(TiffReader r, List<TiffIfd> all)
    {
        foreach (TiffIfd ifd in all)
        {
            if (!ifd.TryGet(TagMakerNote, out TiffEntry e) || e.Count < 18) continue;
            if (!r.Bytes(e.DataOffset, 6).SequenceEqual("Nikon\0"u8)) continue;
            try
            {
                TiffReader mr = r.Embedded(e.DataOffset + 10);
                return (mr, mr.ParseIfd(mr.FirstIfdOffset, out _));
            }
            catch (TiffFormatException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Maps levels given in R, G1, G2, B order (G1 shares a row with R) onto 2×2 CFA positions.</summary>
    private static int[] CfaLevels(RawCfaPattern cfa, ReadOnlySpan<int> rggb)
    {
        int[] result = new int[4];
        for (int pos = 0; pos < 4; pos++)
        {
            int y = pos >> 1, x = pos & 1;
            result[pos] = cfa.At(x, y) switch
            {
                RawColor.Red => rggb[0],
                RawColor.Blue => rggb[3],
                _ => cfa.At(x ^ 1, y) == RawColor.Red ? rggb[1] : rggb[2],
            };
        }
        return result;
    }

    private readonly record struct ImageIfd(int Width, int Height, int Bps, uint Compression, uint Photometric, long Offset, long Bytes, RawCfaPattern Cfa);

    private static bool TryImage(TiffReader r, TiffIfd ifd, out ImageIfd img)
    {
        img = default;
        if (!ifd.TryGet(TagWidth, out TiffEntry w) || !ifd.TryGet(TagHeight, out TiffEntry h) ||
            !ifd.TryGet(TagCompression, out TiffEntry comp) || !ifd.TryGet(TagStripOffsets, out TiffEntry so) ||
            !ifd.TryGet(TagStripByteCounts, out TiffEntry sc) || so.Count != 1 || sc.Count != 1)
        {
            return false;
        }
        int width = (int)Math.Min(r.UInt(w, 0), 1 << 16), height = (int)Math.Min(r.UInt(h, 0), 1 << 16);
        if (width < 32 || height < 2) return false;
        int bps = ifd.TryGet(TagBps, out TiffEntry b) ? (int)r.UInt(b, 0) : 0;
        uint photometric = ifd.TryGet(TagPhotometric, out TiffEntry ph) ? r.UInt(ph, 0) : 0;
        img = new ImageIfd(width, height, bps, r.UInt(comp, 0), photometric, r.UInt(so, 0), r.UInt(sc, 0), Cfa(r, ifd));
        return true;
    }

    private static RawCfaPattern Cfa(TiffReader r, TiffIfd ifd)
    {
        if (ifd.TryGet(TagCfaRepeatDim, out TiffEntry dim) && dim.Count >= 2 && r.UInt(dim, 0) == 2 && r.UInt(dim, 1) == 2 &&
            ifd.TryGet(TagCfaPattern, out TiffEntry pat) && pat.Count >= 4)
        {
            Span<RawColor> c = stackalloc RawColor[4];
            for (int i = 0; i < 4; i++)
            {
                uint v = r.UInt(pat, i);
                if (v > 2) return RawCfaPattern.Rggb;
                c[i] = (RawColor)v;
            }
            RawCfaPattern p = new(c[0], c[1], c[2], c[3]);
            if (p.IsBayer) return p;
        }
        return RawCfaPattern.Rggb;
    }

    private static string FirstString(TiffReader r, List<TiffIfd> all, ushort tag)
    {
        foreach (TiffIfd ifd in all)
        {
            if (ifd.TryGet(tag, out TiffEntry e) && e.Type == TiffType.Ascii) return r.String(e);
        }
        return "";
    }

    private static RawException Unsupported(string message) => new(RawErrorCode.UnsupportedFormat, message);
}
