using System.Security.Cryptography;

namespace Sdcb.SimdRaw.Harness.Engines;

public static class MosaicHasher
{
    /// <summary>SHA-256 of the raw bytes (little-endian samples), lowercase hex.</summary>
    public static unsafe string Sha256(byte* data, long length)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int chunk = 1 << 24;
        for (long offset = 0; offset < length; offset += chunk)
        {
            int n = (int)Math.Min(chunk, length - offset);
            hash.AppendData(new ReadOnlySpan<byte>(data + offset, n));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>RawSpeed rstest "md5sum of per-line md5sums": MD5 over the concatenated
    /// 16-byte MD5 digests of each packed row.</summary>
    public static unsafe string Md5OfLineMd5(byte* data, long rowBytes, int rows)
    {
        byte[] digests = new byte[rows * MD5.HashSizeInBytes];
        for (int y = 0; y < rows; y++)
        {
            ReadOnlySpan<byte> row = new(data + y * rowBytes, checked((int)rowBytes));
            MD5.HashData(row, digests.AsSpan(y * MD5.HashSizeInBytes, MD5.HashSizeInBytes));
        }
        return Convert.ToHexStringLower(MD5.HashData(digests));
    }

    public static string Sha256OfRows(ReadOnlySpan<ushort> buffer, int width, int stride, int rows)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int y = 0; y < rows; y++)
        {
            hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.Slice(y * stride, width)));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
