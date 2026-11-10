using Xunit;

namespace PcTidy.Features.Cleanup.Tests;

/// <summary>Community tweaks §9.6 (Storage Sense) and the mojibake fix in the big-hidden-files progress text.</summary>
public sealed class ResearchAdditionsTests
{
    [Fact]
    public void Storage_sense_downloads_clean_up_is_explained_as_a_permanent_delete()
    {
        var text = CleanupModule.WhatWeDontCleanText;
        Assert.Contains("Storage Sense", text);
        Assert.Contains("for good", text);
        Assert.Contains("Settings → System → Storage → Storage Sense", text);
    }

    [Fact]
    public void Hidden_files_progress_text_has_no_mojibake()
    {
        var src = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "src", "PcTidy.Features.Cleanup", "HiddenFilesScanner.cs"));
        Assert.DoesNotContain("â€", src); // "â€", the start of a UTF-8 "…" read as Windows-1252
        Assert.Contains("Checking {title}…", src);
    }
}
