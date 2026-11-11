using System.Globalization;
using PcTidy.App.Shell;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Controls;

namespace PcTidy.App.Pages;

/// <summary>One System Restore point as Windows lists it (read-only).</summary>
public sealed record RestorePointFact(uint SequenceNumber, string Description, DateTime CreatedUtc, int RestorePointType);

/// <summary>The restore points on this PC, or why they couldn't be read.</summary>
public sealed record RestorePointList(IReadOnlyList<RestorePointFact> Points, string? Problem);

/// <summary>Reads the restore points. Read-only; History's Restore points view uses it.</summary>
public interface IRestorePointReader
{
    Task<RestorePointList> ReadAsync(CancellationToken ct);
}

/// <summary>
/// A read-only WMI query: <c>SELECT … FROM SystemRestore</c> in <c>root\default</c> (the same query PC care's scan and
/// the platform's restore point code use; neither is callable from here). Listing needs admin rights: without them the
/// list is empty and <see cref="RestorePointList.Problem"/> says so. Never changes anything.
/// </summary>
public sealed class WmiRestorePointReader : IRestorePointReader
{
    public Task<RestorePointList> ReadAsync(CancellationToken ct) => Task.Run(Read, ct);

    private static RestorePointList Read()
    {
        try
        {
            var points = new List<RestorePointFact>();
            using var searcher = new System.Management.ManagementObjectSearcher(
                new System.Management.ManagementScope(@"\\.\root\default"),
                new System.Management.ObjectQuery("SELECT SequenceNumber, Description, CreationTime, RestorePointType FROM SystemRestore"));
            using var results = searcher.Get();
            foreach (System.Management.ManagementObject mo in results)
            {
                using (mo)
                {
                    uint seq = mo["SequenceNumber"] switch { uint u => u, int i => (uint)i, _ => 0u };
                    if (seq == 0) continue;
                    var created = mo["CreationTime"] is string cim ? System.Management.ManagementDateTimeConverter.ToDateTime(cim).ToUniversalTime() : DateTime.MinValue;
                    int type = mo["RestorePointType"] switch { uint u => (int)u, int i => i, _ => 0 };
                    points.Add(new RestorePointFact(seq, mo["Description"] as string ?? "", created, type));
                }
            }
            return new RestorePointList(points, null);
        }
        catch (System.Management.ManagementException ex) when (ex.ErrorCode == System.Management.ManagementStatus.AccessDenied)
        {
            return new RestorePointList(Array.Empty<RestorePointFact>(), RestorePointsModel.NeedsAdminText);
        }
        catch (UnauthorizedAccessException)
        {
            return new RestorePointList(Array.Empty<RestorePointFact>(), RestorePointsModel.NeedsAdminText);
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException or PlatformNotSupportedException)
        {
            return new RestorePointList(Array.Empty<RestorePointFact>(), "Windows couldn't list restore points right now.");
        }
    }
}

/// <summary>Invented restore points for demo mode (and the screenshots).</summary>
public sealed class DemoRestorePointReader : IRestorePointReader
{
    private static readonly DateTime Started = DateTime.UtcNow.AddMinutes(-DateTime.UtcNow.Minute);

    public Task<RestorePointList> ReadAsync(CancellationToken ct)
    {
        var now = Started; // fixed for the whole session, so a colour tag stays on its point
        return Task.FromResult(new RestorePointList(new[]
        {
            new RestorePointFact(41, "PC Tidy", now.AddDays(-1).AddHours(-3), RestorePointsModel.ModifySettings),
            new RestorePointFact(40, "Windows Update", now.AddDays(-6), RestorePointsModel.ApplicationInstall),
            new RestorePointFact(39, "Installed Sample Photo Editor", now.AddDays(-13), RestorePointsModel.ApplicationInstall),
            new RestorePointFact(38, "Scheduled checkpoint", now.AddDays(-20), RestorePointsModel.ModifySettings),
        }, null));
    }
}

/// <summary>One restore point card in History › Restore points.</summary>
public sealed class RestorePointRow
{
    public RestorePointRow(RestorePointFact fact, bool isNewest, string? tag)
    {
        Fact = fact;
        IsNewest = isNewest;
        Tag = RestorePointsModel.Tags.Contains(tag ?? "") ? tag : null;
        Name = string.IsNullOrWhiteSpace(fact.Description) ? "Restore point" : fact.Description.Trim();
        Description = RestorePointsModel.TypeText(fact.RestorePointType);
        When = fact.CreatedUtc == DateTime.MinValue ? "Date unknown" : fact.CreatedUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
    }

    public RestorePointFact Fact { get; }
    public uint SequenceNumber => Fact.SequenceNumber;
    public string Name { get; }
    /// <summary>What made it ("Made before an app was installed"…).</summary>
    public string Description { get; }
    public string When { get; }
    /// <summary>The newest point is always kept, so there is still a way back: it never offers Delete.</summary>
    public bool IsNewest { get; }
    public bool CanDelete => !IsNewest;
    /// <summary>The optional colour tag ("Green", "Blue", "Amber", "Red"), stored on this PC only; null = none.</summary>
    public string? Tag { get; }
    public string TagKey => RestorePointsModel.TagKey(Fact);
    public string AutomationName => $"{Name}, {When}, {Description}" + (Tag is null ? "" : $", tagged {Tag}") + (IsNewest ? ", newest, always kept" : "");
}

/// <summary>History › Restore points: cards, the colour tags, and the delete plan (never for the newest point).</summary>
public static class RestorePointsModel
{
    // Windows' RestorePointType values (SRSetRestorePoint / SystemRestore WMI class).
    public const int ApplicationInstall = 0;
    public const int ApplicationUninstall = 1;
    public const int DeviceDriverInstall = 10;
    public const int ModifySettings = 12;
    public const int CancelledOperation = 13;

    public const string NeedsAdminText = "Windows only lists restore points for an admin. Start PC Tidy as administrator to see them here, or open System Restore.";

    /// <summary>The colour tags a card can carry (none is the default).</summary>
    public static readonly IReadOnlyList<string> Tags = new[] { "Green", "Blue", "Amber", "Red" };

    public static PillTone ToneOf(string? tag) => tag switch
    {
        "Green" => PillTone.Success,
        "Blue" => PillTone.Info,
        "Amber" => PillTone.Caution,
        "Red" => PillTone.Critical,
        _ => PillTone.Neutral,
    };

    /// <summary>Plain words for what made the point.</summary>
    public static string TypeText(int type) => type switch
    {
        ApplicationInstall => "Made before an app or update was installed",
        ApplicationUninstall => "Made before an app was removed",
        DeviceDriverInstall => "Made before a driver was installed",
        ModifySettings => "Made before settings were changed",
        CancelledOperation => "Made for a change that was cancelled",
        _ => "Made by Windows or an app", // other values: verify against Microsoft's RestorePointType list
    };

    /// <summary>Newest first; only the newest is marked so (it never offers Delete).</summary>
    public static IReadOnlyList<RestorePointRow> Build(IReadOnlyList<RestorePointFact> points, IReadOnlyDictionary<string, string>? tags = null)
    {
        var ordered = points.OrderByDescending(p => p.CreatedUtc).ThenByDescending(p => p.SequenceNumber).ToList();
        return ordered.Select((p, i) => new RestorePointRow(p, i == 0, tags is not null && tags.TryGetValue(TagKey(p), out var t) ? t : null)).ToList();
    }

    /// <summary>A tag belongs to one point: its sequence number and creation time (numbers can be reused after a reset).</summary>
    public static string TagKey(RestorePointFact p) => $"{p.SequenceNumber}@{p.CreatedUtc.ToUniversalTime():yyyyMMddHHmmss}";

    /// <summary>The tags with <paramref name="key"/> set to <paramref name="tag"/> (null removes it).</summary>
    public static Dictionary<string, string> WithTag(IReadOnlyDictionary<string, string> tags, string key, string? tag)
    {
        var copy = new Dictionary<string, string>(tags);
        if (tag is null || !Tags.Contains(tag)) copy.Remove(key);
        else copy[key] = tag;
        return copy;
    }

    /// <summary>
    /// The plan "Delete" sends through the normal confirm flow: PC care's RestorePointDelete item (admin, can't be
    /// undone). Null for the newest point: it is always kept, and the platform refuses it too.
    /// </summary>
    public static ActionPlan? DeletePlan(RestorePointRow row)
    {
        if (!row.CanDelete) return null;
        var item = PcTidy.Features.PcCare.Planning.PcCareActions.DeleteRestorePoint(row.SequenceNumber, $"Delete the restore point \"{row.Name}\" ({row.When})");
        return new ActionPlan { FeatureId = item.FeatureId, Title = "Delete a restore point", Items = new[] { item } };
    }
}
