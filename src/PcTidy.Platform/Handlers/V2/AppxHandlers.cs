using System.Text.RegularExpressions;
using PcTidy.Core.Model;
using Windows.Management.Deployment;

namespace PcTidy.Platform;

/// <summary>A Store package installed for the current user.</summary>
public sealed record AppxPackageFacts(string FullName, string FamilyName, string? InstalledLocation, bool IsFramework, bool IsSystem);

/// <summary>Store packages of the current user. Real: Windows.Management.Deployment.PackageManager. Tests: a fake.</summary>
public interface IAppxPackageControl
{
    bool TouchesRealSystem { get; }
    AppxPackageFacts? Find(string packageFullName);
    IReadOnlyList<AppxPackageFacts> FindFamily(string packageFamilyName);
    /// <summary>True if the package's files (AppxManifest.xml) are still on the PC, so it can be registered again.</summary>
    bool IsStaged(string installedLocation);
    Task RemoveAsync(string packageFullName, CancellationToken ct);
    Task RegisterAsync(string packageFullName, CancellationToken ct);
    /// <summary>Runs System32\WSReset.exe and returns its exit code.</summary>
    Task<int> ResetStoreCacheAsync(string logPath, CancellationToken ct);
}

public sealed class WinRtAppxPackageControl : IAppxPackageControl
{
    private readonly PackageManager _pm = new();

    public bool TouchesRealSystem => true;

    public AppxPackageFacts? Find(string packageFullName)
    {
        // "" = the current user.
        var p = _pm.FindPackageForUser(string.Empty, packageFullName);
        return p is null ? null : Facts(p);
    }

    public IReadOnlyList<AppxPackageFacts> FindFamily(string packageFamilyName)
        => _pm.FindPackagesForUser(string.Empty, packageFamilyName).Select(Facts).ToList();

    public bool IsStaged(string installedLocation) => File.Exists(Path.Combine(installedLocation, "AppxManifest.xml"));

    public async Task RemoveAsync(string packageFullName, CancellationToken ct)
        => Check(await _pm.RemovePackageAsync(packageFullName).AsTask(ct).ConfigureAwait(false));

    public async Task RegisterAsync(string packageFullName, CancellationToken ct)
        => Check(await _pm.RegisterPackageByFullNameAsync(packageFullName, null, DeploymentOptions.None).AsTask(ct).ConfigureAwait(false));

    public async Task<int> ResetStoreCacheAsync(string logPath, CancellationToken ct)
        => (await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "WSReset.exe"), Array.Empty<string>(), TimeSpan.FromMinutes(5), logPath, ct).ConfigureAwait(false)).ExitCode;

    private static void Check(DeploymentResult result)
    {
        if (result.ExtendedErrorCode is { } e && e.HResult != 0) throw new InvalidOperationException(result.ErrorText is { Length: > 0 } t ? t : e.Message);
    }

    private static AppxPackageFacts Facts(Windows.ApplicationModel.Package p)
    {
        string? location = null;
        try { location = p.InstalledLocation?.Path; } catch (Exception) { /* not available (e.g. being serviced) */ }
        var system = false;
        try { system = p.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System; } catch (Exception) { }
        return new AppxPackageFacts(p.Id.FullName, p.Id.FamilyName, location, p.IsFramework, system);
    }
}

/// <summary>Store package names and the second wall against removing the ones Windows depends on (same lists as the SafetyGuard).</summary>
internal static partial class AppxNames
{
    [GeneratedRegex(@"^[A-Za-z0-9.\-]{2,64}_[0-9.]{1,23}_[A-Za-z0-9]{0,10}_[A-Za-z0-9.\-~]{0,64}_[a-z0-9]{13}$", RegexOptions.CultureInvariant)]
    public static partial Regex FullName();

    [GeneratedRegex(@"^[A-Za-z0-9.\-]{2,64}_[a-z0-9]{13}$", RegexOptions.CultureInvariant)]
    public static partial Regex FamilyName();

    private static readonly string[] Critical =
    {
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.MicrosoftEdge*",
        "Microsoft.WebView2*", "Microsoft.Win32WebViewHost", "Microsoft.XboxIdentityProvider", "Microsoft.GamingServices",
        "Microsoft.Xbox.TCUI", "Microsoft.VCLibs*", "Microsoft.NET.*", "Microsoft.UI.Xaml*", "Microsoft.WindowsAppRuntime*",
        "Microsoft.SecHealthUI", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl", "Microsoft.LockApp",
        "Microsoft.Windows.*", "MicrosoftWindows.*", "Windows.*", "Microsoft.Services.Store.Engagement", "Microsoft.HEIFImageExtension",
        "Microsoft.VP9VideoExtensions", "Microsoft.WebMediaExtensions", "Microsoft.HEVCVideoExtension", "Microsoft.WindowsTerminal",
    };

    private static readonly string[] Exceptions =
        { "Microsoft.Windows.Photos", "Microsoft.Windows.DevHome", "MicrosoftWindows.Client.WebExperience", "MicrosoftWindows.CrossDevice" };

    public static bool IsCritical(string fullName)
    {
        var name = fullName.Split('_')[0];
        if (Exceptions.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        return Critical.Any(p => p.EndsWith('*') ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase) : name.Equals(p, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// AppxRemoveForUser. Args: "packageFullName" (+ optional "displayName"). Removes a Store app for the current user only
/// (PackageManager.RemovePackageAsync). Undo: when the app's files are still on the PC (staged, e.g. another account
/// has it), it is registered again from them; otherwise PC Tidy says honestly that it has to come back from the
/// Microsoft Store. Frameworks, system apps and packages Windows depends on are never removed.
/// </summary>
public sealed class AppxRemoveForUserHandler(IAppxPackageControl packages) : SystemActionHandler
{
    internal const string LocationData = "installedLocation";
    internal const string FamilyData = "packageFamilyName";

    public AppxRemoveForUserHandler() : this(new WinRtAppxPackageControl()) { }

    protected override ActionKind Kind => ActionKind.AppxRemoveForUser;
    protected override bool TouchesRealSystem => packages.TouchesRealSystem;
    protected override bool PerUserOnly(PlanItem item) => true;

    private static string FullName(PlanItem item) => item.Arg("packageFullName").Trim();
    private static string Display(PlanItem item) => item.Arg("displayName").Trim() is { Length: > 0 } d ? d : FullName(item).Split('_')[0];

    protected override string? Validate(PlanItem item)
    {
        if (!AppxNames.FullName().IsMatch(FullName(item))) return "This step doesn't name a valid Store app.";
        return AppxNames.IsCritical(FullName(item)) ? "Windows or other apps depend on this, so PC Tidy never removes it." : null;
    }

    protected override string Describe(PlanItem item) => $"Would remove {Display(item)} ({FullName(item)}) for your account (PackageManager.RemovePackageAsync)";

    protected override bool CanUndo(StepRecord step) => step.Undo(LocationData) is not null;
    protected override string DescribeUndo(StepRecord step) => $"Would register {Display(step.Item)} again from its files, if they are still on the PC";

    protected override async Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var full = FullName(step.Item);
        var before = packages.Find(full);
        if (before is null) return step.Skipped($"{Display(step.Item)} isn't installed for your account.");
        if (before.IsFramework || before.IsSystem) return step.Skipped("This is part of Windows, so PC Tidy never removes it.");

        await packages.RemoveAsync(full, ct).ConfigureAwait(false);
        if (packages.Find(full) is not null) return step.Failed($"Windows didn't remove {Display(step.Item)}.");

        var staged = before.InstalledLocation is { } loc && packages.IsStaged(loc);
        var done = step.Done(staged
            ? $"Removed {Display(step.Item)} for your account. Undo can add it back."
            : $"Removed {Display(step.Item)} for your account. To get it back, install it again from the Microsoft Store.");
        done = done.WithUndo((FamilyData, before.FamilyName));
        return before.InstalledLocation is { } location ? done.WithUndo((LocationData, location)) : done;
    }

    protected override async Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var full = FullName(step.Item);
        var location = step.Undo(LocationData);
        if (location is null || !packages.IsStaged(location))
            return step.Skipped($"PC Tidy can't put {Display(step.Item)} back: its files are gone. Install it again from the Microsoft Store.");
        if (packages.Find(full) is not null) return step.Skipped($"{Display(step.Item)} is installed again already.");
        await packages.RegisterAsync(full, ct).ConfigureAwait(false);
        return packages.Find(full) is not null
            ? step.Undone($"Added {Display(step.Item)} back for your account.")
            : step.UndoFailed($"Windows didn't add {Display(step.Item)} back. Install it again from the Microsoft Store.");
    }

    protected override Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(packages.Find(FullName(step.Item)) is null);
}

/// <summary>
/// StoreRepair. Args: "wsreset" (any value: run System32\WSReset.exe, which clears the Store cache and opens the
/// Store) or "packageFamilyName" (register that app's packages again from their own files, like
/// Add-AppxPackage -Register, for the current user). Harmless and not reversible (nothing to put back).
/// </summary>
public sealed class StoreRepairHandler(IAppxPackageControl packages) : SystemActionHandler
{
    public StoreRepairHandler() : this(new WinRtAppxPackageControl()) { }

    protected override ActionKind Kind => ActionKind.StoreRepair;
    protected override bool TouchesRealSystem => packages.TouchesRealSystem;
    protected override bool PerUserOnly(PlanItem item) => true;

    private static bool IsReset(PlanItem item) => item.Args?.ContainsKey("wsreset") == true;
    private static string Family(PlanItem item) => item.Arg("packageFamilyName").Trim();

    protected override string? Validate(PlanItem item)
    {
        if (IsReset(item)) return Family(item).Length == 0 ? null : "This step asks for two different repairs.";
        return AppxNames.FamilyName().IsMatch(Family(item)) ? null : "This step doesn't name a valid Store app.";
    }

    protected override string Describe(PlanItem item) => IsReset(item)
        ? $"Would run {Path.Combine(Environment.SystemDirectory, "WSReset.exe")} (clears the Microsoft Store cache)"
        : $"Would register {Family(item)} again from its own files (PackageManager.RegisterPackageByFullNameAsync)";

    protected override async Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (IsReset(step.Item))
        {
            var log = Path.Combine(DataPaths.Logs(ctx.Locations.DataRoot), "tools", $"{SafeRunId(ctx)}-{step.Index}-wsreset.log");
            var code = await packages.ResetStoreCacheAsync(log, ct).ConfigureAwait(false);
            return code == 0 ? step.Done("Cleared the Microsoft Store cache. The Store opens when it's done.") : step.Failed($"WSReset reported a problem (code {code}).");
        }

        var found = packages.FindFamily(Family(step.Item)).Where(p => !p.IsFramework).ToList();
        if (found.Count == 0) return step.Skipped("This app isn't installed for your account.");
        foreach (var p in found)
        {
            if (p.InstalledLocation is not { } loc || !packages.IsStaged(loc)) return step.Failed("This app's files are missing. Reinstall it from the Microsoft Store.");
            await packages.RegisterAsync(p.FullName, ct).ConfigureAwait(false);
        }
        return step.Done("Registered the app again for your account.");
    }

    protected override Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(step.Skipped("A repair doesn't remove anything, so there's nothing to undo."));
}
