using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace PcTidy.App.Updates;

public enum SignatureState { NotSigned, Valid, Invalid }

/// <summary>
/// An Authenticode result: the state and, when valid, who signed it: the signing certificate's subject, its
/// thumbprint, its issuer and the SHA-256 of its public key (SubjectPublicKeyInfo), so a publisher is never matched
/// by the subject text alone (3.1.1 review L10).
/// </summary>
public sealed record SignatureInfo(SignatureState State, string? Signer = null, string? Thumbprint = null)
{
    public static readonly SignatureInfo NotSigned = new(SignatureState.NotSigned);
    public static readonly SignatureInfo Invalid = new(SignatureState.Invalid);

    /// <summary>The signing certificate's issuer (the CA that issued it).</summary>
    public string? Issuer { get; init; }
    /// <summary>SHA-256 (hex) of the signing certificate's SubjectPublicKeyInfo.</summary>
    public string? PublicKeySha256 { get; init; }

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
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
            return new SignatureInfo(SignatureState.Valid, cert.Subject, cert.Thumbprint) { Issuer = cert.Issuer, PublicKeySha256 = key };
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
/// Who may install an update (UPD-1, decided 2026-11-11 by the lead and the Hub; recorded here on purpose).
/// <list type="bullet">
/// <item><b>The running PC Tidy is signed:</b> the update must be validly signed by the SAME publisher (certificate
/// subject plus the same certificate, public key or issuer: <see cref="SamePublisher"/>). A checksum alone proves
/// nothing here, because whoever controls a release chooses its checksum too.</item>
/// <item><b>The running PC Tidy is unsigned</b> (the beta: no code signing, by the owner's choice on 2026-11-11): it
/// can't check who made an update, so it accepts one only with a <see cref="VerifiedRelease"/>: the file came over
/// HTTPS from the mintggs2/pc-tidy Releases API, the release is not a draft or a pre-release, its version is higher,
/// the file name is exactly the expected one, and its SHA-256 matches that release's SHA256SUMS.txt. A signed update
/// offered to an unsigned copy is fine too (with the same proof).</item>
/// <item><b>An invalid signature is always refused</b>, whatever the running copy.</item>
/// </list>
/// Once PC Tidy is published signed, the first rule applies by itself: nothing to switch. The file stays locked against
/// writes from the final hash check until Setup has started (<see cref="VerifiedInstaller"/>).
/// </summary>
public static class UpdateTrust
{
    public const string UnsignedSelf = "This copy of PC Tidy isn't digitally signed (a test version), so it installs an update only when it comes straight from PC Tidy's GitHub releases and matches their checksums.";
    public const string InvalidSignature = "The installer's digital signature isn't valid, so it wasn't started.";
    public const string DamagedSelf = "This copy of PC Tidy has a digital signature that isn't valid, so it won't install updates by itself. Get the new version from the release page.";

    /// <summary>Null when <paramref name="installer"/> may be started, else the plain-English reason it may not.</summary>
    /// <param name="release">Proof that the file passed every release check (null: it didn't, or it isn't known).</param>
    public static string? Problem(SignatureInfo own, SignatureInfo installer, VerifiedRelease? release = null)
    {
        if (installer.State == SignatureState.Invalid || (installer.State == SignatureState.Valid && installer.Signer is null)) return InvalidSignature;
        if (own.State == SignatureState.Valid && own.Signer is not null)
        {
            if (installer.State == SignatureState.NotSigned) return "The installer isn't digitally signed, so PC Tidy can't tell who made it. It wasn't started.";
            if (!SamePublisher(own, installer))
                return $"The installer is signed by \"{installer.SignerName}\", not by PC Tidy's publisher (\"{own.SignerName}\"), so it wasn't started.";
            return null;
        }
        if (own.State != SignatureState.NotSigned) return DamagedSelf;
        // Unsigned PC Tidy (the beta): only with the release proof, whether the update is unsigned or signed.
        return release is null ? UnsignedSelf : null;
    }

    /// <summary>
    /// The same publisher (review L10): the same certificate subject AND the same certificate (thumbprint), or the same
    /// public key, or a renewed certificate from the same issuer. A certificate with an identical subject from another
    /// CA, with another key, is refused. Nothing to compare (unknown thumbprint, key and issuer) counts as different.
    /// </summary>
    public static bool SamePublisher(SignatureInfo own, SignatureInfo other)
    {
        if (own.Signer is null || !string.Equals(own.Signer, other.Signer, StringComparison.Ordinal)) return false;
        static bool Same(string? a, string? b) => a is { Length: > 0 } && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        return Same(own.Thumbprint, other.Thumbprint) || Same(own.PublicKeySha256, other.PublicKeySha256)
               || (own.Issuer is { Length: > 0 } && string.Equals(own.Issuer, other.Issuer, StringComparison.Ordinal));
    }

    /// <summary>One sentence for the install confirm: why this file is trusted.</summary>
    public static string Describe(SignatureInfo own, SignatureInfo installer)
    {
        const string fromGitHub = "It came over HTTPS from PC Tidy's GitHub releases (mintggs2/pc-tidy), it is a finished release newer than this one, its file name is exactly right and its SHA-256 matches the release's SHA256SUMS.txt.";
        if (own.State == SignatureState.Valid) return Authenticode.Describe(installer);
        return installer.State == SignatureState.Valid
            ? $"It is digitally signed by {installer.SignerName}. {fromGitHub}"
            : $"Neither this PC Tidy nor the update is digitally signed (the beta isn't). {fromGitHub}";
    }
}
