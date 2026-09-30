using Xunit;

namespace Sdcb.SimdRaw.Tests;

/// <summary>Unknown or damaged input must surface as <see cref="RawException"/>, never as an arbitrary exception.</summary>
public class IdentifyTests
{
    [Fact]
    public void RandomTiffLikeInput_OnlyThrowsRawException()
    {
        Random rnd = new(42);
        for (int i = 0; i < 3000; i++)
        {
            byte[] data = new byte[rnd.Next(8, 4096)];
            rnd.NextBytes(data);
            bool big = (i & 1) != 0;
            data[0] = data[1] = big ? (byte)'M' : (byte)'I';
            data[2] = big ? (byte)0 : (byte)42;
            data[3] = big ? (byte)42 : (byte)0;
            // Point IFD0 into the buffer most of the time.
            int ifd = rnd.Next(8, data.Length);
            if (rnd.Next(4) != 0)
            {
                byte[] off = BitConverter.GetBytes(ifd);
                if (big) Array.Reverse(off);
                off.CopyTo(data, 4);
            }

            using RawFile file = RawDecoder.Open(data);
            RawException ex = Assert.Throws<RawException>(() => file.DecodeMosaic());
            Assert.Contains(ex.Code, new[] { RawErrorCode.UnsupportedFormat, RawErrorCode.CorruptData });
        }
    }

    [Fact]
    public void NonTiffInput_IsUnsupported()
    {
        using RawFile file = RawDecoder.Open(new byte[] { 1, 2, 3 });
        Assert.Equal(RawErrorCode.UnsupportedFormat, Assert.Throws<RawException>(() => file.DecodeMosaic()).Code);
        Assert.Null(file.Make);
    }
}
