using PcTidy.Features.Tweaks.Reading;

namespace PcTidy.Features.Tweaks.Tweaks;

/// <summary>Three-state view (census: Available / Already applied / Differs) plus the cases where nothing can be offered.</summary>
public enum TweakStatus
{
    /// <summary>Windows default (or unset): the tweak can be applied.</summary>
    Available,
    /// <summary>Already set the way the tweak sets it.</summary>
    Applied,
    /// <summary>Set to something else (by the user or another tool): applying changes it to the tweak's value.</summary>
    Differs,
    /// <summary>The value has an unexpected type: left alone.</summary>
    Blocked,
    /// <summary>The thing to change isn't on this PC (app or service not installed).</summary>
    NotPresent,
    /// <summary>Couldn't be read (e.g. a Windows feature query failed).</summary>
    Unknown,
    /// <summary>Guidance or placebo row: nothing to read.</summary>
    Information,
}

/// <summary>The current state of one change, plus the data PC Tidy would write.</summary>
public sealed record ChangeState(TweakChange Change, TweakStatus Status, string Now, string? Problem = null, string? PlannedData = null);

public sealed record TweakState(TweakStatus Status, IReadOnlyList<ChangeState> Changes, string? Problem)
{
    /// <summary>Changes still to make (applied ones are skipped so the plan matches what will really happen).</summary>
    public IEnumerable<ChangeState> Pending => Changes.Where(c => c.Status is TweakStatus.Available or TweakStatus.Differs);
}

/// <summary>Reads the live state of each change through the (possibly redirected) environment. Read-only.</summary>
public static class TweakStateReader
{
    public static TweakState Read(TweakDefinition tweak, TweakEnvironment env)
    {
        var states = tweak.Changes.Select(c => ReadChange(c, env)).ToList();
        var actionable = states.Where(s => s.Change is RegistryValueChange or RegistryTokenChange or ServiceStartChange or AppxRemoveChange or OptionalFeatureChange).ToList();
        if (actionable.Count == 0) return new TweakState(TweakStatus.Information, states, null);
        if (actionable.FirstOrDefault(s => s.Status == TweakStatus.Blocked) is { } blocked) return new TweakState(TweakStatus.Blocked, states, blocked.Problem);
        if (actionable.Any(s => s.Status == TweakStatus.NotPresent)) return new TweakState(TweakStatus.NotPresent, states, null);
        if (actionable.Any(s => s.Status == TweakStatus.Unknown)) return new TweakState(TweakStatus.Unknown, states, null);
        if (actionable.All(s => s.Status == TweakStatus.Applied)) return new TweakState(TweakStatus.Applied, states, null);
        if (actionable.Any(s => s.Status == TweakStatus.Differs)) return new TweakState(TweakStatus.Differs, states, null);
        return new TweakState(TweakStatus.Available, states, null);
    }

    public static ChangeState ReadChange(TweakChange change, TweakEnvironment env) => change switch
    {
        RegistryValueChange r => ReadValue(r, env.Registry),
        RegistryTokenChange t => ReadToken(t, env.Registry),
        ServiceStartChange s => ReadService(s, env.Registry),
        AppxRemoveChange a => env.Apps.FindPackageFullName(a.PackageName) is { } full
            ? new ChangeState(a, TweakStatus.Available, "Installed", PlannedData: full)
            : new ChangeState(a, TweakStatus.NotPresent, "Not installed"),
        OptionalFeatureChange f => env.Probe.OptionalFeatureState(f.FeatureName) switch
        {
            1 => new ChangeState(f, TweakStatus.Available, "On"),
            2 or 3 => new ChangeState(f, TweakStatus.Applied, "Off"),
            _ => new ChangeState(f, TweakStatus.Unknown, "Couldn't be checked"),
        },
        _ => new ChangeState(change, TweakStatus.Information, ""),
    };

    private static ChangeState ReadValue(RegistryValueChange r, IRegistryReader registry)
    {
        var now = registry.Read(r.Hive, r.KeyPath, r.ValueName);
        if (now is null)
        {
            var effectiveDefault = r.WindowsDefault;
            var applied = effectiveDefault is not null && RegData.SameValue(r.Kind, effectiveDefault, r.Data);
            var text = effectiveDefault is null ? "Not set" : $"Not set (Windows uses {RegData.Show(r.Kind, effectiveDefault)})";
            return new ChangeState(r, applied ? TweakStatus.Applied : TweakStatus.Available, text, PlannedData: r.Data);
        }
        if (now.Kind != r.Kind && !(IsText(now.Kind) && IsText(r.Kind)))
            return new ChangeState(r, TweakStatus.Blocked, $"{RegData.Show(now.Kind, now.Data)} ({RegData.KindName(now.Kind)})",
                $"This setting on your PC is stored as {RegData.KindName(now.Kind)} instead of {RegData.KindName(r.Kind)}, so PC Tidy leaves it alone.");
        var shown = RegData.Show(now.Kind, now.Data);
        if (RegData.SameValue(r.Kind, now.Data, r.Data)) return new ChangeState(r, TweakStatus.Applied, shown, PlannedData: r.Data);
        if (r.WindowsDefault is { } d && RegData.SameValue(r.Kind, now.Data, d)) return new ChangeState(r, TweakStatus.Available, shown, PlannedData: r.Data);
        return new ChangeState(r, TweakStatus.Differs, shown, PlannedData: r.Data);
    }

    private static bool IsText(RegistryKind? k) => k is RegistryKind.String or RegistryKind.ExpandString;

    private static ChangeState ReadToken(RegistryTokenChange t, IRegistryReader registry)
    {
        var now = registry.Read(t.Hive, t.KeyPath, t.ValueName);
        if (now is not null && !IsText(now.Kind))
            return new ChangeState(t, TweakStatus.Blocked, RegData.KindName(now.Kind),
                $"This setting on your PC is stored as {RegData.KindName(now.Kind)} instead of text, so PC Tidy leaves it alone.");
        var current = now?.Data ?? "";
        var tokens = TokenList.Parse(current);
        var has = tokens.FirstOrDefault(x => x.Key.Equals(t.Token, StringComparison.OrdinalIgnoreCase));
        var shown = now is null ? "Not set" : $"{t.Token}={(has.Key is null ? "not set" : has.Value)}";
        if (has.Key is not null && has.Value == t.Value) return new ChangeState(t, TweakStatus.Applied, shown, PlannedData: current);
        return new ChangeState(t, TweakStatus.Available, shown, PlannedData: TokenList.Merge(current, t.Token, t.Value));
    }

    private static ChangeState ReadService(ServiceStartChange s, IRegistryReader registry)
    {
        var state = ServiceState.Read(registry, s.ServiceName);
        if (state is null) return new ChangeState(s, TweakStatus.NotPresent, "Not installed");
        var now = RegistryNames.Friendly(state.StartType);
        // Disabled is stricter than the Manual a tweak sets: already done (PC Tidy never switches it back on here).
        if (state.StartType == s.Target || (s.Target == ServiceStart.Manual && state.IsDisabled)) return new ChangeState(s, TweakStatus.Applied, now);
        return new ChangeState(s, state.StartType == s.WindowsDefault ? TweakStatus.Available : TweakStatus.Differs, now);
    }
}

/// <summary>"A=1;B=2;" lists (DirectXUserGlobalSettings). Merging keeps every other token and its order.</summary>
public static class TokenList
{
    public static IReadOnlyList<KeyValuePair<string, string>> Parse(string text)
        => text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                var eq = part.IndexOf('=');
                return eq < 0 ? new KeyValuePair<string, string>(part, "") : new KeyValuePair<string, string>(part[..eq].Trim(), part[(eq + 1)..].Trim());
            }).ToList();

    /// <summary>Sets <paramref name="token"/> to <paramref name="value"/>; every other entry is kept exactly as written.</summary>
    public static string Merge(string existing, string token, string value)
    {
        var parts = existing.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var replaced = false;
        for (var i = 0; i < parts.Count; i++)
        {
            var eq = parts[i].IndexOf('=');
            var key = (eq < 0 ? parts[i] : parts[i][..eq]).Trim();
            if (!key.Equals(token, StringComparison.OrdinalIgnoreCase)) continue;
            parts[i] = $"{token}={value}";
            replaced = true;
        }
        if (!replaced) parts.Add($"{token}={value}");
        return string.Concat(parts.Select(p => p + ";"));
    }
}
