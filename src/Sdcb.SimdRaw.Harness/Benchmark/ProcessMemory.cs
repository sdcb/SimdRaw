using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sdcb.SimdRaw.Harness.Benchmark;

/// <summary>Allocation-free working-set probes, so sampling does not disturb GC measurements.</summary>
internal static partial class ProcessMemory
{
    private static readonly long s_pageSize = Environment.SystemPageSize;
    private static SafeFileHandle? s_statm;
    private static readonly byte[] s_statmBuffer = new byte[256];
    private static readonly Lock s_statmLock = new();

    /// <summary>How per-file peaks are obtained on this OS.</summary>
    public static string PeakMethod { get; private set; } = OperatingSystem.IsLinux()
        ? "linux: VmHWM reset per file via /proc/self/clear_refs"
        : "OS peak working set (monotonic) + 1 ms sampling";

    public static long CurrentWorkingSet()
    {
        if (OperatingSystem.IsWindows()) return QueryWindows().WorkingSetSize;
        if (OperatingSystem.IsLinux()) return ReadStatmResident();
        return Environment.WorkingSet;
    }

    public static long PeakWorkingSet()
    {
        if (OperatingSystem.IsWindows()) return QueryWindows().PeakWorkingSetSize;
        if (OperatingSystem.IsLinux()) return ReadVmHwm() ?? CurrentWorkingSet();
        using System.Diagnostics.Process p = System.Diagnostics.Process.GetCurrentProcess();
        return p.PeakWorkingSet64;
    }

    /// <summary>Resets the OS peak to the current working set where supported (Linux).</summary>
    public static bool TryResetPeak()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            File.WriteAllText("/proc/self/clear_refs", "5");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PeakMethod = "linux: VmHWM (monotonic, clear_refs unavailable) + 1 ms sampling";
            return false;
        }
    }

    private static long ReadStatmResident()
    {
        lock (s_statmLock)
        {
            try
            {
                s_statm ??= File.OpenHandle("/proc/self/statm");
                int n = RandomAccess.Read(s_statm, s_statmBuffer, 0);
                // "size resident shared text lib data dt" in pages
                ReadOnlySpan<byte> span = s_statmBuffer.AsSpan(0, n);
                int sp = span.IndexOf((byte)' ');
                span = span[(sp + 1)..];
                long pages = 0;
                foreach (byte b in span)
                {
                    if (b is < (byte)'0' or > (byte)'9') break;
                    pages = pages * 10 + (b - '0');
                }
                return pages * s_pageSize;
            }
            catch (IOException)
            {
                return Environment.WorkingSet;
            }
        }
    }

    private static long? ReadVmHwm()
    {
        foreach (string line in File.ReadLines("/proc/self/status"))
        {
            if (!line.StartsWith("VmHWM:", StringComparison.Ordinal)) continue;
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return long.Parse(parts[1]) * 1024;
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Cb;
        public uint PageFaultCount;
        public nint PeakWorkingSetSize;
        public nint WorkingSetSize;
        public nint QuotaPeakPagedPoolUsage;
        public nint QuotaPagedPoolUsage;
        public nint QuotaPeakNonPagedPoolUsage;
        public nint QuotaNonPagedPoolUsage;
        public nint PagefileUsage;
        public nint PeakPagefileUsage;
    }

    private static (long WorkingSetSize, long PeakWorkingSetSize) QueryWindows()
    {
        ProcessMemoryCounters c = default;
        c.Cb = (uint)Marshal.SizeOf<ProcessMemoryCounters>();
        if (!K32GetProcessMemoryInfo(-1, ref c, c.Cb)) return (Environment.WorkingSet, Environment.WorkingSet);
        return (c.WorkingSetSize, c.PeakWorkingSetSize);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool K32GetProcessMemoryInfo(nint process, ref ProcessMemoryCounters counters, uint cb);
}

/// <summary>Background sampler recording the max working set between <see cref="Begin"/> and <see cref="End"/>.</summary>
internal sealed class WorkingSetSampler : IDisposable
{
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile bool _active;
    private long _max;

    public WorkingSetSampler()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "ws-sampler", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Begin()
    {
        Interlocked.Exchange(ref _max, ProcessMemory.CurrentWorkingSet());
        _active = true;
    }

    public long End()
    {
        _active = false;
        Sample();
        return Interlocked.Read(ref _max);
    }

    private void Loop()
    {
        while (!_stop)
        {
            if (_active) Sample();
            Thread.Sleep(1);
        }
    }

    private void Sample()
    {
        long ws = ProcessMemory.CurrentWorkingSet();
        long cur;
        while (ws > (cur = Interlocked.Read(ref _max)) && Interlocked.CompareExchange(ref _max, ws, cur) != cur) { }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
    }
}
