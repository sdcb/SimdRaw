using System.Runtime.ExceptionServices;
using Sdcb.SimdRaw.Decoders;
using Sdcb.SimdRaw.Develop;
using Sdcb.SimdRaw.Formats;

namespace Sdcb.SimdRaw;

/// <summary>One opened RAW document: parsed metadata, undecoded payload.</summary>
public sealed class RawFile : IDisposable
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly IDisposable? _owner;
    private readonly RawSource? _source;
    private readonly ExceptionDispatchInfo? _identifyError;
    private bool _disposed;

    /// <param name="owner">Owner of <paramref name="data"/> (native file bytes), released on <see cref="Dispose"/>.</param>
    internal RawFile(ReadOnlyMemory<byte> data, DecoderOptions options, IDisposable? owner)
    {
        _data = data;
        _owner = owner;
        Options = options;
        try
        {
            _source = RawIdentifier.Identify(data);
        }
        catch (RawException ex)
        {
            _identifyError = ExceptionDispatchInfo.Capture(ex);
        }
    }

    internal DecoderOptions Options { get; }

    /// <summary>Size of the underlying RAW payload in bytes.</summary>
    public int Length => _data.Length;

    /// <summary>Camera make as stored in the file, null when the file was not recognized.</summary>
    public string? Make => _source?.Make;

    /// <summary>Camera model as stored in the file, null when the file was not recognized.</summary>
    public string? Model => _source?.Model;

    /// <summary>Decode a raw mosaic frame (u16, pre-demosaic, linearization applied).</summary>
    public RawMosaic DecodeMosaic(int frameIndex = 0, DecodeOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        RawSource source = Source();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frameIndex, 0);
        if (options?.Region is not null)
        {
            throw new RawException(RawErrorCode.UnsupportedVariant, "Region decoding is not implemented yet.");
        }
        if (options is { ApplyLinearization: false } && source.Codec == RawCodec.SonyArw2)
        {
            throw new RawException(RawErrorCode.UnsupportedVariant, "Sony ARW2 samples are only available through the tone curve.");
        }

        DevelopMetadata meta = source.Develop;
        RawMosaic mosaic = new(source.Width, source.Height, source.Width, 1, meta.ActiveArea,
            (ushort)Math.Clamp(meta.Black.Min(), 0, ushort.MaxValue), (ushort)Math.Clamp(meta.White, 0, ushort.MaxValue), uninitialized: true)
        {
            CfaPattern = meta.Cfa,
        };
        Span<ushort> dst = mosaic.Buffer.Span;
        switch (source.Codec)
        {
            case RawCodec.SonyArw2:
                SonyArw2Decoder.Decode(_data.Span, source.DataOffset, source.Width, source.Height, source.Curve, dst, Kernels.Best);
                break;
            case RawCodec.Nikon14Bit:
                Nikon14BitDecoder.Decode(_data.Span, source.DataOffset, source.Width, source.Height, dst, Kernels.Best);
                break;
            default:
                throw new RawException(RawErrorCode.UnsupportedFormat, $"Codec {source.Codec} has no decoder.");
        }
        return mosaic;
    }

    /// <summary>Full pipeline: decode + black level + white balance + demosaic + color + gamma.</summary>
    public RawBitmap Develop(DevelopOptions? options = null)
    {
        using RawMosaic mosaic = DecodeMosaic();
        return Develop(mosaic, options);
    }

    /// <summary>Develops a mosaic previously returned by <see cref="DecodeMosaic"/> of this file:
    /// black level, white balance, bilinear demosaic, camera → sRGB matrix, sRGB gamma.</summary>
    public RawBitmap Develop(RawMosaic mosaic, DevelopOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(mosaic);
        RawSource source = Source();
        if (mosaic.Width != source.Width || mosaic.Height != source.Height || mosaic.PlaneCount != 1)
        {
            throw new ArgumentException("The mosaic was not decoded from this file.", nameof(mosaic));
        }
        return DevelopPipeline.Run(mosaic, source.Develop, options ?? new DevelopOptions());
    }

    private RawSource Source()
    {
        _identifyError?.Throw();
        return _source!;
    }

    /// <summary>Releases the file bytes read by <see cref="RawDecoder.OpenFile"/> / <see cref="RawDecoder.Open(Stream, DecoderOptions?)"/>.
    /// Mosaics and bitmaps produced earlier own their memory and stay valid.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner?.Dispose();
    }
}
