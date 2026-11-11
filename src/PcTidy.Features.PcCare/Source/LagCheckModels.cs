using System.Globalization;
using PcTidy.Features.PcCare.Analysis;

namespace PcTidy.Features.PcCare.Source;

/// <summary>
/// A test server for the lag check: a download URL that answers GET ?bytes=N with N bytes, and an upload URL that
/// accepts a POST body and throws it away. Both must be https.
/// </summary>
/// <param name="TermsUrl">Where the server's terms of use are (shown next to the Run button).</param>
public sealed record LagCheckEndpoint(string Name, Uri DownloadUrl, Uri UploadUrl, string TermsUrl)
{
    public string Host => DownloadUrl.Host;

    /// <summary>Both URLs https, on the same host (so the button can name one host).</summary>
    public bool IsValid => DownloadUrl.Scheme == Uri.UriSchemeHttps && UploadUrl.Scheme == Uri.UriSchemeHttps
                           && string.Equals(DownloadUrl.Host, UploadUrl.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// CANDIDATE ONLY, NOT ENABLED. Cloudflare's speed.cloudflare.com __down / __up endpoints (the cloudflare/speedtest
    /// client library is MIT; that licence covers the code, not the server). Terms checked 10 Nov 2026:
    /// https://www.cloudflare.com/policies/terms/ (Website and Online Services Terms of Use) does not mention the speed
    /// test, says "Except as expressly authorized by Cloudflare you may not make use of the Materials", bars automated
    /// bots from using the websites' content and bars exceeding the services' limits. No terms page was found that
    /// clearly allows a third-party app to use these endpoints, so the owner must decide (or get Cloudflare's OK) first.
    /// </summary>
    public static readonly LagCheckEndpoint CloudflareCandidate = new(
        "Cloudflare speed test", new Uri("https://speed.cloudflare.com/__down"), new Uri("https://speed.cloudflare.com/__up"),
        "https://www.cloudflare.com/policies/terms/");
}

/// <summary>
/// The lag check's setting. Off (no endpoint) until PC Tidy's owner picks a server whose terms allow this use.
/// Lead integration: wire this to AppSettings and pass it to <see cref="PcCareModule"/>.
/// </summary>
public sealed record LagCheckSettings
{
    /// <summary>Null = the test is disabled and the Run button says why.</summary>
    public LagCheckEndpoint? Endpoint { get; init; }

    public static readonly LagCheckSettings Default = new();

    public bool Enabled => Endpoint is { IsValid: true };
}

/// <summary>How long and how hard the lag check runs. The defaults are what the Run button describes.</summary>
public sealed record LagCheckPlan
{
    public int IdleSamples { get; init; } = 10;
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Each loaded phase (download, then upload) lasts at most this long.</summary>
    public TimeSpan LoadDuration { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Samples in the first part of a loaded phase are skipped while the line fills up.</summary>
    public TimeSpan RampUp { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>A short quiet gap between the download and upload phases, so queues drain.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(1);
    public long DownloadBudgetBytes { get; init; } = 250L * 1000 * 1000;
    public long UploadBudgetBytes { get; init; } = 50L * 1000 * 1000;
    public long DownloadChunkBytes { get; init; } = 25L * 1000 * 1000;
    public long UploadChunkBytes { get; init; } = 5L * 1000 * 1000;
    /// <summary>Parallel transfers in a loaded phase.</summary>
    public int Streams { get; init; } = 4;
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan ChunkTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public static readonly LagCheckPlan Default = new();

    /// <summary>Most timing requests the test sends: one warm-up, the idle ones and one per interval in each loaded phase.</summary>
    public int MaxPings => 1 + IdleSamples + 2 * (int)Math.Ceiling(LoadDuration / PingInterval);

    public TimeSpan ApproxDuration => IdleSamples * PingInterval + LoadDuration + Settle + LoadDuration;

    /// <summary>What the Run button says before anything is sent.</summary>
    public string Describe(LagCheckEndpoint endpoint)
    {
        var c = CultureInfo.CurrentCulture;
        return $"Sends up to {MaxPings.ToString(c)} tiny timing requests to {endpoint.Host}, downloads up to {Mb(DownloadBudgetBytes)} MB of test data from it "
               + $"and uploads up to {Mb(UploadBudgetBytes)} MB of blank test data to it, over about {Math.Round(ApproxDuration.TotalSeconds).ToString(c)} seconds. "
               + "Nothing else is sent, apart from what any web request carries (like your IP address). Results stay on this PC. Nothing changes.";
    }

    private static string Mb(long bytes) => (bytes / 1_000_000d).ToString("0", CultureInfo.CurrentCulture);
}

public enum LagCheckStage { Idle, Download, Settle, Upload, Done }

/// <summary>Progress for the page: the stage, 0..1 of the whole test, and a plain message.</summary>
public sealed record LagCheckProgress(LagCheckStage Stage, double Fraction, string Message);

public enum LagCheckOutcome
{
    /// <summary>All three phases measured.</summary>
    Done,
    /// <summary>Idle measured, but a loaded phase couldn't be (too few answers or the transfer failed).</summary>
    Partial,
    /// <summary>The server didn't answer the idle timing requests.</summary>
    NoAnswer,
}

/// <summary>The lag check's result. Read-only facts: it carries no PlanItems and changes nothing.</summary>
public sealed record LagCheckResult
{
    public required LagCheckOutcome Outcome { get; init; }
    public required string Host { get; init; }
    public required DateTime FinishedUtc { get; init; }
    public LatencyStats? Idle { get; init; }
    public LatencyStats? UnderDownload { get; init; }
    public LatencyStats? UnderUpload { get; init; }
    public long BytesDownloaded { get; init; }
    public long BytesUploaded { get; init; }
    public double? DownloadMbps { get; init; }
    public double? UploadMbps { get; init; }
    /// <summary>A plain reason when something couldn't be measured.</summary>
    public string? Problem { get; init; }

    public double? AddedMs => LagCheckMath.AddedMs(Idle, UnderDownload, UnderUpload);
    public LagGrade Grade => LagCheckMath.Grade(AddedMs);
}
