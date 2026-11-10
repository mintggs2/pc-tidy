using CommunityToolkit.Mvvm.ComponentModel;
using PcTidy.Features.Services.Model;
using PcTidy.Features.Services.Scanning;
using PcTidy.UI.Controls;

namespace PcTidy.Features.Services.UI;

/// <summary>A small coloured label on a row.</summary>
public sealed record ServiceTag(string Text, PillTone Tone);

/// <summary>One change a row can make; the details pane shows these as radio buttons.</summary>
public sealed partial class RowOption : ObservableObject
{
    private readonly Action<RowOption> _picked;

    public RowOption(string featureId, ServiceStart target, string label, string explanation, Action<RowOption> picked)
    {
        FeatureId = featureId;
        Target = target;
        Label = label;
        Explanation = explanation;
        _picked = picked;
    }

    /// <summary>"services" or "services.repair" (the guard checks each with its own rule).</summary>
    public string FeatureId { get; }
    public ServiceStart Target { get; }
    public string Label { get; }
    public string Explanation { get; }

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _picked(this);
    }
}

/// <summary>
/// One service on the page: its facts, tags, verdict and the changes it may make. Ticking it adds the selected
/// option to the plan. Rows without options can't be ticked and say why.
/// </summary>
public sealed partial class ServiceRowViewModel : ObservableObject
{
    private readonly Action _changed;

    public ServiceRowViewModel(ServiceInfo info, ServiceRepair? repair, Action changed)
    {
        Info = info;
        Repair = repair;
        _changed = changed;
        var options = new List<RowOption>();
        // A repair comes first: it puts back what Windows ships, and it's the only change offered for protected services.
        if (repair is not null)
            options.Add(new RowOption(ServicesIds.Repair, repair.Target, $"Put back Windows default ({ServiceText.Start(repair.Target)})",
                "Fixes what an old optimizer or \"disable these\" list switched off.", Pick));
        foreach (var o in info.Assessment.Options.Where(o => options.All(x => x.Target != o.Target)))
            options.Add(new RowOption(ServicesIds.Module, o.Target, o.Label, o.Explanation, Pick));
        Options = options;
        if (options.Count > 0) options[0].IsSelected = true;
        Tags = BuildTags(info, repair);
        Verdict = VerdictPill(info.Assessment.Verdict, repair is not null);
        Reasons = repair is null ? info.Assessment.Reasons
            : new[] { repair.Why }.Concat(info.Assessment.Reasons.Where(x => !repair.Why.Contains(x, StringComparison.Ordinal))).ToList();
    }

    public ServiceInfo Info { get; }
    public ServiceRepair? Repair { get; }
    public IReadOnlyList<RowOption> Options { get; }
    public IReadOnlyList<ServiceTag> Tags { get; }
    public ServiceTag Verdict { get; }
    public IReadOnlyList<string> Reasons { get; }

    public string Title => Info.DisplayName;
    public string Name => Info.Name;
    public string WhatItDoes => Info.WhatItDoes;
    public string MetaLine => $"{Info.Name} · {Info.StartText} · {Info.StateText}" + (Info.Usage is { } u ? " · " + Sizes.Format(u.PrivateBytes) : "");
    public string Publisher => Info.Facts.Publisher ?? "Unknown publisher";
    public string Signature => Info.Facts.SignatureText;
    public string File => Info.BinaryPath ?? "Not known";
    public string UsageText => Info.Usage?.Text ?? (Info.Record.State == Sources.ServiceRunState.Running ? "Couldn't be measured" : "Not running");
    public string NeedsText => Info.Record.DependsOn.Count == 0 ? "Nothing else" : string.Join(", ", Info.Record.DependsOn.Select(d => d.StartsWith('+') ? d[1..] + " (group)" : d));
    public string NeededByText => Info.Dependents.Count == 0 ? "No other service" : string.Join(", ", Info.Dependents);
    public string? SourceText => Info.Entry?.Source;
    public bool HasSource => SourceText is not null;
    public string? BreaksText => string.IsNullOrWhiteSpace(Info.Entry?.BreaksIf) ? null : Info.Entry!.BreaksIf;
    public bool HasBreaks => BreaksText is not null;

    public bool CanChange => Options.Count > 0;
    /// <summary>Why nothing is offered (shown greyed under the row and in the details pane).</summary>
    public string? NotOfferedReason => CanChange ? null : Info.Assessment.NotOfferedReason ?? "Nothing to change.";
    public bool HasNotOfferedReason => NotOfferedReason is not null;
    public double TextOpacity => CanChange ? 1.0 : 0.72;
    public bool IsThirdParty => Info.IsThirdParty;
    public bool IsFlagged => Info.IsFlagged || Repair is not null;
    public bool IsProtected => Info.Assessment.Verdict == ServiceVerdictKind.Protected;
    public string AutomationName => CanChange ? $"Change {Title}" : $"{Title}: {NotOfferedReason}";

    public RowOption? SelectedOption => Options.FirstOrDefault(o => o.IsSelected) ?? Options.FirstOrDefault();

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value)
    {
        if (value && !CanChange)
        {
            IsChecked = false;
            return;
        }
        _changed();
    }

    /// <summary>Ticks or unticks without telling the page each time (bulk changes; the page updates once).</summary>
    public void SetCheckedQuietly(bool value)
    {
#pragma warning disable MVVMTK0034 // set the field so OnIsCheckedChanged doesn't run per row
        _isChecked = value && CanChange;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(IsChecked));
    }

    /// <summary>Selects one option (profiles pick the recommended one); false when the row doesn't offer it.</summary>
    public bool Select(string featureId, ServiceStart target)
    {
        var o = Options.FirstOrDefault(x => x.FeatureId == featureId && x.Target == target);
        if (o is null) return false;
        o.IsSelected = true;
        return true;
    }

    /// <summary>The plan item for the ticked option (null when not ticked or nothing is offered).</summary>
    public PlanItem? PlanItem()
    {
        if (!IsChecked || SelectedOption is not { } o) return null;
        return o.FeatureId == ServicesIds.Repair && Repair is not null
            ? ServiceRepairScanner.Row(Repair).Actions[0]
            : ServicePlanItems.SetStart(o.FeatureId, Info, o.Target);
    }

    private void Pick(RowOption picked)
    {
        foreach (var o in Options.Where(o => !ReferenceEquals(o, picked))) o.IsSelected = false;
        OnPropertyChanged(nameof(SelectedOption));
        if (IsChecked) _changed();
    }

    private static IReadOnlyList<ServiceTag> BuildTags(ServiceInfo s, ServiceRepair? repair)
    {
        var tags = new List<ServiceTag>();
        if (repair is not null) tags.Add(new ServiceTag("Switched off: needs repair", PillTone.Caution));
        if (s.IsDriver) tags.Add(new ServiceTag("Driver", PillTone.Neutral));
        else tags.Add(s.IsThirdParty ? new ServiceTag(s.Facts.Publisher ?? "Other company", PillTone.Neutral) : new ServiceTag("Microsoft", PillTone.Neutral));
        if (s.Flags.HasFlag(ServiceFlags.Unsigned)) tags.Add(new ServiceTag("Not signed", PillTone.Critical));
        if (s.Flags.HasFlag(ServiceFlags.Leftover)) tags.Add(new ServiceTag("File missing", PillTone.Caution));
        if (s.Flags.HasFlag(ServiceFlags.SuspiciousPath)) tags.Add(new ServiceTag("Unusual folder", PillTone.Critical));
        if (s.Flags.HasFlag(ServiceFlags.AutoStartNotRunning)) tags.Add(new ServiceTag("Set to start, not running", PillTone.Caution));
        if (s.Usage is { SharedWith: > 0 } u) tags.Add(new ServiceTag($"Shared with {u.SharedWith}", PillTone.Neutral));
        return tags;
    }

    private static ServiceTag VerdictPill(ServiceVerdictKind v, bool repair) => v switch
    {
        _ when repair => new ServiceTag("Repair", PillTone.Caution),
        ServiceVerdictKind.Proven => new ServiceTag("Proven", PillTone.Success),
        ServiceVerdictKind.Situational => new ServiceTag("Situational", PillTone.Caution),
        ServiceVerdictKind.Placebo => new ServiceTag("Doesn't help", PillTone.Neutral),
        ServiceVerdictKind.Harmful => new ServiceTag("Don't change", PillTone.Critical),
        ServiceVerdictKind.Protected => new ServiceTag("Protected", PillTone.Info),
        ServiceVerdictKind.ThirdParty => new ServiceTag("Manual only", PillTone.Neutral),
        ServiceVerdictKind.Driver => new ServiceTag("Driver", PillTone.Neutral),
        _ => new ServiceTag("Windows", PillTone.Neutral),
    };
}
