using PcTidy.Catalog.Insights;
using PcTidy.Features.Insights.Model;
using PcTidy.Features.Insights.Source;

namespace PcTidy.Features.Insights.Cards;

/// <summary>
/// Read-only cards about protections only the user can switch (Community tweaks §9.6): BitLocker and its recovery key,
/// and Smart App Control. PC Tidy never changes either; the cards explain and open the right Windows page.
/// </summary>
internal static class ProtectionCards
{
    // ------------------------------------------------------------------ BitLocker

    public static InsightCard BitLocker(CardContext c)
    {
        const string title = "BitLocker and its recovery key";
        const string icon = "LockClosed24";
        var s = c.Source.ReadSecurity();
        var drive = c.Locations.SystemDrive.Length >= 2 ? c.Locations.SystemDrive[..2] : "C:";
        const string keyAdvice = "If Windows ever asks for the BitLocker recovery key (after a BIOS update, a new motherboard or some boot changes), "
                                 + "you can only get back in with it. Microsoft can't recover a lost key for you.";
        var links = new[]
        {
            new InsightLink("Check my saved recovery keys", InsightsLinks.RecoveryKeys),
            new InsightLink("Open Device encryption", InsightsLinks.SettingsDeviceEncryption),
        };

        if (s.BitLocker == TriState.Yes)
            return new InsightCard
            {
                Id = InsightsIds.BitLocker, Category = InsightCategory.Safety, Title = title, Icon = icon, Status = InsightStatus.Info,
                Headline = $"BitLocker protects {drive}: make sure the recovery key is saved",
                Explanation = keyAdvice + " PC Tidy can't see where your key is kept, so check it once.",
                WhatToDo = "Sign in at the Microsoft link below with the account you use on this PC and check a key is listed for it. "
                           + "If none is, print or save one now: Start → type \"Manage BitLocker\" → Back up your recovery key.",
                Facts = new[] { new InsightFact("Windows drive", $"{drive} encrypted with BitLocker") },
                Links = links,
            };
        if (s.BitLocker == TriState.No)
            return new InsightCard
            {
                Id = InsightsIds.BitLocker, Category = InsightCategory.Safety, Title = title, Icon = icon, Status = InsightStatus.Info,
                Headline = $"{drive} isn't protected by BitLocker",
                Explanation = "Without drive encryption, anyone who takes the drive out (or the laptop) can read your files. "
                              + "That matters most on a laptop. " + keyAdvice,
                WhatToDo = c.IsLaptop
                    ? "Consider turning on Device encryption or BitLocker in Settings. Save the recovery key to your Microsoft account when Windows offers it."
                    : "Nothing you must do. On a desktop that stays at home, encryption is optional.",
                Facts = new[] { new InsightFact("Windows drive", $"{drive} not encrypted") },
                Links = links,
            };
        return new InsightCard
        {
            Id = InsightsIds.BitLocker, Category = InsightCategory.Safety, Title = title, Icon = icon,
            Status = s.BitLockerNeedsAdmin ? InsightStatus.NeedsAdmin : InsightStatus.Info,
            Headline = s.BitLockerNeedsAdmin ? "Only an administrator can read BitLocker's status" : "PC Tidy couldn't read BitLocker's status on this PC",
            Explanation = keyAdvice + " If your drive is encrypted (Settings → Privacy & security → Device encryption shows it), check the key is saved.",
            WhatToDo = "Open the Microsoft link below to see the recovery keys saved to your account, or run PC Tidy as administrator to see BitLocker's status here.",
            Links = links,
        };
    }

    // ------------------------------------------------------------------ Smart App Control

    public static InsightCard SmartAppControl(CardContext c)
    {
        const string title = "Smart App Control";
        const string icon = "ShieldTask24";
        var state = c.Source.ReadSecurity().SmartAppControl;
        const string oneWay = "Once Smart App Control is turned off, Settings can't turn it back on: normally only resetting or reinstalling Windows brings it back.";
        var links = new[] { new InsightLink("Open App & browser control (Windows Security)", InsightsLinks.SettingsWindowsSecurity) };

        if (c.WindowsBuild is > 0 and < 22621 || state is null)
            return InsightCard.NotAvailable(InsightsIds.SmartAppControl, InsightCategory.Safety, title, icon,
                "Smart App Control is part of Windows 11 (22H2 and later) and isn't on this PC.");

        return state switch
        {
            1 => new InsightCard
            {
                Id = InsightsIds.SmartAppControl, Category = InsightCategory.Safety, Title = title, Icon = icon, Status = InsightStatus.Good,
                Headline = "Smart App Control is on: keep it on",
                Explanation = "It blocks apps that are unsigned and unknown to Microsoft before they can run. " + oneWay
                              + " If it blocks an app you trust, look for a signed version of that app rather than turning it off.",
                WhatToDo = "Nothing to do. Don't turn it off to make an \"optimizer\" or game tweak run.",
                Links = links,
            },
            2 => new InsightCard
            {
                Id = InsightsIds.SmartAppControl, Category = InsightCategory.Safety, Title = title, Icon = icon, Status = InsightStatus.Info,
                Headline = "Smart App Control is checking how you use this PC",
                Explanation = "In evaluation mode Windows watches for a while, then switches Smart App Control on by itself if it won't get in your way, or off if it would. "
                              + oneWay,
                WhatToDo = "Nothing to do: let Windows decide. Avoid switching it off by hand.",
                Links = links,
            },
            _ => new InsightCard
            {
                Id = InsightsIds.SmartAppControl, Category = InsightCategory.Safety, Title = title, Icon = icon, Status = InsightStatus.Info,
                Headline = "Smart App Control is off",
                Explanation = "It blocks unsigned, unknown apps before they run. " + oneWay + " Your antivirus still protects you without it.",
                WhatToDo = "Nothing to change now. On a future reset or reinstall of Windows, leave it on.",
                Links = links,
            },
        };
    }
}
