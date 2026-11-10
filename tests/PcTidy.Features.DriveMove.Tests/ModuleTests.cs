using System.Text.RegularExpressions;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Common;
using PcTidy.Features.DriveMove.Drives;
using Xunit;

namespace PcTidy.Features.DriveMove.Tests;

/// <summary>The module's shape, one-drive PCs, plain-English reasons everywhere, and the read-only rule for this project.</summary>
[Collection("drivemove-testbed")]
public class ModuleTests
{
    [Fact]
    public void Module_is_the_v2_move_to_drive_section_in_Organize()
    {
        var m = new DriveMoveModule();
        Assert.Equal("drivemove", m.Id);
        Assert.Equal("Move to drive", m.Title);
        Assert.Equal(NavGroup.Organize, m.Group);
        Assert.Equal("ArrowSwap24", m.Icon);
        Assert.True(Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(m.Icon, out _), "the icon must exist in WPF-UI");
        Assert.Equal(Release.V2, m.Release);
        var scanner = Assert.IsType<DriveMoveScanner>(Assert.Single(m.Scanners));
        Assert.False(scanner.IncludedInOptimizeAll);
        foreach (DriveMovePartScanner part in new DriveMovePartScanner[] { new DriveBalanceScanner(), new FolderMoveScanner(), new UserFolderScanner(), new GameDriveScanner(), new AppCacheScanner() })
        {
            Assert.False(part.IncludedInOptimizeAll);
            Assert.Equal("drivemove", part.FeatureId);
        }
    }

    [Fact]
    public async Task A_one_drive_PC_gets_not_available_from_every_part()
    {
        using var s = new DriveMoveSandbox("one-drive", SandboxProfile.Minimal);
        foreach (DriveMovePartScanner part in new DriveMovePartScanner[] { new FolderMoveScanner(), new UserFolderScanner(), new GameDriveScanner(), new AppCacheScanner(), new DriveBalanceScanner(), new DriveMoveScanner() })
        {
            var r = await part.ScanAsync(s.Context(), null, CancellationToken.None);
            Assert.Contains(NotAvailable.SingleDrive, r.Notes);
            Assert.DoesNotContain(r.AllItems, i => i.Actions.Count > 0);
        }
    }

    [Fact]
    public async Task Drive_balance_on_a_full_drive_suggests_moves_that_carry_real_plan_items()
    {
        using var s = new DriveMoveSandbox("balance");
        s.Put(Path.Combine(s.K.Videos, "clip.mp4"), DriveMoveSandbox.Bytes(50_000, 1));
        s.Put(Path.Combine(s.K.Downloads, "Installers", "big.iso"), DriveMoveSandbox.Bytes(20_000, 2));
        s.SteamGame(s.K.SteamLibraries[0], "100", "Old Game", "OldGame", 30_000, new DateTimeOffset(DriveMoveSandbox.Now.AddDays(-300)).ToUnixTimeSeconds(), 1);
        s.Drives((1_000_000, 100_000, DriveMedia.Ssd), (1_000_000, 900_000, DriveMedia.Hdd)); // C: 10% free
        var scanner = new DriveBalanceScanner();
        var r = await scanner.ScanAsync(s.Context(), null, CancellationToken.None);

        var drives = r.Groups.Single(g => g.Id == DriveMoveIds.GroupDrives);
        Assert.Equal(2, drives.Items.Count);
        Assert.Contains("Getting full", drives.Items[0].Reason);
        var suggestions = r.Groups.Single(g => g.Id == DriveMoveIds.GroupSuggestions).Items;
        // The move, then the old copy's quarantine steps that wait for it.
        Assert.Contains(suggestions, i => i.Title.StartsWith("Move Videos (") && i.Actions[0].Kind == ActionKind.KnownFolderRedirect
            && i.Actions.Skip(1).All(q => q.Kind == ActionKind.QuarantinePath && q.DependsOn!.Single() == i.Actions[0].Id) && i.Actions.Count == 2);
        Assert.Contains(suggestions, i => i.Title.StartsWith("1 Steam game not played in 6 months") && i.Actions.Single().Kind == ActionKind.OpenExternal);
        // Videos and Downloads are suggested whole, so their subfolders aren't suggested again.
        Assert.DoesNotContain(suggestions, i => i.Title.StartsWith("Move Installers"));
        Assert.All(suggestions, i => Assert.False(i.DefaultSelected));
        Assert.NotNull(scanner.LastAdvice);
        Assert.Single(scanner.LastAdvice!.FullDrives);
    }

    [Fact]
    public async Task Every_group_and_item_explains_itself_and_nothing_risky_is_preticked()
    {
        using var s = new DriveMoveSandbox("quality", generate: true);
        s.Host.Settings.AllowedAskFirstFolders.Add(s.K.Videos);
        var r = await new DriveMoveScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        Assert.NotEmpty(r.Groups);
        foreach (var g in r.Groups)
        {
            Assert.False(string.IsNullOrWhiteSpace(g.Description), g.Id);
            foreach (var i in g.Items)
            {
                Assert.False(string.IsNullOrWhiteSpace(i.Reason), i.Title);
                Assert.False(i.DefaultSelected, i.Title);
                Assert.DoesNotContain("TODO", i.Reason);
                // A row that can't be ticked never adds to the totals.
                if (i.DisabledReason is not null) Assert.Equal(0, i.SizeBytes);
                foreach (var a in i.Actions) Assert.False(string.IsNullOrWhiteSpace(a.Description));
            }
        }
        // The suggestions repeat the other tabs' items, so the combined scan leaves them out.
        Assert.DoesNotContain(r.Groups, g => g.Id == DriveMoveIds.GroupSuggestions);
    }

    /// <summary>Plan rule 5: feature projects are read-only. Scan this project's own source for change APIs.</summary>
    [Fact]
    public void The_feature_project_never_calls_a_changing_API()
    {
        var src = Path.GetFullPath(Path.Combine(DriveMoveSandbox.TestbedRoot, "..", "..", "src", "PcTidy.Features.DriveMove"));
        var forbidden = new Regex(@"\b(File\.(Delete|Move|Copy|WriteAll\w*|Replace|Create|AppendAll\w*|SetAttributes)|Directory\.(Delete|Move|CreateDirectory)|\.MoveTo\(|\.Delete\(|Process\.Start|SetValue\(|CreateSubKey|DeleteSubKey|DeleteValue|writable:\s*true)", RegexOptions.CultureInvariant);
        var hits = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"))
            .SelectMany(f => File.ReadLines(f).Select((line, n) => (f, n, line)))
            .Where(x => !x.line.TrimStart().StartsWith("//") && !x.line.TrimStart().StartsWith("///") && forbidden.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.n + 1}: {x.line.Trim()}")
            .ToList();
        Assert.True(hits.Count == 0, string.Join("\n", hits));
    }
}
