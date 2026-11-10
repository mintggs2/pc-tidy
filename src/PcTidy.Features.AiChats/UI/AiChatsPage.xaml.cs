using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.AiChats.Archive;
using PcTidy.Features.AiChats.Export;
using PcTidy.Features.AiChats.Library;
using PcTidy.Features.AiChats.Local;
using PcTidy.Features.AiChats.Model;
using PcTidy.UI.Controls;
using PcTidy.UI.Flow;

namespace PcTidy.Features.AiChats.UI;

/// <summary>
/// The AI chats page: Library · Agent chats on this PC · Exposed secrets · How to export. Importing only reads the
/// export/transcripts and writes PC Tidy's own masked library; every change to your files (notes, archive copies,
/// removing originals) goes through the shell's confirm dialog (IFlowHost) and can be undone from History.
/// Works at 1000 px wide: only the lists scroll.
/// </summary>
public partial class AiChatsPage : UserControl, IFixedHeightPage
{
    private readonly IServiceProvider _services;
    private readonly AppSettings _settings;
    private readonly IKnownLocations _locations;
    private readonly IReadOnlyDictionary<string, object> _options;
    private readonly ChatLibraryStore _store;
    private readonly LibraryImporter _importer;
    private readonly ObservableCollection<FilterRow> _filters = new();
    private readonly ObservableCollection<ChatRow> _chats = new();
    private readonly ObservableCollection<MessageRow> _messages = new();
    private readonly ObservableCollection<string> _tags = new();
    private readonly ObservableCollection<AgentRow> _agents = new();
    private readonly ObservableCollection<SecretRow> _secrets = new();
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private ChatDetail? _current;
    private LiveSessions? _live;
    private bool _agentsLoaded;
    private bool _busy;
    private int _queryVersion;
    /// <summary>What was imported since the page opened, so a second import doesn't report only itself.</summary>
    private int _importedFiles, _importedChats, _importedNew;

    internal AiChatsPage(IServiceProvider services, IReadOnlyDictionary<string, object> options)
    {
        InitializeComponent();
        _services = services;
        _options = options;
        _settings = services.Get<AppSettings>();
        _locations = services.Get<IKnownLocations>();
        _store = new ChatLibraryStore(_settings.DataRoot);
        _importer = new LibraryImporter(_store);

        PrivacyPill.Text = "Private: stays on this PC";
        PrivacyPill.Tone = PillTone.Success;
        FilterList.ItemsSource = _filters;
        ChatList.ItemsSource = _chats;
        MessageList.ItemsSource = _messages;
        TagList.ItemsSource = _tags;
        AgentList.ItemsSource = _agents;
        SecretList.ItemsSource = _secrets;
        CleanupInfo.Message = AiChatsScanner.CleanupAdvice;
        KeepSecretsBox.IsChecked = Saved.KeepSecretsInExports;
        ShowExportFolder();
        ShowArchiveFolder();
        BuildGuide();
        LibraryEmpty.Message = "Import a ChatGPT or Claude export zip, or add your Claude Code and Codex chats from \"Agent chats on this PC\".";

        _searchDelay.Tick += async (_, _) => { _searchDelay.Stop(); await SafeAsync(RefreshChatsAsync, "Search couldn't run"); };
        Tabs.SelectionChanged += async (_, _) => await SafeAsync(() => ShowTabAsync(Tabs.SelectedIndex), "Couldn't open that tab");
        Loaded += async (_, _) => await SafeAsync(RefreshLibraryAsync, "Couldn't open the chat library");
    }

    private AiChatsSettings Saved => AiChatsSettings.From(_settings);
    private IFlowHost? Flow => _services.GetService(typeof(IFlowHost)) as IFlowHost;

    private async Task ShowTabAsync(int index)
    {
        LibraryPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        AgentPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SecretsPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        GuidePanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (index == 1 && !_agentsLoaded) await RefreshAgentsAsync();
        if (index == 2) await RefreshSecretsAsync();
    }

    // =====================================================================  Library

    private async Task RefreshLibraryAsync()
    {
        await RefreshFiltersAsync();
        await RefreshChatsAsync();
    }

    private async Task RefreshFiltersAsync()
    {
        var selected = FilterList.SelectedItem as FilterRow;
        try
        {
            var counts = await _store.CountsAsync(CancellationToken.None);
            var groups = await _store.GroupsAsync(CancellationToken.None);
            var tags = await _store.TagsAsync(CancellationToken.None);
            var folders = await _store.FoldersAsync(CancellationToken.None);
            var pinned = await _store.CountAsync(LibraryView.Pinned, CancellationToken.None);
            var nearEmpty = await _store.CountAsync(LibraryView.NearEmpty, CancellationToken.None);
            var dupes = await _store.CountAsync(LibraryView.Duplicates, CancellationToken.None);
            var withSecrets = await _store.CountAsync(LibraryView.WithSecrets, CancellationToken.None);

            _filters.Clear();
            _filters.Add(new FilterRow { Kind = FilterKind.All, Label = "All chats", Count = counts.Sum(c => c.Chats) });
            _filters.Add(FilterRow.Header("Sources"));
            foreach (var s in ChatSources.All)
                _filters.Add(new FilterRow { Kind = FilterKind.Source, Label = ChatSources.Display(s), Source = s, Count = counts.FirstOrDefault(c => c.Source == s)?.Chats ?? 0 });
            _filters.Add(FilterRow.Header("Views"));
            _filters.Add(new FilterRow { Kind = FilterKind.View, View = LibraryView.Pinned, Label = "Pinned", Count = pinned });
            _filters.Add(new FilterRow { Kind = FilterKind.View, View = LibraryView.Biggest, Label = "Biggest" });
            _filters.Add(new FilterRow { Kind = FilterKind.View, View = LibraryView.NearEmpty, Label = "Near-empty", Count = nearEmpty });
            _filters.Add(new FilterRow { Kind = FilterKind.View, View = LibraryView.Duplicates, Label = "Duplicates", Count = dupes });
            _filters.Add(new FilterRow { Kind = FilterKind.View, View = LibraryView.WithSecrets, Label = "With masked keys", Count = withSecrets });
            if (folders.Count > 0)
            {
                _filters.Add(FilterRow.Header("Folders"));
                foreach (var f in folders) _filters.Add(new FilterRow { Kind = FilterKind.Folder, Label = f.Name, Value = f.Name, Count = f.Chats });
            }
            if (tags.Count > 0)
            {
                _filters.Add(FilterRow.Header("Tags"));
                foreach (var t in tags) _filters.Add(new FilterRow { Kind = FilterKind.Tag, Label = "#" + t.Name, Value = t.Name, Count = t.Chats });
            }
            if (groups.Count > 0)
            {
                _filters.Add(FilterRow.Header("Projects and topics"));
                foreach (var g in groups) _filters.Add(new FilterRow { Kind = FilterKind.Group, Label = g.Name, Value = g.Name, Count = g.Chats });
            }
            FilterList.SelectedItem = _filters.FirstOrDefault(f => selected is not null && f.Kind == selected.Kind && f.Label == selected.Label) ?? _filters[0];
            FolderBox.ItemsSource = folders.Select(f => f.Name).ToList();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            if (ChatLibraryStore.IsDamaged(ex)) ShowDamaged();
            LibraryStatus.Text = "The chat library couldn't be opened (" + ex.Message + ").";
        }
    }

    // ---------------------------------------------------------------------  failures

    /// <summary>
    /// Runs a page action and turns any failure into a message on the page instead of the app-wide error dialog
    /// (these handlers are async void, so an exception would otherwise escape to the crash handler).
    /// A damaged library also shows the "Start a new library" bar.
    /// </summary>
    private async Task SafeAsync(Func<Task> work, string what)
    {
        try { await work(); }
        catch (OperationCanceledException) { /* stopped on purpose */ }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ChatLibraryStore.IsDamaged(ex)) { ShowDamaged(); return; }
            Flow?.ShowMessage(what, ex.Message, isError: true);
        }
    }

    private void ShowDamaged()
    {
        DamagedInfo.IsOpen = true;
        LibraryStatus.Text = "The chat library is damaged. Start a new one to keep using AI chats.";
    }

    private async void OnRebuildLibrary(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var aside = await _store.SetAsideDamagedAsync(CancellationToken.None);
            DamagedInfo.IsOpen = false;
            _current = null;
            await RefreshLibraryAsync();
            LibraryStatus.Text = "A new, empty library was started." + (aside is null ? "" : $" The damaged one was kept as {aside} in PC Tidy's data folder.")
                                 + " Import your exports and agent chats again to bring the chats back.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Flow?.ShowMessage("Couldn't start a new library", "The damaged library file is in use or can't be renamed: " + ex.Message, isError: true);
        }
        finally { _busy = false; }
    }

    private LibraryQuery CurrentQuery()
    {
        var f = FilterList.SelectedItem as FilterRow;
        return new LibraryQuery
        {
            Text = SearchBox.Text,
            Source = f?.Kind == FilterKind.Source ? f.Source : null,
            View = f?.Kind == FilterKind.View && f.View is { } v ? v : LibraryView.All,
            Group = f?.Kind == FilterKind.Group ? f.Value : null,
            Tag = f?.Kind == FilterKind.Tag ? f.Value : null,
            Folder = f?.Kind == FilterKind.Folder ? f.Value : null,
        };
    }

    private async Task RefreshChatsAsync()
    {
        var version = ++_queryVersion;
        var keep = _current?.Summary.Id;
        IReadOnlyList<ChatSummary> rows;
        try { rows = await _store.QueryAsync(CurrentQuery(), CancellationToken.None); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            if (ChatLibraryStore.IsDamaged(ex)) { ShowDamaged(); return; }
            LibraryStatus.Text = "Search couldn't run (" + ex.Message + ").";
            return;
        }
        if (version != _queryVersion) return; // a newer search is on its way
        _chats.Clear();
        foreach (var r in rows) _chats.Add(new ChatRow(r));
        var total = _filters.FirstOrDefault(f => f.Kind == FilterKind.All)?.Count ?? 0;
        LibraryEmpty.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Nothing to export from an empty library (or an empty search).
        ExportButton.IsEnabled = rows.Count > 0;
        LibraryStatus.Text = total == 0 ? "" : $"{rows.Count:N0} chat{(rows.Count == 1 ? "" : "s")} shown · {Sizes.Format(rows.Sum(r => r.SizeBytes))}. Select several (Ctrl or Shift) to export just those.";
        var again = _chats.FirstOrDefault(c => c.Summary.Id == keep);
        if (again is not null) ChatList.SelectedItem = again;
        else await ShowChatAsync(null);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private async void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterList.SelectedItem is FilterRow { IsHeader: false }) await SafeAsync(RefreshChatsAsync, "Couldn't show those chats");
    }

    private async void OnChatSelected(object sender, SelectionChangedEventArgs e)
        => await SafeAsync(() => ShowChatAsync(ChatList.SelectedItems.Count == 1 ? (ChatList.SelectedItem as ChatRow)?.Summary.Id : null), "Couldn't open that chat");

    private async Task ShowChatAsync(string? id)
    {
        _current = id is null ? null : await _store.GetAsync(id, CancellationToken.None);
        _messages.Clear();
        _tags.Clear();
        PreviewPanel.Visibility = _current is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewEmpty.Visibility = _current is null ? Visibility.Visible : Visibility.Collapsed;
        if (ChatList.SelectedItems.Count > 1)
        {
            PreviewEmpty.Title = $"{ChatList.SelectedItems.Count} chats selected";
            PreviewEmpty.Message = "Export to Obsidian exports just these.";
        }
        else
        {
            PreviewEmpty.Title = "Pick a chat";
            PreviewEmpty.Message = "Its messages show here, with keys and passwords masked.";
        }
        if (_current is not { } c) return;

        var s = c.Summary;
        PreviewTitle.Text = s.Title;
        var meta = new List<string> { ChatSources.Display(s.Source) };
        if (s.Date is { } d) meta.Add(d.ToLocalTime().ToString("f", CultureInfo.CurrentCulture));
        if (!string.IsNullOrWhiteSpace(s.Model)) meta.Add(s.Model!);
        meta.Add(s.IsAgent ? "Project: " + AgentProjects.ShortName(s.Project) : "Topic: " + s.Group);
        if (s.AlternateCount > 0) meta.Add($"{s.AlternateCount} alternate version{(s.AlternateCount == 1 ? "" : "s")}");
        if (s.AttachmentCount > 0) meta.Add($"{s.AttachmentCount} attachment{(s.AttachmentCount == 1 ? "" : "s")}");
        if (s.SecretCount > 0) meta.Add($"{s.SecretCount} key{(s.SecretCount == 1 ? "" : "s")} masked");
        PreviewMeta.Text = string.Join(" · ", meta);
        PinButton.IsChecked = s.Pinned;
        PinButton.Content = s.Pinned ? "Pinned" : "Pin";
        FolderBox.Text = s.Folder ?? "";
        TopicBox.Text = s.IsAgent ? "" : s.Topic ?? "";
        TopicBox.IsEnabled = !s.IsAgent;
        TagBox.Text = "";
        foreach (var t in s.Tags) _tags.Add(t);

        foreach (var m in c.Messages) _messages.Add(Row(s.Source, m, false));
        for (int i = 0; i < c.Alternates.Count; i++)
        {
            _messages.Add(new MessageRow { Header = $"Alternate version {i + 2} (instead of message {c.Alternates[i].ForkAt + 1} onwards)", Text = "", IsAlternate = true });
            foreach (var m in c.Alternates[i].Messages) _messages.Add(Row(s.Source, m, true));
        }
        if (c.Messages.Count == 0) _messages.Add(new MessageRow { Header = "No messages", Text = "This chat is empty." });
        MessageList.ScrollIntoView(_messages.FirstOrDefault());
    }

    private static MessageRow Row(ChatSource source, ChatMessage m, bool alternate) => new()
    {
        Header = ChatSources.RoleName(source, m.Role) + (m.CreatedUtc is { } t ? " · " + t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : ""),
        // Very long tool outputs would make one row taller than the screen: the note export keeps the full text.
        Text = m.Text.Length > 8000 ? m.Text[..8000] + "\n… (shortened here; the full text is in the library and exports)" : m.Text,
        IsUser = m.Role == ChatRole.User,
        IsAlternate = alternate,
    };

    private async void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (_current is not { } current) return;
        await SafeAsync(async () =>
        {
            await _store.SetPinnedAsync(current.Summary.Id, PinButton.IsChecked == true, CancellationToken.None);
            await RefreshLibraryAsync();
        }, "Couldn't pin that chat");
    }

    private async void OnFolderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SafeAsync(CommitFolderAsync, "Couldn't change the folder");
    }

    private async void OnFolderCommit(object sender, KeyboardFocusChangedEventArgs e) => await SafeAsync(CommitFolderAsync, "Couldn't change the folder");

    private async Task CommitFolderAsync()
    {
        if (_current is null) return;
        var folder = string.IsNullOrWhiteSpace(FolderBox.Text) ? null : FolderBox.Text.Trim();
        if (string.Equals(folder, _current.Summary.Folder, StringComparison.Ordinal)) return;
        await _store.SetFolderAsync(_current.Summary.Id, folder, CancellationToken.None);
        await RefreshLibraryAsync();
    }

    private async void OnTopicKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _current is not { } current) return;
        await SafeAsync(async () =>
        {
            await _store.SetTopicAsync(current.Summary.Id, TopicBox.Text, CancellationToken.None);
            await RefreshLibraryAsync();
        }, "Couldn't change the topic");
    }

    private async void OnTagKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _current is not { } current || string.IsNullOrWhiteSpace(TagBox.Text)) return;
        await SafeAsync(async () =>
        {
            await _store.AddTagAsync(current.Summary.Id, TagBox.Text, CancellationToken.None);
            TagBox.Text = "";
            await RefreshLibraryAsync();
        }, "Couldn't add the tag");
    }

    private async void OnRemoveTag(object sender, RoutedEventArgs e)
    {
        if (_current is not { } current || (sender as FrameworkElement)?.Tag is not string tag) return;
        await SafeAsync(async () =>
        {
            await _store.RemoveTagAsync(current.Summary.Id, tag, CancellationToken.None);
            await RefreshLibraryAsync();
        }, "Couldn't remove the tag");
    }

    // ---------------------------------------------------------------------  importing

    private async void OnImportZip(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose your ChatGPT or Claude export zip",
            Filter = "Export zip (*.zip)|*.zip",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await ImportAsync(dialog.FileName);
    }

    private async void OnImportFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder you extracted the export to" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && !string.IsNullOrWhiteSpace(dialog.FolderName)) await ImportAsync(dialog.FolderName);
    }

    private async Task ImportAsync(string path)
    {
        if (_busy) return;
        _busy = true;
        LibraryStatus.Text = "Reading the export… (it's only read, never changed)";
        try
        {
            var progress = new Progress<string>(m => LibraryStatus.Text = m);
            var summary = await Task.Run(() => _importer.ImportExportAsync(path, progress, CancellationToken.None));
            await RefreshLibraryAsync();
            if (summary.Chats > 0) { _importedFiles++; _importedChats += summary.Chats; _importedNew += summary.Added; }
            LibraryStatus.Text = RunningImportText(summary);
            if (summary.Chats == 0) Flow?.ShowMessage("Nothing imported", summary.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            LibraryStatus.Text = "";
            Flow?.ShowMessage("Couldn't import", ex.Message, isError: true);
        }
        finally { _busy = false; }
    }

    /// <summary>"Imported 3 chats from 2 files (3 new)." after more than one import; the single import's own text otherwise.</summary>
    private string RunningImportText(ImportSummary last)
    {
        if (_importedFiles < 2 || last.Chats == 0) return last.Message;
        var text = $"Imported {_importedChats:N0} chat{(_importedChats == 1 ? "" : "s")} from {_importedFiles:N0} files ({_importedNew:N0} new).";
        if (last.WithSecrets > 0) text += " Keys and passwords in them are masked, and listed under Exposed secrets.";
        return text;
    }

    // ---------------------------------------------------------------------  export to Obsidian

    private void ShowExportFolder()
    {
        var folder = _settings.ObsidianExportFolder;
        ExportFolderText.Text = folder is null ? "Obsidian folder: not chosen yet (you'll be asked on the first export)" : "Exports to: " + folder;
        ExportFolderText.ToolTip = folder;
    }

    private bool ChooseExportFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder in your Obsidian vault for chat notes" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return false;
        _settings.ObsidianExportFolder = dialog.FolderName;
        ShowExportFolder();
        return true;
    }

    private void OnChooseExportFolder(object sender, RoutedEventArgs e) => ChooseExportFolder();

    private void OnKeepSecretsChanged(object sender, RoutedEventArgs e)
    {
        var s = Saved;
        s.KeepSecretsInExports = KeepSecretsBox.IsChecked == true;
        s.Store(_settings);
        if (s.KeepSecretsInExports)
            Flow?.ShowMessage("Keys will be kept in exports",
                "Exported notes will contain keys and passwords exactly as they were in your chats. Anyone who can read your vault (or a sync service) could use them. The library and search stay masked.");
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var ids = ChatList.SelectedItems.Count > 0
            ? ChatList.SelectedItems.OfType<ChatRow>().Select(r => r.Summary.Id).ToList()
            : _chats.Select(r => r.Summary.Id).ToList();
        if (ids.Count == 0) { Flow?.ShowMessage("Nothing to export", "Import some chats first, or clear the search."); return; }
        if (_settings.ObsidianExportFolder is null && !ChooseExportFolder()) return;
        _busy = true;
        try
        {
            LibraryStatus.Text = "Preparing notes…";
            var request = new ExportRequest { Folder = _settings.ObsidianExportFolder!, ChatIds = ids, KeepSecrets = Saved.KeepSecretsInExports };
            var plan = await Task.Run(() => new ObsidianExporter(_store).PlanAsync(request, CancellationToken.None));
            LibraryStatus.Text = string.Join(" ", plan.Notes);
            if (plan.Items.Count == 0) { Flow?.ShowMessage("Already up to date", plan.Notes.FirstOrDefault() ?? "Every note is already up to date."); return; }
            if (Flow is { } flow) await flow.ReviewAndRunAsync(plan.ToActionPlan(), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Flow?.ShowMessage("Couldn't prepare the export", ex.Message, isError: true);
        }
        finally { _busy = false; }
    }

    // =====================================================================  Agent chats on this PC

    private void ShowArchiveFolder()
    {
        var folder = Saved.ArchiveFolder;
        ArchiveFolderText.Text = folder is null ? "Archive folder: not chosen yet" : "Archive folder: " + folder;
        ArchiveFolderText.ToolTip = folder;
    }

    private bool ChooseArchiveFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for copies of your agent chats (another drive is a good choice)" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return false;
        var s = Saved;
        s.ArchiveFolder = dialog.FolderName;
        s.Store(_settings);
        ShowArchiveFolder();
        return true;
    }

    private async void OnChooseArchiveFolder(object sender, RoutedEventArgs e)
    {
        if (ChooseArchiveFolder()) await SafeAsync(RefreshAgentsAsync, "Couldn't list the agent chats");
    }

    private async void OnRefreshAgents(object sender, RoutedEventArgs e) => await SafeAsync(RefreshAgentsAsync, "Couldn't list the agent chats");

    private async Task RefreshAgentsAsync()
    {
        _agentsLoaded = true;
        AgentSelectedText.Text = "Looking for Claude Code and Codex chats…";
        var probe = _options.TryGetValue(AiChatsOptions.ProcessProbe, out var p) ? p as IProcessProbe : null;
        var clock = _options.TryGetValue(AiChatsOptions.Clock, out var c) ? c as TimeProvider : null;
        var archive = Saved.ArchiveFolder;
        var (sessions, live, archived) = await Task.Run(async () =>
        {
            var list = await AgentChatInventory.ListAsync(_locations, CancellationToken.None);
            var l = new LiveSessionDetector(probe, clock).Detect(_locations);
            var done = archive is null ? new HashSet<string>() : list.Where(s => AgentArchivePlanner.VerifyCopy(archive, s, CancellationToken.None).Verified).Select(s => s.TranscriptPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (list, l, done);
        });
        _live = live;
        _agents.Clear();
        foreach (var g in sessions.GroupBy(s => (s.Source, s.ProjectName, Month: s.LastWriteUtc.ToLocalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture)))
                     .OrderBy(g => g.Key.Source).ThenBy(g => g.Key.ProjectName, StringComparer.OrdinalIgnoreCase).ThenByDescending(g => g.Key.Month))
        {
            var list = g.ToList();
            var liveCount = list.Count(live.IsLive);
            var archivedCount = list.Count(s => archived.Contains(s.TranscriptPath));
            var state = new List<string>();
            if (archivedCount > 0) state.Add(archivedCount == list.Count ? "all archived" : $"{archivedCount} archived");
            if (liveCount > 0) state.Add(liveCount == list.Count ? "in use right now" : $"{liveCount} in use right now");
            _agents.Add(new AgentRow
            {
                Source = g.Key.Source,
                Project = g.Key.ProjectName,
                ProjectHint = AgentProjects.Hint(list.Select(s => s.ProjectPath).FirstOrDefault(p => p is not null), _locations.UserProfile),
                Month = g.Key.Month,
                Sessions = list,
                LiveCount = liveCount,
                StateText = state.Count == 0 ? "not archived" : string.Join(", ", state),
            });
        }
        AgentEmpty.Visibility = _agents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAgentBar();
    }

    private void OnAgentRowChecked(object sender, RoutedEventArgs e) => UpdateAgentBar();

    private void OnRemoveOkChanged(object sender, RoutedEventArgs e) => UpdateAgentBar();

    private IReadOnlyList<AgentSession> TickedSessions()
        => _agents.Where(a => a.IsChecked).SelectMany(a => a.Sessions).ToList();

    private void UpdateAgentBar()
    {
        var ticked = TickedSessions();
        var total = _agents.Sum(a => a.Sessions.Count);
        AgentSelectedText.Text = ticked.Count == 0
            ? $"{total:N0} agent chat{(total == 1 ? "" : "s")} · {Sizes.Format(_agents.Sum(a => a.Bytes))}"
            : $"{ticked.Count:N0} chat{(ticked.Count == 1 ? "" : "s")} ticked · {Sizes.Format(ticked.Sum(s => s.TotalBytes))}";
        AgentSelectedHint.Text = ticked.Count == 0 ? "Tick the ones to archive." : "";
        AgentSelectedHint.Visibility = ticked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Without an archive folder the button picks one first: say so instead of looking broken.
        bool hasFolder = Saved.ArchiveFolder is not null;
        ArchiveButton.Content = hasFolder ? "Archive a copy" : "Choose a folder to archive…";
        AutomationProperties.SetName(ArchiveButton, hasFolder ? "Archive a copy of the ticked agent chats" : "Choose an archive folder, then archive a copy of the ticked agent chats");
        ArchiveButton.IsEnabled = ticked.Count > 0;
        AddToLibraryButton.IsEnabled = ticked.Count > 0;
        RemoveOriginalsButton.IsEnabled = ticked.Count > 0 && RemoveOkBox.IsChecked == true && Saved.ArchiveFolder is not null;
    }

    private async void OnAddAgentsToLibrary(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var ticked = TickedSessions();
            var progress = new Progress<string>(m => AgentSelectedText.Text = m);
            var summary = await Task.Run(() => _importer.ImportAgentChatsAsync(_locations, ticked, progress, CancellationToken.None));
            AgentSelectedText.Text = summary.Message;
            await RefreshLibraryAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Flow?.ShowMessage("Couldn't add them", ex.Message, isError: true);
        }
        finally { _busy = false; }
    }

    private async void OnArchive(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (Saved.ArchiveFolder is null && !ChooseArchiveFolder()) return;
        var archive = Saved.ArchiveFolder!;
        var ticked = TickedSessions();
        var live = _live ?? LiveSessions.None(DateTime.UtcNow);
        var withMarkdown = MarkdownCopyBox.IsChecked == true;
        _busy = true;
        try
        {
            AgentSelectedText.Text = "Preparing the copies…";
            var plan = await Task.Run(async () =>
            {
                var markdown = withMarkdown ? await ArchiveMarkdown.BuildAsync(_locations, ticked, CancellationToken.None) : null;
                return AgentArchivePlanner.PlanCopy(ticked, archive, live, markdown, CancellationToken.None);
            });
            if (plan.Items.Count == 0)
            {
                Flow?.ShowMessage("Nothing to copy", string.Join(" ", plan.Notes.DefaultIfEmpty("These chats are already in the archive.")));
                return;
            }
            AgentSelectedText.Text = string.Join(" ", plan.Notes);
            if (Flow is { } flow) await flow.ReviewAndRunAsync(plan.ToActionPlan("Archive a copy of agent chats"), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Flow?.ShowMessage("Couldn't prepare the copies", ex.Message, isError: true);
        }
        finally
        {
            _busy = false;
            await SafeAsync(RefreshAgentsAsync, "Couldn't list the agent chats");
        }
    }

    private async void OnRemoveOriginals(object sender, RoutedEventArgs e)
    {
        if (_busy || Saved.ArchiveFolder is not { } archive || RemoveOkBox.IsChecked != true) return;
        var ticked = TickedSessions();
        _busy = true;
        try
        {
            // Live state is checked again right now: a session may have started since the list was made.
            var probe = _options.TryGetValue(AiChatsOptions.ProcessProbe, out var p) ? p as IProcessProbe : null;
            var clock = _options.TryGetValue(AiChatsOptions.Clock, out var c) ? c as TimeProvider : null;
            var plan = await Task.Run(() =>
            {
                var live = new LiveSessionDetector(probe, clock).Detect(_locations);
                return AgentArchivePlanner.PlanRemoveOriginals(ticked, archive, live, CancellationToken.None);
            });
            if (plan.Items.Count == 0)
            {
                Flow?.ShowMessage("Nothing to remove", string.Join(" ", plan.Notes.DefaultIfEmpty("No ticked chat has a checked copy yet.")));
                return;
            }
            if (Flow is { } flow) await flow.ReviewAndRunAsync(plan.ToActionPlan("Move archived agent chats to quarantine"), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Flow?.ShowMessage("Couldn't prepare the removal", ex.Message, isError: true);
        }
        finally
        {
            // The OK is for one run only.
            RemoveOkBox.IsChecked = false;
            _busy = false;
            await SafeAsync(RefreshAgentsAsync, "Couldn't list the agent chats");
        }
    }

    // =====================================================================  Exposed secrets

    private async Task RefreshSecretsAsync()
    {
        IReadOnlyList<SecretReportRow> rows;
        try { rows = await _store.SecretsAsync(liveOnly: AllSecretsBox.IsChecked != true, CancellationToken.None); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            if (ChatLibraryStore.IsDamaged(ex)) ShowDamaged();
            rows = Array.Empty<SecretReportRow>();
        }
        _secrets.Clear();
        foreach (var r in rows) _secrets.Add(new SecretRow(r));
        // The warning only goes with keys on screen; with none, the green "No exposed keys" says it all.
        SecretsWarning.IsOpen = _secrets.Count > 0;
        SecretsEmpty.Visibility = _secrets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnAllSecretsChanged(object sender, RoutedEventArgs e) => await SafeAsync(RefreshSecretsAsync, "Couldn't list the keys");

    private async void OnOpenSecretChat(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SecretRow row) return;
        await SafeAsync(async () =>
        {
            SearchBox.Text = "";
            FilterList.SelectedIndex = 0;
            Tabs.SelectedIndex = 0;
            await ShowTabAsync(0);
            await RefreshChatsAsync();
            var target = _chats.FirstOrDefault(c => c.Summary.Id == row.Row.ChatId);
            if (target is null) return;
            ChatList.SelectedItem = target;
            ChatList.ScrollIntoView(target);
        }, "Couldn't open that chat");
    }

    // =====================================================================  How to export

    private void BuildGuide()
    {
        void Heading(string text) => GuideContent.Children.Add(new TextBlock { Text = text, Style = TryStyle("SectionTitle"), Margin = new Thickness(0, 14, 0, 6) });
        void Para(string text) => GuideContent.Children.Add(new TextBlock { Text = text, Style = TryStyle("Body"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        void Steps(params string[] steps)
        {
            for (int i = 0; i < steps.Length; i++)
                GuideContent.Children.Add(new TextBlock { Text = $"{i + 1}.  {steps[i]}", Style = TryStyle("Body"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 4) });
        }

        Para("PC Tidy never signs in to ChatGPT or Claude, never uses your cookies or passwords, and never calls their APIs. You ask each service for your data yourself; it emails you a download link; then you import the zip here. Nothing is uploaded anywhere.");
        Heading("ChatGPT");
        Steps("Open ChatGPT and sign in.", "Click your profile picture, then Settings.", "Open Data controls and choose Export data, then Confirm export.",
            "Wait for the email from OpenAI and download the zip. The link stops working after 24 hours.",
            "In PC Tidy, choose Import export zip… and pick that zip. You don't need to unzip it.");
        Para("Newer exports split chats over several files (conversations-000.json, conversations-001.json …). PC Tidy reads both kinds.");
        Heading("Claude");
        Steps("Open claude.ai and sign in.", "Click your name, then Settings.", "Open Privacy and choose Export data.",
            "Wait for the email from Anthropic and download the zip.", "In PC Tidy, choose Import export zip… and pick that zip.");
        Heading("Claude Code and Codex");
        Para("These keep their chats on this PC (in the .claude and .codex folders of your user folder). Open \"Agent chats on this PC\" to see them, add them to the library or archive a copy. PC Tidy only reads the chat files: their settings, logins and memory files are never opened.");
        Para(AiChatsScanner.CleanupAdvice);
        Heading("Importing again");
        Para("Import a newer export at any time: chats already in the library are updated, not duplicated, and your tags, pins, folders and topics are kept.");
    }

    private static Style? TryStyle(string key) => Application.Current?.TryFindResource(key) as Style;
}
