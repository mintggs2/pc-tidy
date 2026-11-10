using System.Globalization;
using System.Text.RegularExpressions;
using PcTidy.Catalog.Drivers;
using PcTidy.Core.Model;
using PcTidy.Features.Drivers.Analysis;
using PcTidy.Features.Drivers.Model;
using PcTidy.Features.Drivers.Planning;

namespace PcTidy.Features.Drivers.Scanning;

/// <summary>The scan result plus a plain-English line per tab when part of it isn't available on this PC.</summary>
public sealed record DriverScanReport(ScanResult Result, IReadOnlyDictionary<string, string> TabNotes, DriverInventory Inventory)
{
    /// <summary>
    /// Tabs PC Tidy couldn't check on this PC (Windows didn't share the list). Their empty state must say
    /// "Not available on this PC", never a green "all good".
    /// </summary>
    public IReadOnlySet<string> UnavailableTabs { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Turns a <see cref="DriverInventory"/> into scan groups for the five tabs. Pure: no I/O, so every rule is testable
/// with fixtures. Information rows have no actions; every change row is Check, unticked and admin.
/// </summary>
public static partial class DriverResultBuilder
{
    private const string OptionalUpdatesPage = "ms-settings:windowsupdate-optionalupdates";

    [GeneratedRegex(@"^[A-Za-z0-9\-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UpdateIdPattern();

    public static DriverScanReport Build(DriverInventory inv, string dataRoot, DateTime nowUtc, DriverScanOptions options, TimeSpan duration = default)
    {
        var exportFolder = BackupFolders.ForDate(dataRoot, nowUtc.ToLocalTime());
        var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<ScanGroup>();

        groups.AddRange(DeviceGroups(inv, exportFolder, notes));
        groups.AddRange(ProblemGroups(inv, notes));
        groups.AddRange(UpdateGroups(inv, exportFolder, notes));
        groups.AddRange(StoreGroups(inv, exportFolder, nowUtc, options, notes));
        groups.AddRange(GhostGroups(inv, nowUtc, notes));

        var result = new ScanResult
        {
            FeatureId = DriverActions.FeatureId,
            Title = "Drivers",
            Depth = ScanDepth.Quick,
            Duration = duration,
            Groups = groups.Where(g => g.Items.Count > 0).ToList(),
            Notes = notes.Values.Distinct().ToList(),
        };
        return new DriverScanReport(result, notes, inv) { UnavailableTabs = UnavailableTabs(inv) };
    }

    /// <summary>Tabs whose list Windows didn't give PC Tidy: nothing was checked there, so nothing can be called fine.</summary>
    internal static IReadOnlySet<string> UnavailableTabs(DriverInventory inv)
    {
        var tabs = new HashSet<string>(StringComparer.Ordinal);
        if (!DevicesRead(inv)) { tabs.Add(DriverTabs.Devices); tabs.Add(DriverTabs.Problems); }
        if (!inv.Updates.IsAvailable) tabs.Add(DriverTabs.Updates);
        if (!inv.HasPnpUtilXml) { tabs.Add(DriverTabs.Store); tabs.Add(DriverTabs.Ghosts); }
        return tabs;
    }

    /// <summary>True when Windows gave PC Tidy at least one device (pnputil's list, or WMI's as a fallback).</summary>
    private static bool DevicesRead(DriverInventory inv) => inv.HasPnpUtilXml ? inv.Devices.Count > 0 : inv.WmiDrivers.Count > 0;

    // ================================================================== Devices

    private static IEnumerable<ScanGroup> DeviceGroups(DriverInventory inv, string exportFolder, Dictionary<string, string> notes)
    {
        var backupItems = new List<ScanItem>();
        if (inv.HasPnpUtilXml && inv.Packages.Count > 0)
            backupItems.Add(new ScanItem
            {
                Title = "Back up all drivers now",
                Detail = exportFolder,
                Reason = $"Saves a copy of all {inv.Packages.Count} third-party driver packages. Changes nothing on your PC. PC Tidy also does this by itself before any driver change.",
                Badge = Badge.Safe,
                NeedsAdmin = true,
                Actions = new[] { DriverActions.ExportAll(exportFolder) },
            });
        foreach (var b in inv.Backups)
        {
            var when = b.CreatedLocal.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
            backupItems.Add(new ScanItem
            {
                Title = $"Put back drivers from the backup of {when}",
                Detail = b.Folder,
                Reason = $"Adds the {b.PackageCount} saved driver packages back and lets Windows use them where they fit. Your current drivers are backed up first and stay on this PC.",
                Badge = Badge.Check,
                NeedsAdmin = true,
                NeedsRestart = true,
                Actions = DriverActions.AfterBackup(exportFolder, DriverActions.RestoreFromBackup(b.Folder, when)),
            });
        }
        if (backupItems.Count > 0)
            yield return new ScanGroup
            {
                Id = DriverTabs.GroupId(DriverTabs.Devices, "backup"),
                Title = "Backup and restore",
                Description = "A copy of your drivers, made by Windows' own pnputil. PC Tidy keeps backups in its data folder.",
                Badge = Badge.Safe,
                Items = backupItems,
            };

        var rollbacks = new List<ScanItem>();
        foreach (var d in inv.Devices.Where(d => d.IsPresent))
        {
            if (RollbackRules.PreviousDriver(d, inv.Packages) is not { } previous) continue;
            var current = d.InstalledDriver!;
            rollbacks.Add(new ScanItem
            {
                Title = $"Roll back {DeviceName(d)}",
                Detail = $"Now {current.Version.Display} · before {previous.Version.Display}",
                Reason = "Goes back to the driver this device had before. Use it if something broke after a driver update. Your drivers are backed up first.",
                Badge = Badge.Check,
                NeedsAdmin = true,
                NeedsRestart = true,
                CurrentState = current.Version.Version?.ToString(),
                Details = new Dictionary<string, string>
                {
                    ["Current driver"] = $"{current.PublishedName} · {current.Version.Display}",
                    ["Previous driver"] = $"{previous.PublishedName} · {previous.Version.Display}",
                    ["Provider"] = current.Provider,
                },
                Actions = DriverActions.AfterBackup(exportFolder, DriverActions.Rollback(d.InstanceId, DeviceName(d), previous.Version.Display)),
            });
        }
        if (rollbacks.Count > 0)
            yield return new ScanGroup
            {
                Id = DriverTabs.GroupId(DriverTabs.Devices, "rollback"),
                Title = "Go back to the previous driver",
                Description = "Only devices whose previous driver is still on this PC. Windows' own roll back, one device at a time.",
                Badge = Badge.Check,
                Items = rollbacks,
            };

        if (inv.Packages.Count > 0)
        {
            var inUse = StoreCleanupRules.InUseInfs(inv.Packages, inv.AllDevices);
            yield return new ScanGroup
            {
                Id = DriverTabs.GroupId(DriverTabs.Devices, "packages"),
                Title = "Third-party driver packages",
                Description = $"{inv.Packages.Count} packages in the driver store, {inv.Packages.Count(p => inUse.Contains(p.PublishedName))} in use. Information only.",
                Badge = Badge.Safe,
                Items = inv.Packages.OrderBy(p => p.OriginalName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Version.Version)
                    .Select(p => PackageRow(p, inUse.Contains(p.PublishedName))).ToList(),
            };
        }

        if (inv.HasPnpUtilXml)
        {
            foreach (var g in inv.Devices.Where(d => d.IsPresent).GroupBy(d => DeviceClassNames.For(d.ClassName), StringComparer.CurrentCultureIgnoreCase)
                         .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
                yield return new ScanGroup
                {
                    Id = DriverTabs.GroupId(DriverTabs.Devices, "class-" + Slug(g.Key)),
                    Title = g.Key,
                    Description = $"{g.Count()} device{(g.Count() == 1 ? "" : "s")}. Information only.",
                    Badge = Badge.Safe,
                    Items = g.OrderBy(DeviceName, StringComparer.CurrentCultureIgnoreCase).Select(DeviceRow).ToList(),
                };
        }
        else if (inv.WmiDrivers.Count > 0)
        {
            notes[DriverTabs.Devices] = "This version of Windows can't list everything, so you see the drivers of connected devices only.";
            foreach (var g in inv.WmiDrivers.GroupBy(d => DeviceClassNames.For(d.ClassName), StringComparer.CurrentCultureIgnoreCase)
                         .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
                yield return new ScanGroup
                {
                    Id = DriverTabs.GroupId(DriverTabs.Devices, "class-" + Slug(g.Key)),
                    Title = g.Key,
                    Description = $"{g.Count()} device{(g.Count() == 1 ? "" : "s")}. Information only.",
                    Badge = Badge.Safe,
                    Items = g.OrderBy(w => w.DeviceName, StringComparer.CurrentCultureIgnoreCase).Select(WmiDeviceRow).ToList(),
                };
        }
        else
        {
            notes[DriverTabs.Devices] = "Windows didn't share its device list with PC Tidy on this PC. Device Manager still shows everything.";
        }
    }

    private static ScanItem PackageRow(DriverPackage p, bool inUse) => new()
    {
        Title = $"{Fallback(p.OriginalName, p.PublishedName)} ({Fallback(p.Provider, "unknown maker")})",
        Detail = $"{p.PublishedName} · {DeviceClassNames.For(p.ClassName)} · {p.Version.Display}",
        Reason = p.Devices.Count > 0
            ? "Used by " + string.Join(", ", p.Devices.Select(d => Fallback(d.Description, d.InstanceId)).Distinct().Take(3)) + (p.Devices.Count > 3 ? $" and {p.Devices.Count - 3} more." : ".")
            : inUse ? "Used by a device right now." : "No device uses this package right now.",
        Badge = Badge.Safe,
        CurrentState = inUse ? "In use" : "Not in use",
        Details = new Dictionary<string, string>
        {
            ["Package"] = p.PublishedName,
            ["Original name"] = Fallback(p.OriginalName, "Unknown"),
            ["Provider"] = Fallback(p.Provider, "Unknown"),
            ["Version"] = p.Version.Display,
            ["Signed"] = p.IsSigned ? "Yes, by " + p.Signer : "No signature",
        },
    };

    private static ScanItem DeviceRow(DeviceRecord d)
    {
        var drv = d.InstalledDriver;
        var state = StateText(d);
        var details = new Dictionary<string, string>
        {
            ["Status"] = state,
            ["Class"] = DeviceClassNames.For(d.ClassName),
            ["Driver"] = d.DriverInf.Length > 0 ? d.DriverInf + (drv?.OriginalName is { Length: > 0 } on ? $" ({on})" : "") : "None",
        };
        if (drv is not null)
        {
            details["Provider"] = Fallback(drv.Provider, "Unknown");
            details["Version"] = drv.Version.Display;
            details["Signed"] = drv.Signer.Length > 0 ? "Yes, by " + drv.Signer : "No signature";
        }
        details["Device id"] = d.InstanceId;
        return new ScanItem
        {
            Title = DeviceName(d),
            Detail = drv is null ? (d.DriverInf.Length > 0 ? d.DriverInf : "No driver") : $"{Fallback(drv.Provider, "Unknown maker")} · {drv.Version.Display}",
            Reason = drv is null
                ? d.DriverInf.Length > 0 ? $"Uses {d.DriverInf}." : "No driver is installed for this device."
                : $"Driver from {Fallback(drv.Provider, "an unknown maker")}, {(drv.Signer.Length > 0 ? "signed" : "not signed")}. {state}.",
            Badge = Badge.Safe,
            CurrentState = state,
            Details = details,
        };
    }

    private static ScanItem WmiDeviceRow(WmiSignedDriver w) => new()
    {
        Title = Fallback(w.DeviceName, "Unknown device"),
        Detail = $"{Fallback(w.Provider, "Unknown maker")} · {Fallback(w.DriverVersion, "unknown version")}" + (w.DriverDate is { } d ? $" ({d:d MMM yyyy})" : ""),
        Reason = $"Driver from {Fallback(w.Provider, "an unknown maker")}, {(w.IsSigned ? "signed" : "not signed")}.",
        Badge = Badge.Safe,
        CurrentState = "In use",
        Details = new Dictionary<string, string>
        {
            ["Driver"] = Fallback(w.InfName, "Unknown"),
            ["Signed"] = w.IsSigned ? "Yes" + (w.Signer.Length > 0 ? ", by " + w.Signer : "") : "No signature",
            ["Device id"] = w.InstanceId,
        },
    };

    // ================================================================== Problems

    private static IEnumerable<ScanGroup> ProblemGroups(DriverInventory inv, Dictionary<string, string> notes)
    {
        var findings = ProblemAnalyzer.Analyze(inv);
        var problems = findings.Where(f => !f.IsInfoOnly && !f.IsMissingDriver).Select(ProblemRow).ToList();
        var missing = findings.Where(f => f.IsMissingDriver).Select(ProblemRow).ToList();
        var info = findings.Where(f => f.IsInfoOnly).Select(ProblemRow).ToList();
        if (!DevicesRead(inv) && findings.Count == 0)
            notes[DriverTabs.Problems] = "Windows didn't share its device list with PC Tidy, so problem devices can't be checked here. Device Manager still shows them.";
        else if (findings.Count(f => !f.IsInfoOnly) == 0)
            notes[DriverTabs.Problems] = "No device has a problem right now.";

        yield return new ScanGroup
        {
            Id = DriverTabs.GroupId(DriverTabs.Problems, "found"),
            Title = "Devices with a problem",
            Description = "What Device Manager reports, in plain words, with what to try. Nothing here changes on its own.",
            Badge = Badge.Check,
            Items = problems,
        };
        yield return new ScanGroup
        {
            Id = DriverTabs.GroupId(DriverTabs.Problems, "missing"),
            Title = "Devices without a driver",
            Description = "Windows found these but has no driver for them. The hardware ids tell the maker's site which driver you need.",
            Badge = Badge.Check,
            Items = missing,
        };
        yield return new ScanGroup
        {
            Id = DriverTabs.GroupId(DriverTabs.Problems, "info"),
            Title = "Shown so you know",
            Description = "Turned off on purpose, probably. Not a problem.",
            Badge = Badge.Safe,
            Items = info,
        };
    }

    private static ScanItem ProblemRow(ProblemFinding f)
    {
        var details = new Dictionary<string, string>
        {
            ["Problem"] = $"Code {f.Code.Code}: {f.Code.Name}",
            ["What to do"] = f.Code.Fix,
            ["Class"] = DeviceClassNames.For(f.ClassName),
        };
        if (f.DriverInf.Length > 0) details["Driver"] = f.DriverInf;
        if (f.HardwareIds.Count > 0) details["Hardware ids"] = string.Join("\n", f.HardwareIds.Take(6));
        details["Device id"] = f.InstanceId;
        var waiting = f.WindowsUpdateOffer is { } offer ? $" Windows Update has a driver for it: see Updates ({offer.Title})." : "";
        return new ScanItem
        {
            Title = f.Name,
            Detail = $"Code {f.Code.Code}: {f.Code.Name}",
            Reason = f.Code.Cause + " " + f.Code.Fix + waiting,
            Badge = f.IsInfoOnly ? Badge.Safe : Badge.Check,
            CurrentState = f.Code.Name,
            Details = details,
        };
    }

    // ================================================================== Updates

    private static IEnumerable<ScanGroup> UpdateGroups(DriverInventory inv, string exportFolder, Dictionary<string, string> notes)
    {
        var wu = new List<ScanItem>();
        if (!inv.Updates.IsAvailable) notes[DriverTabs.Updates] = inv.Updates.Problem!;
        else if (inv.Updates.Updates.Count == 0) notes[DriverTabs.Updates] = "Windows Update has no driver updates for this PC right now.";
        foreach (var u in inv.Updates.Updates)
        {
            bool idOk = UpdateIdPattern().IsMatch(u.UpdateId);
            var details = new Dictionary<string, string>
            {
                ["From"] = "Windows Update" + (u.IsOptional ? " (optional update)" : ""),
                ["Provider"] = Fallback(u.Provider, Fallback(u.Manufacturer, "Unknown")),
            };
            if (u.Model.Length > 0) details["For"] = u.Model;
            if (u.DriverClass.Length > 0) details["Kind"] = u.DriverClass;
            if (u.DriverDate is { } date) details["Driver date"] = date.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
            if (u.DownloadBytes > 0) details["Download"] = PcTidy.Core.Sizes.Format(u.DownloadBytes);
            wu.Add(new ScanItem
            {
                Title = u.Title,
                Detail = "From Windows Update" + (u.DriverDate is { } d ? $" · {d:d MMM yyyy}" : ""),
                Reason = $"An official driver from Windows Update{(u.Model.Length > 0 ? " for " + u.Model : "")}. Your current drivers are backed up first. Windows may ask you to restart.",
                Badge = Badge.Check,
                NeedsAdmin = true,
                NeedsRestart = true,
                Details = details,
                DisabledReason = idOk ? null : "Windows Update gave this update an id PC Tidy can't pass on safely. Install it from Settings > Windows Update instead.",
                Actions = idOk
                    ? DriverActions.AfterBackup(exportFolder, DriverActions.InstallWindowsUpdates(new[] { u.UpdateId }, $"Install \"{u.Title}\" from Windows Update"))
                    : Array.Empty<PlanItem>(),
            });
        }
        yield return new ScanGroup
        {
            Id = DriverTabs.GroupId(DriverTabs.Updates, "windows-update"),
            Title = "Driver updates from Windows Update",
            Description = "Official drivers Windows Update offers for this PC. Nothing installs until you confirm.",
            Badge = Badge.Check,
            Items = wu,
        };

        var tools = VendorAdvisor.Tools(inv.Apps, inv.Devices, inv.WmiDrivers).Select(ToolRow).ToList();
        yield return new ScanGroup
        {
            Id = DriverTabs.GroupId(DriverTabs.Updates, "maker-tools"),
            Title = "Makers' own driver tools",
            Description = "Graphics cards and gaming gear get their newest drivers from the maker. Ticking a row opens the maker's official page.",
            Badge = Badge.Safe,
            Items = tools,
        };

        var board = new List<ScanItem>();
        var link = inv.Board is { } b ? BoardVendors.SupportLinkFor(b.Manufacturer, b.Product) : null;
        var realtek = VendorAdvisor.RealtekDevice(inv.Devices, inv.WmiDrivers);
        if (link is not null)
        {
            var model = link.Model is null ? "" : " " + link.Model;
            var realtekLine = realtek is null ? "" : $" Realtek drivers (like {realtek}) come from here too.";
            board.Add(new ScanItem
            {
                Title = link.IsMakerSite ? $"{link.VendorName}{model} support page" : $"Find the{model} support page",
                Detail = link.Url,
                Reason = (link.IsMakerSite
                    ? $"Chipset, audio, network and BIOS updates for your motherboard come from {link.VendorName}. Check the board revision on the page matches yours."
                    : $"PC Tidy doesn't know {link.VendorName}'s support site, so this opens a web search for it. Use the maker's own site.") + realtekLine,
                Badge = Badge.Safe,
                Details = new Dictionary<string, string> { ["Motherboard"] = $"{inv.Board!.Manufacturer} {inv.Board.Product}".Trim() },
                Actions = new[] { DriverActions.Open(link.Url, link.IsMakerSite ? $"Open {link.VendorName}'s support page" : "Search the web for your motherboard's support page") },
            });
        }
        board.Add(new ScanItem
        {
            Title = "Optional updates in Settings",
            Detail = "Settings > Windows Update > Advanced options > Optional updates",
            Reason = "Windows Update's own list of optional driver updates. Opens Settings; nothing installs by itself.",
            Badge = Badge.Safe,
            Actions = new[] { DriverActions.Open(OptionalUpdatesPage, "Open optional updates in Settings") },
        });
        yield return new ScanGroup
        {
            Id = DriverTabs.GroupId(DriverTabs.Updates, "official-pages"),
            Title = "Official support pages",
            Description = "Your motherboard maker and Windows' own optional updates. PC Tidy never uses driver download sites.",
            Badge = Badge.Safe,
            Items = board,
        };
    }

    private static ScanItem ToolRow(VendorSuggestion s) => new()
    {
        Title = s.Installed ? $"{s.Tool.Name} (installed)" : s.Tool.Name,
        Detail = s.Tool.OfficialUrl,
        Reason = s.Installed
            ? $"Keeps {s.Tool.Covers} up to date. Open {s.Tool.Name} from the Start menu and check for updates. Ticking this opens the maker's official page."
            : $"Your {s.MatchedDevice} gets its newest drivers from this maker. Ticking this opens their official page.",
        Badge = Badge.Safe,
        CurrentState = s.Installed ? "Installed" : "Not installed",
        Actions = new[] { DriverActions.Open(s.Tool.OfficialUrl, $"Open the official {s.Tool.Name} page") },
    };

    // ================================================================== Driver store

    private static IEnumerable<ScanGroup> StoreGroups(DriverInventory inv, string exportFolder, DateTime nowUtc, DriverScanOptions options, Dictionary<string, string> notes)
    {
        if (!inv.HasPnpUtilXml)
        {
            notes[DriverTabs.Store] = "This version of Windows can't list the driver store for PC Tidy, so cleanup isn't available here.";
            yield break;
        }
        var decisions = StoreCleanupRules.Evaluate(inv.Packages, inv.AllDevices);
        bool quiet = StoreCleanupRules.IsInQuietPeriod(inv.LastDriverChangeUtc, nowUtc, out var availableFrom);
        string? quietReason = quiet
            ? availableFrom is { } from
                ? $"A driver changed recently. Wait until {DayOnOrAfter(from):d MMM yyyy} so Windows keeps the copy it needs to roll back."
                : "PC Tidy couldn't tell when drivers last changed, so it doesn't offer cleanup (it could remove a rollback copy)."
            : null;

        var old = decisions.Where(d => d.Verdict == StoreVerdict.Old).Select(d => StoreRow(d, inv, exportFolder, quietReason)).ToList();
        var important = decisions.Where(d => d.Verdict == StoreVerdict.OldImportant)
            .Select(d => StoreRow(d, inv, exportFolder, quietReason ?? (options.IncludeImportantClasses ? null
                : $"{DeviceClassNames.For(d.Package.ClassName)} drivers are left alone unless you switch on \"Let me pick important drivers\" and pick them one by one.")))
            .ToList();

        if (old.Count + important.Count == 0)
            notes[DriverTabs.Store] = "No old driver packages: every package is in use, the only one of its kind, or the newest.";
        else if (quiet)
            notes[DriverTabs.Store] = quietReason!;

        yield return new ScanGroup
        {
            Id = DriverTabs.GroupId(DriverTabs.Store, "old"),
            Title = "Old driver packages",
            Description = "Older copies that no device uses, with a newer version of the same driver kept. Your drivers are backed up first (if the backup doesn't finish, nothing is removed), and Windows itself refuses to remove anything in use.",
            Badge = Badge.Check,
            Items = old,
        };
        yield return new ScanGroup
        {
            Id = DriverTabs.GroupId(DriverTabs.Store, "important"),
            Title = "Old important drivers",
            Description = "System, storage, network, Bluetooth, display and input drivers. Left alone unless you pick one yourself.",
            Badge = Badge.Check,
            Items = important,
        };
    }

    private static ScanItem StoreRow(StoreDecision d, DriverInventory inv, string exportFolder, string? disabledReason)
    {
        var p = d.Package;
        var size = inv.PackageSizes.TryGetValue(p.PublishedName, out var s) ? s : 0;
        var what = $"{Fallback(p.OriginalName, p.PublishedName)} {p.Version.Version}";
        return new ScanItem
        {
            Title = $"{Fallback(p.OriginalName, p.PublishedName)} {p.Version.Version} ({Fallback(p.Provider, "unknown maker")})",
            Detail = $"{p.PublishedName} · {DeviceClassNames.For(p.ClassName)} · {p.Version.Display}",
            Reason = $"Older copy: version {d.NewerPackage!.Version.Version} of the same driver is also on this PC, and no device uses this one."
                     + (d.Verdict == StoreVerdict.OldImportant ? " Important driver: only remove it if you're sure." : "")
                     + " Kept in the backup, so it can be put back.",
            Badge = Badge.Check,
            NeedsAdmin = true,
            SizeBytes = size,
            CurrentState = "Not in use",
            Details = new Dictionary<string, string>
            {
                ["Package"] = p.PublishedName,
                ["Newer version"] = $"{d.NewerPackage.PublishedName} · {d.NewerPackage.Version.Display}",
                ["Class"] = DeviceClassNames.For(p.ClassName),
                ["Signed"] = p.IsSigned ? "Yes, by " + p.Signer : "No signature",
            },
            DisabledReason = disabledReason,
            Actions = DriverActions.AfterBackup(exportFolder, DriverActions.DeletePackage(p.PublishedName, what, size)),
        };
    }

    // ================================================================== Ghost devices

    private static IEnumerable<ScanGroup> GhostGroups(DriverInventory inv, DateTime nowUtc, Dictionary<string, string> notes)
    {
        if (!inv.HasPnpUtilXml)
        {
            notes[DriverTabs.Ghosts] = "This version of Windows can't list old devices for PC Tidy.";
            yield break;
        }
        var decisions = GhostRules.Evaluate(inv.Disconnected, nowUtc);
        var removable = decisions.Where(d => d.Verdict == GhostVerdict.Removable).Select(d => d.Device)
            .OrderBy(d => d.LastSeenUtc).Select(GhostRow).ToList();
        var kept = decisions.Where(d => d.Verdict is not (GhostVerdict.Removable or GhostVerdict.SoftwareDevice))
            .OrderBy(d => DeviceName(d.Device), StringComparer.CurrentCultureIgnoreCase).Select(KeptGhostRow).ToList();
        int software = decisions.Count(d => d.Verdict == GhostVerdict.SoftwareDevice);

        if (decisions.Count == 0) notes[DriverTabs.Ghosts] = "No old devices: everything Windows remembers is connected.";
        else if (removable.Count == 0) notes[DriverTabs.Ghosts] = "Nothing worth removing: the old devices here are recent, or are ones Windows needs.";

        yield return new ScanGroup
        {
            Id = DriverTabs.GroupId(DriverTabs.Ghosts, "removable"),
            Title = "Devices not seen for 30 days or more",
            Description = "Windows remembers every device you ever plugged in. Removing an entry loses its saved settings. One at a time.",
            Badge = Badge.Check,
            Items = removable,
        };
        yield return new ScanGroup
        {
            Id = DriverTabs.GroupId(DriverTabs.Ghosts, "kept"),
            Title = "Kept on purpose",
            Description = $"Recently used, or devices Windows needs (disks, volumes, system devices)."
                          + (software > 0 ? $" {software} software devices Windows recreates by itself aren't listed." : ""),
            Badge = Badge.Safe,
            Items = kept,
        };
    }

    private static ScanItem GhostRow(DeviceRecord d) => new()
    {
        Title = DeviceName(d),
        Detail = $"{DeviceClassNames.For(d.ClassName)} · last seen {LastSeen(d)}",
        Reason = $"Not connected since {LastSeen(d)}. Removing it loses its saved settings (COM port number, network settings, controller calibration). If you plug it in again, Windows sets it up fresh.",
        Badge = Badge.Check,
        NeedsAdmin = true,
        LastModifiedUtc = d.LastSeenUtc,
        CurrentState = "Not connected",
        Details = new Dictionary<string, string>
        {
            ["Class"] = DeviceClassNames.For(d.ClassName),
            ["Driver"] = Fallback(d.DriverInf, "None"),
            ["Device id"] = d.InstanceId,
        },
        Actions = new[] { DriverActions.RemoveDevice(d.InstanceId, DeviceName(d)) },
    };

    private static ScanItem KeptGhostRow(GhostDecision g) => new()
    {
        Title = DeviceName(g.Device),
        Detail = $"{DeviceClassNames.For(g.Device.ClassName)} · last seen {LastSeen(g.Device)}",
        Reason = "Shown so you know. PC Tidy doesn't offer to remove it.",
        Badge = Badge.Safe,
        CurrentState = "Not connected",
        DisabledReason = g.Verdict switch
        {
            GhostVerdict.ProtectedClass => "Disks, volumes and system devices stay: Windows needs their entries.",
            GhostVerdict.SeenRecently => "Used in the last 30 days, so it's probably just unplugged for now.",
            GhostVerdict.UnknownLastSeen => "Windows didn't say when this was last used, so PC Tidy leaves it alone.",
            _ => "This device's id has characters PC Tidy can't pass on safely, so it's left alone.",
        },
        Details = new Dictionary<string, string> { ["Device id"] = g.Device.InstanceId },
    };

    // ================================================================== helpers

    private static string DeviceName(DeviceRecord d) => Fallback(d.Description, "Unknown device");

    /// <summary>The first whole local day on which a UTC moment has passed ("wait until" dates never name today too early).</summary>
    private static DateTime DayOnOrAfter(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.TimeOfDay == TimeSpan.Zero ? local.Date : local.Date.AddDays(1);
    }

    private static string LastSeen(DeviceRecord d)
        => d.LastSeenUtc is { } t ? t.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "unknown";

    private static string StateText(DeviceRecord d) => d.State switch
    {
        DeviceState.Started => "Working",
        DeviceState.Disabled => "Turned off",
        DeviceState.Problem => d.ProblemCode != 0 ? $"Problem (code {d.ProblemCode})" : "Problem",
        DeviceState.Stopped => "Stopped",
        DeviceState.Disconnected => "Not connected",
        _ => "Unknown",
    };

    private static string Fallback(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Slug(string text)
        => new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
}
