namespace PcTidy.Rescue.Tests;

[Collection("rescue")]
public class RescueViewModelTests
{
    private sealed class FakeDialogs : IRescueDialogs
    {
        public bool Answer { get; set; } = true;
        public string? Folder { get; set; }
        public List<string> Confirms { get; } = new();
        public List<(string Title, string Message, bool IsError)> Shown { get; } = new();
        public bool Confirm(string title, string message) { Confirms.Add(title); return Answer; }
        public string? PickFolder(string? startIn) => Folder;
        public void Show(string title, string message, bool isError) => Shown.Add((title, message, isError));
        public void OpenFolder(string path) { }
    }

    [Fact]
    public async Task Window_logic_lists_runs_and_undo_asks_then_restores_everything()
    {
        using var bed = new RescueBed("vm-undo");
        var run = await bed.RunAsApp(bed.QuarantineAndMovePlan());
        var dialogs = new FakeDialogs { Answer = false };
        using var vm = new RescueViewModel(RescueTarget.ForSandbox(bed.Root), dialogs);
        await vm.LoadAsync();

        Assert.True(vm.HasRuns);
        Assert.Equal(run.RunId, vm.Selected!.RunId);
        Assert.Equal(6, vm.SelectedSteps.Count);
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.ResumeCommand.CanExecute(null));
        Assert.Contains("practice folder", vm.DataRoot);

        await vm.UndoCommand.ExecuteAsync(); // user says No
        Assert.Single(dialogs.Confirms);
        Assert.False(File.Exists(bed.Downloads("ChromeSetup.exe")));

        dialogs.Answer = true;
        await vm.UndoCommand.ExecuteAsync();
        var shown = Assert.Single(dialogs.Shown);
        Assert.False(shown.IsError, shown.Message);
        bed.AssertIdenticalToStart();
        Assert.Equal("Undone", vm.Selected!.StatusText); // list refreshed, same run still selected
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task Browse_to_another_data_folder_switches_the_list()
    {
        using var bed = new RescueBed("vm-browse");
        await bed.RunAsApp(bed.QuarantineAndMovePlan());
        var empty = Path.Combine(bed.Root, "Empty data");
        Directory.CreateDirectory(empty);
        var dialogs = new FakeDialogs { Folder = empty, Answer = true };
        using var vm = new RescueViewModel(RescueTarget.ForSandbox(bed.Root), dialogs);
        await vm.LoadAsync();
        Assert.True(vm.HasRuns);

        // An empty folder isn't a data folder: asked first, then shown as empty. Opened read-only (no journal yet).
        await vm.BrowseCommand.ExecuteAsync();
        Assert.Single(dialogs.Confirms);
        Assert.False(vm.HasRuns);
        Assert.Null(vm.Selected);
        Assert.StartsWith(empty, vm.DataRoot);
        Assert.DoesNotContain(Directory.GetFileSystemEntries(empty), e => !e.EndsWith("logs"));
    }
}
