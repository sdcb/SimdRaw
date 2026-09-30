using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sdcb.SimdRaw.Decoders;
using Sdcb.SimdRaw.Formats;

namespace Sdcb.SimdRaw.Develop;

/// <summary>Mosaic → sRGB: crop to the active area, black level + white balance (scaled to 16 bit, highlights clipped),
/// bilinear demosaic, camera → linear sRGB (D65) matrix, sRGB transfer curve. Streams one output row at a time with a
/// three-row ring of scaled mosaic rows, so apart from the output only O(width) memory is used.</summary>
internal static class DevelopPipeline
{
    private static readonly double[,] s_xyzToSrgb =
    {
        { 0.412453, 0.357580, 0.180423 },
        { 0.212671, 0.715160, 0.072169 },
        { 0.019334, 0.119193, 0.950227 },
    };

    private static ushort[]? s_srgbCurve;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static RawBitmap Run(RawMosaic mosaic, DevelopMetadata meta, DevelopOptions options)
    {
        if (!meta.Cfa.IsBayer)
        {
            throw new RawException(RawErrorCode.UnsupportedVariant, $"Develop needs a Bayer CFA, got {meta.Cfa}.");
        }
        RawRect a = meta.ActiveArea;
        if (a.Left < 0 || a.Top < 0 || a.Width < 2 || a.Height < 2 || a.Left + a.Width > mosaic.Width || a.Top + a.Height > mosaic.Height)
        {
            throw new RawException(RawErrorCode.CorruptData, $"Active area {a} does not fit the {mosaic.Width}x{mosaic.Height} mosaic.");
        }

        KernelIsa isa = Kernels.Best;
        int width = a.Width, height = a.Height;
        DevelopKernels.ScaleParams[] scale = ScaleParams(meta, a);
        int[] matrix = Q12(CameraToSrgb(meta.CamXyz));
        ushort[] curve = s_srgbCurve ??= SrgbCurve();
        RawBitmap bitmap = new(width, height, options.OutputFormat, uninitialized: true);

        // Native scratch: three padded ring rows of scaled mosaic, then the R, G, B rows.
        int ringStride = width + 2;
        using NativeBuffer<ushort> scratch = new(3 * ringStride + 3 * width, zero: false);
        int[] ringRow = [int.MinValue, int.MinValue, int.MinValue];
        try
        {
            Span<ushort> rgb = scratch.GetSpan()[(3 * ringStride)..];
            Span<ushort> r = rgb[..width], g = rgb.Slice(width, width), b = rgb.Slice(2 * width, width);
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<ushort> prev = Scaled(y - 1), cur = Scaled(y), next = Scaled(y + 1);
                RawColor even = meta.Cfa.At(a.Left, a.Top + y), odd = meta.Cfa.At(a.Left + 1, a.Top + y);
                bool chromaOdd = even == RawColor.Green;
                RawColor site = chromaOdd ? odd : even;
                if (site == RawColor.Red) DevelopKernels.DemosaicRow(prev, cur, next, chromaOdd, r, g, b, isa);
                else DevelopKernels.DemosaicRow(prev, cur, next, chromaOdd, b, g, r, isa);
                DevelopKernels.ColorRow(r, g, b, matrix, isa);
                Encode(r, g, b, curve, bitmap.Row(y), options.OutputFormat);
            }
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        return bitmap;

        // Scaled mosaic row with one mirrored sample on each side; rows outside the area mirror too (parity-preserving).
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        ReadOnlySpan<ushort> Scaled(int y)
        {
            y = y < 0 ? -y : y >= height ? 2 * height - 2 - y : y;
            int slot = y % 3;
            Span<ushort> buf = scratch.GetSpan().Slice(slot * ringStride, ringStride);
            if (ringRow[slot] != y)
            {
                ReadOnlySpan<ushort> row = mosaic.Buffer.Span.Slice((a.Top + y) * mosaic.Stride + a.Left, width);
                DevelopKernels.ScaleRow(row, buf.Slice(1, width), scale[(a.Top + y) & 1], isa);
                buf[0] = buf[2];
                buf[width + 1] = buf[width - 1];
                ringRow[slot] = y;
            }
            return buf;
        }
    }

    /// <summary>Gamma-encodes and interleaves one row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Encode(ReadOnlySpan<ushort> r, ReadOnlySpan<ushort> g, ReadOnlySpan<ushort> b, ushort[] curve, Span<byte> dst, RawPixelFormat format)
    {
        switch (format)
        {
            case RawPixelFormat.Rgb48:
            {
                Span<ushort> o = MemoryMarshal.Cast<byte, ushort>(dst);
                for (int x = 0, i = 0; x < r.Length; x++, i += 3)
                {
                    o[i] = curve[r[x]];
                    o[i + 1] = curve[g[x]];
                    o[i + 2] = curve[b[x]];
                }
                if (!BitConverter.IsLittleEndian) BinaryPrimitives.ReverseEndianness(o, o);
                break;
            }
            case RawPixelFormat.Rgba64:
            {
                Span<ushort> o = MemoryMarshal.Cast<byte, ushort>(dst);
                for (int x = 0, i = 0; x < r.Length; x++, i += 4)
                {
                    o[i] = curve[r[x]];
                    o[i + 1] = curve[g[x]];
                    o[i + 2] = curve[b[x]];
                    o[i + 3] = ushort.MaxValue;
                }
                if (!BitConverter.IsLittleEndian) BinaryPrimitives.ReverseEndianness(o, o);
                break;
            }
            default:
            {
                Span<float> o = MemoryMarshal.Cast<byte, float>(dst);
                const float Scale = 1f / 65535f;
                for (int x = 0, i = 0; x < r.Length; x++, i += 3)
                {
                    o[i] = curve[r[x]] * Scale;
                    o[i + 1] = curve[g[x]] * Scale;
                    o[i + 2] = curve[b[x]] * Scale;
                }
                break;
            }
        }
    }

    /// <summary>Per mosaic-row parity: black, clip limit and Q12 gain mapping [black, white] × WB onto [0, 65535]. The WB
    /// is the as-shot one (or the matrix's D65 white when absent), normalised so the smallest multiplier is 1: every
    /// channel of a clipped pixel then saturates, which keeps highlights neutral.</summary>
    private static DevelopKernels.ScaleParams[] ScaleParams(DevelopMetadata meta, RawRect a)
    {
        double[] wb = meta.AsShotWb is { Length: 3 } w && w.All(v => v > 0) ? [.. w] : DaylightWb(meta.CamXyz);
        double minWb = wb.Min();
        for (int c = 0; c < 3; c++) wb[c] = Math.Min(wb[c] / minWb, 7.9);

        DevelopKernels.ScaleParams[] result = new DevelopKernels.ScaleParams[2];
        uint[] black = new uint[2], limit = new uint[2], mul = new uint[2];
        for (int parity = 0; parity < 2; parity++)
        {
            for (int dx = 0; dx < 2; dx++)
            {
                int y = parity, x = a.Left + dx;
                int bl = Math.Clamp(meta.Black[(y & 1) << 1 | (x & 1)], 0, 65534);
                int lim = Math.Max(Math.Min(meta.White, 65535) - bl, 1);
                double gain = wb[(int)meta.Cfa.At(x, y)] * 65535.0 / lim;
                black[dx] = (uint)bl;
                limit[dx] = (uint)lim;
                mul[dx] = (uint)Math.Min(Math.Round(gain * 4096), 65535.0 * 7.9 * 4096 / lim);
            }
            result[parity] = new DevelopKernels.ScaleParams(black[0], limit[0], mul[0], black[1], limit[1], mul[1]);
        }
        return result;
    }

    /// <summary>dcraw <c>cam_xyz_coeff</c>: camera ← sRGB with rows normalised to 1, inverted.</summary>
    private static double[,] CameraToSrgb(short[]? camXyz)
    {
        if (camXyz is null) return Identity();
        double[,] camRgb = CamRgb(camXyz, out _);
        return Invert(camRgb) ?? Identity();
    }

    private static double[] DaylightWb(short[]? camXyz)
    {
        if (camXyz is null) return [1, 1, 1];
        CamRgb(camXyz, out double[] rowSums);
        return rowSums.All(s => s > 0) ? [1 / rowSums[0], 1 / rowSums[1], 1 / rowSums[2]] : [1, 1, 1];
    }

    private static double[,] CamRgb(short[] camXyz, out double[] rowSums)
    {
        double[,] camRgb = new double[3, 3];
        rowSums = new double[3];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++) sum += camXyz[i * 3 + k] / 10000.0 * s_xyzToSrgb[k, j];
                camRgb[i, j] = sum;
                rowSums[i] += sum;
            }
        }
        for (int i = 0; i < 3; i++)
        {
            if (rowSums[i] == 0) continue;
            for (int j = 0; j < 3; j++) camRgb[i, j] /= rowSums[i];
        }
        return camRgb;
    }

    private static double[,]? Invert(double[,] m)
    {
        double det = m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
                   - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
                   + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        if (Math.Abs(det) < 1e-9) return null;
        double[,] inv = new double[3, 3];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                int r0 = (j + 1) % 3, r1 = (j + 2) % 3, c0 = (i + 1) % 3, c1 = (i + 2) % 3;
                inv[i, j] = (m[r0, c0] * m[r1, c1] - m[r0, c1] * m[r1, c0]) / det;
            }
        }
        return inv;
    }

    private static double[,] Identity() => new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

    private static int[] Q12(double[,] m)
    {
        int[] q = new int[9];
        for (int i = 0; i < 9; i++) q[i] = (int)Math.Clamp(Math.Round(m[i / 3, i % 3] * 4096), -32767, 32767);
        return q;
    }

    private static ushort[] SrgbCurve()
    {
        ushort[] lut = new ushort[65536];
        for (int i = 0; i < lut.Length; i++)
        {
            double x = i / 65535.0;
            double y = x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055;
            lut[i] = (ushort)Math.Clamp(Math.Round(y * 65535), 0, 65535);
        }
        return lut;
    }
}
