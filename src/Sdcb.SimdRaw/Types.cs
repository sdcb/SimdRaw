namespace Sdcb.SimdRaw;

public readonly record struct RawRect(int Left, int Top, int Width, int Height);

public enum RawColor : byte { Red, Green, Blue }

/// <summary>2×2 colour filter array, relative to mosaic buffer position (0, 0).</summary>
public readonly record struct RawCfaPattern(RawColor TopLeft, RawColor TopRight, RawColor BottomLeft, RawColor BottomRight)
{
    public static RawCfaPattern Rggb => new(RawColor.Red, RawColor.Green, RawColor.Green, RawColor.Blue);

    public RawColor At(int x, int y) => ((y & 1) << 1 | (x & 1)) switch
    {
        0 => TopLeft,
        1 => TopRight,
        2 => BottomLeft,
        _ => BottomRight,
    };

    /// <summary>One red, one blue and two diagonal greens.</summary>
    public bool IsBayer =>
        Count(RawColor.Red) == 1 && Count(RawColor.Blue) == 1 &&
        ((TopLeft == RawColor.Green && BottomRight == RawColor.Green) || (TopRight == RawColor.Green && BottomLeft == RawColor.Green));

    private int Count(RawColor c) => (TopLeft == c ? 1 : 0) + (TopRight == c ? 1 : 0) + (BottomLeft == c ? 1 : 0) + (BottomRight == c ? 1 : 0);

    public override string ToString() => string.Concat(Letter(TopLeft), Letter(TopRight), Letter(BottomLeft), Letter(BottomRight));

    private static char Letter(RawColor c) => c switch { RawColor.Red => 'R', RawColor.Green => 'G', _ => 'B' };
}

public enum RawPixelFormat { Rgb48, Rgba64, Rgb96F }

/// <summary>Post-processed output of <see cref="RawFile.Develop(DevelopOptions?)"/>: interleaved, row-major,
/// <see cref="Stride"/> in bytes. <see cref="RawPixelFormat.Rgb48"/> / <see cref="RawPixelFormat.Rgba64"/> are
/// little-endian u16 per channel, <see cref="RawPixelFormat.Rgb96F"/> is f32 per channel; all are sRGB-encoded.
/// The buffer is native memory released by <see cref="Dispose"/>; spans obtained earlier must not be used afterwards.</summary>
public sealed class RawBitmap : IDisposable
{
    private NativeBuffer<byte>? _buffer;

    public RawBitmap(int width, int height, RawPixelFormat pixelFormat) : this(width, height, pixelFormat, uninitialized: false)
    {
    }

    /// <param name="uninitialized">The producer writes every byte itself.</param>
    internal RawBitmap(int width, int height, RawPixelFormat pixelFormat, bool uninitialized)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        Stride = checked(width * BytesPerPixelOf(pixelFormat));
        _buffer = new NativeBuffer<byte>(checked(Stride * height), zero: !uninitialized);
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public RawPixelFormat PixelFormat { get; }
    public int BytesPerPixel => BytesPerPixelOf(PixelFormat);

    /// <summary>Full backing buffer (Stride * Height bytes).</summary>
    public Memory<byte> Buffer => _buffer?.Memory ?? throw new ObjectDisposedException(nameof(RawBitmap));

    public Span<byte> Row(int y) => Buffer.Span.Slice(checked(y * Stride), Stride);

    public void Dispose()
    {
        ((IDisposable?)_buffer)?.Dispose();
        _buffer = null;
    }

    public static int BytesPerPixelOf(RawPixelFormat format) => format switch
    {
        RawPixelFormat.Rgb48 => 6,
        RawPixelFormat.Rgba64 => 8,
        RawPixelFormat.Rgb96F => 12,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };
}

/// <summary>Options that apply at Open() time.</summary>
public class DecoderOptions
{
    internal static DecoderOptions Default { get; } = new();

    /// <summary>Copy the input buffer instead of referencing it.</summary>
    public bool CopyInput { get; init; }

    /// <summary>Max worker threads for tile/strip-parallel codecs. 0 = Environment.ProcessorCount.</summary>
    public int MaxThreads { get; init; }
}

/// <summary>Options for a single <see cref="RawFile.DecodeMosaic"/> call.</summary>
public sealed class DecodeOptions
{
    public RawRect? Region { get; init; }

    /// <summary>Apply sensor-level linearization tables (required for byte-exact goldens).</summary>
    public bool ApplyLinearization { get; init; } = true;
}

public sealed class DevelopOptions : DecoderOptions
{
    public RawPixelFormat OutputFormat { get; init; } = RawPixelFormat.Rgb48;
}

public enum RawErrorCode
{
    Unknown,
    UnsupportedFormat,
    UnsupportedVariant,
    CorruptData,
    TruncatedFile,
    IoError,
    OutOfMemory,
    Cancelled,
}

public class RawException(RawErrorCode code, string message, Exception? inner = null) : Exception(message, inner)
{
    public RawErrorCode Code { get; } = code;
}
