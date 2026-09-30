using System.Buffers.Binary;
using System.Text;

namespace Sdcb.SimdRaw.Containers;

internal enum TiffType : ushort
{
    Byte = 1, Ascii = 2, Short = 3, Long = 4, Rational = 5, SByte = 6, Undefined = 7,
    SShort = 8, SLong = 9, SRational = 10, Float = 11, Double = 12, Ifd = 13,
}

/// <summary>One IFD entry. <see cref="DataOffset"/> is an absolute offset in the reader's address space.</summary>
internal readonly record struct TiffEntry(ushort Tag, TiffType Type, uint Count, long DataOffset)
{
    public int ElementSize => Type switch
    {
        TiffType.Short or TiffType.SShort => 2,
        TiffType.Long or TiffType.SLong or TiffType.Float or TiffType.Ifd => 4,
        TiffType.Rational or TiffType.SRational or TiffType.Double => 8,
        _ => 1,
    };
}

internal sealed class TiffIfd(long offset, Dictionary<ushort, TiffEntry> entries)
{
    public long Offset { get; } = offset;
    public IReadOnlyDictionary<ushort, TiffEntry> Entries { get; } = entries;
    public List<TiffIfd> Children { get; } = [];

    public bool TryGet(ushort tag, out TiffEntry entry) => Entries.TryGetValue(tag, out entry);
}

/// <summary>Thrown for malformed or unrecognized containers; identification maps it to "unsupported".</summary>
internal sealed class TiffFormatException(string message) : Exception(message);

/// <summary>Minimal, bounds-checked TIFF/EP reader. Offsets read from IFDs are relative to <c>origin</c>
/// (TIFF header position); <c>bias</c> maps absolute offsets onto <c>data</c> (used for decrypted Sony SR2 blocks,
/// whose IFD offsets are file offsets).</summary>
internal sealed class TiffReader
{
    public const ushort TagSubIfds = 0x014A;
    public const ushort TagExifIfd = 0x8769;
    private const int MaxIfds = 64;
    private const int MaxEntries = 4096;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly long _bias;
    private readonly long _origin;
    private readonly HashSet<long> _visited = [];

    public TiffReader(ReadOnlyMemory<byte> data, long origin = 0, long bias = 0, bool? bigEndian = null)
    {
        _data = data;
        _origin = origin;
        _bias = bias;
        if (bigEndian is { } be)
        {
            BigEndian = be;
            return;
        }
        ReadOnlySpan<byte> h = Bytes(origin, 8);
        BigEndian = (h[0], h[1]) switch
        {
            ((byte)'I', (byte)'I') => false,
            ((byte)'M', (byte)'M') => true,
            _ => throw new TiffFormatException("not a TIFF container"),
        };
        if (U16(origin + 2) != 42) throw new TiffFormatException("bad TIFF magic");
    }

    public bool BigEndian { get; }

    /// <summary>A reader over the same bytes for a TIFF header embedded at <paramref name="origin"/> (maker notes).</summary>
    public TiffReader Embedded(long origin) => new(_data, origin, _bias);

    public long FirstIfdOffset => _origin + U32(_origin + 4);

    /// <summary>Parses the IFD chain starting at <paramref name="offset"/> (absolute), following SubIFDs and the EXIF IFD.</summary>
    public List<TiffIfd> ParseChain(long offset, int depth = 0)
    {
        List<TiffIfd> chain = [];
        while (offset != _origin && offset > 0 && chain.Count < MaxIfds)
        {
            if (!_visited.Add(offset)) break;
            TiffIfd ifd = ParseIfd(offset, out long next);
            chain.Add(ifd);
            if (depth < 4)
            {
                foreach (ushort tag in (ReadOnlySpan<ushort>)[TagSubIfds, TagExifIfd])
                {
                    if (!ifd.TryGet(tag, out TiffEntry e)) continue;
                    for (int i = 0; i < Math.Min(e.Count, 16u); i++)
                    {
                        long child = _origin + UInt(e, i);
                        if (!InRange(child, 2)) continue;
                        ifd.Children.AddRange(ParseChain(child, depth + 1));
                    }
                }
            }
            offset = next == 0 ? 0 : _origin + next;
        }
        return chain;
    }

    /// <summary>Parses a single IFD (no chain, no children).</summary>
    public TiffIfd ParseIfd(long offset, out long next)
    {
        int count = U16(offset);
        if (count == 0 || count > MaxEntries) throw new TiffFormatException($"implausible IFD entry count {count}");
        Dictionary<ushort, TiffEntry> entries = new(count);
        for (int i = 0; i < count; i++)
        {
            long p = offset + 2 + i * 12L;
            ushort tag = U16(p);
            ushort type = U16(p + 2);
            uint n = U32(p + 4);
            if (type is 0 or > 13) continue;
            TiffEntry probe = new(tag, (TiffType)type, n, 0);
            long size = (long)probe.ElementSize * n;
            long dataOffset = size <= 4 ? p + 8 : _origin + U32(p + 8);
            if (!InRange(dataOffset, size)) continue;
            entries.TryAdd(tag, probe with { DataOffset = dataOffset });
        }
        next = U32(offset + 2 + count * 12L);
        return new TiffIfd(offset, entries);
    }

    public static IEnumerable<TiffIfd> Flatten(IEnumerable<TiffIfd> roots)
    {
        foreach (TiffIfd ifd in roots)
        {
            yield return ifd;
            foreach (TiffIfd child in Flatten(ifd.Children)) yield return child;
        }
    }

    public uint UInt(TiffEntry e, int index)
    {
        if (index >= e.Count) throw new TiffFormatException($"tag 0x{e.Tag:x4} has {e.Count} values, wanted #{index}");
        long p = e.DataOffset + (long)index * e.ElementSize;
        return e.Type switch
        {
            TiffType.Byte or TiffType.Undefined or TiffType.Ascii => Bytes(p, 1)[0],
            TiffType.SByte => (uint)(sbyte)Bytes(p, 1)[0],
            TiffType.Short => U16(p),
            TiffType.SShort => (uint)(short)U16(p),
            TiffType.Long or TiffType.SLong or TiffType.Ifd => U32(p),
            _ => throw new TiffFormatException($"tag 0x{e.Tag:x4} is not an integer"),
        };
    }

    public double Real(TiffEntry e, int index)
    {
        if (index >= e.Count) throw new TiffFormatException($"tag 0x{e.Tag:x4} has {e.Count} values, wanted #{index}");
        long p = e.DataOffset + (long)index * e.ElementSize;
        switch (e.Type)
        {
            case TiffType.Rational:
            {
                uint den = U32(p + 4);
                return den == 0 ? 0 : (double)U32(p) / den;
            }
            case TiffType.SRational:
            {
                int den = (int)U32(p + 4);
                return den == 0 ? 0 : (double)(int)U32(p) / den;
            }
            case TiffType.Float:
                return BitConverter.Int32BitsToSingle((int)U32(p));
            case TiffType.Double:
            {
                ReadOnlySpan<byte> b = Bytes(p, 8);
                long bits = BigEndian ? BinaryPrimitives.ReadInt64BigEndian(b) : BinaryPrimitives.ReadInt64LittleEndian(b);
                return BitConverter.Int64BitsToDouble(bits);
            }
            case TiffType.SShort or TiffType.SLong or TiffType.SByte:
                return (int)UInt(e, index);
            default:
                return UInt(e, index);
        }
    }

    public string String(TiffEntry e)
    {
        ReadOnlySpan<byte> b = Bytes(e.DataOffset, e.Count);
        int nul = b.IndexOf((byte)0);
        return Encoding.ASCII.GetString(nul >= 0 ? b[..nul] : b).Trim();
    }

    public ReadOnlySpan<byte> Bytes(long offset, long length)
    {
        if (!InRange(offset, length)) throw new TiffFormatException($"read of {length} bytes at {offset} is out of bounds");
        return _data.Span.Slice((int)(offset - _bias), (int)length);
    }

    public bool InRange(long offset, long length) =>
        length >= 0 && offset - _bias >= 0 && offset - _bias + length <= _data.Length;

    public ushort U16(long offset)
    {
        ReadOnlySpan<byte> b = Bytes(offset, 2);
        return BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);
    }

    public uint U32(long offset)
    {
        ReadOnlySpan<byte> b = Bytes(offset, 4);
        return BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);
    }
}
