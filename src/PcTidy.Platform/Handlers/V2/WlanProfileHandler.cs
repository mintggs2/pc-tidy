using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>A saved Wi-Fi profile as Windows exports it (key material stays encrypted by Windows).</summary>
public sealed record WlanProfileExport(string Xml, uint Flags)
{
    public const uint GroupPolicy = 1;
    public const uint PerUser = 2;
    public bool IsGroupPolicy => (Flags & GroupPolicy) != 0;
    public bool IsPerUser => (Flags & PerUser) != 0;
}

/// <summary>Saved Wi-Fi profiles. Real: Native Wifi API (wlanapi.dll). Tests: a fake.</summary>
public interface IWlanControl
{
    bool TouchesRealSystem { get; }
    /// <summary>Null when there is no such profile on that adapter.</summary>
    WlanProfileExport? Export(Guid interfaceGuid, string profileName);
    void Delete(Guid interfaceGuid, string profileName);
    void Import(Guid interfaceGuid, string xml, uint flags);
}

public sealed class WindowsWlanControl : IWlanControl
{
    private const int ErrorNotFound = 1168;
    private const int ErrorServiceNotActive = 1062;

    public bool TouchesRealSystem => true;

    public WlanProfileExport? Export(Guid interfaceGuid, string profileName) => WithHandle(h =>
    {
        uint flags = 0; // not WLAN_PROFILE_GET_PLAINTEXT_KEY: the backup keeps Windows' own encrypted key
        var rc = WlanGetProfile(h, ref interfaceGuid, profileName, IntPtr.Zero, out var xml, ref flags, out _);
        if (rc == ErrorNotFound) return null;
        Check(rc);
        try { return new WlanProfileExport(Marshal.PtrToStringUni(xml) ?? "", flags); }
        finally { WlanFreeMemory(xml); }
    });

    public void Delete(Guid interfaceGuid, string profileName)
        => WithHandle(h => { Check(WlanDeleteProfile(h, ref interfaceGuid, profileName, IntPtr.Zero)); return 0; });

    public void Import(Guid interfaceGuid, string xml, uint flags)
        => WithHandle(h => { Check(WlanSetProfile(h, ref interfaceGuid, flags & WlanProfileExport.PerUser, xml, null, false, IntPtr.Zero, out _)); return 0; });

    private static T WithHandle<T>(Func<IntPtr, T> action)
    {
        var rc = WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
        if (rc == ErrorServiceNotActive) throw new InvalidOperationException("Wi-Fi isn't available on this PC (the WLAN service isn't running).");
        Check(rc);
        try { return action(handle); }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
    }

    private static void Check(uint rc)
    {
        if (rc == 5) throw new UnauthorizedAccessException();
        if (rc != 0) throw new Win32Exception((int)rc);
    }

    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanGetProfile(IntPtr clientHandle, ref Guid interfaceGuid, string profileName, IntPtr reserved, out IntPtr profileXml, ref uint flags, out uint grantedAccess);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanDeleteProfile(IntPtr clientHandle, ref Guid interfaceGuid, string profileName, IntPtr reserved);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanSetProfile(IntPtr clientHandle, ref Guid interfaceGuid, uint flags, string profileXml, string? allUserProfileSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool overwrite, IntPtr reserved, out uint reasonCode);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
}

/// <summary>
/// WlanProfileDelete. Args: "profileName", "interfaceGuid". The profile is first exported (WlanGetProfile, key kept
/// encrypted by Windows) to &lt;DataRoot&gt;\backups\wlan (in the admin helper: its admin-only folder), then deleted
/// (WlanDeleteProfile). Undo imports that exact XML again (WlanSetProfile, never overwriting).
/// Profiles set by your organization (Group Policy) are never deleted.
/// </summary>
public sealed class WlanProfileHandler(IWlanControl wlan) : SystemActionHandler
{
    internal const string BackupData = "wlanBackup";
    internal const string FlagsData = "wlanFlags";

    public WlanProfileHandler() : this(new WindowsWlanControl()) { }

    protected override ActionKind Kind => ActionKind.WlanProfileDelete;
    protected override bool TouchesRealSystem => wlan.TouchesRealSystem;
    protected override bool NeedsElevation(PlanItem item) => item.NeedsAdmin;

    private static string Name(PlanItem item) => item.Arg("profileName");
    private static Guid Interface(PlanItem item) => Guid.Parse(item.Arg("interfaceGuid").Trim());

    protected override string? Validate(PlanItem item)
    {
        var name = Name(item);
        if (name.Length is 0 or > 255 || name.Any(char.IsControl)) return "The Wi-Fi network name is not valid.";
        return Guid.TryParse(item.Arg("interfaceGuid").Trim(), out var g) && g != Guid.Empty ? null : "The Wi-Fi adapter id is not valid.";
    }

    protected override string Describe(PlanItem item)
        => $"Would export the Wi-Fi network \"{Name(item)}\" to PC Tidy's backups\\wlan folder and then forget it (WlanGetProfile, WlanDeleteProfile)";

    /// <summary>Same name for the same step, so a step that was cut off mid-way can still be undone from its backup.</summary>
    private static string BackupPath(HandlerContext ctx, StepRecord step)
        => Path.Combine(BackupFolder(ctx, "wlan"), $"{SafeRunId(ctx)}-{step.Index.ToString(CultureInfo.InvariantCulture)}.xml");

    protected override bool CanUndo(StepRecord step) => true;
    protected override string DescribeUndo(StepRecord step) => $"Would add the Wi-Fi network \"{Name(step.Item)}\" back from its backup (WlanSetProfile)";

    protected override Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var item = step.Item;
        var export = wlan.Export(Interface(item), Name(item));
        if (export is null) return Task.FromResult(step.Skipped("This Wi-Fi network isn't saved on this PC any more."));
        if (export.IsGroupPolicy) return Task.FromResult(step.Skipped("Your organization set up this Wi-Fi network, so PC Tidy leaves it alone."));
        if (export.IsPerUser && ctx.IsElevated) return Task.FromResult(step.Skipped(PerUserMessage));
        if (export.Xml.Length == 0) return Task.FromResult(step.Failed("Windows gave an empty copy of this Wi-Fi network, so it wasn't removed."));

        var backup = BackupPath(ctx, step);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, export.Xml, new UTF8Encoding(false));
        if (File.ReadAllText(backup, Encoding.UTF8) != export.Xml) return Task.FromResult(step.Failed("The backup of this Wi-Fi network couldn't be saved, so it wasn't removed."));

        wlan.Delete(Interface(item), Name(item));
        if (wlan.Export(Interface(item), Name(item)) is not null) return Task.FromResult(step.Failed("Windows didn't forget this Wi-Fi network."));
        return Task.FromResult(step.Done($"Forgot the Wi-Fi network \"{Name(item)}\". A backup is kept, so you can undo this.")
            .WithUndo((BackupData, backup), (FlagsData, export.Flags.ToString(CultureInfo.InvariantCulture))));
    }

    protected override Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (Validate(step.Item) is { } problem) return Task.FromResult(step.UndoFailed(problem));
        var backup = step.Undo(BackupData) ?? BackupPath(ctx, step);
        var folder = BackupFolder(ctx, "wlan");
        // Only a backup PC Tidy wrote into its own backup folder is imported.
        if (!PathArgs.IsPlainLocal(backup) || !string.Equals(Path.GetDirectoryName(backup), folder, StringComparison.OrdinalIgnoreCase)
            || !backup.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || !File.Exists(backup))
            return Task.FromResult(step.UndoFailed("The backup of this Wi-Fi network is missing, so PC Tidy can't undo this."));
        if (wlan.Export(Interface(step.Item), Name(step.Item)) is not null) return Task.FromResult(step.Skipped("This Wi-Fi network is saved again already. Nothing to undo."));
        var flags = uint.TryParse(step.Undo(FlagsData), NumberStyles.None, CultureInfo.InvariantCulture, out var f) ? f : 0;
        wlan.Import(Interface(step.Item), File.ReadAllText(backup, Encoding.UTF8), flags);
        return Task.FromResult(wlan.Export(Interface(step.Item), Name(step.Item)) is not null
            ? step.Undone($"Added the Wi-Fi network \"{Name(step.Item)}\" back.")
            : step.UndoFailed("Windows didn't add the Wi-Fi network back."));
    }

    protected override Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(wlan.Export(Interface(step.Item), Name(step.Item)) is null);
}
