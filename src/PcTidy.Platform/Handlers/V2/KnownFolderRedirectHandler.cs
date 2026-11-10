using System.Runtime.InteropServices;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>Result of IKnownFolderManager::Redirect: HRESULT and Windows' own error text (only shown, never parsed).</summary>
public sealed record RedirectOutcome(int HResult, string? Error)
{
    public bool Ok => HResult >= 0;
}

/// <summary>Where Windows' user folders live. Real: SHGetKnownFolderPath + IKnownFolderManager::Redirect. Tests: a fake.</summary>
public interface IKnownFolderControl
{
    bool TouchesRealSystem { get; }
    string? GetPath(Guid folderId);
    /// <summary>checkOnly = KF_REDIRECT_CHECK_ONLY; otherwise KF_REDIRECT_COPY_CONTENTS (never KF_REDIRECT_DEL_SOURCE_CONTENTS).</summary>
    RedirectOutcome Redirect(Guid folderId, string targetPath, bool checkOnly);
}

public sealed class ShellKnownFolderControl : IKnownFolderControl
{
    private const uint KfFlagDontVerify = 0x4000;
    private const uint KfRedirectCheckOnly = 0x10;
    private const uint KfRedirectCopyContents = 0x200;
    private static readonly Guid ClsidKnownFolderManager = new("4df0c730-df9d-4ae3-9153-aa6b82e9795a");

    public bool TouchesRealSystem => true;

    public string? GetPath(Guid folderId)
    {
        if (SHGetKnownFolderPath(ref folderId, KfFlagDontVerify, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public RedirectOutcome Redirect(Guid folderId, string targetPath, bool checkOnly)
    {
        var type = Type.GetTypeFromCLSID(ClsidKnownFolderManager) ?? throw new InvalidOperationException("Windows' known-folder service isn't available.");
        var manager = (IKnownFolderManager)Activator.CreateInstance(type)!;
        try
        {
            var flags = checkOnly ? KfRedirectCheckOnly : KfRedirectCopyContents;
            var hr = manager.Redirect(ref folderId, IntPtr.Zero, flags, targetPath, 0, IntPtr.Zero, out var error);
            string? text = null;
            if (error != IntPtr.Zero) { text = Marshal.PtrToStringUni(error); Marshal.FreeCoTaskMem(error); }
            return new RedirectOutcome(hr, text);
        }
        finally { Marshal.FinalReleaseComObject(manager); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    /// <summary>IKnownFolderManager (shobjidl_core.h). Only Redirect is called; the other slots keep the vtable order.</summary>
    [ComImport, Guid("8BE2D872-86AA-4d47-B776-32CCA40C7018"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKnownFolderManager
    {
        [PreserveSig] int FolderIdFromCsidl(int csidl, out Guid id);
        [PreserveSig] int FolderIdToCsidl(ref Guid id, out int csidl);
        [PreserveSig] int GetFolderIds(out IntPtr ids, ref uint count);
        [PreserveSig] int GetFolder(ref Guid id, out IntPtr folder);
        [PreserveSig] int GetFolderByName([MarshalAs(UnmanagedType.LPWStr)] string name, out IntPtr folder);
        [PreserveSig] int RegisterFolder(ref Guid id, IntPtr definition);
        [PreserveSig] int UnregisterFolder(ref Guid id);
        [PreserveSig] int FindFolderFromPath([MarshalAs(UnmanagedType.LPWStr)] string path, int mode, out IntPtr folder);
        [PreserveSig] int FindFolderFromIDList(IntPtr pidl, out IntPtr folder);
        [PreserveSig] int Redirect(ref Guid id, IntPtr hwnd, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string target, uint exclusionCount, IntPtr exclusions, out IntPtr error);
    }
}

/// <summary>
/// KnownFolderRedirect. Args: "folderId" (Desktop, Documents, Downloads, Pictures, Videos or Music); DestinationPath.
/// Windows' own Location move: IKnownFolderManager::Redirect, first with CHECK_ONLY, then with COPY_CONTENTS and never
/// DEL_SOURCE_CONTENTS, so the old copy stays where it was (removing it is a separate quarantine step in the plan).
/// The move is verified (Windows reports the new path and every file is there with the same size); otherwise the
/// folder is pointed back. Undo redirects back to the recorded old path, copying anything new back with it.
/// </summary>
public sealed class KnownFolderRedirectHandler(IKnownFolderControl folders) : SystemActionHandler
{
    internal const string PriorPathData = "priorPath";
    internal const string CreatedTargetData = "createdTarget";
    private const int MaxVerifiedEntries = 500_000;

    /// <summary>The user libraries PC Tidy may move (same as the SafetyGuard).</summary>
    public static readonly IReadOnlyDictionary<Guid, string> UserFolders = new Dictionary<Guid, string>
    {
        [new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641")] = "Desktop",
        [new("FDD39AD0-238F-46AF-ADB4-6C85480369C7")] = "Documents",
        [new("374DE290-123F-4565-9164-39C4925E467B")] = "Downloads",
        [new("33E28130-4E1E-4676-835A-98395C3BC3BB")] = "Pictures",
        [new("18989B1D-99B5-455B-841C-AB7C74E4DDFC")] = "Videos",
        [new("4BD8D571-6D19-48D3-BE97-422220080E43")] = "Music",
    };

    public KnownFolderRedirectHandler() : this(new ShellKnownFolderControl()) { }

    protected override ActionKind Kind => ActionKind.KnownFolderRedirect;
    protected override bool TouchesRealSystem => folders.TouchesRealSystem;
    protected override bool PerUserOnly(PlanItem item) => true;

    private static Guid Id(PlanItem item) => Guid.Parse(item.Arg("folderId").Trim());
    private static string Name(PlanItem item) => UserFolders[Id(item)];
    private static string Target(PlanItem item) => Normalize(item.DestinationPath!);
    private static string Normalize(string p) => Path.GetFullPath(p).TrimEnd('\\');

    protected override string? Validate(PlanItem item)
    {
        if (!Guid.TryParse(item.Arg("folderId").Trim(), out var id) || !UserFolders.ContainsKey(id)) return "PC Tidy only moves your Desktop, Documents, Downloads, Pictures, Videos and Music folders.";
        if (!PathArgs.IsPlainLocal(item.DestinationPath)) return "The new place for this folder must be a folder on a drive of this PC (not a drive itself or a network share).";
        return null;
    }

    protected override string Describe(PlanItem item)
        => $"Would move your {Name(item)} folder to {Target(item)} with Windows' own Location move (IKnownFolderManager::Redirect, copy contents, old copy kept)";

    protected override bool CanUndo(StepRecord step) => step.Undo(PriorPathData) is not null;
    protected override string DescribeUndo(StepRecord step) => $"Would move your {Name(step.Item)} folder back to {step.Undo(PriorPathData)}";

    protected override Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.Run(() =>
    {
        var name = Name(step.Item);
        var target = Target(step.Item);
        if (folders.GetPath(Id(step.Item)) is not { } currentRaw) return step.Skipped($"Windows didn't say where your {name} folder is.");
        var current = Normalize(currentRaw);
        if (current.Equals(target, StringComparison.OrdinalIgnoreCase)) return step.Skipped($"Your {name} folder is already there. Nothing to change.");
        if (PathUtil.IsUnder(target, current) || PathUtil.IsUnder(current, target)) return step.Skipped("A folder can't be moved into itself.");
        if (!PathArgs.HasNoLinks(target)) return step.Skipped("The new place goes through a link to somewhere else, so PC Tidy won't use it.");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) return step.Skipped("The new place isn't empty, so PC Tidy won't move the folder there.");

        var created = !Directory.Exists(target);
        if (created) Directory.CreateDirectory(target);
        var check = folders.Redirect(Id(step.Item), target, checkOnly: true);
        if (!check.Ok)
        {
            if (created) TryRemoveEmpty(target);
            return step.Skipped($"Windows can't move your {name} folder there{Because(check)}");
        }

        ct.ThrowIfCancellationRequested();
        var moved = folders.Redirect(Id(step.Item), target, checkOnly: false);
        var now = folders.GetPath(Id(step.Item)) is { } p ? Normalize(p) : null;
        if (!moved.Ok || !string.Equals(now, target, StringComparison.OrdinalIgnoreCase))
        {
            if (now is not null && !now.Equals(current, StringComparison.OrdinalIgnoreCase)) folders.Redirect(Id(step.Item), current, checkOnly: false);
            return step.Failed($"Windows didn't move your {name} folder{Because(moved)}");
        }
        var copy = CheckCopy(current, target, ct);
        if (!copy.Complete)
        {
            // Point the folder back; what was copied stays in the new place (nothing is ever deleted here). The old-copy
            // quarantine steps depend on this step being Done, so they don't run either (KFM-1: "not checked" is never
            // "complete").
            folders.Redirect(Id(step.Item), current, checkOnly: false);
            return step.Failed(copy.Missing is { } missing
                ? $"Windows didn't copy everything (for example {missing}), so your {name} folder was left where it was. The copies in {target} were kept."
                : $"PC Tidy couldn't fully check that every file arrived ({copy.Unchecked}), so your {name} folder was left where it was. The copies in {target} were kept.");
        }

        var done = step.Done($"Moved your {name} folder to {target}. The old copy is still in {current} until the next step tidies it.")
            .WithUndo((PriorPathData, current));
        return created ? done.WithUndo((CreatedTargetData, "1")) : done;
    }, ct);

    protected override Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.Run(() =>
    {
        var name = Name(step.Item);
        var prior = step.Undo(PriorPathData)!;
        if (!PathArgs.IsPlainLocal(prior)) return step.UndoFailed("The folder's old place is missing, so PC Tidy can't undo this.");
        prior = Normalize(prior);
        var now = folders.GetPath(Id(step.Item)) is { } p ? Normalize(p) : null;
        if (prior.Equals(now, StringComparison.OrdinalIgnoreCase)) return step.Skipped($"Your {name} folder is already back in {prior}.");
        if (!PathArgs.HasNoLinks(prior)) return step.UndoFailed("The old place now goes through a link to somewhere else, so PC Tidy won't move the folder there.");
        Directory.CreateDirectory(prior);
        var check = folders.Redirect(Id(step.Item), prior, checkOnly: true);
        if (!check.Ok) return step.UndoFailed($"Windows can't move your {name} folder back{Because(check)}");
        var moved = folders.Redirect(Id(step.Item), prior, checkOnly: false);
        var after = folders.GetPath(Id(step.Item)) is { } a ? Normalize(a) : null;
        return moved.Ok && prior.Equals(after, StringComparison.OrdinalIgnoreCase)
            ? step.Undone($"Moved your {name} folder back to {prior}. The copy in {Target(step.Item)} was kept.")
            : step.UndoFailed($"Windows didn't move your {name} folder back{Because(moved)}");
    }, ct);

    protected override Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(folders.GetPath(Id(step.Item)) is { } p && Normalize(p).Equals(Target(step.Item), StringComparison.OrdinalIgnoreCase));

    /// <summary>What the "every file arrived" check found. Complete only when every entry was looked at and found.</summary>
    /// <param name="Missing">First entry (relative path) missing from the new place or with a different size.</param>
    /// <param name="Unchecked">Why the check couldn't look at everything (too many entries, a folder it couldn't read).</param>
    internal sealed record CopyCheck(string? Missing, string? Unchecked)
    {
        public bool Complete => Missing is null && Unchecked is null;
        public static readonly CopyCheck Ok = new(null, null);
    }

    /// <summary>
    /// Checks every entry of <paramref name="source"/> is in <paramref name="target"/> with the same size. Links and
    /// online-only files are not compared (Windows handles them). Hitting <paramref name="maxEntries"/> or a folder that
    /// can't be listed is "couldn't fully check", never "complete" (KFM-1).
    /// </summary>
    internal static CopyCheck CheckCopy(string source, string target, CancellationToken ct, int maxEntries = MaxVerifiedEntries)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
        var stack = new Stack<string>();
        stack.Push(source);
        var seen = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            try
            {
                foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    if (++seen > maxEntries) return new CopyCheck(null, $"the folder has more than {maxEntries:N0} items");
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || CloudFile.IsPlaceholder(info.Attributes)) continue;
                    var relative = Path.GetRelativePath(source, info.FullName);
                    var copy = Path.Combine(target, relative);
                    if (info is DirectoryInfo) { if (!Directory.Exists(copy)) return new CopyCheck(relative, null); stack.Push(info.FullName); }
                    else if (info is FileInfo f && (!File.Exists(copy) || new FileInfo(copy).Length != f.Length)) return new CopyCheck(relative, null);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                var where = Path.GetRelativePath(source, dir);
                return new CopyCheck(null, where == "." ? "the folder couldn't be read" : $"\"{where}\" couldn't be read");
            }
        }
        return CopyCheck.Ok;
    }

    private static string Because(RedirectOutcome o) => o.Error is { Length: > 0 } e ? $": {e.Trim()}" : $" (code 0x{o.HResult:X8}).";

    private static void TryRemoveEmpty(string folder)
    {
        try { if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
