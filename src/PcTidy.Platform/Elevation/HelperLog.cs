namespace PcTidy.Platform;

/// <summary>
/// One-line notes from the admin helper about why it refused to start, in &lt;DataRoot&gt;\logs\helper.log (for the real
/// helper that is its admin-only store). Never throws: a missing log never changes what the helper does.
/// </summary>
internal static class HelperLog
{
    public static void Write(Func<HandlerContext> contextFactory, string message)
    {
        try
        {
            var logs = DataPaths.Logs(contextFactory().Locations.DataRoot);
            Directory.CreateDirectory(logs);
            File.AppendAllText(Path.Combine(logs, "helper.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing more to do: the exit code still says why.
        }
    }
}
