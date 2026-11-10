using System.Globalization;
using PcTidy.Core;

namespace PcTidy.Engine;

/// <summary>
/// A small plain-text log of engine decisions the user never sees in a step message (e.g. why a drive's quarantine
/// folder wasn't used). <c>&lt;DataRoot&gt;\logs\engine.log</c>. Best effort: logging never fails an operation.
/// </summary>
internal static class EngineLog
{
    private static readonly object Gate = new();

    public static void Write(string dataRoot, string message)
    {
        try
        {
            var dir = DataPaths.Logs(dataRoot);
            Directory.CreateDirectory(dir);
            var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z  {message}{Environment.NewLine}");
            lock (Gate) File.AppendAllText(Path.Combine(dir, "engine.log"), line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never let a log line break a run.
        }
    }
}
