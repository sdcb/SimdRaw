using System.Buffers;
using System.Runtime.InteropServices;

namespace Sdcb.SimdRaw;

/// <summary>Owned native memory exposed as <see cref="Memory{T}"/>. Large buffers live here instead of the GC heap so
/// that <see cref="IDisposable.Dispose"/> returns them to the OS immediately, independent of when (or whether) the
/// application collects garbage. There is deliberately no finalizer (CA2015): a span does not keep its manager alive,
/// so finalizer-based freeing could release memory that is still being read. An undisposed buffer leaks.</summary>
internal sealed unsafe class NativeBuffer<T> : MemoryManager<T> where T : unmanaged
{
    private void* _ptr;
    private readonly int _length;

    public NativeBuffer(int length, bool zero)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _length = length;
        nuint size = (nuint)Math.Max((long)length * sizeof(T), 1);
        _ptr = zero ? NativeMemory.AllocZeroed(size) : NativeMemory.Alloc(size);
    }

    public int Length => _length;

    public override Span<T> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_ptr == null, this);
        return new Span<T>(_ptr, _length);
    }

    /// <summary>Native memory never moves; pinning is a no-op.</summary>
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_ptr == null, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)elementIndex, (uint)_length);
        return new MemoryHandle((T*)_ptr + elementIndex);
    }

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        void* p = _ptr;
        if (p == null) return;
        _ptr = null;
        NativeMemory.Free(p);
    }
}
