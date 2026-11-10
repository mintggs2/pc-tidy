using PcTidy.Features.Privacy.Common;

namespace PcTidy.Features.Privacy.Notifications;

/// <summary>
/// What the Notification manager reads, bundled so tests (and the simulation sandbox) swap in a redirected registry
/// and a fixed AI-feature answer. Real PC: HKCU + PackageManager/WMI. Sandbox: HKCU\Software\PCTidyTest\privacy\sandbox
/// and "no Copilot, not a Copilot+ PC".
/// </summary>
public sealed record NotificationsEnvironment(UserRegistry Registry, IAiFeatureProbe Ai)
{
    public static NotificationsEnvironment ForContext(ScanContext ctx)
        => ctx.Locations.IsSandbox
            ? new NotificationsEnvironment(UserRegistry.Redirected(PrivacyIds.SandboxRegistryRoot), FixedAiFeatureProbe.Nothing)
            : new NotificationsEnvironment(UserRegistry.Real, new WindowsAiFeatureProbe());
}
