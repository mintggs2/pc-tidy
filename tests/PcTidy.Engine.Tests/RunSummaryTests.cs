using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;
using static PcTidy.Engine.Tests.TreeTools;

namespace PcTidy.Engine.Tests;

public class RunSummaryTests
{
    [Fact]
    public async Task Summary_and_markdown_report_explain_what_happened()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var junk = WriteFile(Path.Combine(work, "junk.tmp"), 2048, 1);
        var photo = WriteFile(Path.Combine(work, "cat.jpg"), 100, 2);
        var busy = WriteFile(Path.Combine(work, "busy.log"), 10, 3);
        sb.InUse.InUse[busy] = new[] { "Notepad" };

        var run = await sb.Engine().Executor.ExecuteAsync(Plan(
            ScannedItem(ActionKind.QuarantinePath, junk),
            ScannedItem(ActionKind.MovePath, photo, Path.Combine(sb.Locations.Downloads, "Pictures", "cat.jpg")),
            ScannedItem(ActionKind.QuarantinePath, busy)), Real, null, default);

        var summary = RunSummary.Summarize(run);
        Assert.Contains("Moved 2 KB to quarantine.", summary);
        Assert.Contains("Moved or renamed 1 item(s).", summary);
        Assert.Contains("1 skipped.", summary);

        var md = RunSummary.ToMarkdown(run, sb.Settings.QuarantineDays);
        Assert.StartsWith("# Test run", md);
        Assert.Contains("## Skipped (1)", md);
        Assert.Contains("**In use by Notepad.**", md);
        Assert.Contains("You can undo this from History. Quarantined files are kept for 7 days.", md);
        Assert.Contains("| 3 | Skipped |", md);
    }

    [Fact]
    public void Factory_wires_handlers_for_every_engine_file_action()
    {
        using var sb = new TestSandbox();
        var (journal, quarantine, executor, undo) = sb.Engine();
        Assert.NotNull(journal); Assert.NotNull(quarantine); Assert.NotNull(executor); Assert.NotNull(undo);

        var handlers = EngineFactory.CreateFileHandlers();
        foreach (var kind in new[] { ActionKind.QuarantinePath, ActionKind.MovePath, ActionKind.RenamePath, ActionKind.CreateDirectory,
                     ActionKind.WriteTextFile, ActionKind.ArchiveToZip, ActionKind.ExtractZip, ActionKind.MoveFolderWithJunction })
            Assert.Single(handlers, h => h.CanHandle(Item(kind)));
        Assert.DoesNotContain(handlers, h => h.CanHandle(Item(ActionKind.RegistrySetValue)));
    }
}
