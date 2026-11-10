using PcTidy.Core;

namespace PcTidy.App.Updates;

/// <summary>
/// The only code that writes update downloads: &lt;DataRoot&gt;\updates\PC Tidy Setup &lt;version&gt;.exe (PC Tidy's own
/// data). Downloads land in a ".partial" file and only get their final name once their checksum matched.
/// </summary>
public sealed class UpdateStore
{
    public const string InstallerName = "PC Tidy Setup";

    public UpdateStore(string dataRoot) => Folder = Path.Combine(dataRoot, "updates");

    public string Folder { get; }

    public string FinalPath(Version v) => Path.Combine(Folder, $"{InstallerName} {v.ToString(3)}.exe");
    public string PartialPath(Version v) => FinalPath(v) + ".partial";

    public Stream CreatePartial(Version v)
    {
        Directory.CreateDirectory(Folder); // boundary-ok: own data in DataRoot
        return new FileStream(PartialPath(v), FileMode.Create, FileAccess.Write, FileShare.None); // boundary-ok: own data in DataRoot
    }

    public void Complete(Version v) => File.Move(PartialPath(v), FinalPath(v), overwrite: true); // boundary-ok: own data in DataRoot

    public void Discard(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } // boundary-ok: own data in DataRoot
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
