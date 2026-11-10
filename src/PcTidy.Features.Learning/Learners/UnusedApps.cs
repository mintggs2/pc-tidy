using System.Globalization;
using PcTidy.Features.Learning.Model;

namespace PcTidy.Features.Learning.Learners;

/// <summary>An app PC Tidy hasn't seen opened for a while. Never "never used": only what was actually observed.</summary>
/// <param name="LastUsedUtc">Last launch Windows or Steam recorded, or null when none was ever seen.</param>
/// <param name="DaysUnused">Days since the last launch, or since PC Tidy started watching when there is none.</param>
public sealed record UnusedApp(string Key, string Name, AppSource Source, DateTime? LastUsedUtc, int DaysUnused, string Reason)
{
    public string Id => "app:" + Key.ToLowerInvariant();
}

/// <summary>Result of the unused-apps learner. Empty until PC Tidy has watched for 30 days.</summary>
/// <param name="DaysUntilReady">Days of watching still needed before anything is flagged (0 = ready).</param>
public sealed record UnusedAppsReport(IReadOnlyList<UnusedApp> Apps, int DaysWatched, int DaysUntilReady);

/// <summary>
/// "Apps you never open": an app is flagged when PC Tidy has watched for at least 30 days, has watched this app for at
/// least 30 days, and saw no launch in the last 30 days (UserAssist count unchanged, no newer UserAssist / Steam
/// LastPlayed time). Before 30 days of watching nothing is claimed. Pure.
/// </summary>
public static class UnusedApps
{
    public static UnusedAppsReport Evaluate(IEnumerable<AppObservation> observations, DateTime watchingSinceUtc, DateTime nowUtc)
    {
        var watched = nowUtc - watchingSinceUtc;
        int daysWatched = Math.Max(0, (int)Math.Floor(watched.TotalDays));
        if (watched < LearningRules.MinAppWatch)
            return new UnusedAppsReport(Array.Empty<UnusedApp>(), daysWatched, (int)Math.Ceiling((LearningRules.MinAppWatch - watched).TotalDays));

        var result = new List<UnusedApp>();
        foreach (var o in observations)
        {
            var since = o.FirstSeenUtc > watchingSinceUtc ? o.FirstSeenUtc : watchingSinceUtc;
            if (nowUtc - since < LearningRules.MinAppWatch) continue; // this app hasn't been watched long enough yet

            var lastUsed = Max(o.LastRunUtc, o.LastCountChangeUtc);
            var idleSince = lastUsed ?? since;
            var idle = nowUtc - idleSince;
            if (idle < LearningRules.AppUnusedAfter) continue;
            int days = (int)Math.Floor(idle.TotalDays);
            // Says only what PC Tidy saw: starts from Start, the taskbar or a shortcut, and the app running while PC Tidy was open.
            var reason = lastUsed is { } last
                ? $"PC Tidy hasn't seen it opened or running since {last.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)} ({days} days)."
                : $"PC Tidy hasn't seen it opened or running in the {(int)Math.Floor((nowUtc - since).TotalDays)} days it has been watching.";
            result.Add(new UnusedApp(o.Key, o.Name, o.Source, lastUsed, days, reason));
        }
        return new UnusedAppsReport(result.OrderByDescending(a => a.DaysUnused).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList(), daysWatched, 0);
    }

    /// <summary>Updates (or starts) the observation of one app from what its source reports now.</summary>
    public static AppObservation Observe(AppObservation? existing, AppUsage now, DateTime nowUtc)
    {
        if (existing is null)
            return new AppObservation(now.Key, now.Name, now.Source, nowUtc, now.RunCount, now.LastRunUtc, null);
        var changed = now.RunCount > existing.LastCount ? nowUtc : existing.LastCountChangeUtc;
        return existing with
        {
            Name = now.Name,
            LastCount = Math.Max(existing.LastCount, now.RunCount),
            LastRunUtc = Max(existing.LastRunUtc, now.LastRunUtc),
            LastCountChangeUtc = changed,
        };
    }

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
