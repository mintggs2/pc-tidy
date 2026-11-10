using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using PcTidy.Core;
using PcTidy.Features.Assistant.Engine;
using PcTidy.Features.Assistant.Ollama;
using PcTidy.UI.Controls;
using PcTidy.UI.Flow;
using InfoBar = Wpf.Ui.Controls.InfoBar;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
using ProgressRing = Wpf.Ui.Controls.ProgressRing;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;
using UiButton = Wpf.Ui.Controls.Button;
using UiAppearance = Wpf.Ui.Controls.ControlAppearance;

namespace PcTidy.Features.Assistant.UI;

/// <summary>Which card the page shows.</summary>
internal enum AssistantPageState { Off, Checking, NotInstalled, NotRunning, Refused, NoModels, Ready }

/// <summary>
/// The "Ask PC Tidy" chat page. Off until the user turns it on; then it looks for Ollama on 127.0.0.1 and shows
/// "Not available on this PC" with install steps if it isn't there. Questions are answered from the latest scan
/// results (<see cref="IScanResultsCache"/>); answers are plain text plus "Review in … →" buttons, which only open
/// that section's preview with the items pre-selected (<see cref="ISectionReviewHost"/>). Nothing is saved and
/// nothing is sent anywhere but this PC. Only the conversation scrolls; works at 1000 px wide.
/// </summary>
public sealed class AssistantPage : UserControl, IFixedHeightPage
{
    private const int MaxShownTurns = 40;

    private readonly IServiceProvider _services;
    private readonly HttpMessageHandler? _transport;
    private readonly AppSettings _settings;
    private readonly IKnownLocations _locations;
    private readonly IScanResultsCache _cache;
    private readonly OllamaHostSetting _hostSetting;
    private readonly List<ChatTurn> _history = new();
    private readonly List<AssistantAnswer> _answers = new();

    private readonly StackPanel _notices = new();
    private readonly ContentControl _body = new() { Focusable = false };
    private readonly StackPanel _chatList = new() { Margin = new Thickness(0, 4, 12, 4) };
    private readonly ScrollViewer _chatScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Wpf.Ui.Controls.TextBox _input = new() { PlaceholderText = "Ask a question about your PC", MinHeight = 36, VerticalContentAlignment = VerticalAlignment.Center, MaxLength = AssistantPrompt.MaxQuestionLength };
    private readonly UiButton _send = new() { Content = "Ask", Appearance = UiAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.Send24), Margin = new Thickness(8, 0, 0, 0), MinWidth = 96 };
    private readonly UiButton _stop = new() { Content = "Stop", Icon = new SymbolIcon(SymbolRegular.Stop24), Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ComboBox _modelPicker = new() { MinWidth = 280, MaxWidth = 380, Margin = new Thickness(8, 0, 0, 0), DisplayMemberPath = nameof(ModelChoice.Label) };

    private OllamaClient? _client;
    private AssistantEngine? _engine;
    private CancellationTokenSource? _askCts;
    private bool _checkedOnce;

    internal AssistantPage(IServiceProvider services, HttpMessageHandler? transport, Func<string, string?>? environment)
    {
        _services = services;
        _transport = transport;
        _settings = services.Get<AppSettings>();
        _locations = services.Get<IKnownLocations>();
        _cache = services.GetService(typeof(IScanResultsCache)) as IScanResultsCache ?? new ScanResultsCache();
        _hostSetting = OllamaHostSetting.Read(environment);

        AutomationProperties.SetName(this, "Ask PC Tidy");
        SetResourceReference(FontFamilyProperty, "TidyText");
        Content = BuildFrame();
        _chatScroll.Content = _chatList;
        WireInput();

        if (AssistantSettings.From(_settings).Enabled) ShowChecking();
        else ShowOff();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _cache.Changed -= OnCacheChanged;
    }

    internal AssistantPageState State { get; private set; }
    /// <summary>Answers shown in this session (tests read them; nothing is stored).</summary>
    internal IReadOnlyList<AssistantAnswer> Answers => _answers;
    internal IReadOnlyList<ModelChoice> ModelChoices => _modelPicker.ItemsSource as IReadOnlyList<ModelChoice> ?? Array.Empty<ModelChoice>();
    internal string? SelectedModel => (_modelPicker.SelectedItem as ModelChoice)?.Name;

    private IFlowHost? Host => _services.GetService(typeof(IFlowHost)) as IFlowHost;
    private IShellFlowHost? Shell => _services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? Host as IShellFlowHost;
    private IEnumerable<IFeatureModule> Modules => _services.GetService(typeof(IEnumerable<IFeatureModule>)) as IEnumerable<IFeatureModule> ?? Array.Empty<IFeatureModule>();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cache.Changed -= OnCacheChanged;
        _cache.Changed += OnCacheChanged;
        // The switch may also have been flipped in Settings since this page was last shown.
        var enabled = AssistantSettings.From(_settings).Enabled;
        if (!enabled && State != AssistantPageState.Off) ShowOff();
        else if (enabled && (!_checkedOnce || State == AssistantPageState.Off)) CheckAsync().Forget(Host, "Couldn't look for Ollama");
        else UpdateNotices();
    }

    private void OnCacheChanged() => Dispatcher.BeginInvoke(UpdateNotices);

    // ---------------- frame ----------------

    private Grid BuildFrame()
    {
        var root = new Grid();
        root.SetResourceReference(MarginProperty, "TidyPageMargin");
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(Styled(new TextBlock { Text = "Ask PC Tidy" }, "PageTitle"));
        var pill = new BadgePill { Text = "Nothing leaves this PC", Tone = PillTone.Success, Margin = new Thickness(12, 6, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(pill, "Nothing leaves this PC");
        titleRow.Children.Add(pill);
        var header = new StackPanel();
        header.Children.Add(titleRow);
        header.Children.Add(Styled(new TextBlock
        {
            Text = "Ask about your drives and PC Tidy's latest scan. An AI model running on this PC answers. " +
                   "It can point you to items to review; you still check and confirm everything yourself.",
        }, "PageSubtitle"));
        root.Children.Add(header);

        _notices.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(_notices, 1);
        root.Children.Add(_notices);

        _body.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(_body, 2);
        root.Children.Add(_body);
        return root;
    }

    private void SetState(AssistantPageState state, FrameworkElement view)
    {
        State = state;
        _body.Content = view;
        UpdateNotices();
    }

    /// <summary>Read-only warnings: Ollama open to the network (OLLAMA_HOST), no scan results yet.</summary>
    private void UpdateNotices()
    {
        _notices.Children.Clear();
        if (State == AssistantPageState.Off) return;
        if (_hostSetting.NetworkWarning is { } warning)
            _notices.Children.Add(Bar("Ollama can be reached from your network", warning, InfoBarSeverity.Warning));
        if (State == AssistantPageState.Ready && _cache.Latest.Count == 0)
            _notices.Children.Add(Bar(null, "No scan results yet, so answers can only use drive space. Scan in Clean or Space map first for answers about your files.",
                InfoBarSeverity.Informational));
    }

    // ---------------- off ----------------

    private void ShowOff()
    {
        CancelAsk();
        var turnOn = new UiButton { Content = "Turn on Ask PC Tidy", Appearance = UiAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.ChatSparkle24), Margin = new Thickness(0, 16, 0, 0) };
        AutomationProperties.SetName(turnOn, "Turn on Ask PC Tidy");
        turnOn.Click += (_, _) =>
        {
            var s = AssistantSettings.From(_settings);
            s.Enabled = true;
            s.Store(_settings);
            CheckAsync().Forget(Host, "Couldn't look for Ollama");
        };
        SetState(AssistantPageState.Off, Card(SymbolRegular.ChatSparkle24, "Ask PC Tidy is off",
            "It's an optional helper that uses Ollama, a free app that runs AI models on your own PC.\n" +
            "When it's on, PC Tidy sends your question and a summary of its latest scan (file names, sizes, drive space) " +
            $"to Ollama at {OllamaEndpoint.LoopbackAddress}, which is this PC. Nothing is sent to the internet, and nothing is saved.\n" +
            "Answers can only point you to items to review. Nothing is changed until you confirm, as always.",
            turnOn));
    }

    // ---------------- checking / not available ----------------

    private void ShowChecking()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new ProgressRing { IsIndeterminate = true, Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0) });
        row.Children.Add(Styled(new TextBlock { Text = "Looking for Ollama on this PC…", VerticalAlignment = VerticalAlignment.Center }, "Body"));
        AutomationProperties.SetName(row, "Looking for Ollama on this PC");
        SetState(AssistantPageState.Checking, row);
    }

    /// <summary>Asks Ollama (on 127.0.0.1) for its version and models, then shows the matching card. Read-only.</summary>
    internal async Task CheckAsync()
    {
        _checkedOnce = true;
        CancelAsk();
        ShowChecking();
        var saved = AssistantSettings.From(_settings);
        var port = saved.Port ?? _hostSetting.Port ?? OllamaEndpoint.DefaultPort;
        _client?.Dispose();
        _client = new OllamaClient(new OllamaEndpoint(port), _transport);
        _engine = new AssistantEngine(_client, _cache, _services.GetService(typeof(ISystemInfo)) as ISystemInfo, SectionTitle);

        var status = await new OllamaProbe(_client, _locations).CheckAsync(CancellationToken.None);
        if (!AssistantSettings.From(_settings).Enabled) { ShowOff(); return; } // turned off while we looked

        switch (status.State)
        {
            case OllamaState.NotInstalled:
                SetState(AssistantPageState.NotInstalled, Card(SymbolRegular.PlugDisconnected24, OllamaProbe.NotAvailableTitle, OllamaProbe.InstallInstructions, CheckAgainButton(), CopyCommandButton(), TurnOffButton()));
                return;
            case OllamaState.InstalledNotRunning:
                SetState(AssistantPageState.NotRunning, Card(SymbolRegular.PlugDisconnected24, "Ollama isn't running", OllamaProbe.NotRunningInstructions, CheckAgainButton(), TurnOffButton()));
                return;
            case OllamaState.Refused:
                SetState(AssistantPageState.Refused, Card(SymbolRegular.ShieldError24, "PC Tidy didn't connect",
                    (status.Problem ?? "The address wasn't this PC.") + " The helper only ever talks to Ollama on this PC.", CheckAgainButton(), TurnOffButton()));
                return;
        }

        var choices = ModelAdvice.Choices(status.Models);
        if (choices.Count == 0)
        {
            SetState(AssistantPageState.NoModels, Card(SymbolRegular.BrainCircuit24, "No AI model yet", ModelAdvice.NoModelsText, CheckAgainButton(), CopyCommandButton(), TurnOffButton()));
            return;
        }
        ShowReady(choices, saved.Model);
    }

    private UiButton CheckAgainButton()
    {
        var b = new UiButton { Content = "Check again", Appearance = UiAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.ArrowSync24), Margin = new Thickness(0, 16, 8, 0) };
        AutomationProperties.SetName(b, "Check again for Ollama");
        b.Click += (_, _) => CheckAsync().Forget(Host, "Couldn't look for Ollama");
        return b;
    }

    /// <summary>Copies the "ollama pull" line from the steps, so nobody has to type it. PC Tidy still never runs it.</summary>
    private static UiButton CopyCommandButton()
    {
        var b = new UiButton
        {
            Content = "Copy the command", Appearance = UiAppearance.Secondary, Icon = new SymbolIcon(SymbolRegular.Copy24),
            Margin = new Thickness(0, 16, 8, 0), ToolTip = OllamaProbe.PullCommand + " (paste it into Terminal yourself)",
        };
        AutomationProperties.SetName(b, "Copy the command " + OllamaProbe.PullCommand);
        b.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(OllamaProbe.PullCommand);
                b.Content = "Copied";
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                b.Content = "Couldn't copy (try again)";
            }
        };
        return b;
    }

    private UiButton TurnOffButton()
    {
        var b = new UiButton { Content = "Turn off", Appearance = UiAppearance.Secondary, Margin = new Thickness(0, 16, 0, 0) };
        AutomationProperties.SetName(b, "Turn off Ask PC Tidy");
        b.Click += (_, _) =>
        {
            var s = AssistantSettings.From(_settings);
            s.Enabled = false;
            s.Store(_settings);
            ShowOff();
        };
        return b;
    }

    // ---------------- ready: the chat ----------------

    private void ShowReady(IReadOnlyList<ModelChoice> choices, string? savedModel)
    {
        _modelPicker.SelectionChanged -= OnModelPicked;
        _modelPicker.ItemsSource = choices;
        _modelPicker.SelectedItem = ModelAdvice.DefaultChoice(choices, savedModel);
        _modelPicker.SelectionChanged += OnModelPicked;
        AutomationProperties.SetName(_modelPicker, "AI model");
        _modelPicker.ToolTip = "Models that fit an 8 GB graphics card (about 3 to 8 billion parameters) answer quickly. PC Tidy never downloads models.";

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = false };
        var modelRow = new StackPanel { Orientation = Orientation.Horizontal };
        modelRow.Children.Add(Styled(new TextBlock { Text = "Model", VerticalAlignment = VerticalAlignment.Center }, "BodyStrong"));
        modelRow.Children.Add(_modelPicker);
        DockPanel.SetDock(modelRow, Dock.Left);
        toolbar.Children.Add(modelRow);
        var off = TurnOffButton();
        off.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(off, Dock.Right);
        toolbar.Children.Add(off);
        var again = CheckAgainButton();
        again.Appearance = UiAppearance.Secondary;
        again.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(again, Dock.Right);
        toolbar.Children.Add(again);
        grid.Children.Add(toolbar);

        var chatCard = new Border { Padding = new Thickness(14, 10, 4, 10) };
        chatCard.SetResourceReference(StyleProperty, "TidyCard");
        chatCard.Child = _chatScroll;
        AutomationProperties.SetName(_chatScroll, "Conversation");
        if (_chatList.Children.Count == 0) _chatList.Children.Add(Welcome());
        Grid.SetRow(chatCard, 1);
        grid.Children.Add(chatCard);

        var examples = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var q in ExampleQuestions())
        {
            var chip = new UiButton { Content = q, Appearance = UiAppearance.Secondary, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(10, 4, 10, 4) };
            AutomationProperties.SetName(chip, "Ask: " + q);
            chip.Click += (_, _) => AskAsync(q).Forget(Host, "The helper didn't answer");
            examples.Children.Add(chip);
        }
        Grid.SetRow(examples, 2);
        grid.Children.Add(examples);

        var inputRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (_input.Parent is Panel oldParent) oldParent.Children.Remove(_input);
        if (_send.Parent is Panel p1) p1.Children.Remove(_send);
        if (_stop.Parent is Panel p2) p2.Children.Remove(_stop);
        inputRow.Children.Add(_input);
        Grid.SetColumn(_send, 1);
        inputRow.Children.Add(_send);
        Grid.SetColumn(_stop, 2);
        inputRow.Children.Add(_stop);
        Grid.SetRow(inputRow, 3);
        grid.Children.Add(inputRow);

        SetState(AssistantPageState.Ready, grid);
    }

    private void OnModelPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_modelPicker.SelectedItem is not ModelChoice c) return;
        var s = AssistantSettings.From(_settings);
        s.Model = c.Name;
        s.Store(_settings);
    }

    private IEnumerable<string> ExampleQuestions()
    {
        var drive = string.IsNullOrEmpty(_locations.SystemDrive) ? "system" : _locations.SystemDrive.TrimEnd('\\');
        yield return $"Why is my {drive} drive full?";
        yield return "What's safe to clean first?";
        yield return "What are my biggest folders?";
    }

    private void WireInput()
    {
        AutomationProperties.SetName(_input, "Your question");
        _input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            AskAsync(_input.Text).Forget(Host, "The helper didn't answer");
        };
        AutomationProperties.SetName(_send, "Ask");
        _send.Click += (_, _) => AskAsync(_input.Text).Forget(Host, "The helper didn't answer");
        AutomationProperties.SetName(_stop, "Stop waiting for the answer");
        _stop.Click += (_, _) => _askCts?.Cancel();
    }

    /// <summary>One question → one answer bubble. Errors become a calm bubble, never a crash.</summary>
    internal async Task AskAsync(string question)
    {
        var q = AssistantPrompt.CleanQuestion(question);
        if (q.Length == 0 || _engine is null || _askCts is not null) return;
        if (SelectedModel is not { } model) return;
        _input.Text = "";
        AddBubble(UserBubble(q));
        var thinking = Thinking();
        AddBubble(thinking);
        using var cts = new CancellationTokenSource();
        _askCts = cts;
        SetBusy(true);
        try
        {
            var answer = await _engine.AskAsync(model, q, _history.ToList(), cts.Token);
            _answers.Add(answer);
            _history.Add(new ChatTurn(q, answer.Text));
            Replace(thinking, AnswerBubble(answer));
        }
        catch (OperationCanceledException)
        {
            Replace(thinking, NoteBubble("Stopped. Ask again whenever you like.", isError: false));
        }
        catch (OllamaUnavailableException ex)
        {
            Replace(thinking, NoteBubble(ex.Message + " Nothing was changed.", isError: true));
        }
        catch (LoopbackRefusedException ex)
        {
            Replace(thinking, NoteBubble(ex.Message, isError: true));
        }
        finally
        {
            _askCts = null;
            SetBusy(false);
        }
    }

    private void CancelAsk() => _askCts?.Cancel();

    private void SetBusy(bool busy)
    {
        _send.IsEnabled = !busy;
        _input.IsEnabled = !busy;
        _modelPicker.IsEnabled = !busy;
        _stop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy && IsLoaded) _input.Focus();
    }

    // ---------------- "Review in … →" ----------------

    private void OnReview(ValidatedSuggestion s)
    {
        if (!s.IsCurrent(_cache))
        {
            Host?.ShowMessage("These results changed", $"{s.SectionTitle} was scanned again after this answer. Ask again for up-to-date suggestions.");
            return;
        }
        var request = s.ToRequest();
        if (_services.GetService(typeof(ISectionReviewHost)) is ISectionReviewHost review)
        {
            if (!review.OpenForReview(request))
                Host?.ShowMessage("Couldn't show those items", $"{s.SectionTitle} isn't showing that scan any more. Scan it again, then ask again.");
            return;
        }
        // Shell without pre-selection support: open the section and say what to tick.
        if (OwnerModule(s.FeatureId) is { } module) Shell?.NavigateTo(module.Id);
        Host?.ShowMessage($"Look for these in {s.SectionTitle}",
            string.Join("\n", s.Items.Take(8).Select(i => "• " + i.Title)) + (s.Items.Count > 8 ? $"\n…and {s.Items.Count - 8} more" : "") +
            "\nNothing is ticked or changed until you choose.");
    }

    private IFeatureModule? OwnerModule(string featureId)
        => Modules.FirstOrDefault(m => string.Equals(m.Id, featureId, StringComparison.OrdinalIgnoreCase))
           ?? Modules.FirstOrDefault(m => m.Scanners.Any(sc => string.Equals(sc.FeatureId, featureId, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The rail name of the section that owns a scan ("Clean"), for the button text.</summary>
    private string SectionTitle(string featureId) => OwnerModule(featureId)?.Title ?? _cache.Get(featureId)?.Title ?? featureId;

    // ---------------- bubbles ----------------

    private void AddBubble(FrameworkElement bubble)
    {
        if (_chatList.Children.Count == 1 && _chatList.Children[0] is FrameworkElement { Tag: "welcome" }) _chatList.Children.Clear();
        _chatList.Children.Add(bubble);
        while (_chatList.Children.Count > MaxShownTurns * 2) _chatList.Children.RemoveAt(0);
        _chatScroll.ScrollToEnd();
    }

    private void Replace(FrameworkElement old, FrameworkElement now)
    {
        var at = _chatList.Children.IndexOf(old);
        if (at >= 0)
        {
            _chatList.Children.RemoveAt(at);
            _chatList.Children.Insert(at, now);
        }
        else _chatList.Children.Add(now);
        _chatScroll.ScrollToEnd();
    }

    private static FrameworkElement Welcome()
    {
        var t = Styled(new TextBlock
        {
            Text = "Ask about your drives and what PC Tidy found. Answers come from the latest scans in this session, " +
                   "and can point you to items to review. Try one of the questions below.",
            Margin = new Thickness(0, 6, 0, 6),
        }, "Secondary");
        t.Tag = "welcome";
        return t;
    }

    private static FrameworkElement UserBubble(string text)
    {
        var b = new Border { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 620, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(10), Margin = new Thickness(60, 6, 0, 6) };
        b.SetResourceReference(Border.BackgroundProperty, "TidyAccentSoftBrush");
        b.Child = Styled(new TextBlock { Text = text }, "Body");
        AutomationProperties.SetName(b, "You asked: " + text);
        return b;
    }

    private static FrameworkElement Thinking()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 60, 6) };
        row.Children.Add(new ProgressRing { IsIndeterminate = true, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(Styled(new TextBlock { Text = "Thinking… The first answer can take a minute while the model loads.", VerticalAlignment = VerticalAlignment.Center }, "Secondary"));
        AutomationProperties.SetName(row, "Thinking");
        return row;
    }

    private FrameworkElement AnswerBubble(AssistantAnswer answer)
    {
        var stack = new StackPanel();
        stack.Children.Add(Styled(new TextBlock { Text = answer.Text }, "Body"));
        if (!answer.UsedScanResults)
            stack.Children.Add(Styled(new TextBlock { Text = "Answered from drive space only. Scan in Clean or Space map for answers about your files.", Margin = new Thickness(0, 6, 0, 0) }, "Caption"));
        foreach (var s in answer.Suggestions)
        {
            var count = s.Items.Count == 1 ? "1 item" : $"{s.Items.Count} items";
            var caption = s.TotalBytes > 0 ? $"{count} · {Sizes.Format(s.TotalBytes)} · nothing changes until you confirm" : $"{count} · nothing changes until you confirm";
            var button = new UiButton { Content = s.ButtonText, Appearance = UiAppearance.Secondary, Icon = new SymbolIcon(SymbolRegular.Eye24) };
            AutomationProperties.SetName(button, $"Review {count} in {s.SectionTitle}. Opens the preview with them ticked; nothing changes until you confirm.");
            button.Click += (_, _) => OnReview(s);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(button);
            row.Children.Add(Styled(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) }, "Caption"));
            stack.Children.Add(row);
        }
        var b = new Border { HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 720, Padding = new Thickness(12, 8, 12, 10), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 6, 60, 6), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        b.Child = stack;
        AutomationProperties.SetName(b, "Answer: " + answer.Text);
        return b;
    }

    private static FrameworkElement NoteBubble(string text, bool isError)
    {
        var t = Styled(new TextBlock { Text = text, Margin = new Thickness(0, 6, 60, 6) }, isError ? "Body" : "Secondary");
        if (isError) t.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        AutomationProperties.SetName(t, text);
        return t;
    }

    // ---------------- small builders ----------------

    private static FrameworkElement Card(SymbolRegular icon, string title, string text, params UIElement[] actions)
    {
        var card = new Border { Padding = new Thickness(22, 20, 22, 20), VerticalAlignment = VerticalAlignment.Top, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        card.SetResourceReference(StyleProperty, "TidyCard");
        var dock = new DockPanel();
        var tile = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0) };
        tile.SetResourceReference(Border.BackgroundProperty, "TidyAccentSoftBrush");
        var symbol = new SymbolIcon(icon) { FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        symbol.SetResourceReference(ForegroundProperty, "TidyAccentBrush");
        tile.Child = symbol;
        DockPanel.SetDock(tile, Dock.Left);
        dock.Children.Add(tile);
        var stack = new StackPanel();
        stack.Children.Add(Styled(new TextBlock { Text = title }, "SectionTitle"));
        stack.Children.Add(Styled(new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 0) }, "Body"));
        if (actions.Length > 0)
        {
            var row = new WrapPanel();
            foreach (var a in actions) row.Children.Add(a);
            stack.Children.Add(row);
        }
        dock.Children.Add(stack);
        card.Child = dock;
        AutomationProperties.SetName(card, title);
        return card;
    }

    private static InfoBar Bar(string? title, string message, InfoBarSeverity severity)
        => new() { Title = title ?? "", Message = message, IsOpen = true, IsClosable = true, Severity = severity, Margin = new Thickness(0, 0, 0, 6) };

    private static T Styled<T>(T el, string style) where T : FrameworkElement
    {
        el.SetResourceReference(StyleProperty, style);
        if (el is TextBlock tb) tb.TextWrapping = TextWrapping.Wrap;
        return el;
    }
}
