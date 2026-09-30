using Sdcb.SimdRaw.Decoders;
using Xunit;

namespace Sdcb.SimdRaw.Tests;

/// <summary>Every SIMD kernel must reproduce the scalar reference bit for bit, also on garbage input (random blocks hit
/// min &gt; max, imax == imin, every shift, and row widths that are not a multiple of the block size).</summary>
public class DecoderKernelTests
{
    private static readonly KernelIsa[] SimdIsas = [KernelIsa.Vector, KernelIsa.Avx2];

    [Theory]
    [InlineData(2816, 1)]
    [InlineData(4240, 2)]
    [InlineData(6048, 3)]
    [InlineData(94, 4)]
    [InlineData(63, 5)]
    [InlineData(31, 6)]
    public void SonyArw2_SimdMatchesScalar(int width, int seed)
    {
        Random rnd = new(seed);
        const int Height = 9, Offset = 5;
        byte[] file = new byte[Offset + width * Height];
        rnd.NextBytes(file);
        SonyCurve curve = new(2000, 2600, 3225, 3525);

        ushort[] expected = DecodeArw2(file, Offset, width, Height, curve, KernelIsa.Scalar);
        foreach (KernelIsa isa in SimdIsas)
        {
            Assert.Equal(expected, DecodeArw2(file, Offset, width, Height, curve, isa));
        }
    }

    [Fact]
    public void SonyArw2_TruncatedDataReadsAsZero()
    {
        byte[] file = new byte[100 + 2816 * 3];
        new Random(7).NextBytes(file);
        SonyCurve curve = new(2000, 2600, 3225, 3525);
        ushort[] expected = DecodeArw2(file, 100, 2816, 5, curve, KernelIsa.Scalar);
        foreach (KernelIsa isa in SimdIsas) Assert.Equal(expected, DecodeArw2(file, 100, 2816, 5, curve, isa));
    }

    [Theory]
    [InlineData(2000, 2600, 3225, 3525)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(4095, 4095, 4095, 4095)]
    [InlineData(100, 100, 4000, 4095)]
    public void SonyCurve_ArithmeticFormEqualsTable(int c1, int c2, int c3, int c4)
    {
        SonyCurve curve = new((ushort)c1, (ushort)c2, (ushort)c3, (ushort)c4);
        Assert.True(curve.Monotonic);
        for (int p = 0; p < 2048; p++)
        {
            int j = p << 1;
            int f = j + Math.Max(0, j - c1) + (Math.Max(0, j - c2) << 1) + (Math.Max(0, j - c3) << 2) + (Math.Max(0, j - c4) << 3);
            Assert.Equal(curve.Lut[p], f);
        }
    }

    [Fact]
    public void SonyArw2_NonMonotonicCurveUsesTableEverywhere()
    {
        SonyCurve curve = new(3000, 1000, 3500, 2000);
        Assert.False(curve.Monotonic);
        byte[] file = new byte[2816 * 4];
        new Random(11).NextBytes(file);
        ushort[] expected = DecodeArw2(file, 0, 2816, 4, curve, KernelIsa.Scalar);
        foreach (KernelIsa isa in SimdIsas) Assert.Equal(expected, DecodeArw2(file, 0, 2816, 4, curve, isa));
    }

    [Theory]
    [InlineData(5584, 1)]
    [InlineData(6048, 2)]
    [InlineData(4, 3)]
    [InlineData(7, 4)]
    [InlineData(9, 5)]
    [InlineData(101, 6)]
    public void Nikon14Bit_SimdMatchesScalar(int width, int seed)
    {
        Random rnd = new(seed);
        const int Height = 6, Offset = 3;
        int line = Nikon14BitDecoder.LineLength(width);
        foreach (int length in new[] { Offset + line * Height, Offset + line * Height - line / 2 })
        {
            byte[] file = new byte[length];
            rnd.NextBytes(file);
            ushort[] expected = DecodeNikon(file, Offset, width, Height, KernelIsa.Scalar);
            foreach (KernelIsa isa in SimdIsas) Assert.Equal(expected, DecodeNikon(file, Offset, width, Height, isa));
        }
    }

    [Fact]
    public void Nikon14Bit_ScalarMatchesBitstreamDefinition()
    {
        byte[] file = new byte[Nikon14BitDecoder.LineLength(64) * 2];
        new Random(9).NextBytes(file);
        ushort[] got = DecodeNikon(file, 0, 64, 2, KernelIsa.Scalar);
        int line = Nikon14BitDecoder.LineLength(64);
        for (int row = 0; row < 2; row++)
        {
            for (int x = 0; x < 64; x++)
            {
                int bit = 14 * x, o = row * line + bit / 8;
                int b2 = o + 2 < file.Length ? file[o + 2] : 0;
                uint v = (uint)(file[o] | file[o + 1] << 8 | b2 << 16) >> (bit % 8) & 0x3fff;
                Assert.Equal(v, got[row * 64 + x]);
            }
        }
    }

    private static ushort[] DecodeArw2(byte[] file, int offset, int width, int height, SonyCurve curve, KernelIsa isa)
    {
        ushort[] dst = new ushort[width * height];
        Array.Fill(dst, (ushort)0xDEAD);
        SonyArw2Decoder.Decode(file, offset, width, height, curve, dst, isa);
        return dst;
    }

    private static ushort[] DecodeNikon(byte[] file, int offset, int width, int height, KernelIsa isa)
    {
        ushort[] dst = new ushort[width * height];
        Array.Fill(dst, (ushort)0xDEAD);
        Nikon14BitDecoder.Decode(file, offset, width, height, dst, isa);
        return dst;
    }
}
