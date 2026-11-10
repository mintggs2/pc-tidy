using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PcTidy.Features.Search.Backends;

/// <summary>
/// Decides whether Everything's SDK library may be loaded into PC Tidy at all (SRC-1). Loading a DLL runs its code inside
/// PC Tidy, the process the admin helper trusts, so the library must be (all read-only checks):
/// <list type="number">
/// <item>in a folder only admins can change: the DLL, every file beside it and its folder may be changed by nobody but
/// SYSTEM, Administrators and TrustedInstaller, and no parent folder may be renamed, deleted or re-permissioned by
/// anyone else (the rules of PC Tidy's own install-folder check). A portable Everything in Downloads or on a USB stick
/// doesn't qualify;</item>
/// <item>validly Authenticode-signed by voidtools;</item>
/// <item>loaded by full path with <c>LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32</c>, while a handle
/// that denies writes, renames and deletes is held, so the file checked is the file loaded.</item>
/// </list>
/// Otherwise PC Tidy uses its own index. Which window names Everything's folder doesn't matter: any process can create a
/// window with Everything's class name, so only the folder and the signature count.
/// </summary>
internal static class EverythingSdkTrust
{
    public const string Publisher = "voidtools";
    private const int MaxEntries = 2_000;

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier CreatorGroup = new(WellKnownSidType.CreatorGroupSid, null);
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

    private const int WriteData = 0x2, AppendData = 0x4, WriteEa = 0x10, DeleteChild = 0x40, WriteAttributes = 0x100;
    private const int Delete = 0x10000, WriteDac = 0x40000, WriteOwner = 0x80000, GenericAll = 0x10000000, GenericWrite = 0x40000000;
    private const int ContentsWriteRights = WriteData | AppendData | WriteEa | DeleteChild | WriteAttributes | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite;
    private const int ParentWriteRights = DeleteChild | Delete | WriteDac | WriteOwner | GenericAll;
    private const int RootWriteRights = DeleteChild | WriteDac | WriteOwner | GenericAll;

    private const uint LoadLibrarySearchDllLoadDir = 0x00000100, LoadLibrarySearchSystem32 = 0x00000800;

    /// <summary>Loads the SDK if it passes every check; otherwise <see cref="IntPtr.Zero"/> and the reason.</summary>
    public static IntPtr LoadTrusted(string dllPath, out string? problem)
    {
        problem = null;
        try
        {
            if (!Path.IsPathFullyQualified(dllPath) || dllPath.StartsWith(@"\\", StringComparison.Ordinal)) { problem = "Not a local path."; return IntPtr.Zero; }
            var full = Path.GetFullPath(dllPath);
            if (!File.Exists(full)) { problem = "No SDK file."; return IntPtr.Zero; }
            // Held until the library is mapped: nobody can change, rename or delete it in between.
            using var held = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) { problem = "The SDK file is a link."; return IntPtr.Zero; }
            if (AdminOnlyProblem(full) is { } acl) { problem = acl; return IntPtr.Zero; }
            if (SignatureProblem(held.SafeFileHandle, full) is { } sig) { problem = sig; return IntPtr.Zero; }
            var lib = LoadLibraryExW(full, IntPtr.Zero, LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
            if (lib == IntPtr.Zero) problem = "Windows couldn't load the SDK.";
            return lib;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or DllNotFoundException or EntryPointNotFoundException or System.Security.Cryptography.CryptographicException)
        {
            problem = "The SDK couldn't be checked: " + ex.Message;
            return IntPtr.Zero;
        }
    }

    // ---------------- folder permissions ----------------

    private enum Level { Contents, Parent, Root }

    /// <summary>Null when only admins can change the DLL, the files beside it and its folder, and nobody else can swap a parent.</summary>
    internal static string? AdminOnlyProblem(string dll)
    {
        var folder = Path.GetDirectoryName(dll)!;
        var nodes = new List<(string Path, Level Level, bool IsDirectory)> { (folder, Level.Contents, true) };
        var count = 0;
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            if (++count > MaxEntries) return "Too many files beside the SDK to check.";
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return $"{entry.FullName} is a link.";
            if (entry is FileInfo) nodes.Add((entry.FullName, Level.Contents, false));
        }
        var root = Path.GetPathRoot(folder);
        for (var dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var isRoot = root is not null && string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
            nodes.Add((dir, isRoot ? Level.Root : Level.Parent, true));
            if (isRoot) break;
        }
        foreach (var (path, level, isDirectory) in nodes)
            if (Judge(path, level, isDirectory) is { } why) return why;
        return null;
    }

    private static string? Judge(string path, Level level, bool isDirectory)
    {
        RawSecurityDescriptor sd;
        try
        {
            const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Access;
            FileSystemSecurity security = isDirectory ? new DirectoryInfo(path).GetAccessControl(sections) : new FileInfo(path).GetAccessControl(sections);
            sd = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or PlatformNotSupportedException)
        {
            return $"Couldn't read who may change {path}.";
        }
        static bool Trusted(SecurityIdentifier sid) => sid == LocalSystem || sid == Administrators || sid == TrustedInstaller;
        if (sd.Owner is null || !Trusted(sd.Owner)) return $"{path} is owned by an account without admin rights.";
        if ((sd.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || sd.DiscretionaryAcl is null) return $"Everyone may change {path}.";
        var dangerous = level switch { Level.Contents => ContentsWriteRights, Level.Parent => ParentWriteRights, _ => RootWriteRights };
        foreach (var ace in sd.DiscretionaryAcl)
        {
            if (ace is not KnownAce known) continue;
            if (ace.AceType is not (AceType.AccessAllowed or AceType.AccessAllowedCallback or AceType.AccessAllowedObject or AceType.AccessAllowedCallbackObject)) continue;
            if ((ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
            var sid = known.SecurityIdentifier;
            if (Trusted(sid) || sid == CreatorOwner || sid == CreatorGroup || sid == OwnerRights) continue;
            if ((known.AccessMask & dangerous) != 0) return $"An account without admin rights may change {path}.";
        }
        return null;
    }

    // ---------------- signature ----------------

    /// <summary>Null when the file behind <paramref name="handle"/> has a valid Authenticode signature by voidtools.</summary>
    private static string? SignatureProblem(SafeFileHandle handle, string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path, hFile = handle.DangerousGetHandle() };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,           // WTD_UI_NONE
                fdwRevocationChecks = 0,  // no network
                dwUnionChoice = 1,        // WTD_CHOICE_FILE
                pFile = filePtr,
                dwProvFlags = 0x00001000, // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            if (WinVerifyTrust(IntPtr.Zero, ref action, ref data) != 0) return "The SDK isn't validly signed.";
        }
        finally
        {
            if (added) handle.DangerousRelease();
            Marshal.FreeHGlobal(filePtr);
        }
#pragma warning disable SYSLIB0057 // the signer of an Authenticode signature; no replacement API in .NET 8
        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        return IsPublisher(cert.Subject) ? null : "The SDK isn't signed by voidtools.";
    }

    /// <summary>The signer's organisation or common name is voidtools.</summary>
    internal static bool IsPublisher(string subject)
        => subject.Split(',').Select(p => p.Trim())
            .Where(p => p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) || p.StartsWith("O=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[(p.IndexOf('=') + 1)..].Trim('"').Trim())
            .Any(v => v.Equals(Publisher, StringComparison.OrdinalIgnoreCase) || v.StartsWith(Publisher + " ", StringComparison.OrdinalIgnoreCase));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
