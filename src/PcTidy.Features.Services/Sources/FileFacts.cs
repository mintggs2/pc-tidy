using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using static PcTidy.Features.Services.Sources.NativeMethods;

namespace PcTidy.Features.Services.Sources;

// The same read-only signature check the Speed section uses (copied, not shared: feature projects don't reference
// each other). Offline only: no revocation lookups, no downloads, online-only cloud files are never opened.

/// <summary>Authenticode result for a file, checked offline.</summary>
public enum SignatureState
{
    /// <summary>Signed and Windows trusts the signature (embedded or through a Windows catalog).</summary>
    Verified,
    /// <summary>No signature at all.</summary>
    Unsigned,
    /// <summary>Signed, but Windows doesn't trust the signature (tampered, untrusted or expired certificate).</summary>
    NotTrusted,
    /// <summary>The file isn't there.</summary>
    FileMissing,
    /// <summary>Not checked (online-only file, unreadable, or no file to check).</summary>
    NotChecked,
}

/// <summary>Who made a file and whether its signature holds. Read-only.</summary>
public sealed record FileFacts(string? Path, bool Exists, string? Company, string? Description, SignatureState Signature, string? Signer)
{
    public static FileFacts None(string? path) => new(path, false, null, null, path is null ? SignatureState.NotChecked : SignatureState.FileMissing, null);

    /// <summary>
    /// Microsoft's own file: a trusted signature from Microsoft (embedded), or a Windows-catalog signature on a file
    /// whose version info says Microsoft. A file that only *claims* to be Microsoft doesn't count.
    /// </summary>
    public bool IsMicrosoft => Signature == SignatureState.Verified
        && (Contains(Signer, "Microsoft") || (Signer is null && Contains(Company, "Microsoft")));

    /// <summary>Says "Microsoft" in its version info, but the signature doesn't confirm it.</summary>
    public bool ClaimsMicrosoft => !IsMicrosoft && Contains(Company, "Microsoft");

    /// <summary>Publisher to show: the verified signer, else the company in the file's version info.</summary>
    public string? Publisher => Signature == SignatureState.Verified && Signer is not null ? Signer : Company;

    public bool IsUnsigned => Signature is SignatureState.Unsigned or SignatureState.NotTrusted;

    /// <summary>Plain-English signature line for the details pane.</summary>
    public string SignatureText => Signature switch
    {
        SignatureState.Verified when Signer is not null => $"Signed by {Signer} (verified)",
        SignatureState.Verified => "Signed through Windows (verified)",
        SignatureState.Unsigned => "Not signed",
        SignatureState.NotTrusted => "Signature isn't trusted by Windows",
        SignatureState.FileMissing => "File is missing",
        _ => "Not checked",
    };

    private static bool Contains(string? s, string what) => s?.Contains(what, StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>Company, description and signature of files. Real: version resources + WinVerifyTrust. Tests: a fake.</summary>
public interface IFileFactsProvider
{
    FileFacts Get(string? path);
}

/// <summary>Reads the version resource and checks the Authenticode signature (embedded first, then Windows catalogs). Cached per path.</summary>
public sealed class WindowsFileFacts : IFileFactsProvider
{
    private readonly ConcurrentDictionary<string, FileFacts> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FileFacts Get(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return FileFacts.None(null);
        return _cache.GetOrAdd(path, Read);
    }

    private static FileFacts Read(string path)
    {
        FileAttributes attributes;
        try
        {
            if (!File.Exists(path)) return FileFacts.None(path);
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FileFacts(path, false, null, null, SignatureState.NotChecked, null);
        }
        // An online-only placeholder would be downloaded by reading it.
        if (CloudFile.IsPlaceholder(attributes)) return new FileFacts(path, true, null, null, SignatureState.NotChecked, null);

        string? company = null, description = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            company = Clean(info.CompanyName);
            description = Clean(info.FileDescription) ?? Clean(info.ProductName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FileNotFoundException)
        {
            // No version info: shown as "unknown publisher".
        }

        var (signature, signer) = CheckSignature(path);
        return new FileFacts(path, true, company, description, signature, signer);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static (SignatureState, string?) CheckSignature(string path)
    {
        try
        {
            var embedded = VerifyEmbedded(path);
            if (embedded == 0) return (SignatureState.Verified, SignerName(path));
            if (embedded is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN)
                return VerifyCatalog(path) ? (SignatureState.Verified, null) : (SignatureState.Unsigned, null);
            return (SignatureState.NotTrusted, SignerName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return (SignatureState.NotChecked, null);
        }
    }

    private static string? SignerName(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // reading the embedded signer certificate only; no key material is loaded
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var name = cert.GetNameInfo(X509NameType.SimpleName, false);
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static int VerifyEmbedded(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            return Verify(WTD_CHOICE_FILE, filePtr);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }

    /// <summary>Windows' own files are usually signed through a catalog (.cat) rather than inside the file.</summary>
    private static bool VerifyCatalog(string path)
    {
        foreach (var algorithm in new[] { "SHA256", "SHA1" })
        {
            if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0)) continue;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var handle = stream.SafeFileHandle.DangerousGetHandle();
                uint size = 0;
                CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, null, 0);
                if (size == 0 || size > 1024) continue;
                var hash = new byte[size];
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, hash, 0)) continue;

                var catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, IntPtr.Zero);
                if (catalog == IntPtr.Zero) continue;
                try
                {
                    var info = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
                    if (!CryptCATCatalogInfoFromContext(catalog, ref info, 0)) continue;
                    if (VerifyCatalogMember(path, info.wszCatalogFile, hash, admin) == 0) return true;
                }
                finally { CryptCATAdminReleaseCatalogContext(admin, catalog, 0); }
            }
            finally { CryptCATAdminReleaseContext(admin, 0); }
        }
        return false;
    }

    private static int VerifyCatalogMember(string path, string catalogFile, byte[] hash, IntPtr admin)
    {
        var hashPtr = Marshal.AllocHGlobal(hash.Length);
        var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
        try
        {
            Marshal.Copy(hash, 0, hashPtr, hash.Length);
            var info = new WINTRUST_CATALOG_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
                pcwszCatalogFilePath = catalogFile,
                pcwszMemberTag = Convert.ToHexString(hash),
                pcwszMemberFilePath = path,
                pbCalculatedFileHash = hashPtr,
                cbCalculatedFileHash = (uint)hash.Length,
                hCatAdmin = admin,
            };
            Marshal.StructureToPtr(info, infoPtr, false);
            try { return Verify(WTD_CHOICE_CATALOG, infoPtr); }
            finally { Marshal.DestroyStructure<WINTRUST_CATALOG_INFO>(infoPtr); }
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
            Marshal.FreeHGlobal(hashPtr);
        }
    }

    private static int Verify(uint choice, IntPtr info)
    {
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = choice,
            pInfo = info,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
        };
        var result = WinVerifyTrust(new IntPtr(-1) /* INVALID_HANDLE_VALUE: no UI */, WintrustActionGenericVerifyV2, ref data);
        data.dwStateAction = WTD_STATEACTION_CLOSE;
        WinVerifyTrust(new IntPtr(-1), WintrustActionGenericVerifyV2, ref data);
        return result;
    }
}

/// <summary>File facts from a table (tests, the sandbox and screenshots). Paths not listed count as missing.</summary>
public sealed class FixedFileFacts : IFileFactsProvider
{
    private readonly Dictionary<string, FileFacts> _known;

    public FixedFileFacts(IEnumerable<FileFacts>? known = null)
        => _known = (known ?? Array.Empty<FileFacts>()).Where(f => f.Path is not null)
            .GroupBy(f => f.Path!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

    public FileFacts Get(string? path)
        => string.IsNullOrWhiteSpace(path) ? FileFacts.None(null) : _known.TryGetValue(path, out var f) ? f : FileFacts.None(path);

    public static FileFacts Microsoft(string path, string? description = null)
        => new(path, true, "Microsoft Corporation", description, SignatureState.Verified, "Microsoft Corporation");

    public static FileFacts Signed(string path, string company, string? description = null)
        => new(path, true, company, description, SignatureState.Verified, company);

    public static FileFacts Unsigned(string path, string? company = null)
        => new(path, true, company, null, SignatureState.Unsigned, null);
}
