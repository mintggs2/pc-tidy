using System.Management;
using Microsoft.Win32;

namespace PcTidy.Platform;

/// <summary>One System Restore point as Windows lists it.</summary>
public sealed record RestorePointEntry(uint SequenceNumber, string Description, DateTime CreatedUtc, uint RestorePointType);

/// <summary>Result of listing restore points. <see cref="Problem"/> is set when the list couldn't be read.</summary>
public sealed record RestorePointListing(IReadOnlyList<RestorePointEntry> Points, string? Problem)
{
    public RestorePointEntry? Newest => Points.OrderByDescending(p => p.CreatedUtc).FirstOrDefault();
}

public enum SystemProtectionState { On, Off, OffByPolicy, Unknown }

/// <summary>How restore points are read and made. Real: WMI. Tests: a fake (TouchesRealSystem = false).</summary>
internal interface IRestorePointBackend
{
    bool TouchesRealSystem { get; }
    SystemProtectionState GetProtection();
    RestorePointListing List();
    /// <summary>Asks Windows for a restore point. Returns Windows' code (0 = accepted, which does NOT prove one was made).</summary>
    uint Create(string description);
}

/// <summary>
/// System Restore through WMI <c>root\default:SystemRestore</c> (language-neutral, no command output parsed).
/// Listing and creating need an elevated process.
/// </summary>
internal sealed class WmiRestorePointBackend : IRestorePointBackend
{
    private const string Scope = @"\\.\root\default";
    /// <summary>MODIFY_SETTINGS: "a change to system settings", which is what PC Tidy does.</summary>
    private const uint ModifySettings = 12;
    /// <summary>BEGIN_SYSTEM_CHANGE: creates the point now (same as Checkpoint-Computer).</summary>
    private const uint BeginSystemChange = 100;

    public bool TouchesRealSystem => true;

    public SystemProtectionState GetProtection()
    {
        try
        {
            using var lm = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var policy = lm.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore"))
            {
                if (policy?.GetValue("DisableSR") is int disabled && disabled == 1) return SystemProtectionState.OffByPolicy;
            }
            using var sr = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
            // RPSessionInterval is 1 while System Protection is on for at least one drive, 0 when it is off everywhere.
            return sr?.GetValue("RPSessionInterval") switch
            {
                int i when i >= 1 => SystemProtectionState.On,
                int => SystemProtectionState.Off,
                _ => SystemProtectionState.Unknown,
            };
        }
        catch
        {
            return SystemProtectionState.Unknown;
        }
    }

    public RestorePointListing List()
    {
        try
        {
            var points = new List<RestorePointEntry>();
            using var searcher = new ManagementObjectSearcher(new ManagementScope(Scope), new ObjectQuery("SELECT * FROM SystemRestore"));
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    var created = mo["CreationTime"] is string cim ? ManagementDateTimeConverter.ToDateTime(cim).ToUniversalTime() : DateTime.MinValue;
                    points.Add(new RestorePointEntry(
                        Convert.ToUInt32(mo["SequenceNumber"] ?? 0u),
                        mo["Description"] as string ?? "",
                        created,
                        Convert.ToUInt32(mo["RestorePointType"] ?? 0u)));
                }
            }
            return new RestorePointListing(points, null);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return new RestorePointListing(Array.Empty<RestorePointEntry>(), "Listing restore points needs admin rights.");
        }
        catch (UnauthorizedAccessException)
        {
            return new RestorePointListing(Array.Empty<RestorePointEntry>(), "Listing restore points needs admin rights.");
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            return new RestorePointListing(Array.Empty<RestorePointEntry>(), "Windows couldn't list restore points right now.");
        }
    }

    public uint Create(string description)
    {
        using var cls = new ManagementClass(new ManagementScope(Scope), new ManagementPath("SystemRestore"), null);
        using var input = cls.GetMethodParameters("CreateRestorePoint");
        input["Description"] = description;
        input["RestorePointType"] = ModifySettings;
        input["EventType"] = BeginSystemChange;
        using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
        return Convert.ToUInt32(output?["ReturnValue"] ?? uint.MaxValue);
    }
}
