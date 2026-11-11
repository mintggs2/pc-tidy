using System.Diagnostics;
using PcTidy.UI.Flow.Resume;
using Xunit;

namespace PcTidy.UI.Tests;

/// <summary>
/// The scan-checkpoints folder replaced by a junction (re-review N1): PC Tidy must not read, write or delete anything
/// at the junction's target, even files that look like damaged checkpoints.
/// </summary>
public class CheckpointLinkTests
{
    [Fact]
    public void A_linked_checkpoint_folder_is_never_read_written_or_cleaned()
    {
        var root = Path.Combine(Path.GetTempPath(), "pctidy-cp-link-" + Guid.NewGuid().ToString("N")[..8]);
        var dataRoot = Path.Combine(root, "data");
        var target = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(Path.Combine(target, "combined.sections"));
        try
        {
            // Files at the target that the cleanup would otherwise delete: a damaged header and a section file.
            var header = Path.Combine(target, "combined.json");
            var section = Path.Combine(target, "combined.sections", "0-abcdef12.json");
            var other = Path.Combine(target, "combined.sections", "notes.txt");
            File.WriteAllText(header, "{ not json");
            File.WriteAllText(section, "{}");
            File.WriteAllText(other, "keep me");

            var link = Path.Combine(dataRoot, "scan-checkpoints");
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                   { CreateNoWindow = true, UseShellExecute = false })!) p.WaitForExit();
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "test setup: junction not created");

            var store = new FileScanCheckpointStore(dataRoot);
            Assert.Empty(store.LoadAll());
            Assert.Null(store.Load("combined"));
            Assert.Null(store.LoadHeader("combined"));
            store.Delete("combined");

            Assert.True(File.Exists(header));
            Assert.True(File.Exists(section));
            Assert.True(File.Exists(other));
            Assert.Single(Directory.GetFiles(target, "*", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            var link = Path.Combine(dataRoot, "scan-checkpoints");
            if (Directory.Exists(link)) Directory.Delete(link); // removes only the junction
            Directory.Delete(root, recursive: true);
        }
    }
}
