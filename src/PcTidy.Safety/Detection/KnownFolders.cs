using System.Runtime.InteropServices;
using PcTidy.Safety.Native;
using PcTidy.Safety.Paths;

namespace PcTidy.Safety.Detection;

/// <summary>Windows known folders, read with SHGetKnownFolderPath (follows the user's real redirection, e.g. into OneDrive).</summary>
public static class KnownFolders
{
    public static readonly Guid Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    public static readonly Guid Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    public static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public static readonly Guid Pictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    public static readonly Guid Videos = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
    public static readonly Guid Music = new("4BD8D571-6D19-48D3-BE97-422220080E43");
    public static readonly Guid Profile = new("5E6C858F-0E22-4760-9AFE-EA3317B67173");
    public static readonly Guid LocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    public static readonly Guid RoamingAppData = new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D");
    public static readonly Guid ProgramData = new("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97");
    public static readonly Guid Windows = new("F38BF404-1D43-42F2-9305-67DE0B28FC23");
    public static readonly Guid ProgramFiles = new("905E63B6-C1BF-494E-B29C-65B732D3D21A");
    public static readonly Guid ProgramFilesX86 = new("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E");

    /// <summary>The six user folders PC Tidy may redirect (KnownFolderRedirect), by known-folder id.</summary>
    public static readonly IReadOnlyList<Guid> UserLibraries = new[] { Desktop, Documents, Downloads, Pictures, Videos, Music };

    /// <summary>Normalised path of a known folder, or the normalised <paramref name="fallback"/> when the shell can't say.</summary>
    public static string Get(Guid id, Func<string> fallback)
    {
        string? path = null;
        try
        {
            if (NativeMethods.SHGetKnownFolderPath(id, NativeMethods.KfFlagDontVerify, IntPtr.Zero, out var ptr) == 0)
            {
                path = Marshal.PtrToStringUni(ptr);
                Marshal.FreeCoTaskMem(ptr);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException) { }
        if (string.IsNullOrWhiteSpace(path))
        {
            try { path = fallback(); } catch { path = null; }
        }
        return SafePath.TryNormalize(path) ?? path ?? "";
    }
}
