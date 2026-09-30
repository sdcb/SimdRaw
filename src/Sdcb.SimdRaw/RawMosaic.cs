namespace Sdcb.SimdRaw;

/// <summary>Decoded raw mosaic. Buffer is u16, row-major; <see cref="Stride"/> is in elements.
/// When <see cref="PlaneCount"/> &gt; 1 the planes are stored contiguously, <see cref="PlaneStride"/> apart.
/// The buffer is native memory released by <see cref="Dispose"/>; spans obtained earlier must not be used afterwards.</summary>
public sealed class RawMosaic : IDisposable
{
    private NativeBuffer<ushort>? _buffer;

    public RawMosaic(int width, int height, int stride, int planeCount, RawRect activeArea, ushort blackLevel, ushort whiteLevel)
        : this(width, height, stride, planeCount, activeArea, blackLevel, whiteLevel, uninitialized: false)
    {
    }

    /// <param name="uninitialized">The decoder writes every element itself.</param>
    internal RawMosaic(int width, int height, int stride, int planeCount, RawRect activeArea, ushort blackLevel, ushort whiteLevel, bool uninitialized)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(planeCount);
        Width = width;
        Height = height;
        Stride = stride;
        PlaneCount = planeCount;
        PlaneStride = checked(stride * height);
        ActiveArea = activeArea;
        BlackLevel = blackLevel;
        WhiteLevel = whiteLevel;
        int length = checked(PlaneStride * planeCount);
        _buffer = new NativeBuffer<ushort>(length, zero: !uninitialized);
    }

    /// <summary>Raw sensor width including masked edges.</summary>
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int PlaneCount { get; }
    public int PlaneStride { get; }
    public RawRect ActiveArea { get; }
    public ushort BlackLevel { get; }
    public ushort WhiteLevel { get; }

    /// <summary>Colour filter layout of a single-plane mosaic, relative to buffer position (0, 0).</summary>
    public RawCfaPattern CfaPattern { get; init; } = RawCfaPattern.Rggb;

    /// <summary>Full backing buffer (PlaneStride * PlaneCount elements).</summary>
    public Memory<ushort> Buffer => _buffer?.Memory ?? throw new ObjectDisposedException(nameof(RawMosaic));

    public Span<ushort> Plane(int i)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(i);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(i, PlaneCount);
        return Buffer.Span.Slice(i * PlaneStride, PlaneStride);
    }

    public Span<ushort> Row(int y, int plane = 0) => Plane(plane).Slice(y * Stride, Width);

    public void Dispose()
    {
        ((IDisposable?)_buffer)?.Dispose();
        _buffer = null;
    }
}
