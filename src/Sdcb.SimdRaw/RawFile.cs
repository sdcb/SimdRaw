namespace Sdcb.SimdRaw;

/// <summary>One opened RAW document: parsed metadata, undecoded payload.</summary>
public sealed class RawFile : IDisposable
{
    private readonly ReadOnlyMemory<byte> _data;
    private bool _disposed;

    internal RawFile(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        _data = data;
        Options = options;
    }

    internal DecoderOptions Options { get; }

    /// <summary>Size of the underlying RAW payload in bytes.</summary>
    public int Length => _data.Length;

    /// <summary>Decode a raw mosaic frame (u16, pre-demosaic, linearization applied).</summary>
    public RawMosaic DecodeMosaic(int frameIndex = 0, DecodeOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        throw new RawException(RawErrorCode.UnsupportedFormat, "SimdRaw decoders are not implemented yet.");
    }

    /// <summary>Full pipeline: decode + black level + white balance + demosaic + color + gamma.</summary>
    public RawBitmap Develop(DevelopOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new RawException(RawErrorCode.UnsupportedFormat, "SimdRaw develop pipeline is not implemented yet.");
    }

    public void Dispose() => _disposed = true;
}
