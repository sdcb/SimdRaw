using System.Numerics;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdRaw.Decoders;

/// <summary>Kernel family picked at runtime. Every kernel keeps a scalar path for its tail, which is also what runs
/// when hardware intrinsics are disabled (<c>DOTNET_EnableHWIntrinsic=0</c>).</summary>
internal enum KernelIsa
{
    Scalar,
    Vector,
    Avx2,
}

internal static class Kernels
{
    public static KernelIsa Best { get; } =
        Avx2.IsSupported ? KernelIsa.Avx2
        : Vector.IsHardwareAccelerated ? KernelIsa.Vector
        : KernelIsa.Scalar;
}
