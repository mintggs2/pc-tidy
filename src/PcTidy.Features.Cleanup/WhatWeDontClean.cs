namespace PcTidy.Features.Cleanup;

/// <summary>The Clean page's "What we don't clean, and why" help (Plan §7, "Deliberately not offered").</summary>
public static class WhatWeDontClean
{
    public sealed record Entry(string Title, string Why);

    public const string Heading = "What we don't clean, and why";

    public static readonly IReadOnlyList<Entry> Entries = new Entry[]
    {
        new("Prefetch", "Windows uses it to start your apps faster and trims it itself. Cleaning it makes launches slower."),
        new("Event logs", "They're how problems get diagnosed, and Windows already caps their size."),
        new(@"The Windows\Installer folder","Apps need these files to update, repair and uninstall. Removing them breaks uninstalls."),
        new("Driver store", "It holds the drivers your devices use. Old drivers are handled carefully in Drivers instead."),
        new("Pagefile and hibernation file", "Windows needs them. See Big hidden Windows files for the safe options."),
        new("Wipe free space and shredding", "It wears out SSDs and gives no real guarantee on them."),
        new("MFT and USN journal", "Clearing them makes backup, antivirus, search and sync tools rescan everything."),
        new("Browser passwords and site data", "Site data holds your logins, and losing Local State makes every saved password unreadable."),
        new("The whole Downloads folder", "It's your files, not junk. Only old installers are offered, one by one."),
        new("Registry \"junk\"", "Microsoft doesn't support registry cleaners. They free almost nothing and can break apps."),
        // Community tweaks §9.6 correction: Storage Sense comes on by itself when space runs low (Policy CSP Storage), and
        // its Downloads option deletes files outright. PC Tidy can't read that per-user option, so it explains where to check.
        new("Windows' own Storage Sense", "It switches itself on when the drive runs low and removes temporary files, which is fine. "
            + "But if \"Delete files in my Downloads folder\" is set to a number of days, it deletes those files for good, not to the Recycle Bin. "
            + "Check it in Settings → System → Storage → Storage Sense."),
    };

    /// <summary>Plain text for the help page.</summary>
    public static string AsText()
        => Heading + Environment.NewLine + string.Join(Environment.NewLine, Entries.Select(e => $"• {e.Title}: {e.Why}"));
}
