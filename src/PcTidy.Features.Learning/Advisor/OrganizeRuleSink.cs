using PcTidy.Core;
using PcTidy.Features.Learning.Learners;
using PcTidy.Features.Organize;
using PcTidy.Features.Organize.Rules;

namespace PcTidy.Features.Learning.Advisor;

/// <summary>Outcome of handing a suggested rule to Organize.</summary>
public sealed record RuleHandOff(bool Added, string Message, OrganizeRule? Rule);

/// <summary>
/// Where "Make it a rule" sends a suggestion. The shell may register its own implementation (for example one that opens
/// Organize's rule editor pre-filled); otherwise <see cref="OrganizeSettingsRuleSink"/> is used.
/// </summary>
public interface IOrganizeRuleSink
{
    RuleHandOff Add(SuggestedRule suggestion);
}

/// <summary>
/// Default hand-off: adds the rule at the top of the user's Organize rules in the in-memory settings, through
/// Organize's own public settings API (<see cref="OrganizeSettings.Load"/> / <see cref="OrganizeSettings.Store"/>),
/// the same way Organize's watch mode records its ledger. Nothing is written to disk here: the shell saves settings as
/// usual. The rule is never automatic; it runs only from Organize's preview and confirm. An identical rule already in
/// the list is not added twice.
/// </summary>
public sealed class OrganizeSettingsRuleSink : IOrganizeRuleSink
{
    private readonly AppSettings _settings;

    public OrganizeSettingsRuleSink(AppSettings settings) => _settings = settings;

    /// <summary>Raised after a rule was added (the Organize page can reload its rules).</summary>
    public static event EventHandler<OrganizeRule>? RuleAdded;

    public RuleHandOff Add(SuggestedRule suggestion)
    {
        var rule = suggestion.CreateRule();
        lock (OrganizeLedger.SettingsLock)
        {
            var organize = OrganizeSettings.Load(_settings);
            if (organize.Rules.Any(r => SameRule(r, rule)))
                return new RuleHandOff(false, "Organize already has this rule.", null);
            // Top of the list: rules are checked top to bottom, and the built-in categories would otherwise win first.
            organize.Rules.Insert(0, rule);
            organize.Store(_settings);
        }
        RuleAdded?.Invoke(this, rule);
        return new RuleHandOff(true, $"\"{rule.Name}\" is now in Organize's rules. It only runs when you preview and confirm.", rule);
    }

    private static bool SameRule(OrganizeRule a, OrganizeRule b)
        => a.Action.Kind == b.Action.Kind
           && string.Equals(a.Action.Destination?.TrimEnd('\\'), b.Action.Destination?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
           && Signature(a.Conditions) == Signature(b.Conditions);

    private static string Signature(ConditionGroup g)
        => g.Mode + "(" + string.Join(";", g.Conditions.Select(c =>
               $"{c.Kind}:{c.Negate}:{string.Join(",", c.Values.Select(v => v.ToLowerInvariant()).Order())}:{c.Text?.ToLowerInvariant()}:{c.MinBytes}:{c.MaxBytes}").Order())
           + string.Join("", g.Groups.Select(Signature)) + ")";
}
