using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.App.Pages;

public enum HealthTone { Good, Attention, Info }

/// <param name="OpenSection">A section that explains or fixes this reason (Home offers a link to it), or null.</param>
public sealed record HealthReason(HealthTone Tone, string Text, int Points, string? OpenSection = null);

/// <summary>
/// Home health score: honest and explainable (dark-pattern charter rules 1, 3, 4). It only exists after the user
/// asked for a check (Home shows "Not checked yet" until then, never a number). Starts at 100; each reason says
/// what it costs, so the user sees exactly what changes the number. Calm wording, neutral colours.
/// </summary>
public sealed class HealthScore
{
    /// <param name="driveName">How a drive is named on screen ("C:", or a practice folder's name in a sandbox).</param>
    /// <param name="switchedOffServices">Windows services something switched off (Services' repair finds them); null = not read.</param>
    public HealthScore(IReadOnlyList<DriveDetails> drives, long reclaimableBytes, int unfinishedRuns, bool rebootPending, Func<DriveDetails, string> driveName,
        int? switchedOffServices = null)
    {
        var reasons = new List<HealthReason>();
        var system = drives.FirstOrDefault(d => d.IsSystem) ?? drives.FirstOrDefault();
        if (system is { TotalBytes: > 0 })
        {
            double free = (double)system.FreeBytes / system.TotalBytes;
            string name = driveName(system);
            if (free < 0.10) reasons.Add(new(HealthTone.Attention, $"{name} has {free:P0} free. Windows likes at least 10%", -25));
            else if (free < 0.20) reasons.Add(new(HealthTone.Attention, $"{name} has {free:P0} free. A little more room helps updates", -10));
            else reasons.Add(new(HealthTone.Good, $"{name} has plenty of room ({free:P0} free)", 0));
        }
        foreach (var d in drives.Where(d => d != system && d.TotalBytes > 0 && d.FreeBytes < d.TotalBytes / 10))
            reasons.Add(new(HealthTone.Attention, $"{driveName(d)} is nearly full", -5));

        if (reclaimableBytes >= 10L << 30) reasons.Add(new(HealthTone.Attention, $"{Sizes.Format(reclaimableBytes)} of leftovers you could clean", -10));
        else if (reclaimableBytes >= 2L << 30) reasons.Add(new(HealthTone.Attention, $"{Sizes.Format(reclaimableBytes)} of leftovers you could clean", -5));
        else reasons.Add(new(HealthTone.Good, "Not much to clean up", 0));

        if (unfinishedRuns > 0) reasons.Add(new(HealthTone.Attention, "A run didn't finish: resume or roll it back in History", -10));
        if (rebootPending) reasons.Add(new(HealthTone.Info, "Windows is waiting for a restart to finish updating", -5));
        // The same finding as the amber banner on Services, so Home never says "nothing" while Services warns.
        if (switchedOffServices is > 0 and var n)
            reasons.Add(new(HealthTone.Attention, n == 1
                ? "1 Windows service was switched off by something. Services can put it back"
                : $"{n} Windows services were switched off by something. Services can put them back", -5, "services"));
        ServicesChecked = switchedOffServices is not null;

        Reasons = reasons.OrderBy(r => r.Points).ToList();
        Score = Math.Clamp(100 + reasons.Sum(r => r.Points), 0, 100);
    }

    public int Score { get; }
    public IReadOnlyList<HealthReason> Reasons { get; }
    public bool AnyPointsOff => Reasons.Any(r => r.Points != 0);

    /// <summary>Whether Services' switched-off check was part of this score (it can't be read everywhere).</summary>
    public bool ServicesChecked { get; }

    /// <summary>What the score counts, said on the card so it isn't taken for more than it is.</summary>
    public string Covers => "Counts drive space, leftovers to clean, unfinished runs, a waiting restart"
                            + (ServicesChecked ? " and switched-off Windows services." : ".")
                            + " Insights has more things worth a look.";

    /// <summary>Calm wording only: no alarms, no "critical", nothing that pushes a purchase or a click.</summary>
    public string Label => Score >= 85 ? "In good shape" : Score >= 65 ? "A few things to tidy" : "Worth a look";
}
