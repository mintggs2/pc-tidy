using System.Management;
using PcTidy.Catalog.Tweaks;

namespace PcTidy.Features.Privacy.Notifications;

/// <summary>State of Windows' "Recall" optional feature (Win32_OptionalFeature.InstallState).</summary>
public enum RecallState
{
    /// <summary>Not listed on this PC (or removed from the image): Recall doesn't exist here.</summary>
    NotPresent,
    Disabled,
    Enabled,
}

/// <summary>What this PC has of Windows' AI features, read once per scan.</summary>
/// <param name="CopilotPackageFullName">The Copilot Store app installed for this account, or null.</param>
/// <param name="HasNpu">A neural processing unit is present (PnP class ComputeAccelerator).</param>
public sealed record AiFeatureFacts(string? CopilotPackageFullName, bool HasNpu, RecallState Recall)
{
    public static readonly AiFeatureFacts None = new(null, false, RecallState.NotPresent);

    /// <summary>
    /// Copilot+ PC: Recall only exists there (Tweaks.md: "Recall only exists on Copilot+ PCs"). Both signals are needed:
    /// an NPU, and Windows listing the Recall feature. A normal PC (no NPU, or Recall not in its image) is never Copilot+.
    /// </summary>
    public bool IsCopilotPlus => HasNpu && Recall != RecallState.NotPresent;
}

/// <summary>Reads <see cref="AiFeatureFacts"/>. Real: PackageManager + WMI, read-only. Tests and the sandbox: a fixed answer.</summary>
public interface IAiFeatureProbe
{
    AiFeatureFacts Read(CancellationToken ct);
}

public sealed class FixedAiFeatureProbe(AiFeatureFacts facts) : IAiFeatureProbe
{
    public static readonly FixedAiFeatureProbe Nothing = new(AiFeatureFacts.None);
    public AiFeatureFacts Read(CancellationToken ct) => facts;
}

/// <summary>
/// The real PC, read-only: the current user's Store packages (Windows.Management.Deployment.PackageManager), the
/// ComputeAccelerator device class (NPUs) and the Recall optional feature through WMI. Never parses tool output.
/// Any failure reads as "not there", so the worst case is a greyed "Not on this PC" row.
/// </summary>
public sealed class WindowsAiFeatureProbe : IAiFeatureProbe
{
    /// <summary>Device setup class of NPUs ("Neural processors" / ComputeAccelerator).</summary>
    public const string ComputeAcceleratorClassGuid = "{f01a9d53-3ff6-48d2-9f97-c8a7004be10c}";
    public const string RecallFeatureName = "Recall";
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(20);

    public AiFeatureFacts Read(CancellationToken ct)
    {
        var copilot = FindCopilot();
        ct.ThrowIfCancellationRequested();
        var npu = HasNpu();
        ct.ThrowIfCancellationRequested();
        return new AiFeatureFacts(copilot, npu, npu ? ReadRecall() : RecallState.NotPresent);
    }

    private static string? FindCopilot()
    {
        try
        {
            foreach (var p in new Windows.Management.Deployment.PackageManager().FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (!p.IsFramework && p.Id.Name.Equals(TweakCatalog.CopilotPackage, StringComparison.OrdinalIgnoreCase)) return p.Id.FullName;
                }
                catch (Exception) { /* one unreadable package: skip it */ }
            }
        }
        catch (Exception) { /* AppX service unavailable: treat as not installed */ }
        return null;
    }

    private static bool HasNpu()
        => Query($"SELECT PNPClass FROM Win32_PnPEntity WHERE PNPClass = 'ComputeAccelerator' OR ClassGuid = '{ComputeAcceleratorClassGuid}'",
            results => results.Count > 0, fallback: false);

    private static RecallState ReadRecall()
        => Query($"SELECT InstallState FROM Win32_OptionalFeature WHERE Name = '{RecallFeatureName}'", results =>
        {
            foreach (ManagementBaseObject o in results)
            {
                // 1 = enabled, 2 = disabled, 3 = absent (not in the image).
                var state = Convert.ToUInt32(o["InstallState"] ?? 3u, System.Globalization.CultureInfo.InvariantCulture);
                return state switch { 1 => RecallState.Enabled, 2 => RecallState.Disabled, _ => RecallState.NotPresent };
            }
            return RecallState.NotPresent;
        }, fallback: RecallState.NotPresent);

    /// <summary>Runs one read-only WQL query; any WMI failure gives <paramref name="fallback"/>.</summary>
    private static T Query<T>(string wql, Func<ManagementObjectCollection, T> read, T fallback)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\CIMV2"), new ObjectQuery(wql),
                new System.Management.EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = false });
            using var results = searcher.Get();
            return read(results);
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException
                                       or TimeoutException or InvalidCastException or FormatException)
        {
            return fallback;
        }
    }
}
