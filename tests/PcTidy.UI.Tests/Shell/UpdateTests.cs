using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using PcTidy.App.Updates;
using Xunit;

namespace PcTidy.UI.Tests.Shell;

/// <summary>Self-update with a local fake HTTP handler only (nothing goes online): manifest checks, downloads, hashes.</summary>
public class UpdateTests
{
    private static readonly Version Current = new(1, 0, 0, 0);
    private static readonly byte[] Installer = Encoding.ASCII.GetBytes("MZ fake installer bytes for the test");
    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>Answers from a dictionary of URL → body; records every request so tests can check the headers.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _bodies;
        public List<HttpRequestMessage> Requests { get; } = new();
        public FakeHandler(Dictionary<string, byte[]> bodies) => _bodies = bodies;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var key = request.RequestUri!.AbsoluteUri;
            var response = _bodies.TryGetValue(key, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
            return Task.FromResult(response);
        }
    }

    private static byte[] Manifest(string version, string url = "https://example.test/PC%20Tidy%20Setup.exe", string? sha = null, string changelog = "Faster scans.")
        => Encoding.UTF8.GetBytes($$"""{"version":"{{version}}","url":"{{url}}","sha256":"{{sha ?? Hash(Installer)}}","changelog":"{{changelog}}"}""");

    private const string Feed = "https://example.test/pctidy.json";

    [Fact]
    public async Task A_newer_version_with_a_good_hash_is_offered_with_its_changelog()
    {
        var handler = new FakeHandler(new() { [Feed] = Manifest("1.2.0") });
        var result = await new UpdateChecker(handler, Current).CheckAsync(Feed, CancellationToken.None);
        Assert.Equal(UpdateState.Available, result.State);
        Assert.Equal(new Version(1, 2, 0, 0), result.NewVersion);
        Assert.Equal("Faster scans.", result.Manifest!.Changelog);
    }

    [Fact]
    public async Task No_cookies_and_no_telemetry_headers_are_sent()
    {
        var handler = new FakeHandler(new() { [Feed] = Manifest("1.2.0") });
        await new UpdateChecker(handler, Current).CheckAsync(Feed, CancellationToken.None);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("PCTidy-Updater", request.Headers.UserAgent.ToString());
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Single(request.Headers); // only the User-Agent
        Assert.Null(request.Content);
    }

    [Theory]
    [InlineData("1.0.0", UpdateState.UpToDate)]
    [InlineData("1.0", UpdateState.UpToDate)]
    [InlineData("0.9.5", UpdateState.Problem)]
    public async Task The_same_or_an_older_version_is_never_offered(string version, UpdateState expected)
    {
        var handler = new FakeHandler(new() { [Feed] = Manifest(version) });
        var result = await new UpdateChecker(handler, Current).CheckAsync(Feed, CancellationToken.None);
        Assert.Equal(expected, result.State);
        if (expected == UpdateState.Problem) Assert.Contains("older version", result.Message);
    }

    [Theory]
    [InlineData("http://example.test/pctidy.json")]
    [InlineData("file:///C:/pctidy.json")]
    [InlineData("https://user:pw@example.test/pctidy.json")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Only_plain_https_feed_addresses_are_accepted(string url)
        => Assert.NotNull(UpdateChecker.FeedProblem(url));

    [Theory]
    [InlineData("http://example.test/setup.exe", null)]
    [InlineData("https://example.test/setup.zip", null)]
    [InlineData("https://example.test/setup.exe", "abc")]
    [InlineData("https://example.test/setup.exe", "zz11223344556677889900aabbccddeeff00112233445566778899aabbccddee")]
    public void Http_links_wrong_files_and_bad_hashes_are_refused(string url, string? sha)
    {
        var result = new UpdateChecker(new FakeHandler(new()), Current).Evaluate(Manifest("2.0.0", url, sha));
        Assert.Equal(UpdateState.Problem, result.State);
    }

    [Fact]
    public void A_damaged_manifest_is_a_calm_problem_not_a_crash()
        => Assert.Equal(UpdateState.Problem, new UpdateChecker(new FakeHandler(new()), Current).Evaluate("{oops"u8).State);

    [Fact]
    public async Task A_download_with_the_right_hash_is_kept_and_a_wrong_one_is_thrown_away()
    {
        using var folder = new ShellTestFolder("update");
        var store = new UpdateStore(folder.Root);
        var url = "https://example.test/PC%20Tidy%20Setup.exe";

        var good = new FakeHandler(new() { [Feed] = Manifest("1.2.0", url), [url] = Installer });
        var checker = new UpdateChecker(good, Current);
        var check = await checker.CheckAsync(Feed, CancellationToken.None);
        Assert.Null(await checker.DownloadAsync(check, store, null, CancellationToken.None));
        var saved = store.FinalPath(check.NewVersion!);
        Assert.True(File.Exists(saved));
        Assert.StartsWith(Path.Combine(folder.Root, "updates"), saved);
        Assert.Equal(Hash(Installer), Hash(File.ReadAllBytes(saved)));

        var tampered = new FakeHandler(new() { [Feed] = Manifest("1.3.0", url), [url] = Encoding.ASCII.GetBytes("something else") });
        var checker2 = new UpdateChecker(tampered, Current);
        var check2 = await checker2.CheckAsync(Feed, CancellationToken.None);
        var problem = await checker2.DownloadAsync(check2, store, null, CancellationToken.None);
        Assert.Contains("checksum", problem);
        Assert.False(File.Exists(store.FinalPath(check2.NewVersion!)));
        Assert.False(File.Exists(store.PartialPath(check2.NewVersion!)));
    }

    /// <summary>A body that sends a few bytes, then goes quiet forever (hotel Wi-Fi, a proxy holding the connection).</summary>
    private sealed class StallingStream : Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_sent) { _sent = true; buffer.Span[0] = (byte)'M'; return 1; }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StallingHandler(byte[] manifest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(request.RequestUri!.AbsoluteUri == Feed
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(manifest), RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()), RequestMessage = request });
    }

    [Fact]
    public async Task A_download_that_goes_quiet_gives_up_by_itself_and_leaves_nothing()
    {
        using var folder = new ShellTestFolder("update-stall");
        var store = new UpdateStore(folder.Root);
        var checker = new UpdateChecker(new StallingHandler(Manifest("1.2.0")), Current, inactivityTimeout: TimeSpan.FromMilliseconds(300));
        var check = await checker.CheckAsync(Feed, CancellationToken.None);

        var problem = await checker.DownloadAsync(check, store, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(UpdateChecker.StalledMessage, problem);
        Assert.False(File.Exists(store.PartialPath(check.NewVersion!)));
        Assert.False(File.Exists(store.FinalPath(check.NewVersion!)));
    }

    [Fact]
    public async Task Stop_ends_a_download_at_once()
    {
        using var folder = new ShellTestFolder("update-stop");
        var store = new UpdateStore(folder.Root);
        var checker = new UpdateChecker(new StallingHandler(Manifest("1.2.0")), Current, inactivityTimeout: TimeSpan.FromMinutes(5));
        var check = await checker.CheckAsync(Feed, CancellationToken.None);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var problem = await checker.DownloadAsync(check, store, null, stop.Token).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(UpdateChecker.StoppedMessage, problem);
        Assert.False(File.Exists(store.PartialPath(check.NewVersion!)));
    }

    [Fact]
    public void An_unsigned_test_file_is_reported_as_not_signed()
    {
        using var folder = new ShellTestFolder("sig");
        var f = folder.File("fake.exe", "not a real program");
        Assert.NotEqual(SignatureState.Valid, Authenticode.Check(f).State);
    }

    // ------------------------------------------------------------------ UPD-1 / UPD-2

    private static readonly SignatureInfo Publisher = new(SignatureState.Valid, "CN=PC Tidy Test Publisher, O=PC Tidy", "AA");

    [Fact]
    public void Only_an_installer_signed_by_the_same_publisher_as_the_running_PC_Tidy_may_start()
    {
        Assert.Null(UpdateTrust.Problem(Publisher, Publisher with { Thumbprint = "BB" })); // renewed certificate, same publisher
        Assert.Contains("isn't digitally signed", UpdateTrust.Problem(Publisher, SignatureInfo.NotSigned));
        Assert.Contains("isn't valid", UpdateTrust.Problem(Publisher, SignatureInfo.Invalid));
        Assert.Contains("not by PC Tidy's publisher", UpdateTrust.Problem(Publisher, new SignatureInfo(SignatureState.Valid, "CN=Someone Else", "CC")));
        // An unsigned PC Tidy (this beta) never installs an update by itself, whatever the installer.
        Assert.Equal(UpdateTrust.UnsignedSelf, UpdateTrust.Problem(SignatureInfo.NotSigned, Publisher));
        Assert.Equal("PC Tidy Test Publisher", Publisher.SignerName);
    }

    [Fact]
    public void The_installer_is_checked_through_one_handle_that_blocks_changes_until_it_is_released()
    {
        using var folder = new ShellTestFolder("verified");
        var f = folder.File("PC Tidy Setup 1.2.0.exe", "MZ fake installer");
        var sha = Hash(File.ReadAllBytes(f));

        Assert.Throws<InvalidOperationException>(() => VerifiedInstaller.Open(f, sha, SignatureInfo.NotSigned)); // unsigned self
        Assert.Throws<InvalidOperationException>(() => VerifiedInstaller.Open(f, new string('0', 64), Publisher, (_, _) => Publisher)); // wrong hash
        var other = Assert.Throws<InvalidOperationException>(() => VerifiedInstaller.Open(f, sha, Publisher, (_, _) => new SignatureInfo(SignatureState.Valid, "CN=Mallory")));
        Assert.Contains("Mallory", other.Message);

        using (var ok = VerifiedInstaller.Open(f, sha, Publisher, (_, _) => Publisher))
        {
            Assert.Equal(Publisher, ok.Signature);
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(f, "swapped"));
            Assert.ThrowsAny<IOException>(() => File.Move(f, f + ".old"));
        }
        File.WriteAllText(f, "released"); // the lock ends with the handle
    }

    [Fact]
    public void The_release_page_is_the_feeds_https_page_or_else_the_download_link()
    {
        Assert.Equal("https://example.test/releases", new UpdateManifest { Page = "https://example.test/releases", Url = "https://example.test/s.exe" }.ReleasePage()!.AbsoluteUri);
        Assert.Equal("https://example.test/s.exe", new UpdateManifest { Page = "http://example.test/releases", Url = "https://example.test/s.exe" }.ReleasePage()!.AbsoluteUri);
        Assert.Null(new UpdateManifest { Page = "file:///C:/x", Url = "ftp://example.test/s.exe" }.ReleasePage());
    }

    [Fact]
    public void Only_PC_Tidys_own_uninstaller_in_its_install_folder_is_accepted()
    {
        const string dir = @"C:\Program Files\PC Tidy"; // per-machine install
        bool Exists(string _) => true;
        Assert.NotNull(OwnProcesses.Parse($"\"{dir}\\uninstall.exe\" --uninstall", dir, Exists));
        Assert.Null(OwnProcesses.Parse($"\"{dir}\\uninstall.exe\" --uninstall --quiet --remove-data", dir, Exists));
        Assert.Null(OwnProcesses.Parse($"\"C:\\Windows\\System32\\cmd.exe\" --uninstall", dir, Exists));
        Assert.Null(OwnProcesses.Parse($"\"{dir}\\other\\uninstall.exe\" --uninstall", dir, Exists));
        Assert.Null(OwnProcesses.Parse($"{dir}\\uninstall.exe --uninstall", dir, Exists));
        Assert.Null(OwnProcesses.Parse($"\"\\\\server\\share\\uninstall.exe\" --uninstall", @"\\server\share", Exists));
        Assert.Null(OwnProcesses.Parse($"\"{dir}\\uninstall.exe\" --uninstall", dir, _ => false));
    }
}
