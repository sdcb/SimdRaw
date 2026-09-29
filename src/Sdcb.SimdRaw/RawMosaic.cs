namespace Sdcb.SimdRaw;

/// <summary>Decoded raw mosaic. Buffer is u16, row-major; <see cref="Stride"/> is in elements.
/// When <see cref="PlaneCount"/> &gt; 1 the planes are stored contiguously, <see cref="PlaneStride"/> apart.</summary>
public sealed class RawMosaic : IDisposable
{
    private ushort[]? _buffer;

    public RawMosaic(int width, int height, int stride, int planeCount, RawRect activeArea, ushort blackLevel, ushort whiteLevel)
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
        _buffer = new ushort[checked(PlaneStride * planeCount)];
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

    /// <summary>Full backing buffer (PlaneStride * PlaneCount elements).</summary>
    public Memory<ushort> Buffer => _buffer ?? throw new ObjectDisposedException(nameof(RawMosaic));

    public Span<ushort> Plane(int i)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(i);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(i, PlaneCount);
        return Buffer.Span.Slice(i * PlaneStride, PlaneStride);
    }

    public Span<ushort> Row(int y, int plane = 0) => Plane(plane).Slice(y * Stride, Width);

    public void Dispose() => _buffer = null;
}
