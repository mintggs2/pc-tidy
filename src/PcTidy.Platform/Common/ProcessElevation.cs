using System.Security.Principal;

namespace PcTidy.Platform;

/// <summary>Is this process running with a full (elevated) administrator token?</summary>
public static class ProcessElevation
{
    private static readonly Lazy<bool> Elevated = new(() =>
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            // With UAC on, a non-elevated admin token has the Administrators group as deny-only,
            // so IsInRole is true only for an elevated token.
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    });

    public static bool IsElevated => Elevated.Value;
}
