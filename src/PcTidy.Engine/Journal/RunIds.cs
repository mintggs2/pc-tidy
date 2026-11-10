namespace PcTidy.Engine;

/// <summary>Run ids become folder names (journal, quarantine): only plain names are accepted.</summary>
internal static class RunIds
{
    public static string Validate(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || runId.Contains("..")
            || runId.Trim() != runId || runId.EndsWith('.'))
            throw new ArgumentException("Invalid run id.", nameof(runId));
        return runId;
    }
}
