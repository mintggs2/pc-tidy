using PcTidy.Catalog.Apps;

namespace PcTidy.Features.Apps.Inventory;

/// <summary>What PC Tidy needs to know about one Store package (read through PackageManager, or faked in tests).</summary>
public sealed record StorePackage(
    string Name,
    string FullName,
    string FamilyName,
    string? DisplayName,
    string? Publisher,
    string? Version,
    string? InstalledPath,
    DateTime? InstalledDate,
    bool IsFramework,
    bool IsResource,
    bool IsSystemSigned,
    bool IsDevelopmentMode);

/// <summary>Lists the current user's Store packages.</summary>
public interface IStorePackageSource
{
    IReadOnlyList<StorePackage> List(CancellationToken ct);
}

/// <summary>
/// Windows' own PackageManager for the current user (no admin): FindPackagesForUser(""). Read-only.
/// A package whose details can't be read is skipped; a PackageManager failure means "no Store apps found", never a crash.
/// </summary>
public sealed class WindowsStorePackageSource : IStorePackageSource
{
    public IReadOnlyList<StorePackage> List(CancellationToken ct)
    {
        var result = new List<StorePackage>();
        IEnumerable<Windows.ApplicationModel.Package> packages;
        try { packages = new Windows.Management.Deployment.PackageManager().FindPackagesForUser(""); }
        catch (Exception) { return result; } // e.g. the AppX service is off: no Store apps to show

        try
        {
            foreach (var p in packages) Add(p);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* the list broke off part-way: keep what was read */ }
        return result;

        void Add(Windows.ApplicationModel.Package p)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var id = p.Id;
                var v = id.Version;
                result.Add(new StorePackage(
                    Name: id.Name,
                    FullName: id.FullName,
                    FamilyName: id.FamilyName,
                    DisplayName: Try(() => p.DisplayName),
                    Publisher: Try(() => p.PublisherDisplayName),
                    Version: $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
                    InstalledPath: Try(() => p.InstalledPath),
                    InstalledDate: TryDate(() => p.InstalledDate),
                    IsFramework: p.IsFramework,
                    IsResource: p.IsResourcePackage,
                    IsSystemSigned: p.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System,
                    IsDevelopmentMode: p.IsDevelopmentMode));
            }
            catch (Exception) { /* this package's details are unreadable: skip just this one */ }
        }
    }

    private static string? Try(Func<string> read)
    {
        try { var s = read(); return string.IsNullOrWhiteSpace(s) ? null : s; }
        catch (Exception) { return null; }
    }

    private static DateTime? TryDate(Func<DateTimeOffset> read)
    {
        try { var d = read(); return d.Year > 1995 ? d.LocalDateTime : null; }
        catch (Exception) { return null; }
    }
}

/// <summary>Turns Store packages into apps: frameworks, resource packs and Windows' own (system-signed) packages are hidden.</summary>
public static class StoreAppReader
{
    /// <summary>True for packages that aren't apps a person installed or could remove.</summary>
    public static bool IsHidden(StorePackage p) => p.IsFramework || p.IsResource || p.IsSystemSigned;

    public static IReadOnlyList<InstalledApp> Read(IStorePackageSource source, CancellationToken ct)
    {
        var apps = new List<InstalledApp>();
        foreach (var p in source.List(ct))
        {
            if (IsHidden(p)) continue;
            // Unresolved "ms-resource:" names are useless to a person; the package name is clearer.
            var name = p.DisplayName is { } d && !d.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? d : p.Name;
            apps.Add(new InstalledApp
            {
                Id = "store:" + p.FamilyName,
                Name = name,
                Source = AppSource.Store,
                Scope = AppScope.User,
                Publisher = p.Publisher is { } pub && !pub.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? pub : null,
                Version = p.Version,
                InstallLocation = p.InstalledPath,
                InstallDate = p.InstalledDate,
                PackageFullName = p.FullName,
                PackageFamilyName = p.FamilyName,
                PackageName = p.Name,
                Preinstalled = PreinstalledApps.Find(p.Name),
            });
        }
        return apps;
    }
}
