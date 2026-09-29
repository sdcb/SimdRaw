namespace Sdcb.SimdRaw;

/// <summary>Static entry: open RAW sources.</summary>
public static class RawDecoder
{
    /// <summary>Open a RAW file from disk.</summary>
    public static RawFile OpenFile(string path, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            throw new RawException(RawErrorCode.IoError, $"Cannot read '{path}': {ex.Message}", ex);
        }
        return new RawFile(data, options ?? DecoderOptions.Default);
    }

    /// <summary>Open a seekable stream. Takes no ownership.</summary>
    public static RawFile Open(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream ms = new();
        stream.CopyTo(ms);
        return new RawFile(ms.ToArray(), options ?? DecoderOptions.Default);
    }

    /// <summary>Open an in-memory buffer. The caller must keep the buffer alive for the lifetime of
    /// the returned <see cref="RawFile"/> unless <see cref="DecoderOptions.CopyInput"/> is set.</summary>
    public static RawFile Open(ReadOnlyMemory<byte> buffer, DecoderOptions? options = null)
    {
        options ??= DecoderOptions.Default;
        return new RawFile(options.CopyInput ? buffer.ToArray() : buffer, options);
    }
}
