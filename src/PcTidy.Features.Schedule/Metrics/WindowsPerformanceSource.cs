using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PcTidy.Features.Schedule.Metrics;

/// <summary>Live numbers from Windows APIs (GetSystemTimes, GlobalMemoryStatusEx, the process list). Read-only.</summary>
public sealed class WindowsPerformanceSource : IPerformanceSource
{
    private (ulong Idle, ulong Kernel, ulong User)? _last;

    public int? ProcessCount()
    {
        var processes = Process.GetProcesses();
        try { return processes.Length; }
        finally { foreach (var p in processes) p.Dispose(); }
    }

    public double? AvailableMemoryMB()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.AvailPhys / (1024.0 * 1024.0) : null;
    }

    public bool MeasuresCpu => true;

    public double? CpuBusyPercentSinceLastCall()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var now = (Idle: idle.Value, Kernel: kernel.Value, User: user.Value);
        var previous = _last;
        _last = now;
        if (previous is not { } p) return null;
        // Kernel time includes idle time.
        double total = (now.Kernel - p.Kernel) + (now.User - p.User);
        if (total <= 0) return null;
        double busy = total - (now.Idle - p.Idle);
        return Math.Clamp(busy / total * 100.0, 0, 100);
    }

    public TimeSpan? Uptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

/// <summary>
/// Practice PC: the process count comes from the simulated system info; memory, CPU and uptime aren't simulated, so
/// they're left out (never the real PC's numbers mixed into a practice report).
/// </summary>
public sealed class SandboxPerformanceSource : IPerformanceSource
{
    private readonly ISystemInfo _system;
    public SandboxPerformanceSource(ISystemInfo system) => _system = system;
    public int? ProcessCount() => _system.RunningProcesses().Count;
    public double? AvailableMemoryMB() => null;
    public bool MeasuresCpu => false;
    public double? CpuBusyPercentSinceLastCall() => null;
    public TimeSpan? Uptime() => null;
}
