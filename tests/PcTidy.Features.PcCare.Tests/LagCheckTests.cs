using System.Net;
using System.Net.Http;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Analysis;
using PcTidy.Features.PcCare.Source;

namespace PcTidy.Features.PcCare.Tests;

/// <summary>
/// A fake lag check server: no network at all. Timing requests answer with a fixed idle delay, plus a fixed extra while
/// a download or upload is running. Transfers deliver their bytes in small blocks until done or cancelled.
/// </summary>
internal sealed class FakeLagServer : ILagCheckTransport
{
    private int _downloads, _uploads;
    public double IdleMs { get; init; } = 20;
    public double DownloadAddedMs { get; init; } = 40;
    public double UploadAddedMs { get; init; } = 150;
    public bool Answers { get; init; } = true;
    public bool UploadFails { get; init; }
    public int Pings;
    public long Downloaded, Uploaded;

    public Task<TimeSpan?> PingAsync(TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Pings);
        if (!Answers) return Task.FromResult<TimeSpan?>(null);
        double ms = IdleMs + (Volatile.Read(ref _downloads) > 0 ? DownloadAddedMs : 0) + (Volatile.Read(ref _uploads) > 0 ? UploadAddedMs : 0);
        return Task.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(ms));
    }

    public Task DownloadAsync(long bytes, Action<int> onBytes, CancellationToken ct) => Transfer(bytes, onBytes, false, ct);

    public Task UploadAsync(long bytes, Action<int> onBytes, CancellationToken ct)
        => UploadFails ? Task.FromException(new HttpRequestException("refused")) : Transfer(bytes, onBytes, true, ct);

    private async Task Transfer(long bytes, Action<int> onBytes, bool up, CancellationToken ct)
    {
        // Marked busy before the first await, so the runner's samples always see the load.
        if (up) Interlocked.Increment(ref _uploads); else Interlocked.Increment(ref _downloads);
        try
        {
            long left = bytes;
            while (left > 0)
            {
                await Task.Delay(1, ct);
                int n = (int)Math.Min(64 * 1024, left);
                left -= n;
                onBytes(n);
                if (up) Interlocked.Add(ref Uploaded, n); else Interlocked.Add(ref Downloaded, n);
            }
        }
        finally
        {
            if (up) Interlocked.Decrement(ref _uploads); else Interlocked.Decrement(ref _downloads);
        }
    }
}

public class LagCheckTests
{
    /// <summary>Short phases so the suite stays fast; budgets big enough that the time limit ends each phase.</summary>
    private static readonly LagCheckPlan Quick = new()
    {
        IdleSamples = 5,
        PingInterval = TimeSpan.FromMilliseconds(5),
        LoadDuration = TimeSpan.FromMilliseconds(250),
        RampUp = TimeSpan.FromMilliseconds(20),
        Settle = TimeSpan.Zero,
        DownloadBudgetBytes = 1L << 40,
        UploadBudgetBytes = 1L << 40,
        DownloadChunkBytes = 1L << 30,
        UploadChunkBytes = 1L << 30,
        Streams = 2,
    };

    [Fact]
    public async Task Idle_and_loaded_latency_come_from_the_fake_server_and_are_graded()
    {
        var server = new FakeLagServer { IdleMs = 20, DownloadAddedMs = 40, UploadAddedMs = 150 };
        var steps = new List<LagCheckProgress>();
        var result = await new LagCheckRunner(server, "test.invalid", Quick).RunAsync(new SyncProgress<LagCheckProgress>(steps.Add), CancellationToken.None);

        Assert.Equal(LagCheckOutcome.Done, result.Outcome);
        Assert.Equal(20.0, result.Idle!.MedianMs);
        Assert.Equal(0.0, result.Idle.JitterMs);
        Assert.Equal(5, result.Idle.Asked);
        Assert.Equal(60.0, result.UnderDownload!.MedianMs);
        Assert.Equal(170.0, result.UnderUpload!.MedianMs);
        Assert.Equal(150.0, result.AddedMs!.Value);
        Assert.Equal(LagCheckMath.Heavy, result.Grade);
        Assert.True(result.BytesDownloaded > 0 && result.BytesUploaded > 0);
        Assert.Equal(server.Downloaded, result.BytesDownloaded);
        Assert.NotNull(result.DownloadMbps);
        Assert.Contains(steps, s => s.Stage == LagCheckStage.Download);
        Assert.Contains(steps, s => s.Stage == LagCheckStage.Upload);
        Assert.Equal(LagCheckStage.Done, steps[^1].Stage);
        Assert.All(steps, s => Assert.InRange(s.Fraction, 0, 1));
    }

    [Fact]
    public async Task A_line_that_stays_flat_under_load_grades_very_little()
    {
        var server = new FakeLagServer { IdleMs = 12, DownloadAddedMs = 3, UploadAddedMs = 5 };
        var result = await new LagCheckRunner(server, "test.invalid", Quick).RunAsync(null, CancellationToken.None);
        Assert.Equal(5.0, result.AddedMs!.Value);
        Assert.Equal(LagCheckMath.VeryLittle, result.Grade);
    }

    [Fact]
    public async Task No_answer_while_idle_stops_before_any_load_is_sent()
    {
        var server = new FakeLagServer { Answers = false };
        var result = await new LagCheckRunner(server, "test.invalid", Quick).RunAsync(null, CancellationToken.None);
        Assert.Equal(LagCheckOutcome.NoAnswer, result.Outcome);
        Assert.Null(result.Idle);
        Assert.Equal(LagCheckMath.Unknown, result.Grade);
        Assert.Contains("didn't answer", result.Problem);
        Assert.Equal(0L, server.Downloaded + server.Uploaded);
    }

    [Fact]
    public async Task A_failing_upload_gives_a_partial_result_with_a_reason()
    {
        var server = new FakeLagServer { UploadFails = true };
        var result = await new LagCheckRunner(server, "test.invalid", Quick).RunAsync(null, CancellationToken.None);
        Assert.Equal(LagCheckOutcome.Partial, result.Outcome);
        Assert.NotNull(result.UnderDownload);
        Assert.Null(result.UnderUpload);
        Assert.Contains("upload", result.Problem);
        Assert.Equal(40.0, result.AddedMs!.Value); // graded on the phase that worked
    }

    [Fact]
    public async Task The_byte_budget_ends_a_phase_early()
    {
        var server = new FakeLagServer();
        var plan = Quick with { DownloadBudgetBytes = 256 * 1024, DownloadChunkBytes = 128 * 1024, LoadDuration = TimeSpan.FromSeconds(20) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await new LagCheckRunner(server, "test.invalid", plan with { UploadBudgetBytes = 256 * 1024, UploadChunkBytes = 128 * 1024 })
            .RunAsync(null, CancellationToken.None);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), "the phases should end when the budget is used, not after 20 s");
        Assert.Equal(256L * 1024, result.BytesDownloaded);
        Assert.Equal(256L * 1024, result.BytesUploaded);
    }

    [Fact]
    public async Task Cancel_stops_the_test()
    {
        using var cts = new CancellationTokenSource();
        var server = new FakeLagServer();
        var run = new LagCheckRunner(server, "test.invalid", Quick with { LoadDuration = TimeSpan.FromSeconds(30) })
            .RunAsync(new SyncProgress<LagCheckProgress>(p => { if (p.Stage == LagCheckStage.Download) cts.Cancel(); }), cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    // ------------------------------------------------------------------ math and grade bands

    [Fact]
    public void Stats_take_the_median_and_the_mean_step_between_answers()
    {
        var s = LagCheckMath.Stats(new double?[] { 10, 30, null, 20, 40 })!;
        Assert.Equal(25.0, s.MedianMs);             // 10, 20, 30, 40
        Assert.Equal((20 + 10 + 20) / 3.0, s.JitterMs); // 10→30→20→40, the unanswered one skipped
        Assert.Equal(4, s.Answered);
        Assert.Equal(5, s.Asked);
        Assert.Null(LagCheckMath.Stats(new double?[] { 10, null, 12 }));
    }

    [Theory]
    [InlineData(0, "very-little")]
    [InlineData(9.9, "very-little")]
    [InlineData(10, "small")]
    [InlineData(29.9, "small")]
    [InlineData(30, "noticeable")]
    [InlineData(99.9, "noticeable")]
    [InlineData(100, "heavy")]
    [InlineData(2000, "heavy")]
    public void Grade_bands_are_PC_Tidys_own(double added, string key)
    {
        Assert.Equal(key, LagCheckMath.Grade(added).Key);
        Assert.Contains("PC Tidy's own bands", LagCheckMath.BandsText);
        Assert.Contains("Not an industry standard", LagCheckMath.BandsText);
    }

    [Fact]
    public void Added_latency_uses_the_busier_phase_and_never_goes_negative()
    {
        var idle = new LatencyStats(30, 1, 10, 10);
        Assert.Equal(50.0, LagCheckMath.AddedMs(idle, new LatencyStats(80, 2, 9, 9), new LatencyStats(45, 2, 9, 9))!.Value);
        Assert.Equal(0.0, LagCheckMath.AddedMs(idle, new LatencyStats(25, 2, 9, 9), null)!.Value);
        Assert.Null(LagCheckMath.AddedMs(idle, null, null));
        Assert.Equal(LagCheckMath.Unknown, LagCheckMath.Grade(null));
    }

    // ------------------------------------------------------------------ endpoint, button text, read-only

    [Fact]
    public void The_test_is_off_until_an_endpoint_is_chosen()
    {
        Assert.Null(LagCheckSettings.Default.Endpoint);
        Assert.False(LagCheckSettings.Default.Enabled);
        Assert.True(new LagCheckSettings { Endpoint = LagCheckEndpoint.CloudflareCandidate }.Enabled);
        var http = LagCheckEndpoint.CloudflareCandidate with { DownloadUrl = new Uri("http://speed.example/__down") };
        Assert.False(new LagCheckSettings { Endpoint = http }.Enabled);
        var twoHosts = LagCheckEndpoint.CloudflareCandidate with { UploadUrl = new Uri("https://other.example/__up") };
        Assert.False(twoHosts.IsValid);
    }

    [Fact]
    public void The_run_button_says_what_it_sends_to_which_host_how_much_and_that_results_stay_here()
    {
        var text = LagCheckPlan.Default.Describe(LagCheckEndpoint.CloudflareCandidate);
        Assert.Contains("speed.cloudflare.com", text);
        Assert.Contains("250 MB", text);
        Assert.Contains("50 MB", text);
        Assert.Contains("Results stay on this PC", text);
        Assert.Contains(LagCheckPlan.Default.MaxPings.ToString(), text);
        Assert.InRange(LagCheckPlan.Default.ApproxDuration.TotalSeconds, 20, 30);
    }

    [Fact]
    public void The_lag_check_never_carries_plan_items()
    {
        var types = new[] { typeof(LagCheckResult), typeof(LagCheckRunner), typeof(LagCheckProgress), typeof(LatencyStats), typeof(LagGrade), typeof(LagCheckSettings) };
        foreach (var t in types)
            foreach (var p in t.GetProperties())
            {
                Assert.False(typeof(PlanItem).IsAssignableFrom(p.PropertyType), $"{t.Name}.{p.Name}");
                Assert.False(typeof(ActionPlan).IsAssignableFrom(p.PropertyType), $"{t.Name}.{p.Name}");
                Assert.False(typeof(IEnumerable<PlanItem>).IsAssignableFrom(p.PropertyType), $"{t.Name}.{p.Name}");
            }
        Assert.Equal(typeof(Task<LagCheckResult>), typeof(LagCheckRunner).GetMethod(nameof(LagCheckRunner.RunAsync))!.ReturnType);
    }

    [Fact]
    public void Guidance_carries_the_measured_numbers_and_their_sources()
    {
        var all = string.Join(" ", LagCheckGuidance.Tips.Select(t => t.Title + " " + t.Text + " " + t.Measured + " " + t.Source));
        Assert.Contains("16 ms", all);
        Assert.Contains("7 ms jitter", all);
        Assert.Contains("60 ms", all);
        Assert.Contains("20 ms jitter", all);
        Assert.Contains("How-To Geek", all);
        Assert.Contains("RTINGS", all);
        Assert.Contains("CAKE", all);
        Assert.Contains("2018", all);
        Assert.Contains("Pause uploads", all);
        Assert.DoesNotContain("FPS", all);
        Assert.All(LagCheckGuidance.Tips, t => Assert.False(string.IsNullOrWhiteSpace(t.Source)));
    }

    // ------------------------------------------------------------------ the HTTPS transport, against a fake handler (no network)

    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, Uri Uri, long? Length)> Requests = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            long? length = null;
            if (request.Content is not null) length = (await request.Content.ReadAsByteArrayAsync(ct)).LongLength;
            lock (Requests) Requests.Add((request.Method, request.RequestUri!, length));
            var query = request.RequestUri!.Query;
            int bytes = query.StartsWith("?bytes=") ? int.Parse(query[7..]) : 0;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[bytes]) };
        }
    }

    [Fact]
    public async Task The_https_transport_asks_for_exact_byte_counts_and_posts_blank_data()
    {
        var handler = new FakeHandler();
        using var t = new HttpLagCheckTransport(LagCheckEndpoint.CloudflareCandidate with
        {
            DownloadUrl = new Uri("https://lag.example.invalid/down"), UploadUrl = new Uri("https://lag.example.invalid/up"),
        }, handler);

        Assert.NotNull(await t.PingAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        long got = 0, sent = 0;
        await t.DownloadAsync(200_000, n => got += n, CancellationToken.None);
        await t.UploadAsync(150_000, n => sent += n, CancellationToken.None);

        Assert.Equal(200_000L, got);
        Assert.Equal(150_000L, sent);
        Assert.Equal("https://lag.example.invalid/down?bytes=0", handler.Requests[0].Uri.ToString());
        Assert.Equal("https://lag.example.invalid/down?bytes=200000", handler.Requests[1].Uri.ToString());
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Equal(150_000L, handler.Requests[2].Length!.Value);
        Assert.Throws<ArgumentException>(() => new HttpLagCheckTransport(LagCheckEndpoint.CloudflareCandidate with { UploadUrl = new Uri("http://x.example/up") }, handler));
    }

    /// <summary>IProgress that runs inline (Progress&lt;T&gt; posts to a thread pool, which would race the asserts).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _on;
        public SyncProgress(Action<T> on) => _on = on;
        public void Report(T value) { lock (this) _on(value); }
    }
}
