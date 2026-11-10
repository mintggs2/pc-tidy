using PcTidy.App.Shell;
using PcTidy.Core;
using PcTidy.Features.Learning.Advisor;

namespace PcTidy.App.Composition;

/// <summary>
/// The learning section's quiet background chores while PC Tidy is open: its daily pass (reads finished runs, one
/// free-space reading, app launch counts; writes only PC Tidy's own learning file) shortly after start and then about
/// once a day, and an idle sample every 30 minutes. Nothing runs while learning is switched off in Settings.
/// </summary>
public sealed class LearningChores : IAsyncDisposable
{
    private static readonly TimeSpan FirstPassDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PassEvery = TimeSpan.FromHours(23);

    private readonly IServiceProvider _services;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public LearningChores(IServiceProvider services) => _services = services;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        DateTime lastPass = DateTime.MinValue;
        try
        {
            await Task.Delay(FirstPassDelay, ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Tick);
            do
            {
                if (Advisor() is not { } advisor) continue;
                try
                {
                    advisor.SampleIdle();
                    if (DateTime.UtcNow - lastPass >= PassEvery)
                    {
                        await advisor.ObserveAsync(ct).ConfigureAwait(false);
                        lastPass = DateTime.UtcNow;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { CrashHandler.Write(ex, "Learning pass", terminating: false); }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { /* PC Tidy is closing */ }
    }

    private ILearningAdvisor? Advisor()
        => _services.GetService(typeof(AppSettings)) is AppSettings { LearningEnabled: true }
            ? _services.GetService(typeof(ILearningAdvisor)) as ILearningAdvisor
            : null;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
