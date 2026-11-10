using System.Diagnostics;
using System.Text;
using static PcTidy.Features.Speed.Common.NativeMethods;

namespace PcTidy.Features.Speed.Sources;

/// <summary>
/// Which processes hold files open, from Windows' Restart Manager. Read-only: files are only registered with a
/// session and listed (RmGetList); nothing is ever asked to shut down (no RmShutdown, no RmRestart).
/// </summary>
public sealed class RestartManagerLockFinder : ILockFinder
{
    /// <summary>Restart Manager slows down with big sessions; Microsoft's samples use a few hundred files.</summary>
    internal const int BatchSize = 200;

    public IReadOnlyList<LockHolder> Find(IReadOnlyList<string> files, CancellationToken ct)
    {
        var holders = new Dictionary<(int, DateTime?), LockHolder>();
        foreach (var chunk in files.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var h in Query(chunk)) holders.TryAdd((h.Pid, h.StartUtc), h);
        }
        return holders.Values.OrderBy(h => h.AppName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IReadOnlyList<LockHolder> Query(string[] files)
    {
        var key = new StringBuilder(CCH_RM_SESSION_KEY + 1);
        if (RmStartSession(out var session, 0, key) != ERROR_SUCCESS) return Array.Empty<LockHolder>();
        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != ERROR_SUCCESS) return Array.Empty<LockHolder>();
            uint count = 0, reasons = 0;
            RM_PROCESS_INFO[]? infos = null;
            // The list can grow between the two calls; try a few times.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var rc = RmGetList(session, out var needed, ref count, infos, ref reasons);
                if (rc == ERROR_SUCCESS)
                    return (infos ?? Array.Empty<RM_PROCESS_INFO>()).Take((int)count).Select(ToHolder).ToList();
                if (rc != ERROR_MORE_DATA) return Array.Empty<LockHolder>();
                infos = new RM_PROCESS_INFO[needed];
                count = needed;
            }
            return Array.Empty<LockHolder>();
        }
        finally { RmEndSession(session); }
    }

    private static LockHolder ToHolder(RM_PROCESS_INFO info)
    {
        var pid = info.Process.dwProcessId;
        var ft = ((long)(uint)info.Process.ProcessStartTime.dwHighDateTime << 32) | (uint)info.Process.ProcessStartTime.dwLowDateTime;
        DateTime? start = ft > 0 ? DateTime.FromFileTimeUtc(ft) : null;
        var processName = "";
        try
        {
            using var p = Process.GetProcessById(pid);
            processName = p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Ended meanwhile: keep Restart Manager's name.
        }
        var app = !string.IsNullOrWhiteSpace(info.strAppName) ? info.strAppName.Trim() : processName;
        var kind = Enum.IsDefined(typeof(LockHolderKind), info.ApplicationType) ? (LockHolderKind)info.ApplicationType : LockHolderKind.Unknown;
        return new LockHolder(pid, start, processName.Length > 0 ? processName : app, app.Length > 0 ? app : $"Process {pid}", kind,
            string.IsNullOrWhiteSpace(info.strServiceShortName) ? null : info.strServiceShortName.Trim(), (int)info.TSSessionId);
    }
}
