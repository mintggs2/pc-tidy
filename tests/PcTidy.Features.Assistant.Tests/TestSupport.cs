using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Assistant.Engine;
using PcTidy.Features.Assistant.Ollama;

namespace PcTidy.Features.Assistant.Tests;

/// <summary>
/// A pretend Ollama. It is an HttpMessageHandler, so it sits where the socket transport would, BEHIND the
/// loopback check: tests exercise the real request pipeline and nothing ever touches a network.
/// </summary>
public sealed class FakeOllama : HttpMessageHandler
{
    public sealed record Seen(HttpMethod Method, Uri Uri, string? Body);

    public List<Seen> Requests { get; } = new();
    public string Version { get; set; } = "0.12.3";
    public List<OllamaModel> Models { get; } = new() { new OllamaModel("llama3.2:3b", 2_019_393_189, "3.2B", "llama", "Q4_K_M") };
    /// <summary>The model's raw message content for a chat request (gets the request body).</summary>
    public Func<JsonObject, string> Chat { get; set; } = _ => """{"answer":"Hello.","suggestions":[]}""";
    /// <summary>Set to make every request fail like a closed port.</summary>
    public bool NotRunning { get; set; }
    /// <summary>Answer every request with this redirect.</summary>
    public string? RedirectTo { get; set; }
    public TimeSpan Delay { get; set; }
    public HttpStatusCode? ChatStatus { get; set; }
    public string? ChatErrorBody { get; set; }

    public JsonObject? LastChatRequest { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new Seen(request.Method, request.RequestUri!, body));
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (NotRunning) throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it.");
        if (RedirectTo is not null)
        {
            var r = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            r.Headers.Location = new Uri(RedirectTo);
            return r;
        }
        switch (request.RequestUri!.AbsolutePath)
        {
            case "/api/version":
                return Json(new JsonObject { ["version"] = Version });
            case "/api/tags":
                var models = new JsonArray(Models.Select(m => (JsonNode)new JsonObject
                {
                    ["name"] = m.Name, ["model"] = m.Name, ["size"] = m.SizeBytes,
                    ["details"] = new JsonObject { ["parameter_size"] = m.ParameterSize, ["family"] = m.Family, ["quantization_level"] = m.Quantization },
                }).ToArray());
                return Json(new JsonObject { ["models"] = models });
            case "/api/chat":
                LastChatRequest = JsonNode.Parse(body!)!.AsObject();
                if (ChatStatus is { } status)
                    return new HttpResponseMessage(status) { Content = new StringContent(ChatErrorBody ?? "", Encoding.UTF8, "application/json") };
                var content = Chat(LastChatRequest);
                return Json(new JsonObject
                {
                    ["model"] = LastChatRequest["model"]?.GetValue<string>(),
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
                    ["done"] = true,
                });
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"not found"}""") };
        }
    }

    private static HttpResponseMessage Json(JsonNode node)
        => new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
}

/// <summary>Host lookups from a table; anything else "doesn't exist". Never real DNS.</summary>
public sealed class FakeResolver : IHostResolver
{
    private readonly Dictionary<string, IPAddress[]> _table = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Asked { get; } = new();

    public FakeResolver Map(string host, params string[] addresses)
    {
        _table[host] = addresses.Select(IPAddress.Parse).ToArray();
        return this;
    }

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        Asked.Add(host);
        return _table.TryGetValue(host, out var a) ? Task.FromResult(a) : throw new System.Net.Sockets.SocketException(11001);
    }
}

/// <summary>Builds scan results the way feature scanners do (QuarantinePath actions, badges, reasons).</summary>
public static class Scans
{
    public static ScanItem Item(string title, string path, long bytes, Badge badge = Badge.Safe, bool reversible = true, string? disabled = null, string feature = "cleanup")
        => new()
        {
            Title = title,
            Detail = Path.GetDirectoryName(path),
            Reason = "Rebuilt automatically.",
            Badge = badge,
            SizeBytes = bytes,
            DisabledReason = disabled,
            Actions = new[]
            {
                new PlanItem
                {
                    Kind = badge == Badge.Permanent ? ActionKind.EmptyRecycleBin : ActionKind.QuarantinePath,
                    FeatureId = feature, Description = "Quarantine " + title, SourcePath = path, ExpectedSize = bytes, Reversible = reversible,
                },
            },
        };

    public static ScanResult Result(string featureId, string title, params (string Group, ScanItem[] Items)[] groups)
        => new()
        {
            FeatureId = featureId,
            Title = title,
            Groups = groups.Select(g => new ScanGroup { Id = featureId + "." + g.Group, Title = g.Group, Items = g.Items }).ToList(),
        };

    /// <summary>A typical Clean result: two caches, a big download, a protected row and a permanent one.</summary>
    public static ScanResult Clean(string root = @"C:\Users\tester")
        => Result("cleanup", "Clean",
            ("Browser caches", new[]
            {
                Item("Chrome cache", root + @"\AppData\Local\Google\Chrome\User Data\Default\Cache", 1_200_000_000),
                Item("Edge cache", root + @"\AppData\Local\Microsoft\Edge\User Data\Default\Cache", 300_000_000),
            }),
            ("Old downloads", new[] { Item("setup-old.exe", root + @"\Downloads\setup-old.exe", 2_500_000_000, Badge.Check) }),
            ("Not cleaned", new[] { Item("Windows Update store", @"C:\Windows\SoftwareDistribution", 900_000_000, disabled: "Windows keeps this.") }),
            ("Recycle Bin", new[] { Item("Recycle Bin", @"C:\$Recycle.Bin", 700_000_000, Badge.Permanent, reversible: false) }));

    public static ISystemInfo System(params DriveDetails[] drives) => new FakeSystem(drives.Length > 0 ? drives
        : new[] { new DriveDetails(@"C:\", "Windows", DriveMedia.Ssd, 512L << 30, 9L << 30, true) });

    private sealed class FakeSystem : ISystemInfo
    {
        public FakeSystem(IReadOnlyList<DriveDetails> drives) => Drives = drives;
        public GpuVendor PrimaryGpu => GpuVendor.Unknown;
        public IReadOnlyList<string> GpuNames => Array.Empty<string>();
        public bool IsLaptop => false;
        public bool HasBattery => false;
        public bool IsAdmin => false;
        public int WindowsBuild => 26100;
        public bool RebootPending => false;
        public IReadOnlyList<DriveDetails> Drives { get; }
        public IReadOnlySet<string> RunningProcesses() => new HashSet<string>();
    }

    /// <summary>The model's reply JSON.</summary>
    public static string Reply(string answer, params (string Action, string[] Ids)[] suggestions)
        => new JsonObject
        {
            ["answer"] = answer,
            ["suggestions"] = new JsonArray(suggestions.Select(s => (JsonNode)new JsonObject
            {
                ["actionId"] = s.Action,
                ["itemIds"] = new JsonArray(s.Ids.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
            }).ToArray()),
        }.ToJsonString();

    /// <summary>The handle the data block gave an item, by title.</summary>
    public static string HandleOf(ScanDataBlock data, string title) => data.Handles.Values.Single(h => h.Item.Title == title).Handle;
}

/// <summary>A minimal service provider for pages.</summary>
public sealed class Services : IServiceProvider
{
    private readonly Dictionary<Type, object> _map = new();
    public Services Add<T>(T service) where T : class { _map[typeof(T)] = service; return this; }
    public object? GetService(Type serviceType) => _map.TryGetValue(serviceType, out var s) ? s : null;
}

/// <summary>A sandbox folder of this test area under testbed\assistant\, removed afterwards.</summary>
public sealed class TestFolder : IDisposable
{
    public TestFolder(string name)
    {
        Root = Path.Combine(TestbedRoot(), "assistant", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            // A generated fake PC has read-only files and junctions: its own cleaner removes it safely.
            if (File.Exists(Path.Combine(Root, "Sim", "testbed.json"))) PcTidy.TestbedGen.TestbedBuilder.Delete(Root);
            else Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            /* a scanner may still hold a handle for a moment; the next run uses a new folder */
        }
    }

    private static string TestbedRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        if (d == null) throw new InvalidOperationException("PcTidy.sln not found above " + AppContext.BaseDirectory);
        return Path.Combine(d.FullName, "testbed");
    }
}

/// <summary>
/// Runs WPF code on ONE long-lived STA thread with a running dispatcher, so async page code can resume and every
/// UI test shares the same Application resources (they belong to the thread that created them).
/// </summary>
public static class Sta
{
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "assistant-ui-tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    });

    public static void Run(Func<Task> body)
    {
        var task = Ui.Value.InvokeAsync(async () =>
        {
            EnsureAppResources();
            await body();
        }).Task.Unwrap();
        try
        {
            if (!task.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("UI test didn't finish in 60 s.");
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            throw new Xunit.Sdk.XunitException(ex.InnerException.ToString());
        }
    }

    private static void EnsureAppResources()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.Contains("TidyCard")) return;
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PcTidy.UI;component/Themes/TidyDark.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PcTidy.UI;component/Themes/Tidy.xaml") });
    }
}
