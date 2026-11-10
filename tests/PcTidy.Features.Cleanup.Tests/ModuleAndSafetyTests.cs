using System.Text.RegularExpressions;
using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Features.Cleanup.Tests;

public sealed class ModuleAndSafetyTests
{
    [Fact]
    public void Module_is_wired_as_planned()
    {
        var m = new CleanupModule();
        Assert.Equal("cleanup", m.Id);
        Assert.Equal("Clean", m.Title);
        Assert.Equal(NavGroup.CleanUp, m.Group);
        Assert.Equal("Broom24", m.Icon);
        Assert.Equal(Release.V1, m.Release);
        Assert.Null(m.CreatePage(new EmptyServices()));
        Assert.Collection(m.Scanners,
            s => Assert.IsType<CleanupScanner>(s),
            s => Assert.IsType<HiddenFilesScanner>(s));
        var cleanup = m.Scanners[0];
        Assert.Equal("cleanup", cleanup.FeatureId);
        Assert.Equal(Release.V1, cleanup.Release);
        Assert.True(cleanup.IncludedInOptimizeAll);
    }

    [Fact]
    public void Section7_catalog_has_21_rows_and_only_safe_rows_are_ticked()
    {
        Assert.Equal(Enumerable.Range(1, 21), CleanupTargets.All.Select(t => t.Number));
        Assert.Equal(21, CleanupTargets.All.Select(t => t.Id).Distinct().Count());
        Assert.All(CleanupTargets.All.Where(t => t.DefaultSelected), t => Assert.Equal(Badge.Safe, t.Badge));
    }

    [Fact]
    public void What_we_dont_clean_lists_every_deliberately_skipped_area()
    {
        var text = CleanupModule.WhatWeDontCleanText;
        foreach (var word in new[] { "Prefetch", "Event logs", "Installer", "Driver store", "Pagefile", "Wipe free space", "MFT", "passwords", "Downloads", "Registry" })
            Assert.Contains(word, text);
    }

    /// <summary>Feature projects are read-only: no file, registry or process changes anywhere in the source.</summary>
    [Fact]
    public void Source_never_calls_changing_apis()
    {
        var src = Path.Combine(RepoPaths.RepoRoot, "src", "PcTidy.Features.Cleanup");
        var banned = new Regex(
            @"File\.(Delete|Move|Copy|Replace|WriteAll\w*|AppendAll\w*|Create|SetAttributes|Set\w+Time\w*)\s*\(|Directory\.(Delete|Move|CreateDirectory|Set\w+)\s*\(|\.MoveTo\s*\(|\.CopyTo\s*\(|\.Delete\s*\(|"
            + @"\.(SetValue|CreateSubKey|DeleteSubKey\w*|DeleteValue)\s*\(|Process\.Start|new\s+FileStream|File\.Open\w*\s*\(",
            RegexOptions.Compiled);
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((line, n) => (f, n, line)))
            .Where(x => !x.line.TrimStart().StartsWith("//") && banned.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.n + 1}: {x.line.Trim()}")
            .ToList();
        Assert.Empty(offenders);
    }

    /// <summary>Read-only registry/API queries on this real PC: must never throw, whatever the PC looks like.</summary>
    [Fact]
    public void Real_windows_state_reads_without_throwing()
    {
        var state = new PcTidy.Features.Cleanup.SystemState.WindowsSystemState();
        _ = state.IsRestartPending();
        var bin = state.QueryRecycleBin(CancellationToken.None);
        Assert.True(bin is null || bin.Value.Bytes >= 0);
        var power = state.ReadPower();
        Assert.True(power.TotalMemoryBytes is null or > 0);
        _ = state.PagingFiles();
        _ = state.ReservedStorageOn();
        _ = state.CompactOsOn();
        Assert.Null(state.RestoreStorageBytes(CancellationToken.None));
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
