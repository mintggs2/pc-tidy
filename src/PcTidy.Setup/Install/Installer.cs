using System.IO.Compression;
using PcTidy.Setup.Windows;

namespace PcTidy.Setup.Install;

public sealed record InstallRequest
{
    /// <summary>Opens the payload zip (the appended payload, or a test zip).</summary>
    public required Func<Stream> OpenPayload { get; init; }
    /// <summary>
    /// The Setup exe; its first <see cref="UninstallerLength"/> bytes become uninstall.exe when the payload has no
    /// uninstall.exe of its own (release builds ship one that loads nothing from the temporary folder). Empty = the
    /// payload must bring its own uninstall.exe (the admin part never copies a file from a user folder).
    /// </summary>
    public required string UninstallerSource { get; init; }
    /// <summary>Bytes to copy (the stub without the payload); 0 = the whole file.</summary>
    public long UninstallerLength { get; init; }
    public bool StartMenuShortcuts { get; init; } = true;
    public bool DesktopShortcut { get; init; }
}

public sealed record SetupProgress(double Fraction, string Message);

/// <summary>What an install attempt left behind, so the admin part's exit code (and the message) can say it truthfully.</summary>
public enum SetupOutcome
{
    /// <summary>Installed or updated.</summary>
    Done,
    /// <summary>Failed; nothing was left behind (a fresh install was undone, or the attempt stopped before any change).</summary>
    Failed,
    /// <summary>A PC Tidy copy is running from the folder; nothing was changed.</summary>
    AppOpen,
    /// <summary>Only copies in other accounts' sessions run from the folder (fast user switching); nothing was changed.</summary>
    AppOpenElsewhere,
    /// <summary>An update failed part-way and every file was put back: the previous version is intact.</summary>
    RolledBack,
    /// <summary>An update failed part-way and at least one file couldn't be put back: Setup must be run again.</summary>
    NeedsRepair,
}

public sealed record SetupResult(bool Ok, string Message, IReadOnlyList<string> Notes)
{
    public SetupOutcome Outcome { get; init; } = Ok ? SetupOutcome.Done : SetupOutcome.Failed;

    public static SetupResult Fail(string message) => new(false, message, Array.Empty<string>());
}

/// <summary>
/// The admin part of an install (or in-place update): copies the payload into %ProgramFiles%\PC Tidy, makes the folder
/// admin-only, writes uninstall.exe and install.json, makes all-users shortcuts and registers the machine-wide
/// "Installed apps" entry. Runs with admin rights; touches nothing of the user's (that is <see cref="UserSetup"/>).
/// Everything it touches comes from <see cref="InstallEnvironment"/>.
/// </summary>
public sealed class Installer
{
    private readonly InstallEnvironment _env;
    private readonly Registration _registration;

    public Installer(InstallEnvironment env)
    {
        _env = env;
        _registration = new Registration(env.Registry);
    }

    public ExistingInstall? FindExisting() => _registration.FindExisting();

    public Task<SetupResult> InstallAsync(InstallRequest request, IProgress<SetupProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Install(request, progress, ct), ct);

    private SetupResult Install(InstallRequest request, IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        var dir = _env.InstallDir;
        if (FolderSafety.CheckInstallDir(dir, _env.ProgramFilesRoot, _env.DataRoot) is { } problem) return SetupResult.Fail(problem);

        var previous = InstallManifest.Load(_env.ManifestPath);
        var isUpdate = previous is not null || File.Exists(_env.AppPath);
        if (!isUpdate && FolderSafety.HasForeignContent(dir))
            return SetupResult.Fail($"{dir} already has other files in it, so PC Tidy didn't install there. Remove them, then try again.");
        if (OpenRefusal(_env.Processes.FindRunning(dir)) is { } open) return open;

        var previousEntry = _registration.FindExisting();
        var written = new List<string>();
        var shortcuts = new List<string>();
        var newShortcuts = new List<string>(); // shortcuts this attempt created (an update's rollback removes them)
        var swap = new FileSwap();
        bool registered = false;
        try
        {
            Directory.CreateDirectory(dir);
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                return SetupResult.Fail($"{dir} is a link to somewhere else, so PC Tidy didn't install there.");
            // Admin-only before anything is copied, so every file inherits it from the start.
            _env.Hardener.Harden(dir);

            // 1. Stage: every new file is written next to its final name. Nothing of the installed version changes yet.
            progress?.Report(new SetupProgress(0.02, "Checking the files…"));
            string version;
            using (var zip = new ZipArchive(request.OpenPayload(), ZipArchiveMode.Read))
            {
                version = ReadVersion(zip) ?? throw new InvalidDataException("This installer is damaged (no version information). Please download it again.");
                bool payloadUninstaller = zip.GetEntry(Product.UninstallerExe) is not null;
                if (!payloadUninstaller && string.IsNullOrEmpty(request.UninstallerSource))
                    throw new InvalidDataException("This installer is damaged (it has no uninstaller). Please download it again.");
                StageFiles(zip, dir, swap, written, progress, ct);
                if (!payloadUninstaller) StageUninstaller(request, swap);
            }

            // 2. A copy may have started since the first check (Task Scheduler's weekly scan, the tray at sign-in).
            if (OpenRefusal(_env.Processes.FindRunning(dir)) is { } openNow)
            {
                swap.RollBack();
                if (!isUpdate) UndoFreshInstall(written, shortcuts);
                return openNow;
            }

            // 3. Swap every file in (each current one set aside first), then record the new version.
            progress?.Report(new SetupProgress(0.8, "Putting the new files in place…"));
            swap.SwapAll(ct);
            _env.Hardener.HardenContents(dir);

            progress?.Report(new SetupProgress(0.9, "Adding shortcuts…"));
            void Link(string path, string target, string description, string? icon = null)
            {
                if (!File.Exists(path)) newShortcuts.Add(path);
                ShellLink.Create(path, target, description, icon);
                shortcuts.Add(path);
            }
            if (request.StartMenuShortcuts)
            {
                Link(_env.AppShortcut, _env.AppPath, "Clean up, speed up and organize your PC, safely.");
                Link(_env.RescueShortcut, _env.RescuePath, "Undo what PC Tidy changed, even if PC Tidy won't start.", _env.AppPath);
            }
            if (request.DesktopShortcut) Link(_env.DesktopShortcut, _env.AppPath, "Clean up, speed up and organize your PC, safely.");

            progress?.Report(new SetupProgress(0.96, "Registering PC Tidy in Installed apps…"));
            var files = written.Append(Product.UninstallerExe).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            registered = true;
            _registration.Write(_env, version, files.Sum(f => SizeOf(Path.Combine(dir, f))));
            new InstallManifest { Version = version, Files = written, Shortcuts = shortcuts }.Save(_env.ManifestPath);

            // 4. Recorded: from here on the update stands. Remove what the older version had and this one doesn't.
            foreach (var stillInUse in swap.Commit()) _env.Reboot.DeleteAtRestart(stillInUse);
            foreach (var old in (previous?.Files ?? Array.Empty<string>()).Except(written, StringComparer.OrdinalIgnoreCase))
                TryDelete(Path.Combine(dir, old)); // left over from an older version
            foreach (var old in (previous?.Shortcuts ?? Array.Empty<string>()).Except(shortcuts, StringComparer.OrdinalIgnoreCase))
                TryDelete(old); // an option switched off on update
            Shortcuts.RemoveFolderIfEmpty(_env.StartMenuFolder);

            // The same check the app makes before every admin prompt: if it fails here, admin steps would be refused.
            var notes = new List<string>();
            if (_env.Hardener.Verify(_env.AppPath) is { } weak)
                notes.Add("PC Tidy's folder isn't admin-only, so PC Tidy won't run steps that need admin rights: " + weak);
            progress?.Report(new SetupProgress(1, "Done."));
            var message = isUpdate ? $"PC Tidy was updated to {version}." : $"PC Tidy {version} is installed.";
            return new SetupResult(true, message, notes) { Outcome = SetupOutcome.Done };
        }
        catch (Exception ex)
        {
            var reason = ex switch
            {
                OperationCanceledException => "Setup was stopped.",
                UnauthorizedAccessException => "Windows didn't allow writing to Program Files. Setup needs admin rights for that.",
                IOException io when io.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) => "A PC Tidy file is in use (PC Tidy, PC Tidy Rescue or a scheduled scan).",
                InvalidDataException => ex.Message,
                _ => "Setup couldn't finish: " + ex.Message,
            };
            var details = new[] { ex.ToString() };
            if (!isUpdate)
            {
                swap.RollBack();
                UndoFreshInstall(written, shortcuts);
                return new SetupResult(false, reason + " Nothing was left behind.", details) { Outcome = SetupOutcome.Failed };
            }
            var verb = swap.AnySwapped ? "was put back as it was" : "was kept as it was";
            if (RollBackUpdate(swap, registered, previous, previousEntry, newShortcuts))
            {
                var kept = previous?.Version ?? previousEntry?.Version;
                var what = string.IsNullOrEmpty(kept) ? $"PC Tidy {verb}." : $"PC Tidy {kept} {verb}.";
                var retry = ex is IOException or UnauthorizedAccessException ? " Try the update again in a few minutes." : "";
                return new SetupResult(false, $"{reason} {what}{retry}", details) { Outcome = SetupOutcome.RolledBack };
            }
            return new SetupResult(false, $"{reason} Some of PC Tidy's files couldn't be put back, so PC Tidy may not start. Run PC Tidy Setup again to repair it.", details)
                { Outcome = SetupOutcome.NeedsRepair };
        }
    }

    internal const string AppOpenMessage = "PC Tidy is open. Close it, then try again.";

    /// <summary>Null when no copy runs from the folder; else the refusal, naming another account that has it open.</summary>
    internal static SetupResult? OpenRefusal(IReadOnlyList<RunningApp> running)
        => running.Count == 0 ? null : SetupResult.Fail(RunningApp.OpenMessage(running)) with
        {
            Outcome = running.All(r => r.InOtherSession) ? SetupOutcome.AppOpenElsewhere : SetupOutcome.AppOpen,
        };

    /// <summary>
    /// An update that failed part-way: every replaced file goes back, files new in this version and shortcuts this
    /// attempt added go, and the Installed-apps entry shows the old version again. install.json was never rewritten
    /// (it is the last write). False when a file couldn't be restored.
    /// </summary>
    private bool RollBackUpdate(FileSwap swap, bool registered, InstallManifest? previous, ExistingInstall? previousEntry, IEnumerable<string> newShortcuts)
    {
        bool ok = swap.RollBack();
        foreach (var s in newShortcuts) TryDelete(s);
        if (registered)
        {
            try
            {
                var size = (previous?.Files ?? Array.Empty<string>()).Append(Product.UninstallerExe).Sum(f => SizeOf(Path.Combine(_env.InstallDir, f)));
                if (previousEntry is not null) _registration.Write(_env, previousEntry.Version, size);
                else _registration.RemoveUninstallEntry();
            }
            catch { /* the entry only shows a version number; the files are what matter */ }
        }
        return ok;
    }

    private static string? ReadVersion(ZipArchive zip)
    {
        var entry = zip.GetEntry(PayloadDescription.FileName);
        if (entry is null) return null;
        using var r = new StreamReader(entry.Open());
        return PayloadDescription.Parse(r.ReadToEnd())?.Version;
    }

    /// <summary>
    /// Each file is unpacked next to its final name (<see cref="FileSwap.NewSuffix"/>). Nothing is swapped in here,
    /// so a damaged payload, a full disk or a cancel part-way leaves the installed version exactly as it was.
    /// </summary>
    private static void StageFiles(ZipArchive zip, string dir, FileSwap swap, List<string> written, IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        var entries = zip.Entries.Where(e => e.Name.Length > 0 && !e.FullName.Equals(PayloadDescription.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
        long total = Math.Max(1, entries.Sum(e => e.Length)), done = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var relative = SafeRelativePath(entry.FullName);
            var target = Path.GetFullPath(Path.Combine(dir, relative));
            if (!FolderSafety.IsSameOrUnder(target, dir)) throw new InvalidDataException("This installer is damaged (a file points outside the folder).");
            progress?.Report(new SetupProgress(0.05 + 0.75 * done / total, "Copying " + relative + "…"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (FolderSafety.GoesThroughLink(target, dir)) throw new InvalidDataException("The install folder contains a link, so Setup stopped.");
            entry.ExtractToFile(swap.Stage(target), overwrite: true);
            written.Add(relative);
            done += entry.Length;
        }
    }

    private static string SafeRelativePath(string zipName)
    {
        var p = zipName.Replace('/', '\\');
        if (Path.IsPathRooted(p) || p.Split('\\').Any(s => s is ".." or "." || s.Length == 0))
            throw new InvalidDataException("This installer is damaged (bad file name " + zipName + ").");
        return p;
    }

    private void StageUninstaller(InstallRequest request, FileSwap swap)
    {
        var temp = swap.Stage(_env.UninstallerPath);
        using var src = new FileStream(request.UninstallerSource, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var dst = new FileStream(temp, FileMode.Create, FileAccess.Write);
        var length = request.UninstallerLength > 0 ? request.UninstallerLength : src.Length;
        CopyBytes(src, dst, length);
    }

    private static void CopyBytes(Stream src, Stream dst, long count)
    {
        var buffer = new byte[1 << 20];
        while (count > 0)
        {
            var n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n <= 0) throw new EndOfStreamException("The setup file is shorter than expected.");
            dst.Write(buffer, 0, n);
            count -= n;
        }
    }

    /// <summary>A brand-new install that failed half way: remove what it wrote (nothing else).</summary>
    private void UndoFreshInstall(IEnumerable<string> written, IEnumerable<string> shortcuts)
    {
        foreach (var f in written) TryDelete(Path.Combine(_env.InstallDir, f));
        foreach (var f in written) TryDelete(Path.Combine(_env.InstallDir, f) + FileSwap.NewSuffix);
        foreach (var s in shortcuts) TryDelete(s);
        TryDelete(_env.UninstallerPath);
        TryDelete(_env.UninstallerPath + FileSwap.NewSuffix);
        TryDelete(_env.ManifestPath);
        Shortcuts.RemoveFolderIfEmpty(_env.StartMenuFolder);
        try { _registration.RemoveUninstallEntry(); } catch { /* best effort */ }
        Folders.RemoveEmptyTree(_env.InstallDir);
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* left for the uninstaller */ }
    }
}

internal static class Shortcuts
{
    public static void RemoveFolderIfEmpty(string folder)
    {
        try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        catch { /* not empty or in use */ }
    }
}

internal static class Folders
{
    /// <summary>Removes empty folders under (and including) <paramref name="root"/>, deepest first. Never removes files.</summary>
    public static void RemoveEmptyTree(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                         .OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { /* keep */ }
            }
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch { /* best effort */ }
    }
}
