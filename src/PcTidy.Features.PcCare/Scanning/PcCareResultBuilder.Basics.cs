using System.Globalization;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Planning;
using T = PcTidy.Features.PcCare.Scanning.PcCareTabs;

namespace PcTidy.Features.PcCare.Scanning;

/// <summary>App updates, Windows repair, Store fix and network tabs.</summary>
public static partial class PcCareResultBuilder
{
    /// <summary>The Microsoft Store's package family (the same on every PC).</summary>
    public const string StorePackageFamily = "Microsoft.WindowsStore_8wekyb3d8bbwe";

    private static void AddUpdates(PcCareSnapshot s, List<ScanGroup> groups, Dictionary<string, string> notes)
    {
        var w = s.Winget;
        if (w is null)
        {
            notes[T.Updates] = "App updates weren't checked this time.";
            return;
        }
        switch (w.State)
        {
            case WingetState.NotInstalled:
                notes[T.Updates] = "winget (App Installer) isn't on this PC, so app updates can't be checked here. Get \"App Installer\" from the Microsoft Store to use this.";
                return;
            case WingetState.Failed:
                notes[T.Updates] = "winget couldn't list updates. If you've never used it, open Terminal and run \"winget upgrade\" once to read and accept its terms.";
                return;
            case WingetState.Unreadable:
                notes[T.Updates] = "PC Tidy couldn't read winget's list safely on this PC. Open Terminal and run \"winget upgrade\" to see and install updates there.";
                return;
        }

        var items = w.Upgrades.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).Select(u => Pick(
            u.Name,
            "Installs the newest version with the app's own installer. Close the app first; the installer may ask for admin.",
            Badge.Check,
            new[] { PcCareActions.WingetUpgrade(u.Id, u.Name, u.AvailableVersion) },
            detail: $"{u.InstalledVersion} → {u.AvailableVersion} · {u.Id}",
            state: u.AvailableVersion,
            details: new Dictionary<string, string> { ["Package id"] = u.Id, ["Source"] = u.Source, ["Installed"] = u.InstalledVersion, ["Available"] = u.AvailableVersion }))
            .ToList();
        groups.Add(Group(T.Updates, "apps", "App updates",
            "Updates found by winget (Windows Package Manager). Each update can't be undone from History; the app's own installer runs.", Badge.Check, items));

        var note = items.Count == 0 ? "Your apps from winget are up to date." : $"{Plural(items.Count, "update")} found. Tick the ones you want.";
        if (w.UnreadableRows > 0)
            note += $" {Plural(w.UnreadableRows, "more update")} couldn't be read safely: run \"winget upgrade\" in Terminal to see {(w.UnreadableRows == 1 ? "it" : "them")}.";
        notes[T.Updates] = note;
    }

    private static void AddRepair(PcCareSnapshot s, List<ScanGroup> groups, Dictionary<string, string> notes)
    {
        groups.Add(Group(T.Repair, "windows", "Windows repair",
            "Windows' own repair tools for damaged system files. Needs admin; a restore point is made first. You can keep using the PC.",
            Badge.Check, new[]
            {
                Pick("Repair Windows system files",
                    "First fixes Windows' own copy of its files (it may download from Windows Update), then uses it to repair the files in use.",
                    Badge.Check, new[] { PcCareActions.DismRestoreHealth(), PcCareActions.SfcScan() },
                    detail: "Windows' DISM and SFC tools · about 20 to 60 minutes · logs in Windows\\Logs\\DISM and Windows\\Logs\\CBS", state: "20-60 min"),
                Pick("Check system files only",
                    "Quicker: checks the files Windows is using and repairs them from its local copy.",
                    Badge.Check, new[] { PcCareActions.SfcScan() },
                    detail: "Windows' SFC tool · about 10 to 30 minutes · log in Windows\\Logs\\CBS\\CBS.log", state: "10-30 min"),
            }));

        var store = s.Store;
        if (store is null) return;
        if (!store.StoreInstalled)
        {
            notes[T.Repair] = "The Microsoft Store isn't installed on this PC, so the Store fixes are hidden.";
            return;
        }
        groups.Add(Group(T.Repair, "store", "Microsoft Store fix",
            "For a Store that won't open, download or update. Try them in this order.", Badge.Check, new[]
            {
                Pick("Reset the Store cache", "Runs Windows' wsreset. Clears the Store's cache only: your apps and purchases stay.", Badge.Check,
                    new[] { PcCareActions.StoreReset() }, detail: "Takes under a minute; the Store opens when it's done"),
                Pick("Re-register the Microsoft Store", "Registers the Store app again for your account, which fixes a Store that won't start.", Badge.Check,
                    new[] { PcCareActions.StoreReRegister(store.StorePackageFamily ?? StorePackageFamily, "the Microsoft Store") }),
                Pick("Open Windows' troubleshooters", "Opens Settings > Troubleshoot. Pick \"Windows Store Apps\" there.", Badge.Safe,
                    new[] { PcCareActions.Open(PcCareLinks.Troubleshoot, "Open Settings > Troubleshoot") }),
            }));
    }

    private static void AddNetwork(PcCareSnapshot s, List<ScanGroup> groups, Dictionary<string, string> notes)
    {
        var n = s.Network;
        string vpn = n?.HasVpnAdapter == true ? " A VPN adapter is installed on this PC: you may need to reinstall or reconnect the VPN afterwards." : "";
        groups.Add(Group(T.Network, "fixes", "Network fixes",
            "For pages that won't load or a connection that drops. Try the first one first.", Badge.Check, new[]
            {
                Pick("Clear the DNS cache", "Forgets saved website addresses so Windows looks them up fresh. Harmless; takes a second.",
                    Badge.Check, new[] { PcCareActions.FlushDns() }),
                Pick("Reset Winsock and TCP/IP",
                    "Needs a restart, and VPN or firewall apps may need reinstalling. Puts Windows' network settings back to defaults (static IP addresses are cleared). Needs admin." + vpn,
                    Badge.Check, new[] { PcCareActions.WinsockReset() }, detail: "Try this last · needs a restart", restart: true),
            }));

        if (n is null) return;
        var current = n.CurrentDnsServers.Count > 0 ? string.Join(", ", n.CurrentDnsServers) : "not reported";
        if (n.DnsTest is not { Count: > 0 } results)
        {
            notes[T.Network] = $"Your DNS servers: {current}. The DNS speed test sends about 10 lookups to each server and changes nothing.";
            return;
        }

        var rows = results.OrderBy(r => r.MedianMs ?? double.MaxValue).Select(r => Info(
            $"{r.Label} ({r.Server})",
            r.IsCurrent ? "The DNS server your PC uses now." : "A public DNS server, for comparison.",
            detail: $"Answered {r.Answered} of {r.Asked} lookups",
            state: r.MedianMs is { } ms ? ms.ToString("0", CultureInfo.CurrentCulture) + " ms" : "No answer")).ToList();
        groups.Add(Group(T.Network, "dns", "DNS speed test", "Median time to look up a website address (lower is faster). Read-only.", Badge.Safe, rows));
        notes[T.Network] = DnsSuggestion(results);
    }

    /// <summary>A suggestion only (PC Tidy never changes DNS): mention a public server when it's clearly faster.</summary>
    internal static string DnsSuggestion(IReadOnlyList<DnsServerResult> results)
    {
        var mine = results.Where(r => r.IsCurrent && r.MedianMs is not null).OrderBy(r => r.MedianMs).FirstOrDefault();
        var best = results.Where(r => !r.IsCurrent && r.MedianMs is not null).OrderBy(r => r.MedianMs).FirstOrDefault();
        if (mine is null)
            return best is null
                ? "No DNS server answered. Check that you're online, then try again."
                : "Your current DNS server didn't answer the test. If websites are slow to open, a public DNS server may help.";
        if (best is null || best.MedianMs >= mine.MedianMs * 0.8 || mine.MedianMs - best.MedianMs < 5)
            return $"Your DNS answers in about {mine.MedianMs:0} ms, which is fine. No change needed.";
        return $"Your DNS answers in about {mine.MedianMs:0} ms; {best.Label} answered in {best.MedianMs:0} ms. Switching is optional: "
               + "Settings > Network & internet > your connection > DNS server assignment. PC Tidy doesn't change it.";
    }
}
