using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PcTidy.App.Pages.Now;

/// <summary>One reading of the Now card: loads in percent (0-100); GPU values null when they can't be read.</summary>
public sealed record NowSample(double Cpu, double? Gpu, double Ram, int? GpuTemperatureC = null);

/// <summary>Where the Now card's numbers come from. Read-only: performance counters, memory status, nvidia-smi.</summary>
public interface INowSource
{
    /// <summary>
    /// One reading, or null when there is nothing to show yet (counters only just primed). Called about once a second
    /// from a background thread, never on two threads at once.
    /// </summary>
    NowSample? Read();
}

/// <summary>
/// When the Now card samples (layout spec §4.3): only while it is switched on, unfolded, Home is on screen and the
/// window isn't minimised. Otherwise nothing is read at all.
/// </summary>
public static class NowPolicy
{
    public static bool ShouldSample(bool enabled, bool collapsed, bool homeVisible, bool minimized)
        => enabled && !collapsed && homeVisible && !minimized;

    public const string LiveLabel = "Live, not recorded";
}

/// <summary>The last 60 seconds for the Now card's line (kept in memory only, never written anywhere).</summary>
public sealed class NowHistory
{
    public const int Seconds = 60;
    private readonly Queue<double> _cpu = new();
    private readonly Queue<double> _gpu = new();

    public IReadOnlyList<double> Cpu => _cpu.ToList();
    public IReadOnlyList<double> Gpu => _gpu.ToList();
    public bool HasGpu { get; private set; }

    public void Add(NowSample s)
    {
        Push(_cpu, s.Cpu);
        if (s.Gpu is { } g)
        {
            HasGpu = true;
            Push(_gpu, g);
        }
    }

    public void Clear()
    {
        _cpu.Clear();
        _gpu.Clear();
    }

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(Math.Clamp(double.IsFinite(v) ? v : 0, 0, 100));
        while (q.Count > Seconds) q.Dequeue();
    }
}

/// <summary>Samples an <see cref="INowSource"/> about once a second on a background timer while started.</summary>
public sealed class NowSampler : IDisposable
{
    private readonly INowSource _source;
    private readonly TimeSpan _interval;
    private readonly object _lock = new();
    private Timer? _timer;
    private int _busy;

    public NowSampler(INowSource source, TimeSpan? interval = null)
    {
        _source = source;
        _interval = interval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>Raised on a background thread after each reading.</summary>
    public event Action<NowSample>? Sampled;

    public bool IsRunning { get { lock (_lock) return _timer is not null; } }

    public void Start()
    {
        lock (_lock) _timer ??= new Timer(_ => SampleOnce(), null, TimeSpan.Zero, _interval);
    }

    public void Stop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>One reading now (the timer's work; tests call it directly). A slow reading is never overlapped.</summary>
    public void SampleOnce()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            NowSample? sample;
            try { sample = _source.Read(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return; } // a failed reading shows nothing new
            if (sample is not null) Sampled?.Invoke(sample); // null: primed, nothing to show yet
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose()
    {
        Stop();
        (_source as IDisposable)?.Dispose();
    }
}

/// <summary>Invented, gently moving numbers for demo mode and screenshots (never this PC's).</summary>
public sealed class DemoNowSource : INowSource
{
    private int _t;

    public NowSample Read()
    {
        int t = Interlocked.Increment(ref _t);
        double cpu = 18 + 9 * Math.Sin(t / 5.0) + 4 * Math.Sin(t / 1.7);
        double gpu = 34 + 14 * Math.Sin(t / 7.0 + 1) + 5 * Math.Sin(t / 2.3);
        return new NowSample(Math.Round(cpu, 1), Math.Round(gpu, 1), 46 + (t % 10) * 0.3, 52 + (t / 15) % 4);
    }
}

/// <summary>
/// This PC's live loads, read-only: CPU from the "Processor Information" counter (% Processor Utility, as Task
/// Manager shows it; "% Processor Time" where that one is missing), RAM from GlobalMemoryStatusEx, GPU from the
/// "GPU Engine" counters (3D engines, added up) when Windows has them, else hidden, and GPU temperature from
/// nvidia-smi only when it exists (<see cref="NvidiaTemperature"/>). No driver or sensor is installed, nothing is written.
/// A rate counter's first value after it is created is meaningless (0, or the load over a few microseconds), so new
/// counters are only primed: the very first reading is skipped, and after each GPU relist the previous GPU value is
/// shown until the new counters have had a whole tick.
/// </summary>
public sealed class WindowsNowSource : INowSource, IDisposable
{
    private PerformanceCounter? _cpu;
    private bool _cpuTried;
    private readonly List<PerformanceCounter> _gpu = new();
    private DateTime _gpuListedUtc = DateTime.MinValue;
    private bool _gpuMissing;
    private double? _lastGpu;
    private readonly NvidiaTemperature _temperature = new();

    /// <summary>The GPU engines come and go with the apps that use them: list them again every 10 seconds.</summary>
    private static readonly TimeSpan GpuRelist = TimeSpan.FromSeconds(10);

    /// <summary>One reading, or null on the very first call (the counters were only primed).</summary>
    public NowSample? Read()
    {
        bool first = !_cpuTried;
        double cpu = ReadCpu();
        double? gpu = ReadGpu();
        if (first) return null; // primed only: the next tick has real values
        return new NowSample(cpu, gpu, ReadRam(), _temperature.Latest());
    }

    private double ReadCpu()
    {
        if (!_cpuTried)
        {
            _cpuTried = true;
            _cpu = TryCounter("Processor Information", "% Processor Utility", "_Total") ?? TryCounter("Processor", "% Processor Time", "_Total");
            try { _cpu?.NextValue(); } // the first value is always 0: prime now, read on the next tick
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            return 0;
        }
        try { return Math.Clamp(_cpu?.NextValue() ?? 0, 0, 100); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return 0; }
    }

    private double? ReadGpu()
    {
        if (_gpuMissing) return null;
        try
        {
            if (DateTime.UtcNow - _gpuListedUtc > GpuRelist)
            {
                _gpuListedUtc = DateTime.UtcNow;
                foreach (var c in _gpu) c.Dispose();
                _gpu.Clear();
                if (!PerformanceCounterCategory.Exists("GPU Engine")) { _gpuMissing = true; return null; }
                var category = new PerformanceCounterCategory("GPU Engine");
                foreach (var name in category.GetInstanceNames().Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)))
                {
                    var c = TryCounter("GPU Engine", "Utilization Percentage", name);
                    if (c is null) continue;
                    try { c.NextValue(); } // prime only
                    catch (InvalidOperationException) { c.Dispose(); continue; }
                    _gpu.Add(c);
                }
                // The new counters are read on the next tick: reading them now (microseconds after priming) dips toward 0.
                return _lastGpu;
            }
            double sum = 0;
            foreach (var c in _gpu)
            {
                try { sum += c.NextValue(); }
                catch (InvalidOperationException) { /* that app closed: listed again soon */ }
            }
            _lastGpu = Math.Clamp(sum, 0, 100);
            return _lastGpu;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            _gpuMissing = true;
            return null;
        }
    }

    private static PerformanceCounter? TryCounter(string category, string counter, string instance)
    {
        try { return new PerformanceCounter(category, counter, instance, readOnly: true); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { return null; }
    }

    private static double ReadRam()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.dwMemoryLoad : 0;
    }

    public void Dispose()
    {
        _cpu?.Dispose();
        foreach (var c in _gpu) c.Dispose();
        _gpu.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

/// <summary>
/// GPU temperature from nvidia-smi, only when it is installed (never a sensor driver). The same fixed, read-only query
/// as PC care's (<c>--query-gpu=name,temperature.gpu,fan.speed --format=csv,noheader,nounits</c>), parsed with PC care's
/// own parser; at most every 15 seconds, in the background, so the Now card stays light.
/// Never two at once: a new query starts only after the last one has ended. nvidia-smi can hang (a wedged driver, a
/// sleeping GPU on a hybrid laptop), so a query that takes longer than 5 seconds is killed with its process tree, and
/// the Now card stops asking for the rest of the session (the temperature just isn't shown).
/// </summary>
public sealed class NvidiaTemperature
{
    /// <summary>How one query ended.</summary>
    public enum Outcome { Ok, Failed, TimedOut }

    /// <summary>Runs one query with a time limit; returns how it ended and the temperature.</summary>
    public delegate Task<(Outcome Outcome, int? TemperatureC)> QueryRunner(string exe, TimeSpan timeout);

    public static readonly TimeSpan DefaultEvery = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    private readonly string? _exe;
    private readonly QueryRunner _run;
    private readonly TimeSpan _every;
    private DateTime _lastUtc = DateTime.MinValue;
    private int _running;
    private int? _latest;
    private volatile bool _gaveUp;
    private int _started;

    public NvidiaTemperature() : this(FindExe()) { }

    /// <param name="exe">nvidia-smi's path, or null when it isn't installed.</param>
    /// <param name="run">Runs one query (tests pass a fake; the default starts nvidia-smi).</param>
    /// <param name="every">Shortest time between two queries.</param>
    public NvidiaTemperature(string? exe, QueryRunner? run = null, TimeSpan? every = null)
    {
        _exe = exe;
        _run = run ?? RunNvidiaSmiAsync;
        _every = every ?? DefaultEvery;
    }

    /// <summary>The same places PC care looks: System32, then the NVSMI folder.</summary>
    public static string? FindExe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public bool IsAvailable => _exe is not null && !_gaveUp;

    /// <summary>True after a query hung: no more queries this session.</summary>
    public bool GaveUp => _gaveUp;

    /// <summary>Queries started so far.</summary>
    public int Started => Volatile.Read(ref _started);

    /// <summary>The last reading (null until one arrived, without nvidia-smi, or after a hang); starts a new query when due and none is going.</summary>
    public int? Latest()
    {
        if (_exe is null || _gaveUp) return null;
        if (DateTime.UtcNow - _lastUtc >= _every && Interlocked.Exchange(ref _running, 1) == 0)
        {
            _lastUtc = DateTime.UtcNow;
            Interlocked.Increment(ref _started);
            string exe = _exe;
            _ = Task.Run(async () =>
            {
                try
                {
                    var (outcome, temperature) = await _run(exe, QueryTimeout).ConfigureAwait(false);
                    if (outcome == Outcome.TimedOut) _gaveUp = true;
                    else if (outcome == Outcome.Ok && temperature is { } t) _latest = t;
                }
                catch (Exception) { /* a failed query shows nothing new */ }
                finally { Volatile.Write(ref _running, 0); }
            });
        }
        return _latest;
    }

    /// <summary>Starts nvidia-smi with the fixed query; one still running after <paramref name="timeout"/> is killed with its process tree.</summary>
    private static async Task<(Outcome Outcome, int? TemperatureC)> RunNvidiaSmiAsync(string exe, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        psi.ArgumentList.Add("--query-gpu=name,temperature.gpu,fan.speed");
        psi.ArgumentList.Add("--format=csv,noheader,nounits");
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { return (Outcome.Failed, null); }
        if (p is null) return (Outcome.Failed, null);
        using (p)
        {
            using var limit = new CancellationTokenSource(timeout);
            try
            {
                p.StandardInput.Close();
                var text = p.StandardOutput.ReadToEndAsync(limit.Token);
                _ = p.StandardError.ReadToEndAsync(limit.Token);
                await p.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                if (p.ExitCode != 0) return (Outcome.Failed, null);
                return (Outcome.Ok, PcTidy.Features.PcCare.Parsing.NvidiaSmiCsv.Parse(await text.ConfigureAwait(false)).FirstOrDefault()?.TemperatureC);
            }
            catch (OperationCanceledException)
            {
                Kill(p);
                return (Outcome.TimedOut, null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                Kill(p);
                return (Outcome.Failed, null);
            }
        }
    }

    /// <summary>Ends a query that is still running (and anything it started), so no nvidia-smi is left behind.</summary>
    private static void Kill(Process p)
    {
        try
        {
            if (p.HasExited) return;
            p.Kill(entireProcessTree: true);
            p.WaitForExit(2000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* already gone */ }
    }
}
