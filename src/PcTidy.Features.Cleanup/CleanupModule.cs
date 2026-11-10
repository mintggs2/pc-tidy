using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Features.Cleanup;

/// <summary>"Clean" in the left rail: the §7 cleanup list plus the Big hidden Windows files tab, on the shell's standard flow page.</summary>
public sealed class CleanupModule : IFeatureModule
{
    public string Id => "cleanup";
    public string Title => "Clean";
    public string Summary => "Frees space from temp files, caches, old logs and leftovers. You see everything first, and it can be undone unless it's marked Permanent.";
    public NavGroup Group => NavGroup.CleanUp;
    public string Icon => "Broom24";
    public int Order => 10;
    public Release Release => Release.V1;
    public IReadOnlyList<IFeatureScanner> Scanners { get; } = new IFeatureScanner[] { new CleanupScanner(), new HiddenFilesScanner() };

    /// <summary>Help text for the page's "What we don't clean, and why" link.</summary>
    public static string WhatWeDontCleanText => WhatWeDontClean.AsText();

    /// <summary>Null: the shell's standard Scan → Preview → Confirm → Done page is used.</summary>
    public object? CreatePage(IServiceProvider services) => null;
}
