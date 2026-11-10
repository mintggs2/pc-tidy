namespace PcTidy.Features.Organize.Rules;

/// <summary>
/// The rules a new user starts with (Plan §9): the four starter custom rules first (so they win over
/// the broad categories), then one rule per category, then "Other" which leaves everything else in place.
/// Every destination is relative to the folder being sorted: no drive letters, works on any PC.
/// </summary>
public static class DefaultRules
{
    public const string InvoicesId = "starter.invoices";
    public const string ScreenshotsId = "starter.screenshots";
    public const string OldInstallersId = "starter.old-installers";
    public const string TorrentsId = "starter.torrents";
    public const string OtherId = "category.other";

    public static string CategoryRuleId(string categoryId) => "category." + categoryId.ToLowerInvariant();

    public static List<OrganizeRule> Create()
    {
        var rules = new List<OrganizeRule>
        {
            new()
            {
                Id = InvoicesId, Name = "Invoices, receipts and statements", Icon = "DocumentPdf24",
                Conditions = ConditionGroup.AllOf(RuleCondition.Ext("pdf"), RuleCondition.NameHas("invoice", "receipt", "statement")),
                Action = Move(@"Documents\Invoices"),
            },
            new()
            {
                // Name patterns alone would also catch "Snipping Tool setup.exe", so screenshots must be pictures too.
                Id = ScreenshotsId, Name = "Screenshots", Icon = "Screenshot24",
                Conditions = ConditionGroup.AllOf(RuleCondition.Glob("Screenshot*;Screen Shot*;Snip*"), RuleCondition.InCategory(OrganizeCategories.Pictures)),
                Action = Move(@"Pictures\Screenshots"),
            },
            new()
            {
                Id = OldInstallersId, Name = "Installers older than 30 days", Icon = "ArrowDownload24",
                Conditions = ConditionGroup.AllOf(RuleCondition.InCategory(OrganizeCategories.Installers), RuleCondition.OlderThan(30, DateField.Added)),
                Action = new RuleAction { Kind = RuleActionKind.Quarantine },
            },
            new()
            {
                Id = TorrentsId, Name = "Torrent files", Icon = "ArrowDownload24",
                Conditions = ConditionGroup.AllOf(RuleCondition.Ext("torrent")),
                Action = Move(@"Archives\Torrents"),
            },
        };

        foreach (var cat in OrganizeCategories.All)
        {
            rules.Add(new OrganizeRule
            {
                Id = CategoryRuleId(cat.Id),
                Name = cat.Id,
                IsBuiltIn = true,
                Icon = cat.Icon,
                Conditions = cat.Id == OrganizeCategories.Installers
                    ? ConditionGroup.AnyOf(RuleCondition.Ext(cat.Extensions.Order().ToArray()), RuleCondition.IsInstallerExe())
                    : ConditionGroup.AllOf(RuleCondition.Ext(cat.Extensions.Order().ToArray())),
                Action = Move(cat.FolderName),
            });
        }

        rules.Add(new OrganizeRule
        {
            Id = OtherId, Name = "Other", IsBuiltIn = true, Icon = "DocumentQuestionMark24",
            // Matches everything left over, so nothing reaches "no rule matched" by accident.
            Conditions = ConditionGroup.AllOf(RuleCondition.Glob("*")),
            Action = new RuleAction { Kind = RuleActionKind.LeaveInPlace },
        });
        return rules;
    }

    private static RuleAction Move(string destination) => new() { Kind = RuleActionKind.MoveToFolder, Destination = destination };
}
