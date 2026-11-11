using System.Text.RegularExpressions;
using PcTidy.Catalog.Insights;
using PcTidy.Features.Insights.Model;

namespace PcTidy.Features.Insights.Cards;

/// <summary>Cards about what has been done to Windows: "Scan this script", modified Windows, the hosts file.</summary>
internal static partial class SafetyCards
{
    public static InsightCard ScriptScan() => new()
    {
        Id = InsightsIds.ScriptScan, Category = InsightCategory.Safety, Title = "Scan this script", Icon = "DocumentSearch24",
        Status = InsightStatus.Info,
        Headline = "Check a downloaded .ps1, .bat, .cmd, .reg, .vbs or .js before you run it",
        Explanation = "PC Tidy reads the file as text and lists every registry, service, boot, network, Defender and download change it would make, "
                      + "with the research verdict for each. It never runs the file. \"FPS boost\" and \"optimizer\" packs are a known way malware spreads.",
        WhatToDo = "Pick the file. If the result says \"Don't run this\", delete it.",
    };

    // ------------------------------------------------------------------ modified Windows

    public static InsightCard ModifiedWindows(CardContext c)
    {
        var id = c.Identity;
        var signs = new List<(string Build, string Evidence)>();
        foreach (var m in ModifiedWindowsMarkers.Names)
        {
            switch (m.Kind)
            {
                case MarkerKind.OemText:
                    foreach (var (k, v) in id.OemInformation)
                        if (ContainsWord(v, m.Value)) signs.Add((m.Build, $"OEM information {k} says \"{Clip(v)}\""));
                    break;
                case MarkerKind.RegisteredText:
                    foreach (var (label, v) in new[] { ("registered owner", id.RegisteredOwner), ("registered organisation", id.RegisteredOrganization), ("product name", id.ProductName) })
                        if (ContainsWord(v, m.Value)) signs.Add((m.Build, $"The {label} is \"{Clip(v)}\""));
                    break;
                case MarkerKind.WindowsFolder when id.PresentWindowsFolders.Contains(m.Value):
                    signs.Add((m.Build, $@"The folder Windows\{m.Value} exists"));
                    break;
                case MarkerKind.ProgramFilesFolder when id.PresentProgramFilesFolders.Contains(m.Value):
                    signs.Add((m.Build, $@"The folder Program Files\{m.Value} exists"));
                    break;
            }
        }
        var missing = id.MissingCoreServices
            .Select(s => ModifiedWindowsMarkers.CoreServices.FirstOrDefault(x => x.Service.Equals(s, StringComparison.OrdinalIgnoreCase)))
            .Where(x => x.Service is not null).ToList();

        var facts = new List<InsightFact>();
        if (id.ProductName.Length > 0) facts.Add(new("Windows", $"{id.ProductName} (build {id.Build}{(id.Ubr > 0 ? "." + id.Ubr : "")})"));
        facts.AddRange(signs.Select(s => new InsightFact("Sign", s.Evidence)).DistinctBy(f => f.Value));
        facts.AddRange(missing.Select(m => new InsightFact("Missing", $"{m.What} ({m.Service}) isn't installed")));

        const string implications =
            "Playbooks and custom images (Atlas, ReviOS, Tiny11 and similar) remove or change parts of Windows. Feature updates can fail or put things back, "
            + "Microsoft and your PC maker can't support it, and the only full undo is reinstalling Windows. Some PC Tidy fixes may not behave as expected here.";

        if (signs.Count == 0 && missing.Count == 0)
            return new InsightCard
            {
                Id = InsightsIds.ModifiedWindows, Category = InsightCategory.Safety, Title = "Modified Windows", Icon = "WindowShield24",
                Status = InsightStatus.Good, Headline = "No signs of a modified Windows", IsHeuristic = true, Facts = facts,
                Explanation = "PC Tidy looked for the names Atlas, ReviOS, AME, Tiny11 and Nano11 leave behind and for core Windows parts that stripped builds remove. None were found. This is a best guess: these tools don't document their markers.",
                WhatToDo = "Nothing to do.",
            };

        var builds = signs.Select(s => s.Build).Distinct().ToList();
        var named = builds.Count > 0 ? string.Join(" or ", builds) : null;
        return new InsightCard
        {
            Id = InsightsIds.ModifiedWindows, Category = InsightCategory.Safety, Title = "Modified Windows", Icon = "WindowShield24",
            Status = InsightStatus.Attention, IsHeuristic = true, Facts = facts,
            Headline = named is not null ? $"This looks like {named}" : $"Parts of Windows are missing ({Words.Plural(missing.Count, "core service", "core services")})",
            Explanation = implications + " Measured speed gains from these builds were large only on an old processor and none on modern ones (numbers below, from other PCs)."
                          + " This is a best guess from the signs listed: PC Tidy can't be certain.",
            Measured = new[] { MeasuredNumbers.AtlasOldCpu, MeasuredNumbers.AtlasNewCpu, MeasuredNumbers.Nano11 },
            WhatToDo = missing.Count > 0
                ? "If updates or security keep failing, back up your files and do a clean Windows install from Microsoft's own media. PC Tidy won't try to rebuild missing parts."
                : "Nothing to change. If something breaks after an update, the tool's own docs or a clean Windows install are the way back.",
        };
    }

    private static bool ContainsWord(string text, string word)
        => text.Length > 0 && Regex.IsMatch(text, $@"(?<![A-Za-z0-9]){Regex.Escape(word)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase);

    private static string Clip(string s) => s.Length <= 50 ? s : s[..47] + "…";

    // ------------------------------------------------------------------ hosts file

    public static InsightCard Hosts(CardContext c)
    {
        var path = Path.Combine(c.Locations.WindowsDir, "System32", "drivers", "etc", "hosts");
        string[] lines;
        try
        {
            if (!File.Exists(path))
                return InsightCard.NotAvailable(InsightsIds.Hosts, InsightCategory.Safety, "Hosts file", "Globe24", "There's no hosts file on this PC (Windows works fine without one).");
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return InsightCard.NotAvailable(InsightsIds.Hosts, InsightCategory.Safety, "Hosts file", "Globe24", "PC Tidy couldn't read the hosts file.");
        }

        var critical = new List<(string Line, MicrosoftService Service)>();
        var blocks = new List<string>();
        var redirects = new List<string>();
        foreach (var raw in lines)
        {
            var hash = raw.IndexOf('#');
            var content = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (content.Length == 0) continue;
            var parts = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            // Windows' own commented examples are skipped above; "localhost" lines are harmless defaults.
            if (parts.Skip(1).All(h => h.Equals("localhost", StringComparison.OrdinalIgnoreCase))) continue;
            bool sink = MicrosoftHosts.SinkAddresses.Contains(parts[0]);
            var service = parts.Skip(1).Select(MicrosoftHosts.ServiceOf).FirstOrDefault(s => s is not null);
            if (sink && service is { } svc) critical.Add((content, svc));
            else if (sink) blocks.Add(content);
            else redirects.Add(content);
        }

        var facts = new List<InsightFact>();
        facts.AddRange(critical.Take(8).Select(x => new InsightFact("Blocks " + MicrosoftHosts.Describe(x.Service), x.Line)));
        facts.AddRange(blocks.Take(5).Select(l => new InsightFact("Blocks", l)));
        facts.AddRange(redirects.Take(5).Select(l => new InsightFact("Points to", l)));
        int total = critical.Count + blocks.Count + redirects.Count;
        if (total > facts.Count) facts.Add(new InsightFact("And", Words.Plural(total - facts.Count, "more line", "more lines")));

        if (total == 0)
            return new InsightCard
            {
                Id = InsightsIds.Hosts, Category = InsightCategory.Safety, Title = "Hosts file", Icon = "Globe24", Status = InsightStatus.Good,
                Headline = "The hosts file has no extra entries",
                Explanation = "The hosts file lets a PC send a web address somewhere else. Windows ships it empty apart from comments, and that's how it is here.",
                WhatToDo = "Nothing to do.",
            };

        var services = critical.Select(x => MicrosoftHosts.Describe(x.Service)).Distinct().ToList();
        return new InsightCard
        {
            Id = InsightsIds.Hosts, Category = InsightCategory.Safety, Title = "Hosts file", Icon = "Globe24",
            Status = critical.Count > 0 ? InsightStatus.Attention : InsightStatus.Info, Facts = facts,
            Headline = critical.Count > 0
                ? $"{Words.Plural(critical.Count, "line blocks", "lines block")} {string.Join(", ", services)}"
                : $"{Words.Plural(total, "extra entry", "extra entries")}, none blocking Windows",
            Explanation = critical.Count > 0
                ? "Privacy tools and \"debloat\" scripts add lines that send Microsoft's servers nowhere. Blocking these breaks updates, the Store, activation or sign-in. Other entries are listed so you know they're there (apps like Docker add their own)."
                : "These entries were added by an app or by hand. None of them blocks a server Windows needs.",
            WhatToDo = critical.Count > 0
                ? "Open Tweaks → Find bad tweaks: it backs up the hosts file and comments out only the lines that block Windows."
                : "Nothing to do unless you don't recognise an entry.",
            Navigate = critical.Count > 0 ? new InsightNavigation("Open Tweaks", InsightsIds.TweaksModule) : null,
        };
    }
}
