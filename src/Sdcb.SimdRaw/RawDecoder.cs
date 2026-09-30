namespace Sdcb.SimdRaw;

/// <summary>Static entry: open RAW sources.</summary>
public static class RawDecoder
{
    /// <summary>Open a RAW file from disk. The bytes are read into native memory owned by the returned
    /// <see cref="RawFile"/> and released by its <see cref="RawFile.Dispose"/>.</summary>
    public static RawFile OpenFile(string path, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        NativeBuffer<byte>? buffer = null;
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.SequentialScan);
            buffer = new NativeBuffer<byte>(checked((int)fs.Length), zero: false);
            fs.ReadExactly(buffer.GetSpan());
            return new RawFile(buffer.Memory, options ?? DecoderOptions.Default, buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            ((IDisposable?)buffer)?.Dispose();
            throw new RawException(RawErrorCode.IoError, $"Cannot read '{path}': {ex.Message}", ex);
        }
        catch
        {
            ((IDisposable?)buffer)?.Dispose();
            throw;
        }
    }

    /// <summary>Open a stream (read from its current position to the end). Takes no ownership of the stream.</summary>
    public static RawFile Open(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            using MemoryStream ms = new();
            stream.CopyTo(ms);
            return Copy(ms.GetBuffer().AsSpan(0, (int)ms.Length), options ?? DecoderOptions.Default);
        }

        NativeBuffer<byte> buffer = new(checked((int)(stream.Length - stream.Position)), zero: false);
        try
        {
            stream.ReadExactly(buffer.GetSpan());
            return new RawFile(buffer.Memory, options ?? DecoderOptions.Default, buffer);
        }
        catch
        {
            ((IDisposable)buffer).Dispose();
            throw;
        }
    }

    /// <summary>Open an in-memory buffer. The caller must keep the buffer alive for the lifetime of
    /// the returned <see cref="RawFile"/> unless <see cref="DecoderOptions.CopyInput"/> is set.</summary>
    public static RawFile Open(ReadOnlyMemory<byte> buffer, DecoderOptions? options = null)
    {
        options ??= DecoderOptions.Default;
        return options.CopyInput ? Copy(buffer.Span, options) : new RawFile(buffer, options, owner: null);
    }

    private static RawFile Copy(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        NativeBuffer<byte> copy = new(data.Length, zero: false);
        data.CopyTo(copy.GetSpan());
        return new RawFile(copy.Memory, options, copy);
    }
}
