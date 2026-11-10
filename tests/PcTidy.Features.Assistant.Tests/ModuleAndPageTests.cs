using System.Collections;
using System.Resources;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Assistant.Engine;
using PcTidy.Features.Assistant.UI;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;
using Xunit;

namespace PcTidy.Features.Assistant.Tests;

/// <summary>The module's identity, the off-by-default switch, the chat page's states at 1000 px, and pre-selection.</summary>
public class ModuleAndPageTests
{
    [Fact]
    public void Module_is_Ask_PC_Tidy_in_the_You_group_for_v3_with_no_scanners()
    {
        var m = new AssistantModule();
        Assert.Equal("assistant", m.Id);
        Assert.Equal("Ask PC Tidy", m.Title);
        Assert.Equal(NavGroup.You, m.Group);
        Assert.Equal(Release.V3, m.Release);
        Assert.Equal(ModuleRisk.Safe, m.Risk);
        Assert.Empty(m.Scanners);
        Assert.Contains("Nothing leaves this PC", m.Summary);
    }

    [Fact]
    public void The_icon_exists_in_the_icon_font_so_it_renders()
    {
        var m = new AssistantModule();
        Assert.True(Enum.TryParse<SymbolRegular>(m.Icon, out var symbol), m.Icon + " is not a WPF-UI symbol");
        using var folder = new TestFolder("font");
        var font = Path.Combine(folder.Root, "regular.ttf");
        using (var stream = typeof(SymbolIcon).Assembly.GetManifestResourceStream("Wpf.Ui.g.resources")!)
        using (var reader = new ResourceReader(stream))
        {
            var entry = reader.Cast<DictionaryEntry>().Single(e => (string)e.Key == "resources/fonts/fluentsystemicons-regular.ttf");
            using var file = File.Create(font);
            ((Stream)entry.Value!).CopyTo(file);
        }
        var glyphs = new GlyphTypeface(new Uri(font));
        Assert.True(glyphs.CharacterToGlyphMap.ContainsKey((int)symbol), $"{m.Icon} has no glyph in WPF-UI's icon font");
    }

    [Fact]
    public void Settings_are_off_by_default_and_round_trip()
    {
        var settings = new AppSettings();
        var s = AssistantSettings.From(settings);
        Assert.False(s.Enabled);
        Assert.Null(s.Model);
        Assert.Null(s.Port);
        s.Enabled = true;
        s.Model = "llama3.2:3b";
        s.Store(settings);
        var json = System.Text.Json.JsonSerializer.Serialize(settings, AppSettings.Json);
        var back = AssistantSettings.From(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, AppSettings.Json)!);
        Assert.True(back.Enabled);
        Assert.Equal("llama3.2:3b", back.Model);
    }

    [Fact]
    public void Off_by_default_the_page_contacts_nothing()
    {
        using var folder = new TestFolder("page-off");
        var fake = new FakeOllama();
        Sta.Run(async () =>
        {
            var (page, _) = NewPage(folder, fake, enabled: false);
            Layout(page);
            await Task.Yield();
            Assert.Equal(AssistantPageState.Off, page.State);
            Assert.Contains(Texts(page), t => t.Contains("127.0.0.1, which is this PC"));
            Assert.Contains(Texts(page), t => t == "Nothing leaves this PC");
        });
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public void Without_Ollama_the_page_says_not_available_with_install_steps()
    {
        using var folder = new TestFolder("page-none");
        Sta.Run(async () =>
        {
            var (page, _) = NewPage(folder, new FakeOllama { NotRunning = true }, enabled: true);
            await page.CheckAsync();
            Layout(page);
            Assert.Equal(AssistantPageState.NotInstalled, page.State);
            Assert.Contains(Texts(page), t => t == "Ollama isn't installed yet");
            Assert.Contains(Texts(page), t => t.Contains("ollama pull"));
        });
    }

    [Fact]
    public void With_Ollama_but_no_models_the_page_suggests_small_ones_without_downloading()
    {
        using var folder = new TestFolder("page-nomodels");
        var fake = new FakeOllama();
        fake.Models.Clear();
        Sta.Run(async () =>
        {
            var (page, _) = NewPage(folder, fake, enabled: true);
            await page.CheckAsync();
            Layout(page);
            Assert.Equal(AssistantPageState.NoModels, page.State);
            Assert.Contains(Texts(page), t => t.Contains("llama3.2:3b"));
        });
        Assert.DoesNotContain(fake.Requests, r => r.Uri.AbsolutePath != "/api/version" && r.Uri.AbsolutePath != "/api/tags");
    }

    [Fact]
    public void The_network_warning_shows_when_OLLAMA_HOST_listens_on_all_interfaces()
    {
        using var folder = new TestFolder("page-warn");
        var fake = new FakeOllama();
        Sta.Run(async () =>
        {
            var (page, _) = NewPage(folder, fake, enabled: true, ollamaHost: "0.0.0.0:11500");
            await page.CheckAsync();
            Assert.Equal(AssistantPageState.Ready, page.State);
            Assert.Contains(Descendants(page).OfType<InfoBar>(), b => b.Message.Contains("whole network"));
        });
        Assert.All(fake.Requests, r => Assert.Equal(new Uri("http://127.0.0.1:11500/").Authority, r.Uri.Authority)); // its port, our address
    }

    [Fact]
    public void Asking_shows_the_answer_and_Review_opens_the_section_with_those_items()
    {
        using var folder = new TestFolder("page-ask");
        var fake = new FakeOllama
        {
            Chat = req =>
            {
                var data = req["messages"]!.AsArray()[1]!["content"]!.GetValue<string>();
                var id = Regex.Match(data, "\"id\":\"(i[0-9]+)\",\"name\":\"Chrome cache\"").Groups[1].Value;
                return Scans.Reply("Chrome's cache is the biggest thing PC Tidy can clean.", (AssistantActions.ReviewInSection, new[] { id }));
            },
        };
        fake.Models.Add(new Ollama.OllamaModel("qwen2.5:7b", 4_700_000_000, "7.6B", "qwen2"));
        Sta.Run(async () =>
        {
            var (page, services) = NewPage(folder, fake, enabled: true);
            var cache = (ScanResultsCache)services.GetService(typeof(IScanResultsCache))!;
            var clean = Scans.Clean();
            cache.Publish(clean);
            var review = new RecordingReviewHost();
            services.Add<ISectionReviewHost>(review);

            await page.CheckAsync();
            Assert.Equal(AssistantPageState.Ready, page.State);
            Assert.Equal("qwen2.5:7b", page.SelectedModel); // the biggest model that fits an 8 GB card
            Assert.Equal(2, page.ModelChoices.Count);

            await page.AskAsync("Why is my C: drive full?");
            Layout(page);
            var answer = Assert.Single(page.Answers);
            Assert.Contains("Chrome's cache", answer.Text);
            var button = Descendants(page).OfType<Wpf.Ui.Controls.Button>().Single(b => b.Content is string s && s.StartsWith("Review in"));
            Assert.Equal("Review in Clean →", button.Content);

            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var request = Assert.Single(review.Requests);
            Assert.Equal("cleanup", request.FeatureId);
            Assert.Equal(new[] { clean.AllItems.Single(i => i.Title == "Chrome cache").Id }, request.ItemIds);

            // After a rescan the old button refuses instead of pre-selecting stale items.
            cache.Publish(Scans.Clean());
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Single(review.Requests);
            Assert.Contains(((RecordingFlowHost)services.GetService(typeof(IFlowHost))!).Messages, m => m.Title == "These results changed");
        });
        var chat = fake.Requests.Single(r => r.Uri.AbsolutePath == "/api/chat");
        Assert.Equal("127.0.0.1", chat.Uri.Host);
    }

    [Fact]
    public void Picking_a_model_is_remembered()
    {
        using var folder = new TestFolder("page-pick");
        var fake = new FakeOllama();
        fake.Models.Add(new Ollama.OllamaModel("gemma2:2b", 1_600_000_000));
        Sta.Run(async () =>
        {
            var (page, services) = NewPage(folder, fake, enabled: true);
            await page.CheckAsync();
            var picker = Descendants(page).OfType<System.Windows.Controls.ComboBox>().Single();
            picker.SelectedItem = page.ModelChoices.Single(c => c.Name == "gemma2:2b");
            Assert.Equal("gemma2:2b", AssistantSettings.From((AppSettings)services.GetService(typeof(AppSettings))!).Model);
        });
    }

    [Fact]
    public void An_Ollama_failure_mid_question_becomes_a_calm_message()
    {
        using var folder = new TestFolder("page-fail");
        var fake = new FakeOllama();
        Sta.Run(async () =>
        {
            var (page, _) = NewPage(folder, fake, enabled: true);
            await page.CheckAsync();
            fake.NotRunning = true;
            await page.AskAsync("What's safe to clean first?");
            Assert.Empty(page.Answers);
            Assert.Contains(Texts(page), t => t.Contains("isn't running") && t.Contains("Nothing was changed"));
        });
    }

    // ---------------- pre-selection on the shared preview ----------------

    [Fact]
    public void A_review_request_ticks_exactly_its_items_and_opens_their_groups()
    {
        var clean = Scans.Clean();
        var model = new SelectionModel(showSectionHeaders: false);
        model.Load(new[] { new SectionModel("cleanup", "Clean", SymbolRegular.Broom24, clean) }, (_, _, i) => i.Item.Badge == PcTidy.Core.Model.Badge.Safe);
        var wanted = clean.AllItems.Where(i => i.Title is "setup-old.exe" or "Recycle Bin").Select(i => i.Id).ToList();

        var found = SectionReview.TryApply(model, new SectionReviewRequest("cleanup", "Clean", wanted, clean.FinishedUtc));

        Assert.Equal(1, found); // the Permanent Recycle Bin row isn't tickable
        var ticked = model.CheckedItems().Select(i => i.Item.Title).ToList();
        Assert.Equal(new[] { "setup-old.exe" }, ticked); // the Safe caches ticked by default were unticked
        Assert.True(model.Sections[0].Groups.Single(g => g.Title == "Old downloads").IsExpanded);
    }

    [Fact]
    public void A_review_request_for_another_scan_changes_nothing()
    {
        var clean = Scans.Clean();
        var model = new SelectionModel(showSectionHeaders: false);
        model.Load(new[] { new SectionModel("cleanup", "Clean", SymbolRegular.Broom24, clean) }, (_, _, i) => i.Item.Badge == PcTidy.Core.Model.Badge.Safe);
        var before = model.CheckedItems().Select(i => i.Item.Id).ToList();

        Assert.Equal(0, SectionReview.TryApply(model, new SectionReviewRequest("cleanup", "Clean", new[] { Guid.NewGuid().ToString("N") }, DateTime.UtcNow)));
        Assert.Equal(0, SectionReview.TryApply(model, new SectionReviewRequest("other", "Other", before, DateTime.UtcNow)));
        Assert.Equal(before, model.CheckedItems().Select(i => i.Item.Id));
    }

    // ---------------- helpers ----------------

    private static (AssistantPage Page, Services Services) NewPage(TestFolder folder, FakeOllama fake, bool enabled, string? ollamaHost = null)
    {
        var settings = new AppSettings { DataRoot = Path.Combine(folder.Root, "Data") };
        new AssistantSettings { Enabled = enabled }.Store(settings);
        var services = new Services()
            .Add(settings)
            .Add<IKnownLocations>(new SandboxKnownLocations(folder.Root, SandboxProfile.Minimal))
            .Add<ISystemInfo>(Scans.System())
            .Add<IScanResultsCache>(new ScanResultsCache())
            .Add<IFlowHost>(new RecordingFlowHost())
            .Add<IEnumerable<IFeatureModule>>(new IFeatureModule[] { new CleanModuleStub() });
        var module = new AssistantModule(fake, name => name == "OLLAMA_HOST" ? ollamaHost : null);
        return ((AssistantPage)module.CreatePage(services)!, services);
    }

    private static void Layout(FrameworkElement page)
    {
        page.Measure(new Size(1000, 700));
        page.Arrange(new Rect(0, 0, 1000, 700));
        page.UpdateLayout();
        Assert.True(page.DesiredSize.Width <= 1000, $"page wants {page.DesiredSize.Width} px");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static IEnumerable<string> Texts(DependencyObject root)
        => Descendants(root).OfType<System.Windows.Controls.TextBlock>().Select(t => t.Text)
            .Concat(Descendants(root).OfType<PcTidy.UI.Controls.BadgePill>().Select(p => p.Text));

    private sealed class RecordingReviewHost : ISectionReviewHost
    {
        public List<SectionReviewRequest> Requests { get; } = new();
        public bool OpenForReview(SectionReviewRequest request) { Requests.Add(request); return true; }
    }

    private sealed class RecordingFlowHost : IFlowHost
    {
        public List<(string Title, string Message)> Messages { get; } = new();
        public Task<RunRecord?> ReviewAndRunAsync(ActionPlan plan, CancellationToken ct) => throw new InvalidOperationException("The assistant must never run a plan.");
        public Task<RunRecord?> SimulateAsync(ActionPlan plan, CancellationToken ct) => throw new InvalidOperationException("The assistant must never run a plan.");
        public Task<RunRecord?> RunApprovedAsync(ActionPlan plan, CancellationToken ct) => throw new InvalidOperationException("The assistant must never run a plan.");
        public void OpenInExplorer(string path) { }
        public void ShowMessage(string title, string message, bool isError = false) => Messages.Add((title, message));
    }

    private sealed class CleanModuleStub : IFeatureModule
    {
        public string Id => "cleanup";
        public string Title => "Clean";
        public string Summary => "Clean";
        public NavGroup Group => NavGroup.CleanUp;
        public string Icon => "Broom24";
        public int Order => 10;
        public Release Release => Release.V1;
        public IReadOnlyList<IFeatureScanner> Scanners => Array.Empty<IFeatureScanner>();
        public object? CreatePage(IServiceProvider services) => null;
    }
}
