using PcTidy.Features.Tweaks.BadTweaks;
using PcTidy.Features.Tweaks.Reading;
using PcTidy.Features.Tweaks.UI;

namespace PcTidy.Features.Tweaks;

/// <summary>Section 12 · Tweaks: proven tweaks with their live state, presets, Expert mode, and "Find bad tweaks".</summary>
public sealed class TweaksModule : IFeatureModule
{
    public const string ModuleId = "tweaks";

    /// <param name="environment">Readers for both scanners (tests pass redirected ones). Default: real PC, or the sandbox.</param>
    public TweaksModule(Func<ScanContext, TweakEnvironment>? environment = null)
    {
        TweaksScanner = new TweaksScanner(environment);
        BadTweakScanner = new BadTweakScanner(environment);
        Scanners = new IFeatureScanner[] { TweaksScanner, BadTweakScanner };
    }

    public string Id => ModuleId;
    public string Title => "Tweaks";
    public string Summary => "Windows settings that really help, with what each one does and its source. Find bad tweaks puts back what old optimizers broke. Everything can be undone.";
    public NavGroup Group => NavGroup.SpeedUp;
    public string Icon => "Wrench24";
    public int Order => 12;
    public Release Release => Release.V2;
    public IReadOnlyList<IFeatureScanner> Scanners { get; }

    public TweaksScanner TweaksScanner { get; }
    public BadTweakScanner BadTweakScanner { get; }

    public object? CreatePage(IServiceProvider services) => new TweaksPage { DataContext = new TweaksViewModel(services, this) };
}
