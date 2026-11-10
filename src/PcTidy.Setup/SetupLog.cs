namespace PcTidy.Setup;

/// <summary>%TEMP%\PC Tidy Setup\setup-yyyyMMdd-HHmmss.log. Never throws.</summary>
public static class SetupLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string? FilePath => _path;

    public static void Start(string mode)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "PC Tidy Setup");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, $"{mode}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            Info($"PC Tidy Setup {typeof(SetupLog).Assembly.GetName().Version} ({mode}) on {Environment.OSVersion.VersionString}");
        }
        catch { _path = null; }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {ex}");

    public static void Result(Install.SetupResult r)
    {
        Write(r.Ok ? "INFO " : "ERROR", r.Message);
        foreach (var n in r.Notes) Write("     ", n);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                if (_path is not null) File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging never breaks setup */ }
    }
}
