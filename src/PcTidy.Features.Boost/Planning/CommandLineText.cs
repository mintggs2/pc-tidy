namespace PcTidy.Features.Boost.Planning;

/// <summary>Splits a recorded command line into "the exe" and "its arguments", and keeps secrets out of PC Tidy's data.</summary>
internal static class CommandLineText
{
    /// <summary>Words that suggest a command line carries a secret; such arguments are never stored.</summary>
    private static readonly string[] SensitiveWords =
        { "token", "password", "passwd", "pwd=", "secret", "apikey", "api-key", "api_key", "auth", "session=", "cookie", "bearer" };

    /// <summary>Everything after the exe ("C:\x y\a.exe" --flag → "--flag"). Null when there are no arguments.</summary>
    public static string? ArgumentsAfterExe(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var s = commandLine.TrimStart();
        int end;
        if (s.StartsWith('"'))
        {
            var close = s.IndexOf('"', 1);
            end = close < 0 ? s.Length : close + 1;
        }
        else
        {
            end = s.IndexOfAny(new[] { ' ', '\t' });
            if (end < 0) end = s.Length;
        }
        var rest = s[end..].Trim();
        return rest.Length == 0 ? null : rest;
    }

    public static bool LooksSensitive(string arguments)
        => SensitiveWords.Any(w => arguments.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Arguments safe to keep for reopening the app, or null (reopen without arguments).</summary>
    public static string? SafeArguments(string? commandLine)
    {
        var args = ArgumentsAfterExe(commandLine);
        if (args is null || args.Length > 2000 || args.Any(char.IsControl) || LooksSensitive(args)) return null;
        return args;
    }
}
