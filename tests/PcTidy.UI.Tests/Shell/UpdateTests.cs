using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PcTidy.App.Shell;
using PcTidy.App.Updates;
using PcTidy.Core;
using PcTidy.UI.Flow;
using Xunit;

namespace PcTidy.UI.Tests.Shell;

/// <summary>
/// The updater with fake HTTP handlers and temp folders only: nothing goes online and no installer is ever started.
/// Release data, versions and file contents are invented.
/// </summary>
public partial class UpdateTests
{
    private static readonly Version Current = new(1, 0, 0);
    private const string Tag = "v1.2.0";
    private const string SetupName = "PC-Tidy-Setup-1.2.0.exe";
    private const string ZipName = "PC-Tidy-1.2.0-portable.zip";
    private static readonly byte[] Installer = Encoding.ASCII.GetBytes("MZ fake installer bytes for the test");
    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    private static string Dl(string name, string tag = Tag) => $"https://github.com/mintggs2/pc-tidy/releases/download/{tag}/{name}";
    private static string Cdn(string name) => $"https://release-assets.githubusercontent.com/github-production-release-asset/1/{Guid.NewGuid():N}?name={name}";
    private static readonly string Api = ReleaseSource.LatestApi.AbsoluteUri;

    /// <summary>Answers from a table of URL → response; records every request so tests can check the headers.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> Routes { get; } = new();
        public List<HttpRequestMessage> Requests { get; } = new();
        public Exception? Throw { get; set; }

        public void Body(string url, byte[] body, long? declaredLength = null) => Routes[url] = (req, _) =>
        {
            var content = new ByteArrayContent(body);
            if (declaredLength is { } n) content.Headers.ContentLength = n;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = req });
        };

        public void Redirect(string url, string to) => Routes[url] = (req, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = req };
            r.Headers.Location = new Uri(to);
            return Task.FromResult(r);
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Throw is not null) throw Throw;
            return Routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var answer)
                ? answer(request, ct)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }
    }

    private static byte[] ReleaseJson(string tag = Tag, bool draft = false, bool prerelease = false, string body = "Faster scans.", IEnumerable<(string Name, string Url)>? assets = null)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            tag_name = tag, name = "PC Tidy", draft, prerelease, body,
            assets = (assets ?? new[] { (SetupName, Dl(SetupName)), (ZipName, Dl(ZipName)), (UpdateAssets.SumsName, Dl(UpdateAssets.SumsName)) })
                .Select(a => new { name = a.Name, browser_download_url = a.Url, size = 100L }).ToArray(),
        });

    /// <summary>A whole fake release: the API answer, SHA256SUMS.txt, and the file behind a github.com → CDN redirect.</summary>
    private static FakeHandler Release(byte[]? file = null, string? sums = null, string name = SetupName)
    {
        var h = new FakeHandler();
        file ??= Installer;
        h.Body(Api, ReleaseJson());
        h.Body(Dl(UpdateAssets.SumsName), Encoding.UTF8.GetBytes(sums ?? $"{Hash(Installer)}  {SetupName}\n{Hash(Installer)}  {ZipName}\n"));
        var cdn = Cdn(name);
        h.Redirect(Dl(name), cdn);
        h.Body(cdn, file);
        return h;
    }

    private static UpdateChecker Checker(HttpMessageHandler h, TimeSpan? inactivity = null, TimeSpan? checkTimeout = null) => new(h, Current, inactivity, checkTimeout);

    // ------------------------------------------------------------------ versions, drafts, pre-releases

    [Theory]
    [InlineData("v3.1.2", "3.1.2", false)]
    [InlineData("3.1.2", "3.1.2", false)]
    [InlineData("V10.0.1", "10.0.1", false)]
    [InlineData("v3.1.2-beta", "3.1.2", true)]
    [InlineData("v3.1.2-rc.1", "3.1.2", true)]
    [InlineData("v3.1.2+build.7", "3.1.2", true)]
    [InlineData(" v3.1.2 ", "3.1.2", false)]
    public void Tags_are_read_as_X_Y_Z_and_any_suffix_marks_a_pre_release(string tag, string expected, bool preRelease)
    {
        Assert.True(ReleaseVersion.TryParseTag(tag, out var v, out var pre));
        Assert.Equal(Version.Parse(expected), v);
        Assert.Equal(preRelease, pre);
    }

    /// <summary>3.1.1 review L1: "v1.2.0-beta1" without GitHub's pre-release box ticked is never offered as 1.2.0.</summary>
    [Theory]
    [InlineData("v1.2.0-beta1")]
    [InlineData("v1.2.0-rc.2")]
    [InlineData("v9.0.0+build.7")]
    public void A_tag_with_a_suffix_is_treated_as_a_pre_release_and_skipped(string tag)
    {
        var result = Checker(new FakeHandler()).Evaluate(ReleaseJson(tag, prerelease: false));
        Assert.Equal(UpdateState.UpToDate, result.State);
        Assert.Null(result.Offer);
        Assert.Contains("never offers test releases", result.Message);
        // The real release after it is still offered.
        Assert.Equal(UpdateState.Available, Checker(new FakeHandler()).Evaluate(ReleaseJson("v1.2.0")).State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v3.1")]
    [InlineData("v3.1.2.4")]
    [InlineData("latest")]
    [InlineData("v3.x.1")]
    [InlineData("v-3.1.2")]
    [InlineData("v999999.0.0")]
    [InlineData("v3.1.2 beta")]
    [InlineData("v3.1.2-<script>")]
    public void Malformed_tags_are_refused(string? tag) => Assert.False(ReleaseVersion.TryParseTag(tag, out _));

    [Theory]
    [InlineData("v1.2.0", UpdateState.Available)]
    [InlineData("v1.0.0", UpdateState.UpToDate)]
    [InlineData("v0.9.5", UpdateState.UpToDate)]
    [InlineData("nonsense", UpdateState.Problem)]
    public void Only_a_higher_version_is_offered(string tag, UpdateState expected)
    {
        var result = Checker(new FakeHandler()).Evaluate(ReleaseJson(tag));
        Assert.Equal(expected, result.State);
        Assert.Equal(expected == UpdateState.Available, result.Offer is not null);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Drafts_and_pre_releases_are_never_offered(bool draft, bool prerelease)
    {
        var result = Checker(new FakeHandler()).Evaluate(ReleaseJson("v9.0.0", draft, prerelease));
        Assert.Equal(UpdateState.UpToDate, result.State);
        Assert.Null(result.Offer);
    }

    [Fact]
    public void The_running_version_is_read_from_the_informational_version()
    {
        Assert.Equal(new Version(3, 1, 1), ReleaseVersion.Current("3.1.1+abc123"));
        Assert.Equal(new Version(3, 1, 1), ReleaseVersion.Current("3.1.1-beta"));
        Assert.Equal(new Version(3, 1, 0), ReleaseVersion.Current("3.1"));
        Assert.Equal(new Version(0, 0, 0), ReleaseVersion.Current("garbage"));
    }

    [Fact]
    public void Release_notes_are_plain_text_without_control_characters_and_capped()
    {
        Assert.Equal("a\nb\tc", UpdateChecker.PlainNotes("a\r\nb\tc\u0007\u001b"));
        Assert.Equal("No changes listed.", UpdateChecker.PlainNotes("  "));
        Assert.True(UpdateChecker.PlainNotes(new string('x', 50_000)).Length <= 20_002);
        Assert.Equal("<b>bold</b>", UpdateChecker.PlainNotes("<b>bold</b>")); // shown as text, never rendered
    }

    // ------------------------------------------------------------------ the check request

    [Fact]
    public async Task A_check_asks_only_the_fixed_GitHub_address_with_two_headers()
    {
        var h = Release();
        var result = await Checker(h).CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Available, result.State);
        Assert.Equal("Faster scans.", result.Offer!.Notes);
        var request = Assert.Single(h.Requests);
        Assert.Equal("https://api.github.com/repos/mintggs2/pc-tidy/releases/latest", request.RequestUri!.AbsoluteUri);
        Assert.Equal("PC-Tidy/1.0.0", string.Join(" ", request.Headers.GetValues("User-Agent")));
        Assert.Equal("application/vnd.github+json", string.Join(" ", request.Headers.GetValues("Accept")));
        Assert.Equal(2, request.Headers.Count());
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.Null(request.Content);
    }

    [Fact]
    public async Task Offline_is_a_calm_problem_not_a_crash()
    {
        var h = new FakeHandler { Throw = new HttpRequestException("No such host is known.") };
        var result = await Checker(h).CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Problem, result.State);
        Assert.Equal(UpdateChecker.OfflineMessage, result.Message);
    }

    [Fact]
    public async Task A_check_that_takes_too_long_gives_up_by_itself()
    {
        var h = new FakeHandler();
        h.Routes[Api] = async (req, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); };
        var result = await Checker(h, checkTimeout: TimeSpan.FromMilliseconds(200)).CheckAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(UpdateChecker.TimeoutMessage, result.Message);
    }

    [Fact]
    public async Task An_oversized_answer_is_ignored_whether_or_not_it_says_its_size()
    {
        var big = new byte[UpdateChecker.MaxReleaseJsonBytes + 10];
        var declared = new FakeHandler();
        declared.Body(Api, big);
        Assert.Contains("far too big", (await Checker(declared).CheckAsync(CancellationToken.None)).Message);
        var hidden = new FakeHandler();
        hidden.Routes[Api] = (req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(big)), RequestMessage = req });
        Assert.Contains("far too big", (await Checker(hidden).CheckAsync(CancellationToken.None)).Message);
    }

    [Theory]
    [InlineData("{oops")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"tag_name\":12}")]
    [InlineData("{\"draft\":\"yes\",\"tag_name\":\"v2.0.0\"}")]
    [InlineData("{}")]
    public void Bad_or_missing_release_JSON_is_a_calm_problem(string json)
    {
        var result = Checker(new FakeHandler()).Evaluate(Encoding.UTF8.GetBytes(json));
        Assert.Equal(UpdateState.Problem, result.State);
        Assert.Null(result.Offer);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    public async Task Error_answers_are_calm_problems(HttpStatusCode code)
    {
        var h = new FakeHandler();
        h.Routes[Api] = (req, _) => Task.FromResult(new HttpResponseMessage(code) { RequestMessage = req });
        var result = await Checker(h).CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Problem, result.State);
        Assert.Single(h.Requests); // a redirect from the API is not followed
    }

    // ------------------------------------------------------------------ asset choice and exact names

    [Fact]
    public void The_file_depends_on_how_this_copy_is_installed()
    {
        var v = new Version(3, 1, 2);
        Assert.Equal("PC-Tidy-Setup-3.1.2.exe", UpdateAssets.ExpectedName(InstallKind.Installed, v));
        Assert.Equal("PC-Tidy-3.1.2-portable.zip", UpdateAssets.ExpectedName(InstallKind.Portable, v));

        const string installed = @"C:\Program Files\PC Tidy";
        var entry = new UninstallEntry(installed + @"\uninstall.exe", installed);
        Assert.Equal(InstallKind.Installed, UpdateAssets.Detect(entry, installed + @"\"));
        Assert.Equal(InstallKind.Portable, UpdateAssets.Detect(entry, @"D:\Tools\PC Tidy 3.1.1")); // a portable copy on a PC that also has Setup's install
        Assert.Equal(InstallKind.Portable, UpdateAssets.Detect(null, installed));
    }

    [Fact]
    public void Only_an_asset_with_exactly_the_expected_name_is_used()
    {
        var offer = new UpdateOffer(new Version(1, 2, 0), Tag, "", new[]
        {
            new GitHubAsset { Name = "pc-tidy-setup-1.2.0.exe", DownloadUrl = Dl("pc-tidy-setup-1.2.0.exe") },
            new GitHubAsset { Name = SetupName + ".exe", DownloadUrl = Dl(SetupName + ".exe") },
            new GitHubAsset { Name = ZipName, DownloadUrl = Dl(ZipName) },
            new GitHubAsset { Name = ZipName, DownloadUrl = Dl(ZipName) },
        });
        var (setup, missing) = UpdateAssets.Pick(offer, SetupName);
        Assert.Null(setup);
        Assert.Contains(SetupName, missing);
        var (zip, twice) = UpdateAssets.Pick(offer, ZipName);
        Assert.Null(zip);
        Assert.Contains("more than once", twice);
    }

    [Theory]
    [InlineData(@"..\settings.json")]
    [InlineData("../x.exe")]
    [InlineData("a/b.exe")]
    [InlineData(@"C:\Windows\x.exe")]
    [InlineData("C:x.exe")]
    [InlineData("x.exe:stream")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("name with space.exe")]
    [InlineData("")]
    public void Server_names_never_become_paths(string name)
    {
        Assert.False(UpdateAssets.IsSafeFileName(name));
        using var folder = new ShellTestFolder("update-names");
        Assert.Throws<InvalidOperationException>(() => new UpdateStore(folder.Root).FinalPath(name));
    }

    [Fact]
    public async Task A_release_without_the_expected_file_stops_before_downloading_anything()
    {
        using var folder = new ShellTestFolder("update-missing");
        var h = new FakeHandler();
        h.Body(Api, ReleaseJson(assets: new[] { ("PC-Tidy-Setup-1.2.0-InnoSetup.exe", Dl("PC-Tidy-Setup-1.2.0-InnoSetup.exe")), (UpdateAssets.SumsName, Dl(UpdateAssets.SumsName)) }));
        var checker = Checker(h);
        var check = await checker.CheckAsync(CancellationToken.None);
        var result = await checker.DownloadAsync(check, InstallKind.Installed, new UpdateStore(folder.Root), null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains(SetupName, result.Problem);
        Assert.Single(h.Requests); // only the check
    }

    // ------------------------------------------------------------------ hosts

    [Theory]
    [InlineData("https://github.com/mintggs2/pc-tidy/releases/download/v1.2.0/PC-Tidy-Setup-1.2.0.exe", true)]
    [InlineData("https://GitHub.com/Mintggs2/PC-Tidy/releases/download/v1.2.0/PC-Tidy-Setup-1.2.0.exe", true)]
    [InlineData("http://github.com/mintggs2/pc-tidy/releases/download/v1.2.0/PC-Tidy-Setup-1.2.0.exe", false)]
    [InlineData("https://github.com:8443/mintggs2/pc-tidy/releases/download/v1.2.0/PC-Tidy-Setup-1.2.0.exe", false)]
    [InlineData("https://user:pw@github.com/mintggs2/pc-tidy/releases/download/v1.2.0/PC-Tidy-Setup-1.2.0.exe", false)]
    [InlineData("https://github.com/someone/pc-tidy/releases/download/v1.2.0/PC-Tidy-Setup-1.2.0.exe", false)]
    [InlineData("https://github.com/mintggs2/pc-tidy-fork/releases/download/v1.2.0/PC-Tidy-Setup-1.2.0.exe", false)]
    [InlineData("https://github.com/mintggs2/pc-tidy/releases/download/v1.3.0/PC-Tidy-Setup-1.2.0.exe", false)]
    [InlineData("https://github.com/mintggs2/pc-tidy/releases/download/v1.2.0/other/PC-Tidy-Setup-1.2.0.exe", false)]
    [InlineData("https://github.com/mintggs2/pc-tidy/releases/download/v1.2.0/PC-Tidy-Setup-1.2.0.exe?x=1", false)]
    [InlineData("https://evil.example.test/mintggs2/pc-tidy/releases/download/v1.2.0/PC-Tidy-Setup-1.2.0.exe", false)]
    [InlineData("https://github.com.evil.example.test/mintggs2/pc-tidy/releases/download/v1.2.0/PC-Tidy-Setup-1.2.0.exe", false)]
    [InlineData("https://release-assets.githubusercontent.com/PC-Tidy-Setup-1.2.0.exe", false)] // only as a redirect
    public void Only_the_releases_own_download_link_is_used(string url, bool ok)
        => Assert.Equal(ok, ReleaseSource.IsAssetUrl(new Uri(url), SetupName, Tag));

    [Theory]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/abc?sig=x", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset-2e65be/1/abc?sig=x", true)]
    [InlineData("https://github.com/mintggs2/pc-tidy/releases/download/v1.2.0/x", true)]
    [InlineData("http://release-assets.githubusercontent.com/x", false)]
    [InlineData("https://release-assets.githubusercontent.com.evil.example.test/x", false)]
    [InlineData("https://raw.githubusercontent.com/x", false)]
    [InlineData("https://evil.example.test/x", false)]
    [InlineData("https://user@objects.githubusercontent.com/x", false)]
    [InlineData("ftp://objects.githubusercontent.com/x", false)]
    public void Redirects_go_only_to_GitHubs_download_hosts_over_https(string url, bool ok)
        => Assert.Equal(ok, ReleaseSource.IsRedirectTarget(new Uri(url)));

    [Theory]
    [InlineData("https://evil.example.test/PC-Tidy-Setup-1.2.0.exe")]
    [InlineData("http://release-assets.githubusercontent.com/PC-Tidy-Setup-1.2.0.exe")]
    public async Task A_redirect_outside_the_allow_list_is_refused_and_leaves_nothing(string to)
    {
        using var folder = new ShellTestFolder("update-redirect");
        var store = new UpdateStore(folder.Root);
        var h = Release();
        h.Redirect(Dl(SetupName), to);
        h.Body(to, Installer);
        var checker = Checker(h);
        var result = await checker.DownloadAsync(await checker.CheckAsync(CancellationToken.None), InstallKind.Installed, store, null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("isn't one of GitHub's download servers", result.Problem);
        Assert.DoesNotContain(h.Requests, r => r.RequestUri!.AbsoluteUri == new Uri(to).AbsoluteUri);
        Assert.False(File.Exists(store.FinalPath(SetupName)));
        Assert.False(File.Exists(store.PartialPath(SetupName)));
    }

    [Fact]
    public async Task A_download_link_that_isnt_the_releases_own_is_never_requested()
    {
        using var folder = new ShellTestFolder("update-link");
        var h = Release();
        h.Body(Api, ReleaseJson(assets: new[] { (SetupName, "https://evil.example.test/" + SetupName), (UpdateAssets.SumsName, Dl(UpdateAssets.SumsName)) }));
        var checker = Checker(h);
        var result = await checker.DownloadAsync(await checker.CheckAsync(CancellationToken.None), InstallKind.Installed, new UpdateStore(folder.Root), null, CancellationToken.None);
        Assert.Contains("isn't PC Tidy's own GitHub release", result.Problem);
        Assert.Single(h.Requests);
    }

    // ------------------------------------------------------------------ SHA256SUMS.txt

    [Fact]
    public void Sums_lines_are_found_exactly_with_crlf_tabs_extra_spaces_and_a_binary_marker()
    {
        var h = new string('a', 64);
        var other = new string('b', 64);
        var text = $"\uFEFF{other}  PC-Tidy-Rescue-1.2.0.exe\r\n{h.ToUpperInvariant()}   \t*{SetupName}   \r\n{other}  {SetupName}.old\r\n\r\nnot a line\r\n";
        Assert.Equal(h, Sha256Sums.Find(text, SetupName).Sha256);
        Assert.Equal(other, Sha256Sums.Find(text, "PC-Tidy-Rescue-1.2.0.exe").Sha256);
        Assert.Contains("doesn't list", Sha256Sums.Find(text, ZipName).Problem);
        Assert.Contains("doesn't list", Sha256Sums.Find(text, "pc-tidy-setup-1.2.0.exe").Problem); // exact, case included
        Assert.Contains("doesn't list", Sha256Sums.Find($"{h[..63]}  {SetupName}", SetupName).Problem); // 63 digits
        Assert.Contains("two different", Sha256Sums.Find($"{h}  {SetupName}\n{other}  {SetupName}\n", SetupName).Problem);
        Assert.Equal(h, Sha256Sums.Find($"{h}  {SetupName}\n{h}  {SetupName}\n", SetupName).Sha256); // the same line twice is fine
    }

    [Fact]
    public async Task A_good_download_is_kept_with_its_release_proof()
    {
        using var folder = new ShellTestFolder("update-good");
        var store = new UpdateStore(folder.Root);
        var checker = Checker(Release());
        var result = await checker.DownloadAsync(await checker.CheckAsync(CancellationToken.None), InstallKind.Installed, store, null, CancellationToken.None);
        Assert.True(result.Ok, result.Problem);
        Assert.Equal(Path.Combine(folder.Root, "updates", SetupName), result.Path);
        Assert.Equal(Hash(Installer), Hash(File.ReadAllBytes(result.Path!)));
        Assert.Equal(SetupName, result.Release!.AssetName);
        Assert.Equal(Hash(Installer), result.Release.Sha256);
        Assert.Equal(new Version(1, 2, 0), result.Release.Version);
    }

    [Fact]
    public async Task A_portable_copy_downloads_the_portable_zip()
    {
        using var folder = new ShellTestFolder("update-zip");
        var checker = Checker(Release(name: ZipName));
        var result = await checker.DownloadAsync(await checker.CheckAsync(CancellationToken.None), InstallKind.Portable, new UpdateStore(folder.Root), null, CancellationToken.None);
        Assert.True(result.Ok, result.Problem);
        Assert.EndsWith(ZipName, result.Path);
        Assert.Equal(InstallKind.Portable, result.Release!.Kind);
    }

    [Fact]
    public async Task A_checksum_mismatch_deletes_the_download_and_stops()
    {
        using var folder = new ShellTestFolder("update-mismatch");
        var store = new UpdateStore(folder.Root);
        var checker = Checker(Release(file: Encoding.ASCII.GetBytes("something else")));
        var result = await checker.DownloadAsync(await checker.CheckAsync(CancellationToken.None), InstallKind.Installed, store, null, CancellationToken.None);
        Assert.Equal(UpdateChecker.MismatchMessage, result.Problem);
        Assert.Null(result.Release);
        Assert.False(File.Exists(store.FinalPath(SetupName)));
        Assert.False(File.Exists(store.PartialPath(SetupName)));
    }

    [Fact]
    public async Task A_sums_file_without_the_line_stops_before_the_file_is_downloaded()
    {
        using var folder = new ShellTestFolder("update-noline");
        var h = Release(sums: $"{Hash(Installer)}  PC-Tidy-Rescue-1.2.0.exe\n");
        var checker = Checker(h);
        var result = await checker.DownloadAsync(await checker.CheckAsync(CancellationToken.None), InstallKind.Installed, new UpdateStore(folder.Root), null, CancellationToken.None);
        Assert.Contains("doesn't list", result.Problem);
        Assert.DoesNotContain(h.Requests, r => r.RequestUri!.AbsoluteUri == Dl(SetupName));
    }

    [Fact]
    public async Task An_oversized_download_is_refused()
    {
        using var folder = new ShellTestFolder("update-big");
        var store = new UpdateStore(folder.Root);
        var h = Release();
        var cdn = Cdn(SetupName);
        h.Redirect(Dl(SetupName), cdn);
        h.Body(cdn, Installer, declaredLength: UpdateChecker.MaxDownloadBytes + 1);
        var checker = Checker(h);
        var result = await checker.DownloadAsync(await checker.CheckAsync(CancellationToken.None), InstallKind.Installed, store, null, CancellationToken.None);
        Assert.Contains("far too big", result.Problem);
        Assert.False(File.Exists(store.PartialPath(SetupName)));
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

    private static FakeHandler Stalling()
    {
        var h = Release();
        h.Routes[Dl(SetupName)] = (req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()), RequestMessage = req });
        return h;
    }

    [Fact]
    public async Task A_download_that_goes_quiet_gives_up_by_itself_and_leaves_nothing()
    {
        using var folder = new ShellTestFolder("update-stall");
        var store = new UpdateStore(folder.Root);
        var checker = Checker(Stalling(), inactivity: TimeSpan.FromMilliseconds(300));
        var check = await checker.CheckAsync(CancellationToken.None);
        var result = await checker.DownloadAsync(check, InstallKind.Installed, store, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(UpdateChecker.StalledMessage, result.Problem);
        Assert.False(File.Exists(store.PartialPath(SetupName)));
        Assert.False(File.Exists(store.FinalPath(SetupName)));
    }

    [Fact]
    public async Task Stop_ends_a_download_at_once()
    {
        using var folder = new ShellTestFolder("update-stop");
        var store = new UpdateStore(folder.Root);
        var checker = Checker(Stalling(), inactivity: TimeSpan.FromMinutes(5));
        var check = await checker.CheckAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var result = await checker.DownloadAsync(check, InstallKind.Installed, store, null, stop.Token).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(UpdateChecker.StoppedMessage, result.Problem);
        Assert.False(File.Exists(store.PartialPath(SetupName)));
    }

    // ------------------------------------------------------------------ when it checks and offers

    [Fact]
    public void The_automatic_check_runs_at_most_once_a_day()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdatePolicy.AutoCheckDue(true, false, null, now));
        Assert.False(UpdatePolicy.AutoCheckDue(true, false, now.AddHours(-2), now));
        Assert.False(UpdatePolicy.AutoCheckDue(true, false, now.AddHours(-23.9), now));
        Assert.True(UpdatePolicy.AutoCheckDue(true, false, now.AddHours(-24), now));
        Assert.True(UpdatePolicy.AutoCheckDue(true, false, now.AddDays(30), now)); // a clock that went backwards
    }

    [Fact]
    public void The_switch_turns_the_automatic_check_off_and_is_on_by_default()
    {
        var now = DateTime.UtcNow;
        Assert.False(UpdatePolicy.AutoCheckDue(false, false, null, now));
        Assert.True(new ShellPrefs().AutoUpdateCheck);
        var existing = new AppSettings();
        existing.Features[ShellPrefs.Key] = JsonSerializer.SerializeToElement(new { LowDiskAlerts = false }); // an older settings file
        Assert.True(ShellPrefs.Get(existing).AutoUpdateCheck);
    }

    [Fact]
    public void A_skipped_version_isnt_offered_again_by_the_automatic_check()
    {
        var check = Checker(new FakeHandler()).Evaluate(ReleaseJson());
        Assert.False(UpdatePolicy.ShouldNotify(check, "1.2.0", manual: false));
        Assert.True(UpdatePolicy.ShouldNotify(check, "1.2.0", manual: true)); // "Check for updates" still shows it
        Assert.True(UpdatePolicy.ShouldNotify(check, "1.1.0", manual: false)); // a newer one than the skipped one
        Assert.True(UpdatePolicy.ShouldNotify(check, null, manual: false));
        Assert.False(UpdatePolicy.ShouldNotify(UpdateCheck.Fail("offline"), null, manual: true));
    }

    [Fact]
    public void Nothing_is_offered_or_installed_while_a_run_a_scan_or_an_undo_is_going()
    {
        var gate = new RunGate();
        Assert.Null(UpdatePolicy.BusyReason(gate, false, false, false));
        using (gate.Begin("Clean")) Assert.Equal("Available when Clean finishes", UpdatePolicy.BusyReason(gate, false, false, false));
        using (gate.Begin("the undo")) Assert.Equal("Available when the undo finishes", UpdatePolicy.BusyReason(gate, false, false, false));
        Assert.Equal(RunGate.BusyReason, UpdatePolicy.BusyReason(gate, flowWorking: true, false, false));
        Assert.Equal(RunGate.BusyReason, UpdatePolicy.BusyReason(gate, false, executorChanging: true, false));
        Assert.Equal("Available when the scan finishes", UpdatePolicy.BusyReason(gate, false, false, scanning: true));
        Assert.Null(UpdatePolicy.BusyReason(gate, false, false, false));
    }

    [Fact]
    public async Task Practice_and_demo_mode_use_no_network_and_never_check_by_themselves()
    {
        Assert.False(UpdatePolicy.AutoCheckDue(true, practice: true, null, DateTime.UtcNow));
        using var practice = UpdateChecker.Create(practice: true, Current);
        Assert.True(practice.IsPractice);
        var check = await practice.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Available, check.State); // the invented release one version up
        Assert.Equal(new Version(1, 0, 1), check.NewVersion);
        using var real = UpdateChecker.Create(practice: false, Current);
        Assert.False(real.IsPractice);
    }

    // ------------------------------------------------------------------ trust (UPD-1)

    private static readonly SignatureInfo Publisher = new(SignatureState.Valid, "CN=PC Tidy Test Publisher, O=PC Tidy", "AA")
    {
        Issuer = "CN=Invented Code Signing CA, O=Invented Trust", PublicKeySha256 = new string('1', 64),
    };
    private static VerifiedRelease Proof(string sha, string name = SetupName, InstallKind kind = InstallKind.Installed) => new(new Version(1, 2, 0), Tag, name, sha, kind);

    [Fact]
    public void A_signed_PC_Tidy_installs_only_the_same_publishers_update()
    {
        var proof = Proof(new string('a', 64));
        Assert.Null(UpdateTrust.Problem(Publisher, Publisher, proof)); // the same certificate
        Assert.Null(UpdateTrust.Problem(Publisher, Publisher with { Thumbprint = "BB" }, proof)); // renewed certificate, same key
        Assert.Null(UpdateTrust.Problem(Publisher, Publisher with { Thumbprint = "BB", PublicKeySha256 = new string('2', 64) }, proof)); // renewed and re-keyed by the same CA
        Assert.Contains("isn't digitally signed", UpdateTrust.Problem(Publisher, SignatureInfo.NotSigned, proof)); // the release proof isn't enough
        Assert.Contains("not by PC Tidy's publisher", UpdateTrust.Problem(Publisher, new SignatureInfo(SignatureState.Valid, "CN=Someone Else", "CC"), proof));
        Assert.Equal("PC Tidy Test Publisher", Publisher.SignerName);
    }

    /// <summary>3.1.1 review L10: the subject text alone never makes the same publisher.</summary>
    [Fact]
    public void A_certificate_with_the_same_subject_from_another_CA_and_another_key_is_refused()
    {
        var proof = Proof(new string('a', 64));
        var lookalike = Publisher with { Thumbprint = "EE", PublicKeySha256 = new string('9', 64), Issuer = "CN=Some Other CA" };
        Assert.Contains("not by PC Tidy's publisher", UpdateTrust.Problem(Publisher, lookalike, proof));
        Assert.False(UpdateTrust.SamePublisher(Publisher, lookalike));
        // Nothing to compare but the subject: refused too.
        Assert.False(UpdateTrust.SamePublisher(Publisher, new SignatureInfo(SignatureState.Valid, Publisher.Signer)));
        // Same key or same certificate, but another subject: refused.
        Assert.False(UpdateTrust.SamePublisher(Publisher, Publisher with { Signer = "CN=PC Tidy Test Publisher, O=Someone" }));
    }

    [Fact]
    public void An_unsigned_PC_Tidy_installs_only_with_the_release_proof()
    {
        var proof = Proof(new string('a', 64));
        Assert.Equal(UpdateTrust.UnsignedSelf, UpdateTrust.Problem(SignatureInfo.NotSigned, SignatureInfo.NotSigned));
        Assert.Equal(UpdateTrust.UnsignedSelf, UpdateTrust.Problem(SignatureInfo.NotSigned, Publisher));
        Assert.Null(UpdateTrust.Problem(SignatureInfo.NotSigned, SignatureInfo.NotSigned, proof));
        Assert.Null(UpdateTrust.Problem(SignatureInfo.NotSigned, Publisher, proof)); // a signed update for the unsigned beta is fine
    }

    [Fact]
    public void An_invalid_signature_is_always_refused()
    {
        var proof = Proof(new string('a', 64));
        Assert.Equal(UpdateTrust.InvalidSignature, UpdateTrust.Problem(SignatureInfo.NotSigned, SignatureInfo.Invalid, proof));
        Assert.Equal(UpdateTrust.InvalidSignature, UpdateTrust.Problem(Publisher, SignatureInfo.Invalid, proof));
        Assert.Equal(UpdateTrust.InvalidSignature, UpdateTrust.Problem(SignatureInfo.NotSigned, new SignatureInfo(SignatureState.Valid), proof)); // valid but no signer
        Assert.Equal(UpdateTrust.DamagedSelf, UpdateTrust.Problem(SignatureInfo.Invalid, SignatureInfo.NotSigned, proof)); // a damaged running copy
    }

    [Fact]
    public void The_installer_is_checked_through_one_locked_handle_with_the_release_proof()
    {
        using var folder = new ShellTestFolder("verified");
        var f = folder.File(SetupName, "MZ fake installer");
        var sha = Hash(File.ReadAllBytes(f));

        Assert.Throws<InvalidOperationException>(() => VerifiedInstaller.Open(f, sha, SignatureInfo.NotSigned, null, (_, _) => SignatureInfo.NotSigned)); // unsigned, no proof
        Assert.Throws<InvalidOperationException>(() => VerifiedInstaller.Open(f, sha, SignatureInfo.NotSigned, Proof(new string('0', 64)), (_, _) => SignatureInfo.NotSigned)); // proof for another file
        Assert.Throws<InvalidOperationException>(() => VerifiedInstaller.Open(f, sha, SignatureInfo.NotSigned, Proof(sha, "PC-Tidy-Setup-9.9.9.exe"), (_, _) => SignatureInfo.NotSigned)); // another name
        Assert.Throws<InvalidOperationException>(() => VerifiedInstaller.Open(f, sha, SignatureInfo.NotSigned, Proof(sha, kind: InstallKind.Portable), (_, _) => SignatureInfo.NotSigned));
        Assert.Throws<InvalidOperationException>(() => VerifiedInstaller.Open(f, new string('0', 64), Publisher, null, (_, _) => Publisher)); // wrong hash
        Assert.Throws<InvalidOperationException>(() => VerifiedInstaller.Open(f, sha, SignatureInfo.NotSigned, Proof(sha), (_, _) => SignatureInfo.Invalid));
        var other = Assert.Throws<InvalidOperationException>(() => VerifiedInstaller.Open(f, sha, Publisher, Proof(sha), (_, _) => new SignatureInfo(SignatureState.Valid, "CN=Mallory")));
        Assert.Contains("Mallory", other.Message);

        using (var ok = VerifiedInstaller.Open(f, sha, SignatureInfo.NotSigned, Proof(sha), (_, _) => SignatureInfo.NotSigned))
        {
            Assert.Equal(SignatureState.NotSigned, ok.Signature.State);
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(f, "swapped"));
            Assert.ThrowsAny<IOException>(() => File.Move(f, f + ".old"));
            Assert.ThrowsAny<IOException>(() => File.Delete(f));
        }
        File.WriteAllText(f, "released"); // the lock ends with the handle
    }

    [Fact]
    public void Setup_is_started_with_its_own_window_not_silently()
        => Assert.Empty(VerifiedInstaller.SetupArguments);

    [Fact]
    public void An_unsigned_test_file_is_reported_as_not_signed()
    {
        using var folder = new ShellTestFolder("sig");
        var f = folder.File("fake.exe", "not a real program");
        Assert.NotEqual(SignatureState.Valid, Authenticode.Check(f).State);
    }

    // ------------------------------------------------------------------ portable copies

    private static MemoryStream Zip(params (string Name, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open());
                w.Write(content);
            }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void The_portable_zip_unpacks_into_a_new_folder_next_to_the_current_one()
    {
        using var folder = new ShellTestFolder("portable");
        var app = folder.Folder("PC Tidy 1.0.0");
        var updates = Path.Combine(folder.Root, "Data", "updates");
        using var zip = Zip(("PC Tidy 1.2.0/PcTidy.exe", "exe"), ("PC Tidy 1.2.0/sub/a.dll", "dll"), ("PC Tidy 1.2.0/", ""));
        var target = PortableUpdate.UnpackIntoNewFolder(zip, app, updates, new Version(1, 2, 0));
        Assert.Equal(Path.Combine(folder.Root, "PC Tidy 1.2.0"), target);
        Assert.Equal("exe", File.ReadAllText(Path.Combine(target, "PcTidy.exe")));
        Assert.Equal("dll", File.ReadAllText(Path.Combine(target, "sub", "a.dll")));
        Assert.False(File.Exists(Path.Combine(app, "PcTidy.exe"))); // the running copy isn't touched

        // The same version again: that folder exists, so the next one goes into the updates folder.
        using var again = Zip(("PC Tidy 1.2.0/PcTidy.exe", "exe2"));
        var second = PortableUpdate.UnpackIntoNewFolder(again, app, updates, new Version(1, 2, 0));
        Assert.Equal(Path.Combine(updates, "PC Tidy 1.2.0"), second);
        Assert.Equal("exe", File.ReadAllText(Path.Combine(target, "PcTidy.exe")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("PC Tidy 1.2.0/../../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData(@"\evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("a/C:evil.txt")]
    [InlineData("a/file.txt:hidden")]
    [InlineData("a/CON")]
    [InlineData("a/nul.txt")]
    [InlineData("a/trailing.")]
    public void Zip_slip_is_refused_and_nothing_is_written(string badName)
    {
        using var folder = new ShellTestFolder("zipslip");
        var app = folder.Folder("PC Tidy 1.0.0");
        var updates = Path.Combine(folder.Root, "updates");
        using var zip = Zip(("a/good.txt", "ok"), (badName, "bad"));
        Assert.Throws<InvalidOperationException>(() => PortableUpdate.UnpackIntoNewFolder(zip, app, updates, new Version(1, 2, 0)));
        Assert.False(Directory.Exists(Path.Combine(folder.Root, "PC Tidy 1.2.0"))); // the half-made folder is removed again
        Assert.False(File.Exists(Path.Combine(folder.Root, "evil.txt")));
        Assert.Equal(new[] { app }, Directory.GetDirectories(folder.Root));
    }

    [Fact]
    public void An_oversized_zip_or_one_with_too_many_files_is_refused()
    {
        using var folder = new ShellTestFolder("zipbig");
        var target = folder.Folder("new");
        using var big = Zip(("a.txt", new string('x', 5000)));
        Assert.Throws<InvalidDataException>(() => PortableUpdate.Unpack(big, target, maxBytes: 1000));
        using var many = Zip(("a.txt", "1"), ("b.txt", "2"), ("c.txt", "3"));
        Assert.Throws<InvalidDataException>(() => PortableUpdate.Unpack(many, target, maxEntries: 2));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public void A_damaged_zip_is_a_plain_problem()
    {
        using var folder = new ShellTestFolder("zipbad");
        var app = folder.Folder("PC Tidy 1.0.0");
        using var notZip = new MemoryStream(Encoding.ASCII.GetBytes("not a zip at all"));
        var ex = Assert.Throws<InvalidOperationException>(() => PortableUpdate.UnpackIntoNewFolder(notZip, app, Path.Combine(folder.Root, "updates"), new Version(1, 2, 0)));
        Assert.Contains("current PC Tidy wasn't changed", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(folder.Root, "PC Tidy 1.2.0")));
    }

    [Fact]
    public void The_downloaded_zip_is_locked_and_re_hashed_while_it_is_unpacked()
    {
        using var folder = new ShellTestFolder("ziplock");
        var f = Path.Combine(folder.Root, ZipName);
        using (var zip = Zip(("PC Tidy 1.2.0/PcTidy.exe", "exe"))) File.WriteAllBytes(f, zip.ToArray());
        var sha = Hash(File.ReadAllBytes(f));
        Assert.Throws<InvalidOperationException>(() => VerifiedInstaller.OpenArchive(f, new string('0', 64)));
        using var locked = VerifiedInstaller.OpenArchive(f, sha);
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(f, "swapped"));
        Assert.Equal(1, PortableUpdate.Unpack(locked.Stream, folder.Folder("new")));
    }

    // ------------------------------------------------------------------ settings migration

    [Fact]
    public void The_old_custom_update_address_is_dropped_from_stored_settings()
    {
        var s = new AppSettings();
        s.Features[ShellPrefs.Key] = JsonSerializer.SerializeToElement(new { UpdateFeedUrl = "https://updates.example.test/feed.json", LowDiskAlerts = false });
        Assert.True(ShellPrefs.Migrate(s));
        var raw = s.Features[ShellPrefs.Key];
        Assert.False(raw.TryGetProperty("UpdateFeedUrl", out _));
        Assert.False(ShellPrefs.Get(s).LowDiskAlerts); // everything else stays
        Assert.True(ShellPrefs.Get(s).AutoUpdateCheck);
        Assert.False(ShellPrefs.Migrate(s)); // nothing left to do
        Assert.False(ShellPrefs.Migrate(new AppSettings()));
    }

    [Fact]
    public void The_update_source_is_fixed_in_code()
    {
        Assert.Equal("https://api.github.com/repos/mintggs2/pc-tidy/releases/latest", ReleaseSource.LatestApi.AbsoluteUri);
        Assert.DoesNotContain(typeof(ShellPrefs).GetProperties(), p => p.Name.Contains("Url", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Feed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Update_state_describes_this_PC_and_is_kept_out_of_exports()
    {
        var s = new AppSettings();
        ShellState.Set(s, new ShellState { LastUpdateCheckUtc = DateTime.UtcNow, SkippedUpdateVersion = "1.2.0", LastUpdateNote = "x" });
        var (json, _) = PcTidy.App.Portability.SettingsPackage.ExportJson(s, @"C:\Users\someone");
        Assert.DoesNotContain("SkippedUpdateVersion", json);
        Assert.DoesNotContain("LastUpdateCheckUtc", json);
    }

    // ------------------------------------------------------------------ uninstaller lookup (unchanged)

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
