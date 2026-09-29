using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Sdcb.SimdRaw.Harness.Engines;
using Sdcb.SimdRaw.Harness.Reporting;

namespace Sdcb.SimdRaw.Harness.Benchmark;

internal static class EnvironmentProbe
{
    public static EnvironmentInfo Collect() => new()
    {
        Rid = EngineManifest.CurrentRid,
        Os = RuntimeInformation.OSDescription,
        Cpu = CpuName(),
        LogicalCores = Environment.ProcessorCount,
        Runtime = RuntimeInformation.FrameworkDescription,
        EffectiveIsa = EffectiveIsa(),
        VectorBits = Vector<byte>.Count * 8,
        Isa = new Dictionary<string, bool>
        {
            ["avx512f"] = Avx512F.IsSupported,
            ["avx2"] = Avx2.IsSupported,
            ["avx"] = Avx.IsSupported,
            ["sse4.2"] = Sse42.IsSupported,
            ["advsimd"] = AdvSimd.IsSupported,
            ["vectorHardwareAccelerated"] = Vector.IsHardwareAccelerated,
        },
        DotnetEnv = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(e => (Key: (string)e.Key, Value: e.Value as string ?? ""))
            .Where(e => e.Key.StartsWith("DOTNET_Enable", StringComparison.OrdinalIgnoreCase)
                     || e.Key.StartsWith("DOTNET_Tiered", StringComparison.OrdinalIgnoreCase)
                     || e.Key.Equals("DOTNET_ReadyToRun", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .ToDictionary(e => e.Key, e => e.Value),
    };

    /// <summary>Widest SIMD tier the runtime will actually use (honours DOTNET_EnableAVX512F etc.).</summary>
    public static string EffectiveIsa() =>
        Avx512F.IsSupported ? "avx512"
        : Avx2.IsSupported ? "avx2"
        : AdvSimd.IsSupported ? "advsimd"
        : Vector.IsHardwareAccelerated ? "vector"
        : "scalar";

    private static string CpuName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) is string s
                    ? s.Trim()
                    : "unknown";
            }
            if (OperatingSystem.IsLinux())
            {
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.Ordinal)) return line[(line.IndexOf(':') + 1)..].Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return "unknown";
    }
}
