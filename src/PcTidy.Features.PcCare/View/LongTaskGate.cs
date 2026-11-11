using System.Windows.Threading;
using PcTidy.UI.Flow;

namespace PcTidy.Features.PcCare.View;

/// <summary>
/// "One long task at a time" on the PC care page (layout spec §4.6 item 3): a scan, a run, a fix or the lag check.
/// While one runs, the others' buttons are disabled and say why. UI thread only.
/// <para>
/// Folded into the app-wide <see cref="RunGate"/> (UI review M1): the page is also busy while a run, undo or resume
/// started on another page holds <see cref="Shared"/>, and the lag check holds that gate while it sends test data, so
/// other pages wait too. Scans only read, so they never wait for the shared gate (the same rule as RunGate).
/// </para>
/// </summary>
public sealed class LongTaskGate
{
    /// <summary>The generic reason, used while another page's task holds the shared gate.</summary>
    public const string BusyText = RunGate.BusyReason;

    private readonly Dispatcher _dispatcher;

    /// <param name="shared">The app-wide gate (default <see cref="RunGate.Shared"/>; tests pass their own).</param>
    public LongTaskGate(RunGate? shared = null)
    {
        Shared = shared ?? RunGate.Shared;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Shared.Changed += OnSharedChanged;
    }

    public RunGate Shared { get; }

    /// <summary>This page's own task ("scan", "run", "fix", "lagcheck"), or null.</summary>
    public string? Current { get; private set; }

    /// <summary>This page's own task is going (scans check only this).</summary>
    public bool IsLocalBusy => Current is not null;

    /// <summary>This page's task or another page's run is going: Run, Preview and Simulate buttons wait.</summary>
    public bool IsBusy => Current is not null || Shared.IsBusy;

    public event Action? Changed;

    /// <summary>The busy reason, naming this page's task when it's ours ("Available when the scan finishes", UI review L3).</summary>
    public string Reason => Current is { } task ? $"Available when the {Label(task)} finishes" : BusyText;

    /// <param name="waitForShared">False for scans: reading never waits for another page's run.</param>
    public bool TryEnter(string task, bool waitForShared = true)
    {
        if (IsLocalBusy || waitForShared && Shared.IsBusy) return false;
        Current = task;
        Changed?.Invoke();
        return true;
    }

    public void Exit(string task)
    {
        if (Current != task) return;
        Current = null;
        Changed?.Invoke();
    }

    internal static string Label(string task) => task switch
    {
        "scan" => "scan",
        "run" => "run",
        "fix" => "fix",
        "lagcheck" => "lag check",
        _ => "current task",
    };

    private void OnSharedChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess()) Changed?.Invoke();
        else _dispatcher.BeginInvoke(() => Changed?.Invoke());
    }
}
