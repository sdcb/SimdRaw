namespace Sdcb.SimdRaw;

public readonly record struct RawRect(int Left, int Top, int Width, int Height);

public enum RawPixelFormat { Rgb48, Rgba64, Rgb96F }

/// <summary>Post-processed output of <see cref="RawFile.Develop"/>.</summary>
public sealed class RawBitmap : IDisposable
{
    public RawBitmap(int width, int height, int stride, RawPixelFormat pixelFormat)
    {
        Width = width;
        Height = height;
        Stride = stride;
        PixelFormat = pixelFormat;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public RawPixelFormat PixelFormat { get; }

    public void Dispose() { }
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
