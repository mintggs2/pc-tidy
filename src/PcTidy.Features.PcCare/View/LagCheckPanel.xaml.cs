using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PcTidy.Features.PcCare.Analysis;
using PcTidy.Features.PcCare.Source;

namespace PcTidy.Features.PcCare.View;

/// <summary>
/// PC care > Lag check (build list B2). Opt-in: nothing is sent until Run test is pressed, and the button says exactly
/// what it sends and to which host first. The test runs off the UI thread with progress and Stop. Read-only: it shows
/// numbers and tips, and never builds a plan.
/// </summary>
public partial class LagCheckPanel : UserControl
{
    private const string TaskName = "lagcheck";

    private LongTaskGate _gate = new();
    private LagCheckSettings _settings = LagCheckSettings.Default;
    private Func<LagCheckEndpoint, LagCheckRunner>? _runnerFor;
    private bool _sandbox;
    private CancellationTokenSource? _cts;

    public LagCheckPanel()
    {
        InitializeComponent();
        BandsText.Text = LagCheckMath.BandsText;
        BuildGuidance();
    }

    /// <param name="runnerFor">Builds the runner for an endpoint (tests: a fake transport). Default: HTTPS.</param>
    /// <param name="sandbox">In a sandbox the test never runs (it would send real traffic).</param>
    internal void Init(LongTaskGate gate, LagCheckSettings settings, bool sandbox, Func<LagCheckEndpoint, LagCheckRunner>? runnerFor = null)
    {
        _gate.Changed -= UpdateButtons;
        _gate = gate;
        _gate.Changed += UpdateButtons;
        _settings = settings;
        _sandbox = sandbox;
        _runnerFor = runnerFor ?? (e => new LagCheckRunner(new HttpLagCheckTransport(e), e.Host));

        DisabledNote.IsOpen = false;
        if (_sandbox)
        {
            DisabledNote.Title = "Not in the sandbox";
            DisabledNote.Message = "The lag check sends real network traffic, so it never runs in a sandbox.";
            DisabledNote.IsOpen = true;
        }
        else if (!_settings.Enabled)
        {
            DisabledNote.Message = "PC Tidy needs a test server whose terms allow this kind of use. The test stays off until one is chosen. Nothing is sent.";
            DisabledNote.IsOpen = true;
        }
        SendsText.Text = _settings.Endpoint is { IsValid: true } e
            ? LagCheckPlan.Default.Describe(e) + " Server terms: " + e.TermsUrl
            : "When it's on, the test downloads and uploads test data for about 20 seconds. Results stay on this PC.";
        UpdateButtons();
    }

    private bool CanRun => !_sandbox && _settings.Enabled && _runnerFor is not null;

    private void UpdateButtons()
    {
        bool mine = _gate.Current == TaskName;
        RunButton.IsEnabled = CanRun && !_gate.IsBusy;
        RunButton.Visibility = mine ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
        ProgressArea.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = _gate.Reason;
        BusyText.Visibility = CanRun && _gate.IsBusy && !mine ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(RunButton, BusyText.Visibility == Visibility.Visible ? _gate.Reason : SendsText.Text);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnRun(object sender, RoutedEventArgs e) => _ = RunAsync();

    /// <summary>Runs the test (internal for UI tests with a fake runner).</summary>
    internal async Task RunAsync()
    {
        if (!CanRun || _settings.Endpoint is not { } endpoint || !_gate.TryEnter(TaskName)) return;
        // The lag check sends test data for about 20 seconds: it holds the app-wide gate too, so other pages wait.
        using var shared = _gate.Shared.Begin("Lag check");
        _cts = new CancellationTokenSource();
        Progress.Value = 0;
        ProgressText.Text = "Starting…";
        ResultNote.Visibility = Visibility.Collapsed;
        var progress = new Progress<LagCheckProgress>(p =>
        {
            Progress.Value = p.Fraction;
            ProgressText.Text = p.Message;
        });
        var token = _cts.Token;
        try
        {
            using var runner = _runnerFor!(endpoint);
            var result = await Task.Run(() => runner.RunAsync(progress, token), token).ConfigureAwait(true);
            Show(result);
        }
        catch (OperationCanceledException)
        {
            Note("Stopped. Nothing was measured this time.");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidOperationException or ArgumentException)
        {
            Note("The lag check didn't finish: " + ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _gate.Exit(TaskName);
        }
    }

    internal void Show(LagCheckResult r)
    {
        var c = CultureInfo.CurrentCulture;
        string Ms(double v) => v.ToString("0", c) + " ms";

        if (r.Idle is { } idle)
        {
            IdleValue.Text = Ms(idle.MedianMs);
            IdleDetail.Text = $"Jitter {Ms(idle.JitterMs)} · {idle.Answered} of {idle.Asked} answered";
        }
        else
        {
            IdleValue.Text = "—";
            IdleDetail.Text = "No answer";
        }

        var loaded = new[] { r.UnderDownload, r.UnderUpload }.Where(s => s is not null).Select(s => s!).ToList();
        if (loaded.Count > 0)
        {
            var worst = loaded.Max(s => s.MedianMs);
            LoadValue.Text = Ms(worst) + (r.AddedMs is { } added ? $" (+{added.ToString("0", c)})" : "");
            var parts = new List<string>();
            parts.Add("Downloading: " + (r.UnderDownload is { } d ? Ms(d.MedianMs) + Speed(r.DownloadMbps) : "not measured"));
            parts.Add("Uploading: " + (r.UnderUpload is { } u ? Ms(u.MedianMs) + Speed(r.UploadMbps) : "not measured"));
            LoadDetail.Text = string.Join(" · ", parts);
        }
        else
        {
            LoadValue.Text = "—";
            LoadDetail.Text = "Not measured";
        }

        GradeValue.Text = r.Grade.Title;
        GradeDetail.Text = r.Grade.Meaning;
        AutomationProperties.SetName(IdleCard, "Idle latency: " + IdleValue.Text);
        AutomationProperties.SetName(LoadCard, "Latency under load: " + LoadValue.Text);
        AutomationProperties.SetName(GradeCard, "Grade: " + GradeValue.Text);

        var note = $"Measured with {r.Host} at {r.FinishedUtc.ToLocalTime().ToString("t", c)}. Results stay on this PC.";
        if (r.Problem is not null) note = r.Problem + " " + note;
        Note(note);
    }

    private static string Speed(double? mbps) => mbps is { } v ? $" at about {v.ToString("0", CultureInfo.CurrentCulture)} Mbit/s" : "";

    private void Note(string text)
    {
        ResultNote.Text = text;
        ResultNote.Visibility = Visibility.Visible;
    }

    private void BuildGuidance()
    {
        foreach (var tip in LagCheckGuidance.Tips)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var title = new TextBlock { Text = tip.Title, TextWrapping = TextWrapping.Wrap };
            title.SetResourceReference(StyleProperty, "BodyStrong");
            var text = new TextBlock { Text = tip.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            text.SetResourceReference(StyleProperty, "Body");
            var measured = new TextBlock { Text = "Measured: " + tip.Measured, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            measured.SetResourceReference(StyleProperty, "Caption");
            var source = new TextBlock { Text = "Source: " + tip.Source, TextWrapping = TextWrapping.Wrap };
            source.SetResourceReference(StyleProperty, "Caption");
            panel.Children.Add(title);
            panel.Children.Add(text);
            panel.Children.Add(measured);
            panel.Children.Add(source);
            AutomationProperties.SetName(panel, tip.Title);
            Guidance.Children.Add(panel);
        }
    }
}
