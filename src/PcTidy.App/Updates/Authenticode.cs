using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace PcTidy.App.Updates;

public enum SignatureState { NotSigned, Valid, Invalid }

/// <summary>An Authenticode result: the state and, when valid, who signed it (the signing certificate's subject).</summary>
public sealed record SignatureInfo(SignatureState State, string? Signer = null, string? Thumbprint = null)
{
    public static readonly SignatureInfo NotSigned = new(SignatureState.NotSigned);
    public static readonly SignatureInfo Invalid = new(SignatureState.Invalid);

    /// <summary>"CN=…, O=…" → the CN (or the whole subject when there is no CN), for dialogs.</summary>
    public string SignerName => Signer is null ? "" :
        Signer.Split(',').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) is { } cn ? cn[3..].Trim('"') : Signer;
}

/// <summary>
/// Authenticode check with WinVerifyTrust (read-only, no network revocation check, no UI), on an open handle so the
/// file checked is the file that is then started (see <see cref="VerifiedInstaller"/>). A library that can't be loaded
/// counts as Invalid, never as "not signed".
/// </summary>
public static class Authenticode
{
    /// <summary>Checks the file behind <paramref name="handle"/> (<paramref name="path"/> is its name, for the signer lookup).</summary>
    public static SignatureInfo Check(SafeFileHandle handle, string path)
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
                dwUIChoice = 2,              // WTD_UI_NONE
                fdwRevocationChecks = 0,     // WTD_REVOKE_NONE: no network
                dwUnionChoice = 1,           // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 0,
                dwProvFlags = 0x00001000,    // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result switch
            {
                0 => Signed(path),
                unchecked((int)0x800B0100) => SignatureInfo.NotSigned, // TRUST_E_NOSIGNATURE
                unchecked((int)0x800B0003) or unchecked((int)0x800B0001) => SignatureInfo.NotSigned, // subject form / provider unknown
                _ => SignatureInfo.Invalid,
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return SignatureInfo.Invalid; }
        finally
        {
            if (added) handle.DangerousRelease();
            Marshal.FreeHGlobal(filePtr);
        }
    }

    /// <summary>Opens <paramref name="path"/> (denying writes) and checks it.</summary>
    public static SignatureInfo Check(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Check(fs.SafeFileHandle, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return SignatureInfo.Invalid; }
    }

    /// <summary>PC Tidy's own program file: who signed the copy that is running now (NotSigned for this beta).</summary>
    public static SignatureInfo Own() => Environment.ProcessPath is { } self ? Check(self) : SignatureInfo.Invalid;

    /// <summary>The signing certificate of a file WinVerifyTrust just accepted (the caller holds it open, denying writes).</summary>
    private static SignatureInfo Signed(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // the signer of an Authenticode signature; no replacement API in .NET 8
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return new SignatureInfo(SignatureState.Valid, cert.Subject, cert.Thumbprint);
        }
        catch (System.Security.Cryptography.CryptographicException) { return SignatureInfo.Invalid; }
    }

    public static string Describe(SignatureInfo s) => s.State switch
    {
        SignatureState.Valid => $"Digitally signed by {s.SignerName}, the same publisher as this PC Tidy, and the signature is valid.",
        SignatureState.NotSigned => "Not digitally signed.",
        _ => "It has a digital signature that isn't valid.",
    };

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

/// <summary>
/// Who may install an update (UPD-1). The feed's checksum only proves the download wasn't damaged on the way: whoever
/// controls the feed chooses the checksum too. So an installer may only be started when it is validly signed by the
/// SAME publisher (certificate subject) as the PC Tidy that is running. An unsigned PC Tidy (this beta) can't tell who
/// made an update, so it never installs one by itself: it only offers to open the release page.
/// </summary>
public static class UpdateTrust
{
    public const string UnsignedSelf = "This copy of PC Tidy isn't digitally signed (a test version), so it can't check who made an update, and it won't download or install one by itself.";

    /// <summary>Null when <paramref name="installer"/> may be started, else the plain-English reason it may not.</summary>
    public static string? Problem(SignatureInfo own, SignatureInfo installer)
    {
        if (own.State != SignatureState.Valid || own.Signer is null) return UnsignedSelf;
        if (installer.State == SignatureState.NotSigned) return "The installer isn't digitally signed, so PC Tidy can't tell who made it. It wasn't started.";
        if (installer.State != SignatureState.Valid || installer.Signer is null) return "The installer's digital signature isn't valid, so it wasn't started.";
        if (!string.Equals(own.Signer, installer.Signer, StringComparison.Ordinal))
            return $"The installer is signed by \"{installer.SignerName}\", not by PC Tidy's publisher (\"{own.SignerName}\"), so it wasn't started.";
        return null;
    }
}
