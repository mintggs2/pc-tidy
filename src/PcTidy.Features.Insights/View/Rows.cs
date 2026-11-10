using CommunityToolkit.Mvvm.ComponentModel;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Scripts;
using PcTidy.UI.Controls;

namespace PcTidy.Features.Insights.View;

/// <summary>A tickable row inside a card. Never ticked for the user; greyed rows say why.</summary>
public sealed partial class InsightRow : ObservableObject
{
    public InsightRow(ScanItem item) => Item = item;

    public ScanItem Item { get; }
    public string Title => Item.Title;
    public string SecondLine => Item.DisabledReason ?? Item.Reason;
    public string Size => Item.SizeBytes > 0 ? Sizes.Format(Item.SizeBytes) : "";
    public bool IsSelectable => Item.DisabledReason is null && Item.Actions.Count > 0;
    public string AutomationName => $"{Item.Title}, {Size}, {SecondLine}";

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value)
    {
        if (value && !IsSelectable) IsChecked = false;
    }
}

/// <summary>One change from "Scan this script", shown as text (the evidence line is never run).</summary>
public sealed class FindingRow
{
    public FindingRow(ScriptFinding f) => Finding = f;

    public ScriptFinding Finding { get; }
    public string What => Finding.What;
    public string Why => Finding.Why;
    public string Evidence => Finding.Evidence;
    public string LineText => $"line {Finding.Line}";
    public string VerdictText => Finding.RedFlag && Finding.Verdict != ScriptVerdict.Harmful ? "Red flag" : Label(Finding.Verdict);
    public PillTone Tone => Finding.RedFlag ? PillTone.Critical : ToneOf(Finding.Verdict);

    public static string Label(ScriptVerdict v) => v switch
    {
        ScriptVerdict.Proven => "Proven",
        ScriptVerdict.Situational => "Situational",
        ScriptVerdict.Placebo => "Placebo",
        ScriptVerdict.Harmful => "Harmful",
        _ => "Unknown",
    };

    public static PillTone ToneOf(ScriptVerdict v) => v switch
    {
        ScriptVerdict.Proven => PillTone.Success,
        ScriptVerdict.Situational => PillTone.Info,
        ScriptVerdict.Placebo => PillTone.Neutral,
        ScriptVerdict.Harmful => PillTone.Critical,
        _ => PillTone.Caution,
    };
}
