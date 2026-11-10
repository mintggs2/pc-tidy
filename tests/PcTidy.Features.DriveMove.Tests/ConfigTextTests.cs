using PcTidy.Catalog.DriveMove;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Caches;
using PcTidy.Features.DriveMove.Common;
using Xunit;

namespace PcTidy.Features.DriveMove.Tests;

/// <summary>One setting changed, everything else in the tool's config kept exactly; edits of one file merged.</summary>
public class ConfigTextTests
{
    private const string Target = @"D:\Moved by PC Tidy\npm cache";

    [Fact]
    public void Npmrc_replaces_or_appends_the_line_and_drops_later_duplicates()
    {
        Assert.Equal($"cache={Target}\r\n", ConfigText.Set(ConfigFormat.NpmRc, "", "cache", Target));
        var text = "registry=https://registry.npmjs.org/\n# cache=old-comment\ncache=C:\\old\nsave-exact=true\ncache = C:\\older\n";
        var updated = ConfigText.Set(ConfigFormat.NpmRc, text, "cache", Target)!;
        Assert.Equal($"registry=https://registry.npmjs.org/\n# cache=old-comment\ncache={Target}\nsave-exact=true\n", updated);
        Assert.Equal(Target, ConfigText.Get(ConfigFormat.NpmRc, updated, "cache"));
        Assert.Equal("registry=a\nstore-dir=X\n", ConfigText.Set(ConfigFormat.NpmRc, "registry=a\n", "store-dir", "X"));
    }

    [Fact]
    public void Pip_ini_sets_the_key_in_its_section_and_understands_underscores()
    {
        Assert.Equal("[global]\r\ncache-dir = X\r\n", ConfigText.Set(ConfigFormat.PipIni, "", "cache-dir", "X", "global"));
        var text = "[global]\r\ntimeout = 60\r\ncache_dir = C:\\old\r\n\r\n[install]\r\nno-cache-dir = false\r\n";
        var updated = ConfigText.Set(ConfigFormat.PipIni, text, "cache-dir", "X", "global")!;
        Assert.Equal("[global]\r\ntimeout = 60\r\ncache-dir = X\r\n\r\n[install]\r\nno-cache-dir = false\r\n", updated);
        Assert.Equal("[global]\r\ntimeout = 60\r\ncache-dir = X\r\n", ConfigText.Set(ConfigFormat.PipIni, "[global]\r\ntimeout = 60\r\n", "cache-dir", "X", "global"));
        Assert.Equal("[install]\r\nuser = true\r\n\r\n[global]\r\ncache-dir = X\r\n", ConfigText.Set(ConfigFormat.PipIni, "[install]\r\nuser = true\r\n", "cache-dir", "X", "global"));
        Assert.Equal("X", ConfigText.Get(ConfigFormat.PipIni, updated, "cache-dir", "global"));
        Assert.Null(ConfigText.Get(ConfigFormat.PipIni, updated, "no-cache-dir", "global"));
    }

    [Fact]
    public void NuGet_config_gets_one_globalPackagesFolder()
    {
        var fresh = ConfigText.Set(ConfigFormat.NuGetConfig, "", "globalPackagesFolder", @"D:\pk")!;
        Assert.Equal(@"D:\pk", ConfigText.Get(ConfigFormat.NuGetConfig, fresh, "globalPackagesFolder"));
        var existing = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <config>\n    <add key=\"globalPackagesFolder\" value=\"C:\\old\" />\n    <add key=\"http_proxy\" value=\"p\" />\n  </config>\n</configuration>";
        var updated = ConfigText.Set(ConfigFormat.NuGetConfig, existing, "globalPackagesFolder", @"D:\pk")!;
        Assert.Contains("<add key=\"globalPackagesFolder\" value=\"D:\\pk\" />", updated);
        Assert.Contains("http_proxy", updated);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", updated);
        Assert.Null(ConfigText.Set(ConfigFormat.NuGetConfig, "<not-xml", "globalPackagesFolder", "x")); // never guessed
        Assert.Null(ConfigText.Set(ConfigFormat.NuGetConfig, "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"y\">]><configuration/>", "k", "v"));
    }

    [Fact]
    public void Maven_settings_keep_their_namespace()
    {
        var fresh = ConfigText.Set(ConfigFormat.MavenSettings, "", "localRepository", @"D:\m2")!;
        Assert.Equal(@"D:\m2", ConfigText.Get(ConfigFormat.MavenSettings, fresh, "localRepository"));
        var existing = "<settings xmlns=\"http://maven.apache.org/SETTINGS/1.0.0\"><mirrors/></settings>";
        var updated = ConfigText.Set(ConfigFormat.MavenSettings, existing, "localRepository", @"D:\m2")!;
        Assert.Equal("<settings xmlns=\"http://maven.apache.org/SETTINGS/1.0.0\"><localRepository>D:\\m2</localRepository><mirrors /></settings>", updated);
    }

    [Fact]
    public void Two_ticked_edits_of_one_npmrc_become_one_edit_from_the_current_file()
    {
        PlanItem Edit(string tool, string key, string value) => new()
        {
            Kind = ActionKind.EditTextFile, FeatureId = DriveMoveIds.FeatureId, Description = $"{tool}", SourcePath = @"C:\Users\x\.npmrc",
            ExpectedLastWriteUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Args = new()
            {
                [DriveMoveArgs.Content] = "stale", [DriveMoveArgs.Tool] = tool, [DriveMoveArgs.Key] = key, [DriveMoveArgs.NewLocation] = value,
                [DriveMoveArgs.Format] = nameof(ConfigFormat.NpmRc),
            },
        };
        var items = new[] { Edit("npm", "cache", @"D:\npm"), Edit("pnpm", "store-dir", @"D:\pnpm") };
        var merged = Assert.Single(ConfigEditMerger.Merge(items, _ => ("registry=x\n", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
        Assert.Equal("registry=x\ncache=D:\\npm\nstore-dir=D:\\pnpm\n", merged.Args![DriveMoveArgs.Content]);

        Assert.Throws<ConfigEditMerger.FileChangedException>(() =>
            ConfigEditMerger.Merge(items, _ => ("registry=x\n", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc))));
        // A single edit is left exactly as planned.
        Assert.Same(items[0], Assert.Single(ConfigEditMerger.Merge(items.Take(1).ToList())));
    }
}
