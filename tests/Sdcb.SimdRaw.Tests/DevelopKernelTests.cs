using Sdcb.SimdRaw.Decoders;
using Sdcb.SimdRaw.Develop;
using Xunit;

namespace Sdcb.SimdRaw.Tests;

/// <summary>Develop output is hashed across ISA tiers, so every kernel must be bit-exact with its scalar tail.</summary>
public class DevelopKernelTests
{
    private static readonly KernelIsa[] s_simd = [KernelIsa.Vector, KernelIsa.Avx2];

    [Theory]
    [InlineData(1, 5568)]
    [InlineData(2, 37)]
    [InlineData(3, 3)]
    public void Scale_SimdMatchesScalar(int seed, int width)
    {
        Random rnd = new(seed);
        ushort[] src = RandomU16(rnd, width);
        DevelopKernels.ScaleParams[] cases =
        [
            new(1008, 15520 - 1008, 17000, 1008, 15520 - 1008, 36000),
            new(0, 65535, 4096, 65535, 1, 65535u * 79 / 10 * 4096),
            new(512, 3, 4096 * 7, 0, 65535, 1),
        ];
        foreach (DevelopKernels.ScaleParams p in cases)
        {
            ushort[] expected = new ushort[width];
            DevelopKernels.ScaleRow(src, expected, p, KernelIsa.Scalar);
            foreach (KernelIsa isa in s_simd)
            {
                ushort[] got = new ushort[width];
                DevelopKernels.ScaleRow(src, got, p, isa);
                Assert.Equal(expected, got);
            }
        }
    }

    [Theory]
    [InlineData(1, 5568, false)]
    [InlineData(2, 5568, true)]
    [InlineData(3, 19, false)]
    [InlineData(4, 2, true)]
    public void Demosaic_SimdMatchesScalar(int seed, int width, bool chromaOdd)
    {
        Random rnd = new(seed);
        ushort[] prev = RandomU16(rnd, width + 2), cur = RandomU16(rnd, width + 2), next = RandomU16(rnd, width + 2);
        (ushort[] s, ushort[] g, ushort[] o) expected = (new ushort[width], new ushort[width], new ushort[width]);
        DevelopKernels.DemosaicRow(prev, cur, next, chromaOdd, expected.s, expected.g, expected.o, KernelIsa.Scalar);
        foreach (KernelIsa isa in s_simd)
        {
            (ushort[] s, ushort[] g, ushort[] o) got = (new ushort[width], new ushort[width], new ushort[width]);
            DevelopKernels.DemosaicRow(prev, cur, next, chromaOdd, got.s, got.g, got.o, isa);
            Assert.Equal(expected.s, got.s);
            Assert.Equal(expected.g, got.g);
            Assert.Equal(expected.o, got.o);
        }
    }

    [Theory]
    [InlineData(1, 5568)]
    [InlineData(2, 23)]
    public void Color_SimdMatchesScalar(int seed, int width)
    {
        Random rnd = new(seed);
        int[][] matrices =
        [
            [7780, -2622, -1063, -1015, 5767, -656, 180, -1284, 5199],
            [4096, 0, 0, 0, 4096, 0, 0, 0, 4096],
            [32767, -32767, 32767, -32767, 32767, -32767, 32767, 32767, 32767],
        ];
        ushort[] r = RandomU16(rnd, width), g = RandomU16(rnd, width), b = RandomU16(rnd, width);
        foreach (int[] m in matrices)
        {
            (ushort[] r, ushort[] g, ushort[] b) expected = ((ushort[])r.Clone(), (ushort[])g.Clone(), (ushort[])b.Clone());
            DevelopKernels.ColorRow(expected.r, expected.g, expected.b, m, KernelIsa.Scalar);
            foreach (KernelIsa isa in s_simd)
            {
                (ushort[] r, ushort[] g, ushort[] b) got = ((ushort[])r.Clone(), (ushort[])g.Clone(), (ushort[])b.Clone());
                DevelopKernels.ColorRow(got.r, got.g, got.b, m, isa);
                Assert.Equal(expected.r, got.r);
                Assert.Equal(expected.g, got.g);
                Assert.Equal(expected.b, got.b);
            }
        }
    }

    private static ushort[] RandomU16(Random rnd, int n)
    {
        ushort[] v = new ushort[n];
        for (int i = 0; i < n; i++)
        {
            // Mix full-range noise with the extremes.
            v[i] = (rnd.Next(8)) switch { 0 => 0, 1 => 65535, _ => (ushort)rnd.Next(65536) };
        }
        return v;
    }
}
