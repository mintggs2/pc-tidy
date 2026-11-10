using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Developer.Caches;
using PcTidy.Features.Developer.Common;
using PcTidy.Features.Developer.Containers;
using PcTidy.Features.Developer.Projects;

namespace PcTidy.Features.Developer;

/// <summary>
/// The whole Developer section in one scan (build folders, package caches, Docker &amp; WSL) for the shell's standard
/// flow. Archiving is only on the section's own page (it needs an archive folder you chose). Never part of
/// "Optimize &amp; Organize everything": developer folders are your call every time.
/// </summary>
public sealed class DeveloperScanner : IFeatureScanner
{
    private readonly IFeatureScanner[] _parts = { new BuildFoldersScanner(), new PackageCacheScanner(), new DockerWslScanner() };

    public string FeatureId => DevContext.FeatureId;
    public string Title => "Developer";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var results = new List<ScanResult>();
        for (int i = 0; i < _parts.Length; i++)
        {
            int index = i;
            var part = progress is null ? null : new Progress<ScanProgress>(p =>
                progress.Report(p with { Fraction = (index + Math.Clamp(p.Fraction ?? 0, 0, 1)) / _parts.Length }));
            results.Add(await _parts[i].ScanAsync(ctx, part, ct).ConfigureAwait(false));
        }
        return new ScanResult
        {
            FeatureId = FeatureId,
            Title = Title,
            Depth = ctx.Depth,
            Duration = sw.Elapsed,
            Groups = results.SelectMany(r => r.Groups).ToList(),
            Notes = results.SelectMany(r => r.Notes).Distinct().ToList(),
        };
    }
}
