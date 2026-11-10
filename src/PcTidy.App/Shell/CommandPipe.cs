using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PcTidy.App.Shell;

/// <summary>One request a second PC Tidy start hands to the running one (Explorer's right-click entries, the tray).</summary>
public sealed record ShellCommand(string Verb, string? Path)
{
    public const string WhatIs = "whatis";
    public const string Tidy = "tidy";
    public const string Show = "show";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal) { WhatIs, Tidy, Show };

    /// <summary>Only known verbs with a full path (or none, for Show) are accepted; anything else is dropped.</summary>
    public static ShellCommand? Validate(ShellCommand? c)
    {
        if (c is null || !Known.Contains(c.Verb)) return null;
        if (c.Verb == Show) return new ShellCommand(Show, null);
        return c.Path is { } p && AppArgs.ExplorerPath(p) is { } full ? new ShellCommand(c.Verb, full) : null;
    }

    public static ShellCommand? FromArgs(AppArgs args)
        => args.WhatIs is { } w ? new ShellCommand(WhatIs, w) : args.TidyFolder is { } t ? new ShellCommand(Tidy, t) : null;
}

/// <summary>
/// A per-user, per-session named pipe: Explorer starts a new PcTidy.exe for every right-click (and one per selected
/// item); that copy forwards its request here and exits, so there is only ever one PC Tidy. Only the current user can
/// connect (PipeOptions.CurrentUserOnly), messages are one small JSON line, and the request is validated again here.
/// </summary>
public sealed class CommandPipe : IDisposable
{
    private const int MaxMessage = 64 * 1024;
    private readonly string _name;
    private readonly Action<ShellCommand> _onCommand;
    private readonly CancellationTokenSource _stop = new();

    private CommandPipe(string name, Action<ShellCommand> onCommand)
    {
        _name = name;
        _onCommand = onCommand;
        _ = Task.Run(() => ListenAsync(_stop.Token));
    }

    /// <summary>The pipe name for a mode ("PcTidy.Main", "PcTidy.Demo", "PcTidy.Sandbox") in this Windows session.</summary>
    public static string NameFor(string instanceName) => $"{instanceName}.Commands.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    /// <summary>Starts listening (the first PC Tidy). Commands arrive on a thread-pool thread.</summary>
    public static CommandPipe Listen(string instanceName, Action<ShellCommand> onCommand) => new(NameFor(instanceName), onCommand);

    /// <summary>Hands a command to the running PC Tidy. False when none answered within the timeout.</summary>
    public static bool TrySend(string instanceName, ShellCommand command, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", NameFor(instanceName), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command) + "\n");
            if (bytes.Length > MaxMessage) return false;
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { return false; }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                if (await ReadLineAsync(server, ct).ConfigureAwait(false) is { } line
                    && ShellCommand.Validate(Parse(line)) is { } command)
                    _onCommand(command);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { /* a client that connected and sent nothing in 5 s */ }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A broken or hostile client never stops the listener; wait a moment so a failure can't spin.
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
            catch (Exception ex) { CrashHandler.Write(ex, "Command pipe", terminating: false); }
        }
    }

    private static ShellCommand? Parse(string line)
    {
        try { return JsonSerializer.Deserialize<ShellCommand>(line); }
        catch (JsonException) { return null; }
    }

    private static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        int n;
        while ((n = await s.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > MaxMessage) return null;
            if (Array.IndexOf(buffer, (byte)'\n', 0, n) >= 0) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray()).Split('\n')[0];
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
