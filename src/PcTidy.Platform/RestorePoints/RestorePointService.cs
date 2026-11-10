using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>
/// Makes sure a recent System Restore point exists before a system change.
/// Runs in the elevated helper (not elevated → returns a "needs admin" problem). Reuses a point from the last
/// 24 hours, and after asking Windows for a new one it lists the points again, because Windows throttles
/// to one per day and still reports success when it made none.
/// In the sandbox it never calls WMI and returns a fake "created".
/// </summary>
public sealed class RestorePointService : IRestorePointService
{
    internal static readonly TimeSpan ReuseWindow = TimeSpan.FromHours(24);
    /// <summary>Windows keeps short descriptions; longer ones are cut by some tools and UIs.</summary>
    internal const int MaxDescriptionLength = 64;

    private readonly IKnownLocations _locations;
    private readonly IRestorePointBackend _backend;
    private readonly Func<bool> _isElevated;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _verifyDelay;

    public RestorePointService(IKnownLocations locations)
        : this(locations, new WmiRestorePointBackend(), () => ProcessElevation.IsElevated, () => DateTime.UtcNow, TimeSpan.FromSeconds(2)) { }

    internal RestorePointService(IKnownLocations locations, IRestorePointBackend backend, Func<bool> isElevated, Func<DateTime> utcNow, TimeSpan verifyDelay)
    {
        _locations = locations;
        _backend = backend;
        _isElevated = isElevated;
        _utcNow = utcNow;
        _verifyDelay = verifyDelay;
    }

    /// <summary>Read-only list of restore points (needs admin on most PCs; otherwise returns a Problem).</summary>
    public Task<RestorePointListing> ListAsync(CancellationToken ct)
        => Task.Run(() => _backend.List(), ct);

    public Task<RestorePointInfo> EnsureAsync(string description, CancellationToken ct)
        => Task.Run(() => Ensure(description, ct), ct);

    private async Task<RestorePointInfo> Ensure(string description, CancellationToken ct)
    {
        description = Clean(description);

        if (_locations.IsSandbox && _backend.TouchesRealSystem)
            return new RestorePointInfo(true, false, description + " (sandbox)", _utcNow(), null);

        if (!_isElevated())
            return Problem(description, "Making a restore point needs admin rights.");

        var protection = _backend.GetProtection();
        if (protection == SystemProtectionState.OffByPolicy)
            return Problem(description, "System Protection is turned off by your organization, so Windows can't make a restore point.");
        if (protection == SystemProtectionState.Off)
            return Problem(description, "System Protection is off, so Windows can't make a restore point. You can turn it on in System Properties → System Protection (it uses some disk space).");

        var before = _backend.List();
        if (before.Problem is { } listProblem) return Problem(description, listProblem);

        if (before.Newest is { } recent && _utcNow() - recent.CreatedUtc < ReuseWindow && recent.CreatedUtc <= _utcNow().AddMinutes(5))
            return new RestorePointInfo(false, true, recent.Description, recent.CreatedUtc, null);

        ct.ThrowIfCancellationRequested();
        var highestBefore = before.Points.Count == 0 ? 0u : before.Points.Max(p => p.SequenceNumber);
        uint code;
        try
        {
            code = _backend.Create(description);
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return Problem(description, "Windows refused to make a restore point.");
        }

        // Never trust the return code alone: look for a point that wasn't there before.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var after = _backend.List();
            var made = after.Points.Where(p => p.SequenceNumber > highestBefore).OrderByDescending(p => p.SequenceNumber).FirstOrDefault();
            if (made is not null) return new RestorePointInfo(true, false, made.Description, made.CreatedUtc, null);
            if (attempt < 2) await Task.Delay(_verifyDelay, ct).ConfigureAwait(false);
        }

        return Problem(description, code == 0
            ? "Windows said it made a restore point, but none appeared. It allows only one a day, or System Protection may be off for your Windows drive."
            : "Windows couldn't make a restore point right now.");
    }

    private static RestorePointInfo Problem(string description, string problem) => new(false, false, description, null, problem);

    private static string Clean(string description)
    {
        var d = string.IsNullOrWhiteSpace(description) ? "PC Tidy" : description.Trim();
        d = new string(d.Where(c => !char.IsControl(c)).ToArray());
        return d.Length <= MaxDescriptionLength ? d : d[..MaxDescriptionLength];
    }
}
