using System.Buffers.Binary;

namespace Sdcb.SimdRaw.Containers;

/// <summary>Sony SR2 private data: IFD0 tag 0xC634 points at the SR2Private IFD, whose 0x7200/0x7201/0x7221
/// describe an encrypted SR2SubIFD (white balance, black and white level).</summary>
internal static class SonySr2
{
    private const ushort TagDngPrivateData = 0xC634;
    private const ushort TagSubIfdOffset = 0x7200;
    private const ushort TagSubIfdLength = 0x7201;
    private const ushort TagSubIfdKey = 0x7221;

    /// <summary>Returns a reader over the decrypted SR2SubIFD plus the IFD itself, or null when absent.</summary>
    public static (TiffReader Reader, TiffIfd Ifd)? Open(TiffReader file, ReadOnlyMemory<byte> data, TiffIfd ifd0)
    {
        if (!ifd0.TryGet(TagDngPrivateData, out TiffEntry priv)) return null;
        long privOffset = priv.Type is TiffType.Byte or TiffType.Undefined && priv.Count >= 4
            ? file.U32(priv.DataOffset)
            : file.UInt(priv, 0);
        if (!file.InRange(privOffset, 2)) return null;
        TiffIfd sr2 = file.ParseIfd(privOffset, out _);
        if (!sr2.TryGet(TagSubIfdOffset, out TiffEntry offE) || !sr2.TryGet(TagSubIfdLength, out TiffEntry lenE) ||
            !sr2.TryGet(TagSubIfdKey, out TiffEntry keyE))
        {
            return null;
        }
        long offset = file.UInt(offE, 0);
        long length = file.UInt(lenE, 0) & ~3u;
        uint key = keyE.Type is TiffType.Byte or TiffType.Undefined ? file.U32(keyE.DataOffset) : file.UInt(keyE, 0);
        if (length < 2 || !file.InRange(offset, length)) return null;

        byte[] plain = file.Bytes(offset, length).ToArray();
        Decrypt(plain, key);
        TiffReader reader = new(plain, origin: 0, bias: offset, bigEndian: file.BigEndian);
        return (reader, reader.ParseIfd(offset, out _));
    }

    /// <summary>dcraw <c>sony_decrypt</c>: a 127-word XOR pad seeded from <paramref name="key"/>, applied to big-endian words.</summary>
    internal static void Decrypt(Span<byte> data, uint key)
    {
        Span<uint> pad = stackalloc uint[128];
        for (int i = 0; i < 4; i++) pad[i] = key = key * 48828125 + 1;
        pad[3] = pad[3] << 1 | (pad[0] ^ pad[2]) >> 31;
        for (int i = 4; i < 127; i++) pad[i] = (pad[i - 4] ^ pad[i - 2]) << 1 | (pad[i - 3] ^ pad[i - 1]) >> 31;

        uint p = 127;
        for (int i = 0; i + 4 <= data.Length; i += 4)
        {
            p++;
            uint v = pad[(int)((p - 1) & 127)] = pad[(int)(p & 127)] ^ pad[(int)((p + 64) & 127)];
            Span<byte> word = data.Slice(i, 4);
            BinaryPrimitives.WriteUInt32BigEndian(word, BinaryPrimitives.ReadUInt32BigEndian(word) ^ v);
        }
    }
}
