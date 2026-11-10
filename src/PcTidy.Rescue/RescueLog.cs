using PcTidy.Core;

namespace PcTidy.Rescue;

/// <summary>
/// Plain-text log at <c>&lt;data folder&gt;\logs\rescue-yyyyMMdd-HHmmss.log</c> (or %TEMP%\PC Tidy Rescue\logs if the
/// data folder can't be written). Logging never throws: Rescue must keep working even when logging fails.
/// </summary>
public static class RescueLog
{
    private static readonly object Gate = new();
    private static string? _path;

    /// <summary>Current log file (null until <see cref="Use"/> succeeded).</summary>
    public static string? FilePath { get { lock (Gate) return _path; } }

    /// <summary>Starts (or switches to) a log file in this data folder.</summary>
    public static void Use(string? dataRoot)
    {
        var name = $"rescue-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        foreach (var dir in Candidates(dataRoot))
        {
            try
            {
                Directory.CreateDirectory(dir);
                var p = Path.Combine(dir, name);
                File.AppendAllText(p, "");
                lock (Gate) _path = p;
                Info($"PC Tidy Rescue {typeof(RescueLog).Assembly.GetName().Version} on {Environment.OSVersion.VersionString}; data folder: {dataRoot ?? "(none)"}");
                return;
            }
            catch { /* try the next place */ }
        }
    }

    private static IEnumerable<string> Candidates(string? dataRoot)
    {
        if (!string.IsNullOrWhiteSpace(dataRoot)) yield return DataPaths.Logs(dataRoot);
        yield return Path.Combine(Path.GetTempPath(), "PC Tidy Rescue", "logs");
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                if (_path is null) return;
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch { /* never let logging break a rescue */ }
    }
}
