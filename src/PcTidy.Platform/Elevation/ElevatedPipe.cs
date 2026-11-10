using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using static PcTidy.Platform.NativeMethods;

namespace PcTidy.Platform;

/// <summary>Creates and checks the named pipe between PC Tidy and its admin helper.</summary>
internal static class ElevatedPipe
{
    /// <summary>
    /// Server end, created by the normal (non-elevated) app. The ACL allows only the signed-in user, plus elevated
    /// administrators (needed when a standard user approves the prompt with another account's credentials;
    /// a non-elevated token can't use that entry because Administrators is deny-only there).
    /// Single instance and first-instance-only, so nobody can squat the name or open a second instance.
    /// </summary>
    public static NamedPipeServerStream CreateServer(string pipeName)
    {
        var security = new PipeSecurity();
        using var me = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }

    public static NamedPipeClientStream CreateClient(string pipeName)
        => new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    public static int? ServerProcessId(PipeStream pipe)
        => GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) ? (int)pid : null;

    public static int? ClientProcessId(PipeStream pipe)
        => GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) ? (int)pid : null;
}
