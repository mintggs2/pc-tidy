using System.Net;
using System.Text.Json.Nodes;
using PcTidy.Core;
using PcTidy.Features.Assistant.Engine;
using PcTidy.Features.Assistant.Ollama;
using Xunit;

namespace PcTidy.Features.Assistant.Tests;

/// <summary>The Ollama client against a fake HTTP handler: what it sends, what it reads, how it fails.</summary>
public class OllamaClientTests
{
    [Fact]
    public async Task Reads_version_and_installed_models()
    {
        var fake = new FakeOllama();
        fake.Models.Add(new OllamaModel("qwen2.5:7b", 4_700_000_000, "7.6B", "qwen2", "Q4_K_M"));
        using var client = new OllamaClient(transport: fake);

        Assert.Equal("0.12.3", await client.GetVersionAsync(CancellationToken.None));
        var models = await client.ListModelsAsync(CancellationToken.None);
        Assert.Equal(new[] { "llama3.2:3b", "qwen2.5:7b" }, models.Select(m => m.Name));
        Assert.Equal("7.6B", models[1].ParameterSize);
        Assert.All(fake.Requests, r => Assert.Equal("127.0.0.1", r.Uri.Host));
        Assert.All(fake.Requests, r => Assert.Equal(11434, r.Uri.Port));
    }

    [Fact]
    public async Task Chat_is_one_non_streaming_request_constrained_by_the_reply_schema()
    {
        var fake = new FakeOllama { Chat = _ => """{"answer":"Your Downloads folder is big.","suggestions":[]}""" };
        using var client = new OllamaClient(new OllamaEndpoint(11500), fake);
        var messages = new[] { new OllamaChatMessage("system", "rules"), new OllamaChatMessage("user", "hi") };

        var content = await client.ChatAsync("llama3.2:3b", messages, AssistantPrompt.ReplySchema, CancellationToken.None);

        Assert.Contains("Downloads", content);
        var seen = Assert.Single(fake.Requests);
        Assert.Equal(new Uri("http://127.0.0.1:11500/api/chat"), seen.Uri);
        var body = fake.LastChatRequest!;
        Assert.False(body["stream"]!.GetValue<bool>());
        Assert.Equal("llama3.2:3b", body["model"]!.GetValue<string>());
        var format = body["format"]!.AsObject();
        Assert.Equal("object", format["type"]!.GetValue<string>());
        var actionEnum = format["properties"]!["suggestions"]!["items"]!["properties"]!["actionId"]!["enum"]!.AsArray();
        Assert.Equal(new[] { AssistantActions.ReviewInSection }, actionEnum.Select(e => e!.GetValue<string>()));
        Assert.Equal(2, body["messages"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_closed_port_is_unavailable_not_a_crash()
    {
        using var client = new OllamaClient(transport: new FakeOllama { NotRunning = true });
        var ex = await Assert.ThrowsAsync<OllamaUnavailableException>(() => client.GetVersionAsync(CancellationToken.None));
        Assert.Equal("Ollama isn't running on this PC.", ex.Message);
    }

    [Fact]
    public async Task A_slow_Ollama_times_out_with_a_friendly_message()
    {
        var fake = new FakeOllama { Delay = TimeSpan.FromSeconds(5) };
        using var client = new OllamaClient(transport: fake, probeTimeout: TimeSpan.FromMilliseconds(100), chatTimeout: TimeSpan.FromMilliseconds(100));
        var probe = await Assert.ThrowsAsync<OllamaUnavailableException>(() => client.GetVersionAsync(CancellationToken.None));
        Assert.Contains("in time", probe.Message);
        var chat = await Assert.ThrowsAsync<OllamaUnavailableException>(() =>
            client.ChatAsync("m", new[] { new OllamaChatMessage("user", "q") }, AssistantPrompt.ReplySchema, CancellationToken.None));
        Assert.Contains("smaller model", chat.Message);
    }

    [Fact]
    public async Task The_callers_stop_is_a_cancellation_not_an_error()
    {
        var fake = new FakeOllama { Delay = TimeSpan.FromSeconds(5) };
        using var client = new OllamaClient(transport: fake);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ChatAsync("m", new[] { new OllamaChatMessage("user", "q") }, AssistantPrompt.ReplySchema, cts.Token));
    }

    [Fact]
    public async Task Ollama_errors_become_plain_messages()
    {
        var fake = new FakeOllama { ChatStatus = HttpStatusCode.NotFound, ChatErrorBody = """{"error":"model 'gone:7b' not found"}""" };
        using var client = new OllamaClient(transport: fake);
        var ex = await Assert.ThrowsAsync<OllamaUnavailableException>(() =>
            client.ChatAsync("gone:7b", new[] { new OllamaChatMessage("user", "q") }, AssistantPrompt.ReplySchema, CancellationToken.None));
        Assert.Contains("isn't installed", ex.Message);

        fake.ChatStatus = HttpStatusCode.InternalServerError;
        fake.ChatErrorBody = "not json at all";
        var ex2 = await Assert.ThrowsAsync<OllamaUnavailableException>(() =>
            client.ChatAsync("m", new[] { new OllamaChatMessage("user", "q") }, AssistantPrompt.ReplySchema, CancellationToken.None));
        Assert.Contains("500", ex2.Message);
    }

    [Fact]
    public async Task Not_installed_and_installed_but_stopped_are_told_apart()
    {
        using var folder = new TestFolder("probe");
        var locations = new SandboxKnownLocations(folder.Root, SandboxProfile.Minimal);
        using var client = new OllamaClient(transport: new FakeOllama { NotRunning = true });
        var probe = new OllamaProbe(client, locations);

        var none = await probe.CheckAsync(CancellationToken.None);
        Assert.Equal(OllamaState.NotInstalled, none.State);
        Assert.Equal("Ollama isn't installed yet", OllamaProbe.NotAvailableTitle);

        var exe = Path.Combine(locations.LocalAppData, "Programs", "Ollama", "ollama.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        await File.WriteAllBytesAsync(exe, new byte[] { 0x4D, 0x5A }); // sandbox stand-in, never run
        var stopped = await probe.CheckAsync(CancellationToken.None);
        Assert.Equal(OllamaState.InstalledNotRunning, stopped.State);
    }

    [Fact]
    public async Task A_running_Ollama_reports_its_models()
    {
        using var folder = new TestFolder("running");
        using var client = new OllamaClient(transport: new FakeOllama());
        var status = await new OllamaProbe(client, new SandboxKnownLocations(folder.Root)).CheckAsync(CancellationToken.None);
        Assert.True(status.IsRunning);
        Assert.Equal("0.12.3", status.Version);
        Assert.Single(status.Models);
    }

    [Fact]
    public void Install_instructions_are_text_only_and_never_download()
    {
        Assert.Contains("ollama.com", OllamaProbe.InstallInstructions);
        Assert.DoesNotContain("http", OllamaProbe.InstallInstructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never downloads", OllamaProbe.InstallInstructions);
        Assert.Contains("never downloads", ModelAdvice.NoModelsText);
    }

    [Theory]
    [InlineData("0.0.0.0", true, null)]
    [InlineData("0.0.0.0:11434", true, 11434)]
    [InlineData("http://0.0.0.0:11434", true, 11434)]
    [InlineData("[::]:11434", true, 11434)]
    [InlineData("192.168.1.5:11434", true, 11434)]
    [InlineData("127.0.0.1:11500", false, 11500)]
    [InlineData("localhost", false, null)]
    [InlineData(":11600", false, 11600)]
    [InlineData("[::1]:11700", false, 11700)]
    public void OLLAMA_HOST_on_the_network_is_warned_about_and_its_port_is_used(string value, bool warns, int? port)
    {
        var s = OllamaHostSetting.Read(name => name == "OLLAMA_HOST" ? value : null);
        Assert.Equal(warns, s.NetworkWarning is not null);
        Assert.Equal(port, s.Port);
        if (warns) Assert.Contains("only talks to it on this PC", s.NetworkWarning);
    }

    [Fact]
    public void No_OLLAMA_HOST_means_no_warning_and_the_default_port()
    {
        var s = OllamaHostSetting.Read(_ => null);
        Assert.Null(s.NetworkWarning);
        Assert.Null(s.Port);
    }

    [Fact]
    public void The_model_picker_prefers_models_that_fit_an_8_GB_card_and_hides_embedding_models()
    {
        var installed = new[]
        {
            new OllamaModel("llama3.1:70b", 40_000_000_000, "70B", "llama"),
            new OllamaModel("nomic-embed-text:latest", 274_000_000, "137M", "nomic-bert"),
            new OllamaModel("llama3.2:3b", 2_000_000_000, "3.2B", "llama"),
            new OllamaModel("qwen2.5:7b", 4_700_000_000, "7.6B", "qwen2"),
        };
        var choices = ModelAdvice.Choices(installed);
        Assert.Equal(new[] { "qwen2.5:7b", "llama3.2:3b", "llama3.1:70b" }, choices.Select(c => c.Name));
        Assert.True(choices[0].FitsTypicalGpu);
        Assert.Contains("good fit", choices[0].Label);
        Assert.Contains("may be slow", choices[2].Label);
        Assert.Equal("qwen2.5:7b", ModelAdvice.DefaultChoice(choices, saved: null)!.Name);
        Assert.Equal("llama3.1:70b", ModelAdvice.DefaultChoice(choices, saved: "LLAMA3.1:70b")!.Name);
        Assert.Equal("qwen2.5:7b", ModelAdvice.DefaultChoice(choices, saved: "uninstalled:1b")!.Name);
        Assert.All(ModelAdvice.Suggested, s => Assert.Matches(@"^[a-z0-9.]+:\d+b$", s.Name));
    }

    [Fact]
    public async Task Nothing_but_reads_and_chat_are_ever_requested()
    {
        var fake = new FakeOllama();
        fake.Models.Clear(); // no models: the client must not try to fetch one
        using var folder = new TestFolder("nopull");
        using var client = new OllamaClient(transport: fake);
        var status = await new OllamaProbe(client, new SandboxKnownLocations(folder.Root)).CheckAsync(CancellationToken.None);
        Assert.Empty(status.Models);
        Assert.Empty(ModelAdvice.Choices(status.Models));
        Assert.All(fake.Requests, r => Assert.Contains(r.Uri.AbsolutePath, new[] { "/api/version", "/api/tags" }));
    }
}
