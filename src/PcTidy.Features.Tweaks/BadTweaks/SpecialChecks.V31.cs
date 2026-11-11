using PcTidy.Features.Tweaks.Reading;

namespace PcTidy.Features.Tweaks.BadTweaks;

/// <summary>
/// v3.1 finder checks ("FPS boost tweaks" §4 HARMFUL rows, comparison page §8.3). All read-only: registry reads through the
/// (possibly redirected) reader, the system probe's answers, and file NAMES in a few user folders (never file contents).
/// </summary>
internal sealed partial class SpecialChecks
{
    // ------------------------------------------------------------------ Defender

    /// <summary>Defender is in charge, real-time protection is off, and no policy explains it (the policy has its own finding).</summary>
    private CustomFinding? DefenderRealtimeOff()
    {
        if (_env.Probe.ThirdPartyAntivirus.Count > 0 || _env.Probe.Defender is not { AntivirusEnabled: true, RealTimeProtection: false }) return null;
        var policy = _env.Registry.Read(RegistryHive.LocalMachine, DefenderPolicyAllowances.PolicyKey + @"\Real-Time Protection", "DisableRealtimeMonitoring");
        if (policy is { Kind: RegistryKind.DWord } p && RegData.TryNumber(p.Data, out var on) && on == 1) return null;
        return CustomFinding.Lines(new[] { "Microsoft Defender is the antivirus in charge, and its real-time protection is off." });
    }

    /// <summary>
    /// Defender switched off in its own (non-policy) keys, which only scripts with Tamper Protection off can write. Skipped
    /// while another antivirus is in charge (Windows then manages Defender's state itself). verify: these value names.
    /// </summary>
    private CustomFinding? DefenderSwitchedOffLocally()
    {
        if (_env.Probe.ThirdPartyAntivirus.Count > 0) return null;
        const string own = @"SOFTWARE\Microsoft\Windows Defender";
        var lines = new List<string>();
        foreach (var (key, name) in new[] { (own, "DisableAntiSpyware"), (own, "DisableAntiVirus"), (own + @"\Real-Time Protection", "DisableRealtimeMonitoring") })
            if (_env.Registry.Read(RegistryHive.LocalMachine, key, name) is { Kind: RegistryKind.DWord } v && RegData.TryNumber(v.Data, out var n) && n == 1)
                lines.Add($@"HKLM\{key} → {name} = 1 (Defender's own setting, not a policy)");
        return lines.Count > 0 ? CustomFinding.Lines(lines) : null;
    }

    // ------------------------------------------------------------------ network

    /// <summary>netsh template names for MSFT_NetTCPSetting.SettingName. verify: the netsh "template=" names.</summary>
    private static readonly IReadOnlyDictionary<string, string> NetshTemplates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Internet"] = "internet", ["InternetCustom"] = "internetcustom", ["Datacenter"] = "datacenter",
        ["DatacenterCustom"] = "datacentercustom", ["Compat"] = "compat",
    };

    /// <summary>The command that puts one template back on CUBIC, Windows' default. verify on a clean VM.</summary>
    internal static string Bbr2ResetCommand(string template) => $"netsh int tcp set supplemental template={template} congestionprovider=cubic";

    private CustomFinding? Bbr2()
    {
        if (_env.WindowsBuild < BadTweakCatalog.Bbr2FirstBuild || _env.Probe.CongestionProviders is not { Count: > 0 } settings) return null;
        var lines = new List<string>();
        foreach (var s in settings.Where(s => string.Equals(s.Provider, "BBR2", StringComparison.OrdinalIgnoreCase)))
            lines.Add(NetshTemplates.TryGetValue(s.SettingName, out var template)
                ? $"{s.SettingName}: BBR2. To put CUBIC back: {Bbr2ResetCommand(template)}"
                : $"{s.SettingName}: BBR2");
        return lines.Count > 0 ? CustomFinding.Lines(lines) : null;
    }

    /// <summary>
    /// Jumbo packets above 1514 on a PHYSICAL Ethernet adapter (NCF_PHYSICAL in Characteristics, *IfType 6, 802.3 media).
    /// A reset to "1514" is only planned when the value is text and the driver itself lists 1514 (its default or an option);
    /// otherwise the card says to use Device Manager.
    /// </summary>
    private CustomFinding? JumboFrames()
    {
        const RegistryHive lm = RegistryHive.LocalMachine;
        var root = BadTweakAllowances.NetClassKey;
        var evidence = new List<string>();
        var values = new List<FoundValue>();
        foreach (var sub in _env.Registry.SubKeyNames(lm, root))
        {
            if (sub.Length != 4 || !sub.All(char.IsAsciiDigit)) continue;
            var key = root + "\\" + sub;
            if (!IsPhysicalEthernet(key)) continue;
            if (_env.Registry.Read(lm, key, BadTweakAllowances.JumboValue) is not { Kind: { } kind } v) continue;
            if (!RegData.TryNumber(v.Data, out var size) || size <= 1514) continue;
            var name = _env.Registry.Read(lm, key, "DriverDesc") is { Data: { Length: > 0 } d } ? d.Trim() : $"Network adapter {sub}";
            var canReset = kind == RegistryKind.String && DriverOffersStandardSize(key);
            evidence.Add(canReset
                ? $"{name}: Jumbo Packet = {size} bytes"
                : $"{name}: Jumbo Packet = {size} bytes (PC Tidy can't pick the standard size for this driver: use Device Manager)");
            if (canReset) values.Add(new FoundValue(lm, key, BadTweakAllowances.JumboValue, RegistryKind.String, name));
            if (evidence.Count >= MaxListed) break;
        }
        return evidence.Count > 0 ? CustomFinding.Lines(evidence) with { Values = values } : null;
    }

    private bool IsPhysicalEthernet(string key)
    {
        int? Dword(string name) => _env.Registry.Read(RegistryHive.LocalMachine, key, name) is { Kind: RegistryKind.DWord } v && RegData.TryNumber(v.Data, out var n) ? (int)n : null;
        const int ncfPhysical = 0x4, ifTypeEthernet = 6, media8023 = 14;
        return Dword("Characteristics") is { } c && (c & ncfPhysical) != 0
            && Dword("*IfType") == ifTypeEthernet
            && Dword("*PhysicalMediaType") is null or media8023;
    }

    /// <summary>The driver's own option list (Ndi\Params\*JumboPacket: "default" or an Enum entry) has 1514.</summary>
    private bool DriverOffersStandardSize(string key)
    {
        var param = key + @"\Ndi\Params\" + BadTweakAllowances.JumboValue;
        if (_env.Registry.Read(RegistryHive.LocalMachine, param, "default") is { Data: { } d } && d.Trim() == BadTweakAllowances.JumboDefault) return true;
        return _env.Registry.ValueNames(RegistryHive.LocalMachine, param + @"\Enum").Contains(BadTweakAllowances.JumboDefault, StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ BIOS tools, optimizer suites

    private const int MaxFoldersPerRoot = 400;

    /// <summary>
    /// AMI's BIOS tools (SCEWIN/AMISCE, AFUWIN) and their helper driver, by NAME only: a helper driver registered as a service,
    /// and folders (two levels below Desktop, Downloads, Documents and the Windows drive's root) that hold the tool. Export
    /// files (nvram.txt) are only named when they sit next to the tool. No file is ever opened.
    /// </summary>
    private CustomFinding? BiosToolLeftovers()
    {
        var evidence = new List<string>();
        const string services = @"SYSTEM\CurrentControlSet\Services";
        foreach (var name in _env.Registry.SubKeyNames(RegistryHive.LocalMachine, services))
        {
            var state = ServiceState.Read(_env.Registry, name);
            if (state is null || (state.Type & 0x3) == 0 || state.ImagePath is null) continue;
            var file = Path.GetFileName(state.ImagePath.Trim().Trim('"'));
            if (BadTweakLeftoverNames.BiosToolDrivers.TryGetValue(file, out var tool))
                evidence.Add($"Driver '{name}' ({file}, from {tool}){(state.IsDisabled ? ", disabled" : "")}");
            if (evidence.Count >= MaxListed) break;
        }
        foreach (var root in _env.LeftoverFolders)
        {
            if (evidence.Count >= MaxListed) break;
            var visited = 0;
            var isDriveRoot = Path.GetPathRoot(root) is { } r && string.Equals(r.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            WalkForBiosTools(root, depth: 0, isDriveRoot, evidence, ref visited);
        }
        return evidence.Count > 0 ? CustomFinding.Lines(evidence.Take(MaxListed).ToList()) : null;
    }

    private static readonly EnumerationOptions NamesOnly = new()
    {
        IgnoreInaccessible = true, RecurseSubdirectories = false, ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
    };

    private static void WalkForBiosTools(string folder, int depth, bool isDriveRoot, List<string> evidence, ref int visited)
    {
        if (++visited > MaxFoldersPerRoot || evidence.Count >= MaxListed) return;
        try
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0) return;
            var files = dir.EnumerateFiles("*", NamesOnly).Select(f => f.Name).ToList();
            var tools = files.Where(f => BadTweakLeftoverNames.IsBiosToolProgram(f) || BadTweakLeftoverNames.BiosToolDrivers.ContainsKey(f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (tools.Count > 0)
            {
                var exports = files.Where(f => BadTweakLeftoverNames.BiosExportFiles.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                var listed = tools.Concat(exports).Take(8).ToList();
                evidence.Add($"{dir.FullName}: {string.Join(", ", listed)}{(exports.Count > 0 ? " (an export holds BIOS settings)" : "")}");
            }
            if (depth >= 2) return;
            foreach (var sub in dir.EnumerateDirectories("*", NamesOnly))
            {
                if (isDriveRoot && depth == 0 && BadTweakLeftoverNames.SkippedRootFolders.Contains(sub.Name)) continue;
                WalkForBiosTools(sub.FullName, depth + 1, isDriveRoot: false, evidence, ref visited);
                if (visited > MaxFoldersPerRoot || evidence.Count >= MaxListed) return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            // An unreadable folder is skipped.
        }
    }

    private static readonly (RegistryHive Hive, string Key)[] UninstallKeys =
    {
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
    };

    private IReadOnlyList<string>? _installed;

    /// <summary>DisplayNames of installed programs (the Uninstall list, read-only; system components and updates skipped).</summary>
    private IReadOnlyList<string> InstalledPrograms()
    {
        if (_installed is not null) return _installed;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, key) in UninstallKeys)
            foreach (var sub in _env.Registry.SubKeyNames(hive, key))
            {
                var k = key + "\\" + sub;
                if (_env.Registry.Read(hive, k, "DisplayName") is not { Data: { Length: > 0 } name }) continue;
                if (_env.Registry.Read(hive, k, "SystemComponent") is { Kind: RegistryKind.DWord, Data: "1" }) continue;
                if (_env.Registry.Read(hive, k, "ParentKeyName") is { Data: { Length: > 0 } }) continue;
                names.Add(name.Trim());
            }
        return _installed = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private CustomFinding? Programs(ReportedProgramKind kind)
    {
        var lines = InstalledPrograms()
            .Where(n => BadTweakLeftoverNames.MatchProgram(n)?.Kind == kind)
            .Select(n => n.Contains("CCleaner", StringComparison.OrdinalIgnoreCase) ? $"{n} is installed (it includes a registry cleaner)" : $"{n} is installed")
            .Take(MaxListed).ToList();
        return lines.Count > 0 ? CustomFinding.Lines(lines) : null;
    }

    // ------------------------------------------------------------------ CPU and power

    private CustomFinding? ProcessorIdleDisabled()
    {
        var lines = new List<string>();
        if (_env.Probe.ProcessorIdleDisableAc == 1) lines.Add("Active power plan: processor idle states are off when plugged in.");
        if (_env.Probe.ProcessorIdleDisableDc == 1) lines.Add("Active power plan: processor idle states are off on battery.");
        return lines.Count > 0 ? CustomFinding.Lines(lines) : null;
    }

    /// <summary>Store packages every Windows 10/11 install has: if none is listed, the app list couldn't be read (so nothing is said).</summary>
    private static readonly string[] AlwaysPresentPackages =
    {
        "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost", "MicrosoftWindows.Client.CBS",
        "Microsoft.AAD.BrokerPlugin", "Microsoft.Windows.CloudExperienceHost",
    };

    private CustomFinding? X3dGameBarMissing()
    {
        if (_env.Probe.CpuKind != CpuKind.DualCcdX3D) return null;
        if (_env.Apps.FindPackageFullName("Microsoft.XboxGamingOverlay") is not null) return null;
        if (!AlwaysPresentPackages.Any(p => _env.Apps.FindPackageFullName(p) is not null)) return null;
        return CustomFinding.Lines(new[] { "This PC has a Ryzen 9 X3D processor with two chiplets, and the Xbox Game Bar (Microsoft.XboxGamingOverlay) isn't installed." });
    }

    // ------------------------------------------------------------------ graphics

    /// <summary>verify: AMD's driver writes its Adrenalin version as "RadeonSoftwareVersion" in the display adapter's class key.</summary>
    private CustomFinding? AntiLagPlusDriver()
    {
        var lines = new List<string>();
        for (var i = 0; i < BadTweakCatalog.DisplayAdapterInstances; i++)
        {
            var key = $@"{BadTweakCatalog.DisplayAdapterClassKey}\{i:0000}";
            if (_env.Registry.Read(RegistryHive.LocalMachine, key, "RadeonSoftwareVersion") is not { Data: { Length: > 0 } version }) continue;
            var v = version.Trim();
            if (v != "23.10.1" && !v.StartsWith("23.10.1 ", StringComparison.Ordinal) && !v.StartsWith("23.10.1-", StringComparison.Ordinal)) continue;
            var name = _env.Registry.Read(RegistryHive.LocalMachine, key, "DriverDesc") is { Data: { Length: > 0 } d } ? d.Trim() : "AMD graphics";
            lines.Add($"{name}: AMD Software {v}");
        }
        return lines.Count > 0 ? CustomFinding.Lines(lines) : null;
    }
}
