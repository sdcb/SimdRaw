using System.Runtime.InteropServices;
using System.Text;

namespace Sdcb.SimdRaw.Harness.Engines.RawSpeed;

/// <summary>Mirror of <c>RawSpeedResult</c> in native/rawspeed-shim/rawspeed_shim.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RawSpeedResult
{
    public int ErrorCode;
    public int Width;
    public int Height;
    public int Cpp;
    public int CropLeft;
    public int CropTop;
    public int CropWidth;
    public int CropHeight;
    public int DataType;
    public int BytesPerSample;
    public int BlackLevel;
    public int WhitePoint;
    public uint CfaFilters;
    public int Reserved;
    public double DecodeTimeMs;
    public double TotalTimeMs;
    public byte* Pixels;
    public long PixelsLen;
    public fixed byte Make[64];
    public fixed byte Model[64];
    public fixed byte ErrorMsg[256];
}

internal static class RawSpeedErrorCode
{
    public const int Ok = 0;
    public const int Unsupported = 1;
    public const int Corrupt = 2;
    public const int Io = 3;
    public const int NotInitialized = 4;
    public const int OutOfMemory = 5;
    public const int Internal = 6;
}

/// <summary>Loads the shim with <see cref="NativeLibrary"/> and binds exports as function pointers.</summary>
internal sealed unsafe class RawSpeedNative : IDisposable
{
    private nint _handle;
    private readonly delegate* unmanaged[Cdecl]<byte*> _version;
    private readonly delegate* unmanaged[Cdecl]<byte*, int> _init;
    private readonly delegate* unmanaged[Cdecl]<byte*> _lastError;
    private readonly delegate* unmanaged[Cdecl]<byte*, RawSpeedResult*, int> _decode;
    private readonly delegate* unmanaged[Cdecl]<RawSpeedResult*, void> _free;
    private readonly delegate* unmanaged[Cdecl]<void> _shutdown;

    public RawSpeedNative(string libraryPath)
    {
        _handle = NativeLibrary.Load(libraryPath);
        _version = (delegate* unmanaged[Cdecl]<byte*>)NativeLibrary.GetExport(_handle, "rawspeed_version");
        _init = (delegate* unmanaged[Cdecl]<byte*, int>)NativeLibrary.GetExport(_handle, "rawspeed_init");
        _lastError = (delegate* unmanaged[Cdecl]<byte*>)NativeLibrary.GetExport(_handle, "rawspeed_last_error");
        _decode = (delegate* unmanaged[Cdecl]<byte*, RawSpeedResult*, int>)NativeLibrary.GetExport(_handle, "rawspeed_decode");
        _free = (delegate* unmanaged[Cdecl]<RawSpeedResult*, void>)NativeLibrary.GetExport(_handle, "rawspeed_free");
        _shutdown = (delegate* unmanaged[Cdecl]<void>)NativeLibrary.GetExport(_handle, "rawspeed_shutdown");
    }

    public string Version => Marshal.PtrToStringUTF8((nint)_version()) ?? "";

    public int Init(string camerasXmlPath, out string error)
    {
        byte[] path = Utf8Z(camerasXmlPath);
        int rc;
        fixed (byte* p = path) rc = _init(p);
        error = rc == RawSpeedErrorCode.Ok ? "" : Marshal.PtrToStringUTF8((nint)_lastError()) ?? "";
        return rc;
    }

    public int Decode(string filePath, RawSpeedResult* result)
    {
        byte[] path = Utf8Z(filePath);
        fixed (byte* p = path) return _decode(p, result);
    }

    public void Free(RawSpeedResult* result) => _free(result);

    public static string ReadString(byte* buffer, int capacity)
    {
        ReadOnlySpan<byte> span = new(buffer, capacity);
        int nul = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul < 0 ? span : span[..nul]);
    }

    private static byte[] Utf8Z(string s)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, bytes);
        return bytes;
    }

    public void Dispose()
    {
        if (_handle == 0) return;
        _shutdown();
        NativeLibrary.Free(_handle);
        _handle = 0;
    }
}
