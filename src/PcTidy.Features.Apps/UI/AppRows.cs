using CommunityToolkit.Mvvm.ComponentModel;
using PcTidy.Catalog.Apps;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Apps.Inventory;
using PcTidy.Features.Apps.Leftovers;
using PcTidy.UI.Controls;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace PcTidy.Features.Apps.UI;

/// <summary>One app in the list.</summary>
public sealed partial class AppRow : ObservableObject
{
    public AppRow(InstalledApp app, DateTime utcNow)
    {
        App = app;
        LastUsedText = AppFacts.LastUsedText(app, utcNow);
        InstallDateText = AppFacts.InstallDateText(app);
        Tag = app.IsOrphan ? "Ghost entry"
            : app.ManagedBy ?? (app.Preinstalled is { } p ? (p.Tier == PreinstalledTier.Never ? "Part of Windows" : "Came with Windows") : null);
    }

    public InstalledApp App { get; }
    public string Name => App.Name;
    public string Subtitle => App.Publisher ?? AppFacts.SourceText(App);
    public string? Tag { get; }
    public bool HasTag => Tag is not null;
    public PillTone TagTone => App.IsOrphan ? PillTone.Caution : App.IsGame ? PillTone.Info : PillTone.Neutral;
    public string LastUsedText { get; }
    public string InstallDateText { get; }

    public SymbolRegular Icon => App.Source switch
    {
        AppSource.Store => SymbolRegular.StoreMicrosoft24,
        AppSource.Steam or AppSource.Epic => SymbolRegular.Games24,
        _ => SymbolRegular.WindowApps24,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeBytes), nameof(SizeText), nameof(SizeCaption))]
    private long? _measuredBytes;

    /// <summary>Measured size when known, else what the app reported; -1 = unknown (sorted last).</summary>
    public long SizeBytes => MeasuredBytes ?? App.ReportedSizeBytes ?? -1;
    public string SizeText => SizeBytes >= 0 ? Sizes.Format(SizeBytes) : "—";
    public string SizeCaption => MeasuredBytes is not null ? "measured" : App.ReportedSizeBytes is not null ? "reported" : "size unknown";

    public string AccessibleName => $"{Name}, {SizeText} {SizeCaption}, last used {LastUsedText}";
}

/// <summary>One fact in the details panel ("Last used: 3 days ago").</summary>
public sealed record FactRow(string Label, string Value);

/// <summary>One leftover with its own tick box.</summary>
public sealed partial class LeftoverRow : ObservableObject
{
    public LeftoverRow(ScanItem item, LeftoverConfidence confidence)
    {
        Item = item;
        Confidence = confidence;
        _isSelected = item.DefaultSelected && item.DisabledReason is null;
    }

    public ScanItem Item { get; }
    public LeftoverConfidence Confidence { get; }
    public string Title => Item.Title;
    public string? Detail => Item.Detail;
    public string Reason => Item.Reason;
    public string? DisabledReason => Item.DisabledReason;
    public bool IsEnabled => Item.DisabledReason is null;
    public string SizeText => Item.SizeBytes > 0 ? Sizes.Format(Item.SizeBytes) : "—";
    public string ConfidenceText => LeftoverItems.Label(Confidence);
    public PillTone ConfidenceTone => Confidence switch
    {
        LeftoverConfidence.VeryGood => PillTone.Success,
        LeftoverConfidence.Good => PillTone.Accent,
        LeftoverConfidence.Questionable => PillTone.Caution,
        _ => PillTone.Critical,
    };
    public string AccessibleName => $"{Title}, {ConfidenceText} match, {SizeText}";

    [ObservableProperty]
    private bool _isSelected;
}
