using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdRaw.Decoders;

/// <summary>LibRaw <c>nikon_14bit_load_raw</c>: rows of <c>ceil(raw_width·7/4 / 16)·16</c> bytes; every 7 bytes hold four
/// 14-bit samples as a little-endian 56-bit word.</summary>
internal static class Nikon14BitDecoder
{
    public static int LineLength(int width) => (width * 7 / 4 + 15) / 16 * 16;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Decode(ReadOnlySpan<byte> file, long offset, int width, int height, Span<ushort> dst, KernelIsa isa)
    {
        int line = LineLength(width);
        int maxGroups = width >= 4 && line >= 7 ? Math.Min((width - 4) / 4 + 1, (line - 7) / 7 + 1) : 0;
        for (int row = 0; row < height; row++)
        {
            long start = offset + (long)row * line;
            int available = start >= file.Length ? 0 : (int)Math.Min(line, file.Length - start);
            int groups = available >= 7 ? Math.Min(maxGroups, (available - 7) / 7 + 1) : 0;
            Span<ushort> d = dst.Slice(row * width, width);
            d[(groups * 4)..].Clear();
            if (groups == 0) continue;

            int done = isa switch
            {
                KernelIsa.Avx2 => UnpackAvx2(file, (int)start, d, groups),
                KernelIsa.Vector => UnpackVector(file, (int)start, d, groups),
                _ => 0,
            };
            UnpackScalar(file.Slice((int)start, available), done, groups, d);
        }
    }

    /// <summary>Reference: LibRaw <c>unpack7bytesto4x16_nikon</c> for groups [<paramref name="from"/>, <paramref name="to"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void UnpackScalar(ReadOnlySpan<byte> row, int from, int to, Span<ushort> dst)
    {
        for (int g = from; g < to; g++)
        {
            ReadOnlySpan<byte> s = row.Slice(7 * g, 7);
            dst[4 * g + 3] = (ushort)(s[6] << 6 | s[5] >> 2);
            dst[4 * g + 2] = (ushort)((s[5] & 0x3) << 12 | s[4] << 4 | s[3] >> 4);
            dst[4 * g + 1] = (ushort)((s[3] & 0xf) << 10 | s[2] << 2 | s[1] >> 6);
            dst[4 * g + 0] = (ushort)((s[1] & 0x3f) << 8 | s[0]);
        }
    }

    /// <summary>Four groups (28 bytes → 16 samples) per iteration from one 32-byte load: vpermd puts one group at the
    /// start of each 128-bit lane, pshufb spreads its samples into 32-bit lanes, vpsrlvd aligns them.
    /// Returns groups decoded.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe int UnpackAvx2(ReadOnlySpan<byte> file, int start, Span<ushort> dst, int groups)
    {
        if (!Avx2.IsSupported) return 0;
        const sbyte Z = -1;
        Vector256<uint> permA = Vector256.Create(0u, 1, 2, 3, 1, 2, 3, 4);
        Vector256<uint> permB = Vector256.Create(3u, 4, 5, 6, 5, 6, 7, 7);
        Vector256<sbyte> shufA = Vector256.Create(
            0, 1, Z, Z, 1, 2, 3, Z, 3, 4, 5, Z, 5, 6, Z, Z,
            3, 4, Z, Z, 4, 5, 6, Z, 6, 7, 8, Z, 8, 9, Z, Z);
        Vector256<sbyte> shufB = Vector256.Create(
            2, 3, Z, Z, 3, 4, 5, Z, 5, 6, 7, Z, 7, 8, Z, Z,
            1, 2, Z, Z, 2, 3, 4, Z, 4, 5, 6, Z, 6, 7, Z, Z);
        Vector256<uint> shifts = Vector256.Create(0u, 6, 4, 2, 0, 6, 4, 2);
        Vector256<uint> mask = Vector256.Create(0x3fffu);

        int n = 0;
        long last = file.Length - 32L;
        fixed (byte* f = file)
        fixed (ushort* d = dst)
        {
            for (; n + 4 <= groups && start + 7L * n <= last; n += 4)
            {
                Vector256<uint> v = Avx.LoadVector256((uint*)(f + start + 7 * n));
                Vector256<uint> a = Avx2.Shuffle(Avx2.PermuteVar8x32(v, permA).AsSByte(), shufA).AsUInt32();
                Vector256<uint> b = Avx2.Shuffle(Avx2.PermuteVar8x32(v, permB).AsSByte(), shufB).AsUInt32();
                a = Avx2.And(Avx2.ShiftRightLogicalVariable(a, shifts), mask);
                b = Avx2.And(Avx2.ShiftRightLogicalVariable(b, shifts), mask);
                Vector256<ushort> packed = Avx2.PackUnsignedSaturate(a.AsInt32(), b.AsInt32());
                Avx.Store(d + 4 * n, Avx2.Permute4x64(packed.AsUInt64(), 0b11_01_10_00).AsUInt16());
            }
        }
        return n;
    }

    /// <summary>Portable kernel: lanes are groups. Lane j of <c>load(p - j)</c> starts at <c>p + 7j</c>, so N loads merged
    /// with constant lane masks form the stride-7 gather; the four samples of a group then fit in the lane's own
    /// 64 bits. Returns groups decoded.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe int UnpackVector(ReadOnlySpan<byte> file, int start, Span<ushort> dst, int groups)
    {
        if (!Vector.IsHardwareAccelerated) return 0;
        int lanes = Vector<ulong>.Count;
        if (start < lanes - 1) return 0;
        Vector<ulong>* laneMask = stackalloc Vector<ulong>[lanes];
        ulong* tmp = stackalloc ulong[lanes];
        for (int j = 0; j < lanes; j++)
        {
            for (int i = 0; i < lanes; i++) tmp[i] = i == j ? ulong.MaxValue : 0;
            laneMask[j] = Vector.Load(tmp);
        }
        Vector<ulong> m0 = new(0x3fffUL), m1 = new(0x3fffUL << 16), m2 = new(0x3fffUL << 32), m3 = new(0x3fffUL << 48);

        int n = 0;
        long last = file.Length - 8L * lanes;
        fixed (byte* f = file)
        fixed (ushort* d = dst)
        {
            for (; n + lanes <= groups && start + 7L * n <= last; n += lanes)
            {
                byte* p = f + start + 7 * n;
                Vector<ulong> x = Vector.Load((ulong*)p);
                for (int j = 1; j < lanes; j++) x = Vector.ConditionalSelect(laneMask[j], Vector.Load((ulong*)(p - j)), x);
                Vector<ulong> o = (x & m0) | ((x << 2) & m1) | ((x << 4) & m2) | ((x << 6) & m3);
                o.Store((ulong*)(d + 4 * n));
            }
        }
        return n;
    }
}
