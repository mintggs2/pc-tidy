using System.Windows;
using System.Windows.Automation;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Controls;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;

namespace PcTidy.UI.Flow.Resume;

/// <summary>The two calm notes resumable scans show: "Resume your last scan?" and "Windows Update is waiting to restart".</summary>
public static class ResumeNotes
{
    /// <summary>Feature id on the "Open Windows Update" page link (an OpenExternal plan item; nothing on the PC changes).</summary>
    public const string FeatureId = "scan.resume";

    /// <summary>"Your last scan stopped after 7 of 12 sections (12:41)." with Resume and Start over.</summary>
    public static FrameworkElement ResumeOffer(ScanCheckpoint cp, Action resume, Action startOver)
    {
        var bar = new InfoBar
        {
            Title = ResumeText.Stopped(cp),
            Message = ResumeText.Limits,
            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 12),
        };
        AutomationProperties.SetName(bar, ResumeText.Stopped(cp) + " " + ResumeText.Limits);
        var go = new Button { Content = "Resume", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.Play24) };
        AutomationProperties.SetName(go, $"Resume the last scan: {cp.ProgressText} are done");
        go.Click += (_, _) => resume();
        var over = new Button { Content = "Start over", Icon = new SymbolIcon(SymbolRegular.ArrowCounterclockwise24) };
        AutomationProperties.SetName(over, "Start the scan over from the beginning");
        over.Click += (_, _) => startOver();
        var panel = InfoBars.WithActions(bar, go, over);
        panel.Tag = "resume-offer";
        return panel;
    }

    /// <summary>The restart note, with a link to Windows Update in Settings (opened only; PC Tidy never changes update settings).</summary>
    public static FrameworkElement RestartWarning(Action? openWindowsUpdate)
    {
        var bar = new InfoBar
        {
            Message = ResumeText.RestartMessage,
            IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 12),
        };
        AutomationProperties.SetName(bar, ResumeText.RestartMessage);
        FrameworkElement panel;
        if (openWindowsUpdate is null) panel = bar;
        else
        {
            var open = new Button { Content = "Open Windows Update", Icon = new SymbolIcon(SymbolRegular.Open24) };
            AutomationProperties.SetName(open, "Open Windows Update in Windows Settings");
            open.Click += (_, _) => openWindowsUpdate();
            panel = InfoBars.WithActions(bar, open);
        }
        panel.Tag = "restart-warning";
        return panel;
    }

    /// <summary>"Open Windows Update" as a plan the shell previews (OpenExternal, a fixed ms-settings address).</summary>
    public static ActionPlan OpenWindowsUpdatePlan() => new()
    {
        FeatureId = FeatureId,
        Title = "Open Windows Update",
        Items = new[]
        {
            new PlanItem
            {
                Kind = ActionKind.OpenExternal,
                FeatureId = FeatureId,
                Description = "Open Windows Update in Windows Settings (nothing is changed)",
                Args = new Dictionary<string, string> { ["target"] = ResumeText.WindowsUpdateSettings },
            },
        },
    };

    /// <summary>Opens Windows Update through the flow host's usual preview (no-op without a host).</summary>
    public static async Task OpenWindowsUpdateAsync(IFlowHost? host)
    {
        if (host is null) return;
        await host.ReviewAndRunAsync(OpenWindowsUpdatePlan(), CancellationToken.None).ConfigureAwait(true);
    }
}
