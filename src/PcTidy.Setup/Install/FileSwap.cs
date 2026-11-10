namespace PcTidy.Setup.Install;

/// <summary>
/// Replaces a set of files all-or-nothing. Every new file is first written next to its final name
/// (<see cref="NewSuffix"/>); only when all of them are there does <see cref="SwapAll"/> move each current file aside
/// (<see cref="OldSuffix"/>) and the new one into place. <see cref="RollBack"/> puts every replaced file back and
/// removes files that are new in this version; <see cref="Commit"/> deletes the set-aside copies once the update is
/// recorded. A half-updated folder (new and old assemblies mixed) therefore never stays behind.
/// </summary>
internal sealed class FileSwap
{
    public const string NewSuffix = ".pctidy-new";
    public const string OldSuffix = ".pctidy-old";

    private readonly List<string> _staged = new();
    // Recorded BEFORE each move, so a failure between "current file set aside" and "new file in place" is still undone.
    private readonly List<(string Target, string? Backup)> _swapped = new();

    /// <summary>Where the new copy of <paramref name="target"/> is to be written; it is swapped in by <see cref="SwapAll"/>.</summary>
    public string Stage(string target)
    {
        _staged.Add(target);
        return target + NewSuffix;
    }

    public IReadOnlyList<string> Staged => _staged;

    public bool AnySwapped => _swapped.Count > 0;

    public void SwapAll(CancellationToken ct)
    {
        foreach (var target in _staged)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(target))
            {
                var backup = target + OldSuffix;
                _swapped.Add((target, backup));
                File.Move(target, backup, overwrite: true);
            }
            else
            {
                _swapped.Add((target, null));
            }
            File.Move(target + NewSuffix, target, overwrite: false);
        }
    }

    /// <summary>Puts every file back as it was. Returns false when at least one file couldn't be restored.</summary>
    public bool RollBack()
    {
        bool ok = true;
        for (int i = _swapped.Count - 1; i >= 0; i--)
        {
            var (target, backup) = _swapped[i];
            try
            {
                if (backup is null) { if (File.Exists(target)) File.Delete(target); }
                else if (File.Exists(backup)) File.Move(backup, target, overwrite: true);
                else if (!File.Exists(target)) ok = false; // nothing to put back: the old file is lost
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false; // the set-aside copy stays as *.pctidy-old so Setup can repair it next time
            }
        }
        _swapped.Clear();
        DeleteStaged();
        return ok;
    }

    /// <summary>
    /// The update is recorded: the set-aside copies go. Returns the ones still in use (they can't be deleted yet), for
    /// Windows to delete at the next restart.
    /// </summary>
    public IReadOnlyList<string> Commit()
    {
        var inUse = new List<string>();
        foreach (var (_, backup) in _swapped)
        {
            if (backup is null) continue;
            Installer.TryDelete(backup);
            if (File.Exists(backup)) inUse.Add(backup);
        }
        _swapped.Clear();
        DeleteStaged();
        return inUse;
    }

    private void DeleteStaged()
    {
        foreach (var target in _staged) Installer.TryDelete(target + NewSuffix);
    }
}
