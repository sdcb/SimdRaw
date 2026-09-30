using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Sdcb.SimdRaw.Decoders;

namespace Sdcb.SimdRaw.Develop;

/// <summary>Per-row develop kernels. All arithmetic is integer and every SIMD path computes exactly the scalar
/// formula, so output is bit-identical across ISAs:
/// <list type="bullet">
/// <item>scale: <c>min(((clamp(v - black, 0, limit) · mul) + 2048) &gt;&gt; 12, 65535)</c> in u32 (Q12 multiplier);</item>
/// <item>demosaic: <c>avg(a, b) = (a + b + 1) &gt;&gt; 1</c> (pavgw), four-neighbour means are averages of averages;</item>
/// <item>colour: <c>clamp((m0·r + m1·g + m2·b + 2048) &gt;&gt; 12, 0, 65535)</c> in i32 (Q12 matrix).</item>
/// </list></summary>
internal static class DevelopKernels
{
    /// <summary>Row-constant scale parameters for even (0) and odd (1) columns.</summary>
    internal readonly record struct ScaleParams(uint Black0, uint Limit0, uint Mul0, uint Black1, uint Limit1, uint Mul1);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ScaleRow(ReadOnlySpan<ushort> src, Span<ushort> dst, in ScaleParams p, KernelIsa isa)
    {
        int done = isa switch
        {
            KernelIsa.Avx2 => ScaleAvx2(src, dst, p),
            KernelIsa.Vector => ScaleVector(src, dst, p),
            _ => 0,
        };
        for (int x = done; x < src.Length; x++)
        {
            bool odd = (x & 1) != 0;
            uint black = odd ? p.Black1 : p.Black0, limit = odd ? p.Limit1 : p.Limit0, mul = odd ? p.Mul1 : p.Mul0;
            int t = src[x] - (int)black;
            uint c = t < 0 ? 0 : Math.Min((uint)t, limit);
            dst[x] = (ushort)Math.Min((c * mul + 2048) >> 12, 65535u);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe int ScaleAvx2(ReadOnlySpan<ushort> src, Span<ushort> dst, in ScaleParams p)
    {
        if (!Avx2.IsSupported) return 0;
        Vector256<int> black = Vector256.Create((int)p.Black0, (int)p.Black1, (int)p.Black0, (int)p.Black1, (int)p.Black0, (int)p.Black1, (int)p.Black0, (int)p.Black1);
        Vector256<int> limit = Vector256.Create((int)p.Limit0, (int)p.Limit1, (int)p.Limit0, (int)p.Limit1, (int)p.Limit0, (int)p.Limit1, (int)p.Limit0, (int)p.Limit1);
        Vector256<int> mul = Vector256.Create((int)p.Mul0, (int)p.Mul1, (int)p.Mul0, (int)p.Mul1, (int)p.Mul0, (int)p.Mul1, (int)p.Mul0, (int)p.Mul1);
        Vector256<int> round = Vector256.Create(2048);
        int x = 0, n = src.Length;
        fixed (ushort* s = src)
        fixed (ushort* d = dst)
        {
            for (; x + 16 <= n; x += 16)
            {
                Vector256<int> lo = Avx2.ConvertToVector256Int32(s + x);
                Vector256<int> hi = Avx2.ConvertToVector256Int32(s + x + 8);
                lo = Avx2.Min(Avx2.Max(Avx2.Subtract(lo, black), Vector256<int>.Zero), limit);
                hi = Avx2.Min(Avx2.Max(Avx2.Subtract(hi, black), Vector256<int>.Zero), limit);
                lo = Avx2.ShiftRightLogical(Avx2.Add(Avx2.MultiplyLow(lo, mul), round), 12);
                hi = Avx2.ShiftRightLogical(Avx2.Add(Avx2.MultiplyLow(hi, mul), round), 12);
                Vector256<ushort> packed = Avx2.PackUnsignedSaturate(lo, hi);
                Avx.Store(d + x, Avx2.Permute4x64(packed.AsUInt64(), 0b11_01_10_00).AsUInt16());
            }
        }
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int ScaleVector(ReadOnlySpan<ushort> src, Span<ushort> dst, in ScaleParams p)
    {
        if (!Vector.IsHardwareAccelerated) return 0;
        Vector<uint> black = Alternating(p.Black0, p.Black1), limit = Alternating(p.Limit0, p.Limit1), mul = Alternating(p.Mul0, p.Mul1);
        Vector<uint> round = new(2048), max = new(65535);
        int x = 0, n = src.Length, step = Vector<ushort>.Count;
        for (; x + step <= n; x += step)
        {
            Vector.Widen(new Vector<ushort>(src[x..]), out Vector<uint> lo, out Vector<uint> hi);
            lo = Vector.Min(Vector.Max(lo, black) - black, limit);
            hi = Vector.Min(Vector.Max(hi, black) - black, limit);
            lo = Vector.Min((lo * mul + round) >> 12, max);
            hi = Vector.Min((hi * mul + round) >> 12, max);
            Vector.Narrow(lo, hi).CopyTo(dst[x..]);
        }
        return x;
    }

    /// <summary>Bilinear demosaic of one output row. <paramref name="prev"/>/<paramref name="cur"/>/<paramref name="next"/>
    /// are scaled rows padded by one mirrored sample on each side (index x + 1 is column x). The non-green site of this
    /// row is at odd columns when <paramref name="chromaOdd"/>; its colour goes to <paramref name="site"/>, the other
    /// chroma to <paramref name="other"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void DemosaicRow(ReadOnlySpan<ushort> prev, ReadOnlySpan<ushort> cur, ReadOnlySpan<ushort> next, bool chromaOdd,
        Span<ushort> site, Span<ushort> green, Span<ushort> other, KernelIsa isa)
    {
        int width = site.Length;
        int done = isa switch
        {
            KernelIsa.Avx2 => DemosaicAvx2(prev, cur, next, chromaOdd, site, green, other),
            KernelIsa.Vector => DemosaicVector(prev, cur, next, chromaOdd, site, green, other),
            _ => 0,
        };
        for (int x = done; x < width; x++)
        {
            int c = cur[x + 1];
            int h = Avg(cur[x], cur[x + 2]);
            int v = Avg(prev[x + 1], next[x + 1]);
            if (((x & 1) != 0) == chromaOdd)
            {
                site[x] = (ushort)c;
                green[x] = (ushort)Avg(v, h);
                other[x] = (ushort)Avg(Avg(prev[x], prev[x + 2]), Avg(next[x], next[x + 2]));
            }
            else
            {
                site[x] = (ushort)h;
                green[x] = (ushort)c;
                other[x] = (ushort)v;
            }
        }

        static int Avg(int a, int b) => (a + b + 1) >> 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe int DemosaicAvx2(ReadOnlySpan<ushort> prev, ReadOnlySpan<ushort> cur, ReadOnlySpan<ushort> next, bool chromaOdd,
        Span<ushort> site, Span<ushort> green, Span<ushort> other)
    {
        if (!Avx2.IsSupported) return 0;
        Vector256<byte> chroma = Vector256.Create(chromaOdd ? 0xFFFF0000u : 0x0000FFFFu).AsByte();
        int x = 0, width = site.Length;
        fixed (ushort* p = prev)
        fixed (ushort* c = cur)
        fixed (ushort* n = next)
        fixed (ushort* s = site)
        fixed (ushort* g = green)
        fixed (ushort* o = other)
        {
            for (; x + 16 <= width; x += 16)
            {
                Vector256<ushort> pw = Avx.LoadVector256(p + x), pc = Avx.LoadVector256(p + x + 1), pe = Avx.LoadVector256(p + x + 2);
                Vector256<ushort> cw = Avx.LoadVector256(c + x), cc = Avx.LoadVector256(c + x + 1), ce = Avx.LoadVector256(c + x + 2);
                Vector256<ushort> nw = Avx.LoadVector256(n + x), nc = Avx.LoadVector256(n + x + 1), ne = Avx.LoadVector256(n + x + 2);
                Vector256<ushort> h = Avx2.Average(cw, ce);
                Vector256<ushort> v = Avx2.Average(pc, nc);
                Vector256<ushort> cross = Avx2.Average(v, h);
                Vector256<ushort> diag = Avx2.Average(Avx2.Average(pw, pe), Avx2.Average(nw, ne));
                Avx.Store(s + x, Avx2.BlendVariable(h.AsByte(), cc.AsByte(), chroma).AsUInt16());
                Avx.Store(g + x, Avx2.BlendVariable(cc.AsByte(), cross.AsByte(), chroma).AsUInt16());
                Avx.Store(o + x, Avx2.BlendVariable(v.AsByte(), diag.AsByte(), chroma).AsUInt16());
            }
        }
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int DemosaicVector(ReadOnlySpan<ushort> prev, ReadOnlySpan<ushort> cur, ReadOnlySpan<ushort> next, bool chromaOdd,
        Span<ushort> site, Span<ushort> green, Span<ushort> other)
    {
        if (!Vector.IsHardwareAccelerated) return 0;
        Vector<ushort> chroma = Vector.AsVectorUInt16(new Vector<uint>(chromaOdd ? 0xFFFF0000u : 0x0000FFFFu));
        int x = 0, width = site.Length, step = Vector<ushort>.Count;
        for (; x + step <= width; x += step)
        {
            Vector<ushort> pw = new(prev[x..]), pc = new(prev[(x + 1)..]), pe = new(prev[(x + 2)..]);
            Vector<ushort> cw = new(cur[x..]), cc = new(cur[(x + 1)..]), ce = new(cur[(x + 2)..]);
            Vector<ushort> nw = new(next[x..]), nc = new(next[(x + 1)..]), ne = new(next[(x + 2)..]);
            Vector<ushort> h = Avg(cw, ce);
            Vector<ushort> v = Avg(pc, nc);
            Vector<ushort> cross = Avg(v, h);
            Vector<ushort> diag = Avg(Avg(pw, pe), Avg(nw, ne));
            Vector.ConditionalSelect(chroma, cc, h).CopyTo(site[x..]);
            Vector.ConditionalSelect(chroma, cross, cc).CopyTo(green[x..]);
            Vector.ConditionalSelect(chroma, diag, v).CopyTo(other[x..]);
        }
        return x;
    }

    /// <summary>Camera RGB → linear sRGB, in place. <paramref name="m"/> is a row-major 3×3 Q12 matrix.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ColorRow(Span<ushort> r, Span<ushort> g, Span<ushort> b, ReadOnlySpan<int> m, KernelIsa isa)
    {
        int done = isa switch
        {
            KernelIsa.Avx2 => ColorAvx2(r, g, b, m),
            KernelIsa.Vector => ColorVector(r, g, b, m),
            _ => 0,
        };
        for (int x = done; x < r.Length; x++)
        {
            int rr = r[x], gg = g[x], bb = b[x];
            r[x] = Clamp((m[0] * rr + m[1] * gg + m[2] * bb + 2048) >> 12);
            g[x] = Clamp((m[3] * rr + m[4] * gg + m[5] * bb + 2048) >> 12);
            b[x] = Clamp((m[6] * rr + m[7] * gg + m[8] * bb + 2048) >> 12);
        }

        static ushort Clamp(int v) => (ushort)Math.Clamp(v, 0, 65535);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe int ColorAvx2(Span<ushort> r, Span<ushort> g, Span<ushort> b, ReadOnlySpan<int> m)
    {
        if (!Avx2.IsSupported) return 0;
        Vector256<int> m0 = Vector256.Create(m[0]), m1 = Vector256.Create(m[1]), m2 = Vector256.Create(m[2]);
        Vector256<int> m3 = Vector256.Create(m[3]), m4 = Vector256.Create(m[4]), m5 = Vector256.Create(m[5]);
        Vector256<int> m6 = Vector256.Create(m[6]), m7 = Vector256.Create(m[7]), m8 = Vector256.Create(m[8]);
        Vector256<int> round = Vector256.Create(2048);
        int x = 0, n = r.Length;
        fixed (ushort* pr = r)
        fixed (ushort* pg = g)
        fixed (ushort* pb = b)
        {
            for (; x + 16 <= n; x += 16)
            {
                Vector256<int> r0 = Avx2.ConvertToVector256Int32(pr + x), r1 = Avx2.ConvertToVector256Int32(pr + x + 8);
                Vector256<int> g0 = Avx2.ConvertToVector256Int32(pg + x), g1 = Avx2.ConvertToVector256Int32(pg + x + 8);
                Vector256<int> b0 = Avx2.ConvertToVector256Int32(pb + x), b1 = Avx2.ConvertToVector256Int32(pb + x + 8);
                Avx.Store(pr + x, PackAvx2(MixAvx2(r0, g0, b0, m0, m1, m2, round), MixAvx2(r1, g1, b1, m0, m1, m2, round)));
                Avx.Store(pg + x, PackAvx2(MixAvx2(r0, g0, b0, m3, m4, m5, round), MixAvx2(r1, g1, b1, m3, m4, m5, round)));
                Avx.Store(pb + x, PackAvx2(MixAvx2(r0, g0, b0, m6, m7, m8, round), MixAvx2(r1, g1, b1, m6, m7, m8, round)));
            }
        }
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> MixAvx2(Vector256<int> r, Vector256<int> g, Vector256<int> b,
        Vector256<int> mr, Vector256<int> mg, Vector256<int> mb, Vector256<int> round) =>
        Avx2.ShiftRightArithmetic(
            Avx2.Add(Avx2.Add(Avx2.Add(Avx2.MultiplyLow(r, mr), Avx2.MultiplyLow(g, mg)), Avx2.MultiplyLow(b, mb)), round), 12);

    /// <summary>packus saturates to [0, 65535]; the permute restores element order across 128-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ushort> PackAvx2(Vector256<int> lo, Vector256<int> hi) =>
        Avx2.Permute4x64(Avx2.PackUnsignedSaturate(lo, hi).AsUInt64(), 0b11_01_10_00).AsUInt16();

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int ColorVector(Span<ushort> r, Span<ushort> g, Span<ushort> b, ReadOnlySpan<int> m)
    {
        if (!Vector.IsHardwareAccelerated) return 0;
        Vector<int> m0 = new(m[0]), m1 = new(m[1]), m2 = new(m[2]), m3 = new(m[3]), m4 = new(m[4]);
        Vector<int> m5 = new(m[5]), m6 = new(m[6]), m7 = new(m[7]), m8 = new(m[8]);
        Vector<int> round = new(2048), max = new(65535);
        int x = 0, n = r.Length, step = Vector<ushort>.Count;
        for (; x + step <= n; x += step)
        {
            Vector.Widen(new Vector<ushort>(r[x..]), out Vector<uint> r0u, out Vector<uint> r1u);
            Vector.Widen(new Vector<ushort>(g[x..]), out Vector<uint> g0u, out Vector<uint> g1u);
            Vector.Widen(new Vector<ushort>(b[x..]), out Vector<uint> b0u, out Vector<uint> b1u);
            Vector<int> r0 = Vector.AsVectorInt32(r0u), r1 = Vector.AsVectorInt32(r1u), g0 = Vector.AsVectorInt32(g0u), g1 = Vector.AsVectorInt32(g1u), b0 = Vector.AsVectorInt32(b0u), b1 = Vector.AsVectorInt32(b1u);
            Vector<ushort> outR = Vector.Narrow(Q12(r0 * m0 + g0 * m1 + b0 * m2, round, max), Q12(r1 * m0 + g1 * m1 + b1 * m2, round, max));
            Vector<ushort> outG = Vector.Narrow(Q12(r0 * m3 + g0 * m4 + b0 * m5, round, max), Q12(r1 * m3 + g1 * m4 + b1 * m5, round, max));
            Vector<ushort> outB = Vector.Narrow(Q12(r0 * m6 + g0 * m7 + b0 * m8, round, max), Q12(r1 * m6 + g1 * m7 + b1 * m8, round, max));
            outR.CopyTo(r[x..]);
            outG.CopyTo(g[x..]);
            outB.CopyTo(b[x..]);
        }
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<uint> Q12(Vector<int> sum, Vector<int> round, Vector<int> max) =>
        Vector.AsVectorUInt32(Vector.Min(Vector.Max(Vector.ShiftRightArithmetic(sum + round, 12), Vector<int>.Zero), max));

    /// <summary>(a + b + 1) &gt;&gt; 1 without widening.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<ushort> Avg(Vector<ushort> a, Vector<ushort> b) => (a | b) - ((a ^ b) >> 1);

    private static Vector<uint> Alternating(uint even, uint odd)
    {
        Span<uint> v = stackalloc uint[Vector<uint>.Count];
        for (int i = 0; i < v.Length; i++) v[i] = (i & 1) == 0 ? even : odd;
        return new Vector<uint>(v);
    }
}
