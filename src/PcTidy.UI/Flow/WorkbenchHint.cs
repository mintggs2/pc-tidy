using PcTidy.Core;

namespace PcTidy.UI.Flow;

/// <summary>
/// The 3.2 Workbench layout's one-time hint ("Tick groups on the left…"), shown on the first results page until "Got it".
/// Stored in the settings under "workbench" (saved with the other settings at the shell's next save point).
/// </summary>
public static class WorkbenchHint
{
    public const string FeatureId = "workbench";

    public sealed class State
    {
        public bool HintSeen { get; set; }
    }

    public static void Wire(ResultsPanel results, IServiceProvider services)
    {
        if (services.GetService(typeof(AppSettings)) is not AppSettings settings) return;
        bool seen;
        try { seen = settings.GetFeature<State>(FeatureId)?.HintSeen == true; }
        catch (System.Text.Json.JsonException) { seen = false; }
        results.ShowHint = !seen;
        Dismissed += () => results.ShowHint = false; // "Got it" on one page hides it on the others too
        results.HintDismissed += () =>
        {
            settings.SetFeature(FeatureId, new State { HintSeen = true });
            Dismissed?.Invoke();
        };
    }

    private static event Action? Dismissed;
}
