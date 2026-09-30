using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdRaw.Decoders;

/// <summary>Sony tone curve from tag 0x7010: four knees (already <c>&gt;&gt; 2 &amp; 0xfff</c>) splitting 0..4095 into
/// segments of slope 1, 2, 4, 8, 16. Missing tag = identity (all knees at 4095).</summary>
internal readonly struct SonyCurve
{
    public SonyCurve(ushort c1, ushort c2, ushort c3, ushort c4)
    {
        C1 = c1; C2 = c2; C3 = c3; C4 = c4;
        Monotonic = c1 <= c2 && c2 <= c3 && c3 <= c4;
        ushort[] curve = new ushort[4096];
        for (int i = 0; i < curve.Length; i++) curve[i] = (ushort)i;
        ReadOnlySpan<int> knees = [0, c1, c2, c3, c4, 4095];
        for (int i = 0; i < 5; i++)
        {
            for (int j = knees[i] + 1; j <= knees[i + 1]; j++) curve[j] = (ushort)(curve[j - 1] + (1 << i));
        }
        Lut = new ushort[2048];
        for (int p = 0; p < Lut.Length; p++) Lut[p] = curve[p << 1];
    }

    public static SonyCurve Identity => new(4095, 4095, 4095, 4095);

    public ushort C1 { get; }
    public ushort C2 { get; }
    public ushort C3 { get; }
    public ushort C4 { get; }

    /// <summary>When the knees are ordered, <c>curve[j] = j + Σ max(0, j - c_m) · 2^(m-1)</c>, which the SIMD kernels
    /// evaluate arithmetically instead of through a table.</summary>
    public bool Monotonic { get; }

    /// <summary>LibRaw output for an 11-bit sample: <c>curve[p &lt;&lt; 1]</c> (at most 4094·16 = 65504).</summary>
    public ushort[] Lut { get; }
}

/// <summary>LibRaw <c>sony_arw2_load_raw</c>: each row is <c>raw_width</c> bytes of 16-byte blocks. A block holds
/// 11-bit max/min, their 4-bit slot indices and 14 × 7-bit deltas (shifted by 0..4) for 16 same-colour pixels; two
/// consecutive blocks cover 32 columns (even, then odd). Output is <c>curve[pix &lt;&lt; 1]</c>.</summary>
internal static class SonyArw2Decoder
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Decode(ReadOnlySpan<byte> file, long offset, int width, int height, in SonyCurve curve, Span<ushort> dst, KernelIsa isa)
    {
        long needed = (long)width * height;
        ReadOnlySpan<byte> data = offset < file.Length ? file[(int)offset..] : [];
        using NativeBuffer<byte>? padded = data.Length < needed ? new NativeBuffer<byte>(checked((int)needed), zero: true) : null;
        if (padded != null)
        {
            data.CopyTo(padded.GetSpan());
            data = padded.GetSpan();
        }

        int blocks = BlockCount(width);
        int pairs = blocks / 2;
        if (!curve.Monotonic) isa = KernelIsa.Scalar;
        for (int row = 0; row < height; row++)
        {
            ReadOnlySpan<byte> src = data.Slice(row * width, width);
            Span<ushort> d = dst.Slice(row * width, width);
            d[(pairs * 32)..].Clear();
            int done = isa switch
            {
                KernelIsa.Avx2 => DecodePairsAvx2(src, d, pairs, curve) * 2,
                KernelIsa.Vector => DecodeBlocksVector(src, d, pairs * 2, curve),
                _ => 0,
            };
            for (int b = done; b < blocks; b++) DecodeBlockScalar(src, b, d, curve.Lut);
        }
    }

    /// <summary>Blocks LibRaw decodes per row: block b starts at column 32·(b/2) + (b&amp;1) and runs while col &lt; width - 30.</summary>
    public static int BlockCount(int width)
    {
        int b = 0;
        while (32 * (b >> 1) + (b & 1) < width - 30) b++;
        return b;
    }

    /// <summary>Reference implementation (verbatim LibRaw semantics, including the 15th delta when imax == imin,
    /// which reads past the block; bytes past the row read as 0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void DecodeBlockScalar(ReadOnlySpan<byte> row, int block, Span<ushort> dst, ReadOnlySpan<ushort> lut)
    {
        int dp = block * 16;
        int col = 32 * (block >> 1) + (block & 1);
        uint val = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(dp, 4));
        int max = (int)(val & 0x7ff);
        int min = (int)(val >> 11 & 0x7ff);
        int imax = (int)(val >> 22 & 0x0f);
        int imin = (int)(val >> 26 & 0x0f);
        int sh = 0;
        while (sh < 4 && 0x80 << sh <= max - min) sh++;
        for (int bit = 30, i = 0; i < 16; i++)
        {
            int pix;
            if (i == imax) pix = max;
            else if (i == imin) pix = min;
            else
            {
                int o = dp + (bit >> 3);
                int u = At(row, o) | At(row, o + 1) << 8;
                pix = ((u >> (bit & 7) & 0x7f) << sh) + min;
                if (pix > 0x7ff) pix = 0x7ff;
                bit += 7;
            }
            dst[col + 2 * i] = lut[pix];
        }

        static int At(ReadOnlySpan<byte> s, int i) => (uint)i < (uint)s.Length ? s[i] : 0;
    }

    /// <summary>One block pair (32 input bytes → 32 output columns) per iteration. Deltas are gathered with pshufb into
    /// 16-bit lanes pre-aligned for the three possible slot offsets (0, 1 or 2 special slots before the lane), shifted
    /// into place with a multiply, then selected by comparing the lane index with imax/imin. Returns pairs decoded.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe int DecodePairsAvx2(ReadOnlySpan<byte> src, Span<ushort> dst, int pairs, in SonyCurve curve)
    {
        if (!Avx2.IsSupported) return 0;
        Arw2Avx2Tables t = new();
        Vector256<byte> sh0 = t.Shuffle(0), sh1 = t.Shuffle(1), sh2 = t.Shuffle(2);
        Vector256<ushort> mul0 = t.Multiplier(0), mul1 = t.Multiplier(1), mul2 = t.Multiplier(2);
        Vector256<short> iota = Vector256.Create((short)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
        Vector256<ushort> c1 = Vector256.Create(curve.C1), c2 = Vector256.Create(curve.C2);
        Vector256<ushort> c3 = Vector256.Create(curve.C3), c4 = Vector256.Create(curve.C4);
        Vector256<ushort> limit = Vector256.Create((ushort)0x7ff);

        fixed (byte* s = src)
        fixed (ushort* d = dst)
        {
            for (int k = 0; k < pairs; k++)
            {
                byte* blk = s + 32 * k;
                uint va = *(uint*)blk, vb = *(uint*)(blk + 16);
                if ((va >> 22 & 0xf) == (va >> 26 & 0xf) || (vb >> 22 & 0xf) == (vb >> 26 & 0xf))
                {
                    DecodeBlockScalar(src, 2 * k, dst, curve.Lut);
                    DecodeBlockScalar(src, 2 * k + 1, dst, curve.Lut);
                    continue;
                }
                Vector256<ushort> even = default, odd = default;
                for (int h = 0; h < 2; h++)
                {
                    uint val = h == 0 ? va : vb;
                    int max = (int)(val & 0x7ff), min = (int)(val >> 11 & 0x7ff);
                    int imax = (int)(val >> 22 & 0xf), imin = (int)(val >> 26 & 0xf);
                    int diff = max - min;
                    int sh = (diff >= 0x80 ? 1 : 0) + (diff >= 0x100 ? 1 : 0) + (diff >= 0x200 ? 1 : 0) + (diff >= 0x400 ? 1 : 0);

                    Vector256<byte> b = Avx2.BroadcastVector128ToVector256(blk + 16 * h);
                    Vector256<ushort> d0 = Avx2.ShiftRightLogical(Avx2.MultiplyLow(Avx2.Shuffle(b, sh0).AsUInt16(), mul0), 9);
                    Vector256<ushort> d1 = Avx2.ShiftRightLogical(Avx2.MultiplyLow(Avx2.Shuffle(b, sh1).AsUInt16(), mul1), 9);
                    Vector256<ushort> d2 = Avx2.ShiftRightLogical(Avx2.MultiplyLow(Avx2.Shuffle(b, sh2).AsUInt16(), mul2), 9);

                    Vector256<short> vimax = Vector256.Create((short)imax), vimin = Vector256.Create((short)imin);
                    Vector256<byte> gtMax = Avx2.CompareGreaterThan(iota, vimax).AsByte();
                    Vector256<byte> gtMin = Avx2.CompareGreaterThan(iota, vimin).AsByte();
                    Vector256<byte> sel = Avx2.BlendVariable(d0.AsByte(), d1.AsByte(), Avx2.Xor(gtMax, gtMin));
                    sel = Avx2.BlendVariable(sel, d2.AsByte(), Avx2.And(gtMax, gtMin));

                    Vector256<ushort> pix = Avx2.ShiftLeftLogical(sel.AsUInt16(), Vector128.CreateScalar((ulong)sh).AsUInt16());
                    pix = Avx2.Min(Avx2.Add(pix, Vector256.Create((ushort)min)), limit);
                    pix = Avx2.BlendVariable(pix.AsByte(), Vector256.Create((ushort)max).AsByte(), Avx2.CompareEqual(iota, vimax).AsByte()).AsUInt16();
                    pix = Avx2.BlendVariable(pix.AsByte(), Vector256.Create((ushort)min).AsByte(), Avx2.CompareEqual(iota, vimin).AsByte()).AsUInt16();

                    Vector256<ushort> j = Avx2.Add(pix, pix);
                    Vector256<ushort> f = Avx2.Add(j, Avx2.SubtractSaturate(j, c1));
                    f = Avx2.Add(f, Avx2.ShiftLeftLogical(Avx2.SubtractSaturate(j, c2), 1));
                    f = Avx2.Add(f, Avx2.ShiftLeftLogical(Avx2.SubtractSaturate(j, c3), 2));
                    f = Avx2.Add(f, Avx2.ShiftLeftLogical(Avx2.SubtractSaturate(j, c4), 3));
                    if (h == 0) even = f;
                    else odd = f;
                }
                Vector256<ushort> lo = Avx2.UnpackLow(even, odd);
                Vector256<ushort> hi = Avx2.UnpackHigh(even, odd);
                Avx.Store(d + 32 * k, Avx2.Permute2x128(lo, hi, 0x20));
                Avx.Store(d + 32 * k + 16, Avx2.Permute2x128(lo, hi, 0x31));
            }
        }
        return pairs;
    }

    /// <summary>Portable kernel: lanes are blocks. Four contiguous vectors are de-interleaved into the four block words
    /// with two rounds of <see cref="Vector.Narrow(Vector{ulong}, Vector{ulong})"/>; header, deltas, slot selection and
    /// curve are then uniform lane-wise ops. The final scatter into stride-2 columns is scalar (Vector&lt;T&gt; has no
    /// shuffles). Returns blocks decoded (a multiple of the lane count).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe int DecodeBlocksVector(ReadOnlySpan<byte> src, Span<ushort> dst, int blocks, in SonyCurve curve)
    {
        if (!Vector.IsHardwareAccelerated) return 0;
        int n = Vector<uint>.Count;
        int vs = Vector<byte>.Count;
        blocks -= blocks % n;
        Vector<uint> c1 = new(curve.C1), c2 = new(curve.C2), c3 = new(curve.C3), c4 = new(curve.C4);
        Vector<uint> m7f = new(0x7f), m7ff = new(0x7ff), m0f = new(0x0f);
        Vector<int> t1 = new(0x80), t2 = new(0x100), t3 = new(0x200), t4 = new(0x400);
        uint* scratch = stackalloc uint[16 * n];
        Vector<uint>* delta = stackalloc Vector<uint>[14];

        fixed (byte* s = src)
        fixed (ushort* d = dst)
        {
            for (int b0 = 0; b0 < blocks; b0 += n)
            {
                byte* p = s + 16 * b0;
                Vector<ulong> a0 = Vector.Load((ulong*)p), a1 = Vector.Load((ulong*)(p + vs));
                Vector<ulong> a2 = Vector.Load((ulong*)(p + 2 * vs)), a3 = Vector.Load((ulong*)(p + 3 * vs));
                Vector<uint> eLo = Vector.Narrow(a0, a1), eHi = Vector.Narrow(a2, a3);
                Vector<uint> oLo = Vector.Narrow(a0 >> 32, a1 >> 32), oHi = Vector.Narrow(a2 >> 32, a3 >> 32);
                Vector<uint> w0 = Vector.Narrow(Vector.AsVectorUInt64(eLo), Vector.AsVectorUInt64(eHi));
                Vector<uint> w2 = Vector.Narrow(Vector.AsVectorUInt64(eLo) >> 32, Vector.AsVectorUInt64(eHi) >> 32);
                Vector<uint> w1 = Vector.Narrow(Vector.AsVectorUInt64(oLo), Vector.AsVectorUInt64(oHi));
                Vector<uint> w3 = Vector.Narrow(Vector.AsVectorUInt64(oLo) >> 32, Vector.AsVectorUInt64(oHi) >> 32);

                Vector<uint> max = w0 & m7ff, min = (w0 >> 11) & m7ff;
                Vector<uint> imax = (w0 >> 22) & m0f, imin = (w0 >> 26) & m0f;
                if (Vector.EqualsAny(imax, imin))
                {
                    for (int b = b0; b < b0 + n; b++) DecodeBlockScalar(src, b, dst, curve.Lut);
                    continue;
                }
                Vector<int> diff = Vector.AsVectorInt32(max) - Vector.AsVectorInt32(min);
                Vector<uint> pow = Vector.AsVectorUInt32(Vector<int>.One
                    - Vector.GreaterThanOrEqual(diff, t1)
                    - (Vector.GreaterThanOrEqual(diff, t2) << 1)
                    - (Vector.GreaterThanOrEqual(diff, t3) << 2)
                    - (Vector.GreaterThanOrEqual(diff, t4) << 3));

                delta[0] = ((w0 >> 30) | (w1 << 2)) & m7f;
                delta[1] = (w1 >> 5) & m7f;
                delta[2] = (w1 >> 12) & m7f;
                delta[3] = (w1 >> 19) & m7f;
                delta[4] = ((w1 >> 26) | (w2 << 6)) & m7f;
                delta[5] = (w2 >> 1) & m7f;
                delta[6] = (w2 >> 8) & m7f;
                delta[7] = (w2 >> 15) & m7f;
                delta[8] = (w2 >> 22) & m7f;
                delta[9] = ((w2 >> 29) | (w3 << 3)) & m7f;
                delta[10] = (w3 >> 4) & m7f;
                delta[11] = (w3 >> 11) & m7f;
                delta[12] = (w3 >> 18) & m7f;
                delta[13] = (w3 >> 25) & m7f;

                for (int i = 0; i < 16; i++)
                {
                    Vector<uint> vi = new((uint)i);
                    Vector<uint> g1 = Vector.LessThan(imax, vi), g2 = Vector.LessThan(imin, vi);
                    Vector<uint> cand0 = i <= 13 ? delta[i] : Vector<uint>.Zero;
                    Vector<uint> cand1 = i is >= 1 and <= 14 ? delta[i - 1] : Vector<uint>.Zero;
                    Vector<uint> cand2 = i >= 2 ? delta[i - 2] : Vector<uint>.Zero;
                    Vector<uint> sel = Vector.ConditionalSelect(g1 ^ g2, cand1, cand0);
                    sel = Vector.ConditionalSelect(g1 & g2, cand2, sel);
                    Vector<uint> pix = Vector.Min(sel * pow + min, m7ff);
                    pix = Vector.ConditionalSelect(Vector.Equals(imax, vi), max, pix);
                    pix = Vector.ConditionalSelect(Vector.Equals(imin, vi), min, pix);

                    Vector<uint> j = pix << 1;
                    Vector<uint> f = j + (Vector.Max(j, c1) - c1)
                        + ((Vector.Max(j, c2) - c2) << 1)
                        + ((Vector.Max(j, c3) - c3) << 2)
                        + ((Vector.Max(j, c4) - c4) << 3);
                    f.Store(scratch + i * n);
                }

                for (int lane = 0; lane < n; lane++)
                {
                    int b = b0 + lane;
                    ushort* o = d + 32 * (b >> 1) + (b & 1);
                    for (int i = 0; i < 16; i++) o[2 * i] = (ushort)scratch[i * n + lane];
                }
            }
        }
        return blocks;
    }

    /// <summary>pshufb masks / multipliers producing, in 16-bit lane i, delta (i - offset) of the block shifted to bits 9..15.</summary>
    private unsafe struct Arw2Avx2Tables
    {
        private fixed byte _shuffle[3 * 32];
        private fixed ushort _mul[3 * 16];

        public Arw2Avx2Tables()
        {
            for (int s = 0; s < 3; s++)
            {
                for (int i = 0; i < 16; i++)
                {
                    int k = i - s;
                    byte lo = 0x80, hi = 0x80;
                    ushort mul = 0;
                    if (k is >= 0 and <= 13)
                    {
                        int bit = 30 + 7 * k, o = bit >> 3, q = bit & 7;
                        lo = (byte)o;
                        hi = q > 1 ? (byte)(o + 1) : (byte)0x80;
                        mul = (ushort)(1 << (9 - q));
                    }
                    _shuffle[s * 32 + 2 * i] = lo;
                    _shuffle[s * 32 + 2 * i + 1] = hi;
                    _mul[s * 16 + i] = mul;
                }
            }
        }

        public Vector256<byte> Shuffle(int s)
        {
            fixed (byte* p = _shuffle) return Avx.LoadVector256(p + s * 32);
        }

        public Vector256<ushort> Multiplier(int s)
        {
            fixed (ushort* p = _mul) return Avx.LoadVector256(p + s * 16);
        }
    }
}
