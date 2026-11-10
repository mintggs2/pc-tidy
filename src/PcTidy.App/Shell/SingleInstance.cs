namespace PcTidy.App.Shell;

/// <summary>
/// One PC Tidy per Windows session. A second start either wakes the first one and exits, or (Explorer's right-click
/// entries) hands its request over through <see cref="CommandPipe"/> and exits.
/// Demo and sandbox (practice) modes use their own names so they can run next to the real app.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle? _wait;

    private SingleInstance(string name, Mutex mutex, EventWaitHandle activate, Action onActivate)
    {
        Name = name;
        _mutex = mutex;
        _activate = activate;
        _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>"PcTidy.Main", "PcTidy.Demo" or "PcTidy.Sandbox" (also names the command pipe).</summary>
    public string Name { get; }

    public static string NameFor(bool demo, bool sandbox) => sandbox ? "PcTidy.Sandbox" : demo ? "PcTidy.Demo" : "PcTidy.Main";

    /// <summary>
    /// Returns null when another instance already owns the session. Then, unless <paramref name="wakeExisting"/> is
    /// false (the caller forwards a command instead), the running copy is brought forward.
    /// </summary>
    public static SingleInstance? TryAcquire(bool demo, Action onActivate, bool sandbox = false, bool wakeExisting = true)
    {
        var name = NameFor(demo, sandbox);
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + name, out bool created);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + name + ".Activate");
        if (!created)
        {
            if (wakeExisting) activate.Set();
            activate.Dispose();
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(name, mutex, activate, onActivate);
    }

    /// <summary>Wakes the running copy (used when forwarding a command failed).</summary>
    public static void Wake(bool demo, bool sandbox)
    {
        try
        {
            using var activate = EventWaitHandle.OpenExisting(@"Local\" + NameFor(demo, sandbox) + ".Activate");
            activate.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException) { }
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _activate.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
