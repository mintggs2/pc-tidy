using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace PcTidy.Setup.Windows;

/// <summary>Creates and reads .lnk shortcuts through the Windows shell (IShellLinkW + IPersistFile).</summary>
public static class ShellLink
{
    public sealed record Info(string Target, string Arguments, string WorkingDirectory, string IconPath, int IconIndex, string Description);

    public static void Create(string lnkPath, string target, string description, string? iconPath = null, string arguments = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
            link.SetDescription(description);
            link.SetIconLocation(iconPath ?? target, 0);
            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    public static Info Read(string lnkPath)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            ((IPersistFile)link).Load(lnkPath, 0 /* STGM_READ */);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
            var target = sb.ToString();
            sb.Clear(); link.GetArguments(sb, sb.Capacity);
            var args = sb.ToString();
            sb.Clear(); link.GetWorkingDirectory(sb, sb.Capacity);
            var dir = sb.ToString();
            sb.Clear(); link.GetIconLocation(sb, sb.Capacity, out var index);
            var icon = sb.ToString();
            sb.Clear(); link.GetDescription(sb, sb.Capacity);
            return new Info(target, args, dir, icon, index, sb.ToString());
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    /// <summary>The shortcut's target, or null if it isn't a readable shortcut.</summary>
    public static string? TryReadTarget(string lnkPath)
    {
        try { return File.Exists(lnkPath) ? Read(lnkPath).Target : null; }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException) { return null; }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
