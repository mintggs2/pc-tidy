using System.Text;
using System.Text.RegularExpressions;
using PcTidy.Catalog.Insights;
using PcTidy.Catalog.Tweaks;
using PcTidy.Core.Model;

namespace PcTidy.Features.Insights.Scripts;

/// <summary>
/// Recognises the commands "optimizer" scripts use, one logical line at a time, by pattern only (PowerShell, batch, VBScript,
/// JScript). Nothing is evaluated: a line is text, matched against regular expressions, and each hit becomes a finding.
/// </summary>
internal static partial class CommandRules
{
    /// <summary>A hidden -EncodedCommand payload found on a line (decoded as text for a second look).</summary>
    public sealed record Payload(int Line, string Text);

    public static IEnumerable<ScriptFinding> Scan(int line, string text, bool decoded, bool fileMentionsHosts, List<Payload> payloads)
    {
        var found = new List<ScriptFinding>();
        void Add(ScriptFinding? f) { if (f is not null) found.Add(f); }
        ScriptFinding F(ScriptArea area, string what, ScriptVerdict v, string why, bool red = false, string? id = null)
            => new() { Line = line, Area = area, What = what, Verdict = v, Why = why, Evidence = CatalogMatcher.Shorten(text), RedFlag = red, MatchedId = id, FromDecodedPayload = decoded };

        // ---- hidden code: command names broken into pieces (i`e`x, 'Inv'+'oke-Expression', p^o^w^e^r^s^h^e^l^l) ----
        var joined = Unbroken(text);
        if (!ReferenceEquals(joined, text) && SensitiveName().IsMatch(joined) && !SensitiveName().IsMatch(text))
        {
            Add(F(ScriptArea.HiddenCode, "Breaks command names into pieces so they're hard to spot", ScriptVerdict.Unknown,
                "Windows runs the command the same way either way. Writing it in pieces (like i`e`x or 'Inv'+'oke') only stops a quick read, or a simple antivirus check, from recognising it. "
                + "PC Tidy joined the pieces back together to read the rest of this line.", red: true));
            text = joined; // the rules below read the joined text (shown as the evidence)
        }

        // ---- registry -------------------------------------------------------------------------
        foreach (Match m in PsRegistryCmdlet().Matches(text)) Add(PsRegistry(line, text, m, decoded));
        foreach (Match m in RegExe().Matches(text)) Add(RegExeCommand(line, text, m, decoded, F));
        foreach (Match m in DotNetSetValue().Matches(text))
            if (RegistryText.TrySplit(m.Groups["key"].Value, out var rh, out var h, out var k))
                Add(CatalogMatcher.ForRegistry(new RegistryWrite(line, text, h, rh, k, m.Groups["name"].Value, RegistryOp.SetValue, null, Literal(m.Groups["val"].Value)), decoded));
        foreach (Match m in WshRegWrite().Matches(text)) Add(WshRegistry(line, text, m, delete: false, decoded));
        foreach (Match m in WshRegDelete().Matches(text)) Add(WshRegistry(line, text, m, delete: true, decoded));

        // ---- services --------------------------------------------------------------------------
        if (SetService().Match(text) is { Success: true } ss)
        {
            var a = ArgTokens.PsArgs.Parse(ArgTokens.Split(ss.Groups["args"].Value), new[] { "Name", "StartupType", "Status", "DisplayName", "InputObject" });
            var name = a.Bind("Name")["Name"];
            if (name is not null)
            {
                if (a.Get("StartupType") is { } st) Add(CatalogMatcher.ForService(line, text, name, CatalogMatcher.FromStartWord(st), stop: false, decoded));
                if (a.Get("Status") is { } status && status.Equals("Stopped", StringComparison.OrdinalIgnoreCase))
                    Add(CatalogMatcher.ForService(line, text, name, null, stop: true, decoded));
            }
        }
        foreach (Match m in ScConfig().Matches(text))
            Add(CatalogMatcher.ForService(line, text, m.Groups["name"].Value.Trim('"'), CatalogMatcher.FromStartWord(m.Groups["type"].Value), stop: false, decoded));
        foreach (Match m in ServiceStop().Matches(text))
        {
            var name = m.Groups["name"].Value.Trim('"', '\'');
            if (name.Length > 0 && !name.StartsWith('-')) Add(CatalogMatcher.ForService(line, text, name, null, stop: true, decoded));
        }
        if (StopServiceCmdlet().Match(text) is { Success: true } sp)
        {
            var a = ArgTokens.PsArgs.Parse(ArgTokens.Split(sp.Groups["args"].Value), new[] { "Name", "DisplayName", "InputObject" });
            var name = a.Bind("Name")["Name"] ?? (GetServiceName().Match(text) is { Success: true } gs ? gs.Groups["name"].Value : null);
            if (name is not null) Add(CatalogMatcher.ForService(line, text, name.Trim('"', '\''), null, stop: true, decoded));
        }
        foreach (Match m in ScDelete().Matches(text))
        {
            var name = m.Groups["name"].Value.Trim('"');
            bool security = name.Equals("WinDefend", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Wd", StringComparison.OrdinalIgnoreCase);
            Add(F(security ? ScriptArea.Defender : ScriptArea.Service, $"Deletes the '{name}' service", security ? ScriptVerdict.Harmful : ScriptVerdict.Unknown,
                "Deleting a service removes it for good; Windows can't start it again without a repair.", red: security));
        }

        // ---- boot settings ---------------------------------------------------------------------
        if (Bcdedit().Match(text) is { Success: true } bcd) Add(BootCommand(line, text, bcd.Groups["args"].Value, decoded, F));

        // ---- network and firewall --------------------------------------------------------------
        if (FirewallOff().IsMatch(text) || PsFirewallOff().IsMatch(text))
            Add(F(ScriptArea.Firewall, "Turns Windows Firewall off", ScriptVerdict.Harmful,
                BadTweakCatalog.Find("security.firewall-off")?.WhyBad ?? "Without the firewall, programs on your network can reach services on your PC.", red: true, id: "security.firewall-off"));
        else if (Netsh().Match(text) is { Success: true } ns) Add(NetshCommand(line, text, ns.Groups["args"].Value, decoded, F));

        // ---- power -----------------------------------------------------------------------------
        if (Powercfg().Match(text) is { Success: true } pc) Add(PowercfgCommand(line, text, pc.Groups["args"].Value, decoded, F));

        // ---- storage behaviour -----------------------------------------------------------------
        if (FsutilBehavior().Match(text) is { Success: true } fb) Add(FsutilCommand(line, text, fb.Groups["name"].Value, fb.Groups["val"].Value, F));
        if (Fsutil8dot3Strip().IsMatch(text))
            Add(F(ScriptArea.Files, "Strips short (8.3) file names", ScriptVerdict.Harmful, "Microsoft warns this can stop apps from uninstalling or updating."));
        if (DisableMmAgent().IsMatch(text))
            Add(F(ScriptArea.Other, "Turns off memory compression", ScriptVerdict.Placebo, "No measured gain; Windows uses compression to avoid writing to the page file."));

        // ---- Store apps ------------------------------------------------------------------------
        if (AppxRemove().IsMatch(text)) Add(CatalogMatcher.ForAppxRemoval(line, text, AppxNames(text), decoded));
        if (WingetUninstall().Match(text) is { Success: true } wu)
            Add(F(ScriptArea.StoreApp, $"Uninstalls '{wu.Groups["id"].Value.Trim('"')}' with winget", ScriptVerdict.Situational,
                "Uninstalling an app you don't use is fine; it doesn't come back by itself."));

        // ---- scheduled tasks -------------------------------------------------------------------
        if (SchtasksCommand().Match(text) is { Success: true } st2) Add(SchtasksCommandFinding(line, text, st2.Groups["args"].Value, decoded, F));
        if (DisableTaskCmdlet().Match(text) is { Success: true } dt)
        {
            var a = ArgTokens.PsArgs.Parse(ArgTokens.Split(dt.Groups["args"].Value), new[] { "TaskName", "TaskPath" }).Bind("TaskName", "TaskPath");
            if (a["TaskName"] is { } tn) Add(CatalogMatcher.ForTask(line, text, (a["TaskPath"] ?? "") + tn, disable: true, decoded));
        }
        if (RegisterTask().IsMatch(text))
            Add(F(ScriptArea.ScheduledTask, "Creates a scheduled task", ScriptVerdict.Unknown,
                "A task runs by itself later, even after you close the script. Optimizer packs use this to re-apply their changes."));

        // ---- Microsoft Defender ----------------------------------------------------------------
        foreach (Match m in DefenderExclusion().Matches(text))
        {
            var target = m.Groups["what"].Value.Trim().Trim('"', '\'');
            var place = SelfFolder().IsMatch(target) ? "its own folder" : TempFolder().IsMatch(target) ? "the temporary folder" : null;
            Add(place is null
                ? F(ScriptArea.Defender, $"Adds a Microsoft Defender exclusion ({m.Groups["kind"].Value}: {Clip(target)})",
                    ScriptVerdict.Harmful, "Defender stops checking whatever is excluded. Fake \"FPS boost\" packs add exclusions so their own files are never scanned. PC Tidy never adds exclusions.", red: true)
                : F(ScriptArea.Defender, $"Adds a Microsoft Defender exclusion for {place} ({m.Groups["kind"].Value}: {Clip(target)})", ScriptVerdict.Harmful,
                    $"Defender stops checking {place}, so whatever the script unpacks or downloads there next is never scanned. "
                    + "Packs that hide their real program, or ship it in parts that are joined later, do this so antivirus never sees it. PC Tidy never adds exclusions.", red: true));
        }
        foreach (Match mp in MpPreference().Matches(text))
            foreach (Match m in DefenderDisableOption().Matches(mp.Groups["args"].Value))
            {
                bool on = m.Groups["val"].Value.Trim().TrimStart('$') is "true" or "1";
                var opt = m.Groups["opt"].Value;
                var id = BadTweakCatalog.Find(DefenderCheckFor(opt))?.Id;
                Add(on
                    ? F(ScriptArea.Defender, $"Turns off Microsoft Defender protection ({opt})", ScriptVerdict.Harmful,
                        "It switches off part of Defender's protection. Malware disguised as optimizers does this first.", red: true, id: id)
                    : F(ScriptArea.Defender, $"Turns Microsoft Defender protection back on ({opt})", ScriptVerdict.Proven, "That's Windows' default.", id: id));
            }
        if (DefenderCloudOff().IsMatch(text))
            Add(F(ScriptArea.Defender, "Turns off Defender's cloud protection or sample sending", ScriptVerdict.Harmful, "Defender catches new threats much later without cloud protection.", red: true,
                id: BadTweakCatalog.Find("security.defender-protection-reduced")?.Id));
        if (DefenderFeatureRemove().IsMatch(text))
            Add(F(ScriptArea.Defender, "Removes Microsoft Defender from Windows", ScriptVerdict.Harmful, "Your PC is left with no antivirus, and Windows Update may fail.", red: true));

        // ---- download and run -----------------------------------------------------------------
        bool download = Download().IsMatch(text);
        bool runsText = RunText().IsMatch(text);
        if (download && (runsText || PipeToIex().IsMatch(text)))
            Add(F(ScriptArea.Download, $"Downloads code{From(text)} and runs it straight away", ScriptVerdict.Harmful,
                "You can't see what it runs, and it can change between today and tomorrow. This is the most common way \"FPS boost\" and \"debloat\" scripts deliver malware.", red: true));
        else if (download)
            Add(F(ScriptArea.Download, $"Downloads a file{From(text)}", ScriptVerdict.Unknown, "PC Tidy can't see what the downloaded file does. Scan that file too before you run anything."));
        else if (runsText)
            Add(F(ScriptArea.HiddenCode, "Runs text as code (Invoke-Expression / eval)", ScriptVerdict.Unknown,
                "What runs is built while the script runs, so this check can't see it. Scripts that hide their real work use this.", red: true));

        // ---- hidden code -----------------------------------------------------------------------
        foreach (Match m in EncodedCommand().Matches(text))
        {
            var payload = TryDecode(m.Groups["b64"].Value);
            if (payload is not null) payloads.Add(new Payload(line, payload));
            Add(F(ScriptArea.HiddenCode, "Runs a hidden (encoded) PowerShell command", ScriptVerdict.Harmful,
                payload is null
                    ? "The command is scrambled so you can't read it. Honest scripts have no reason to do this."
                    : "The command is scrambled so you can't read it. PC Tidy decoded it as text: its changes are listed with \"(hidden)\".", red: true));
        }
        if (FromBase64().IsMatch(text))
            Add(F(ScriptArea.HiddenCode, "Decodes hidden text (Base64)", ScriptVerdict.Unknown, "Scripts that hide their real work keep it Base64-encoded so it can't be read.", red: true));
        else if (!EncodedCommand().IsMatch(text) && LongBase64().IsMatch(text))
            Add(F(ScriptArea.HiddenCode, "Contains a large block of scrambled data", ScriptVerdict.Unknown, "It may be a hidden program or script. PC Tidy can't tell what it does.", red: true));
        if (HiddenWindow().IsMatch(text))
            Add(F(ScriptArea.HiddenCode, "Runs with no window, so you don't see it", ScriptVerdict.Unknown, "Hiding the window is common in scripts that don't want to be noticed."));
        if (CharCodes().IsMatch(text))
            Add(F(ScriptArea.HiddenCode, "Spells text out as character codes", ScriptVerdict.Unknown,
                "Writing words as numbers ([char]105 and so on) means nobody reading the file, and no simple antivirus check, can see what it says. Honest scripts just write the text.", red: true));
        if (CopyJoin().Match(text) is { Success: true } cj && JoinedPieceHint().IsMatch(cj.Value))
            Add(F(ScriptArea.HiddenCode, "Joins several files into one", ScriptVerdict.Unknown,
                "Programs are sometimes shipped as harmless-looking pieces that are only put together on your PC, so antivirus never sees the whole file. PC Tidy can't see what the joined file does.", red: true));

        // ---- execution policy ------------------------------------------------------------------
        if (SetExecutionPolicy().Match(text) is { Success: true } ep)
        {
            var scope = ep.Groups["scope"].Success ? ep.Groups["scope"].Value : "LocalMachine";
            Add(scope.Equals("Process", StringComparison.OrdinalIgnoreCase)
                ? F(ScriptArea.ExecutionPolicy, $"Lets any script run in this window ({ep.Groups["policy"].Value})", ScriptVerdict.Situational, "It only lasts until the window closes.")
                : F(ScriptArea.ExecutionPolicy, $"Lets any PowerShell script run without asking ({ep.Groups["policy"].Value}, {scope})", ScriptVerdict.Harmful,
                    BadTweakCatalog.Find("security.execution-policy")?.WhyBad ?? "Any script you download can run without a warning.", id: "security.execution-policy"));
        }
        else if (BypassPolicyArg().IsMatch(text))
            Add(F(ScriptArea.ExecutionPolicy, "Skips PowerShell's script check for this run", ScriptVerdict.Unknown, "It only affects this run, but it means the script didn't want to be checked."));

        // ---- hosts file -------------------------------------------------------------------------
        bool hostsWrite = HostsPath().IsMatch(text) && HostsWriteVerb().IsMatch(text);
        var block = HostsLine().Match(text);
        if (hostsWrite || (block.Success && fileMentionsHosts))
        {
            var hosts = HostNames().Matches(text).Select(x => x.Value).ToList();
            var service = hosts.Select(MicrosoftHosts.ServiceOf).FirstOrDefault(s => s is not null);
            if (service is { } svc)
                Add(F(ScriptArea.HostsFile, "Blocks Microsoft servers in the hosts file", ScriptVerdict.Harmful,
                    $"It breaks {MicrosoftHosts.Describe(svc)}. Blocking Microsoft in the hosts file is never a safe tweak.", id: "network.hosts-blocks-microsoft"));
            else if (hosts.Any(IsMicrosoftDomain))
                Add(F(ScriptArea.HostsFile, "Blocks Microsoft servers in the hosts file", ScriptVerdict.Harmful,
                    TweakCatalog.Find("privacy.hosts-telemetry-block") is { } t ? t.Caveat ?? t.Effect : "Blocking the wrong host breaks Windows Update, the Store and activation.",
                    id: "privacy.hosts-telemetry-block"));
            else
                Add(F(ScriptArea.HostsFile, "Edits the hosts file", ScriptVerdict.Unknown, "The hosts file decides where web addresses go. A wrong line can break sites or updates."));
        }

        // ---- files and recovery -------------------------------------------------------------------
        if (DeleteFiles().Match(text) is { Success: true } del && !RegistryPathArg().IsMatch(del.Groups["args"].Value)) Add(FileDelete(del.Groups["args"].Value, F));
        if (TakeOwn().IsMatch(text))
            Add(F(ScriptArea.Files, "Takes ownership of Windows' own files", ScriptVerdict.Harmful, "It removes Windows' protection of its system files, so updates and repairs can fail."));
        if (ShadowDelete().IsMatch(text))
            Add(F(ScriptArea.Files, "Deletes or turns off restore points", ScriptVerdict.Harmful, "You lose your way back if something breaks. Ransomware does this first.", red: true));
        if (ClearLogs().IsMatch(text))
            Add(F(ScriptArea.Other, "Erases Windows' event logs", ScriptVerdict.Unknown, "It hides what happened on the PC; there's no speed gain.", red: true));

        // ---- devices (Device Manager) -----------------------------------------------------------
        if (DisableDevice().Match(text) is { Success: true } dd) Add(DeviceOff(text, dd, F));

        // ---- read-only game settings files ----------------------------------------------------------
        if ((AttribCommand().Match(text) is { Success: true } at && AttribPlusR().IsMatch(at.Groups["args"].Value)) || PsReadOnly().IsMatch(text))
            if (ConfigFile().Match(text) is { Success: true } cfg && !cfg.Value.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase))
                Add(F(ScriptArea.Files, $"Makes the settings file {Clip(Path.GetFileName(cfg.Value.Trim('"', '\'')))} read-only", ScriptVerdict.Situational,
                    "Guides do this so a game can't change a tweaked setting back. The game then can't save anything you change in its own menus either. "
                    + "Only useful if you know which setting keeps being reset; PC Tidy's Games section lists read-only settings files and how to undo it."));

        return found;
    }

    // ================================================================== registry commands

    private static ScriptFinding? PsRegistry(int line, string text, Match m, bool decoded)
    {
        var cmd = m.Groups["cmd"].Value.ToLowerInvariant();
        var tokens = ArgTokens.Split(m.Groups["args"].Value);
        var a = ArgTokens.PsArgs.Parse(tokens, new[] { "Path", "LiteralPath", "PSPath", "Name", "Value", "Type", "PropertyType", "Force", "Recurse" });
        var bound = a.Bind("Path", "Name", "Value");
        var path = a.Get("LiteralPath") ?? a.Get("PSPath") ?? bound["Path"];
        if (path is null || !RegistryText.TrySplit(path, out var rh, out var hive, out var key)) return null;
        var name = bound["Name"];
        switch (cmd)
        {
            case "remove-itemproperty" or "rp":
                return name is null ? null : CatalogMatcher.ForRegistry(new RegistryWrite(line, text, hive, rh, key, name, RegistryOp.DeleteValue), decoded);
            case "remove-item" or "ri":
                return CatalogMatcher.ForRegistry(new RegistryWrite(line, text, hive, rh, key, null, RegistryOp.DeleteKey), decoded);
            default:
                if (name is null) return null;
                var kind = PsKind(a.Get("Type") ?? a.Get("PropertyType"));
                return CatalogMatcher.ForRegistry(new RegistryWrite(line, text, hive, rh, key, name, RegistryOp.SetValue, kind, Literal(bound["Value"])), decoded);
        }
    }

    private static ScriptFinding? RegExeCommand(int line, string text, Match m, bool decoded, Func<ScriptArea, string, ScriptVerdict, string, bool, string?, ScriptFinding> f)
    {
        var verb = m.Groups["verb"].Value.ToLowerInvariant();
        var tokens = ArgTokens.Split(m.Groups["args"].Value);
        if (verb == "import")
            return f(ScriptArea.Registry, $"Imports the registry file {(tokens.Count > 0 ? tokens[0] : "")}", ScriptVerdict.Unknown,
                "That file holds more changes. Scan it too before running anything.", false, null);
        if (tokens.Count == 0 || !RegistryText.TrySplit(tokens[0], out var rh, out var hive, out var key)) return null;
        string? name = null, type = null, data = null;
        bool defaultValue = false, allValues = false;
        for (int i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i].ToLowerInvariant();
            string? Next() => i + 1 < tokens.Count ? tokens[++i] : null;
            switch (t)
            {
                case "/v" or "-v": name = Next(); break;
                case "/ve" or "-ve": defaultValue = true; break;
                case "/va" or "-va": allValues = true; break;
                case "/t" or "-t": type = Next(); break;
                case "/d" or "-d": data = Next(); break;
            }
        }
        if (defaultValue) name = "";
        if (verb == "delete")
        {
            if (allValues || name is null)
                return CatalogMatcher.ForRegistry(new RegistryWrite(line, text, hive, rh, key, null, RegistryOp.DeleteKey), decoded);
            return CatalogMatcher.ForRegistry(new RegistryWrite(line, text, hive, rh, key, name, RegistryOp.DeleteValue), decoded);
        }
        if (name is null) return null; // "reg add KEY /f" only creates the key
        var kind = RegKind(type);
        if (kind == RegistryKind.MultiString && data is not null) data = data.Replace(@"\0", "\n", StringComparison.Ordinal);
        return CatalogMatcher.ForRegistry(new RegistryWrite(line, text, hive, rh, key, name, RegistryOp.SetValue, kind, data ?? ""), decoded);
    }

    private static ScriptFinding? WshRegistry(int line, string text, Match m, bool delete, bool decoded)
    {
        var full = m.Groups["path"].Value;
        var cut = full.LastIndexOf('\\');
        if (cut <= 0) return null;
        bool keyOnly = full.EndsWith('\\');
        var keyPart = keyOnly ? full.TrimEnd('\\') : full[..cut];
        var name = keyOnly ? null : full[(cut + 1)..];
        if (!RegistryText.TrySplit(keyPart, out var rh, out var hive, out var key)) return null;
        if (delete)
            return CatalogMatcher.ForRegistry(new RegistryWrite(line, text, hive, rh, key, name, name is null ? RegistryOp.DeleteKey : RegistryOp.DeleteValue), decoded);
        if (name is null) return null;
        return CatalogMatcher.ForRegistry(new RegistryWrite(line, text, hive, rh, key, name, RegistryOp.SetValue,
            RegKind(m.Groups["type"].Success ? m.Groups["type"].Value : null), Literal(m.Groups["val"].Value)), decoded);
    }

    private static RegistryKind? PsKind(string? t) => t?.Trim('"', '\'').ToLowerInvariant() switch
    {
        "dword" => RegistryKind.DWord,
        "qword" => RegistryKind.QWord,
        "string" => RegistryKind.String,
        "expandstring" => RegistryKind.ExpandString,
        "multistring" => RegistryKind.MultiString,
        "binary" => RegistryKind.Binary,
        _ => null,
    };

    private static RegistryKind? RegKind(string? t) => t?.Trim('"').ToUpperInvariant() switch
    {
        "REG_DWORD" => RegistryKind.DWord,
        "REG_QWORD" => RegistryKind.QWord,
        "REG_SZ" => RegistryKind.String,
        "REG_EXPAND_SZ" => RegistryKind.ExpandString,
        "REG_MULTI_SZ" => RegistryKind.MultiString,
        "REG_BINARY" => RegistryKind.Binary,
        null => RegistryKind.String, // reg add's default type
        _ => null,
    };

    /// <summary>A literal value as written ("0", '1', 0x26, $true) without quotes; null when it's an expression.</summary>
    private static string? Literal(string? raw)
    {
        if (raw is null) return null;
        var t = raw.Trim().TrimEnd(')', ';').Trim();
        if (t.StartsWith('"') || t.StartsWith('\'')) return t.Trim('"', '\'');
        if (t.Equals("$true", StringComparison.OrdinalIgnoreCase)) return "1";
        if (t.Equals("$false", StringComparison.OrdinalIgnoreCase)) return "0";
        return t;
    }

    // ================================================================== other commands

    private static ScriptFinding? BootCommand(int line, string text, string args, bool decoded, Func<ScriptArea, string, ScriptVerdict, string, bool, string?, ScriptFinding> f)
    {
        var t = ArgTokens.Split(args).ToList();
        if (t.Count == 0) return null;
        var verb = t[0].ToLowerInvariant();
        int i = 1;
        if (i < t.Count && t[i].StartsWith('{')) i++; // {current}, {default}, a GUID
        if (verb is "/set" or "-set" && i < t.Count)
        {
            var name = t[i];
            var value = i + 1 < t.Count ? t[i + 1] : null;
            return CatalogMatcher.ForCommand(line, text, "bcdedit /set " + name + (value is null ? "" : " " + value), ScriptArea.BootSettings, decoded) is { Verdict: not ScriptVerdict.Harmful } fromTable
                   && !BadBootName(name)
                ? fromTable
                : CatalogMatcher.ForBoot(line, text, name, value, delete: false, decoded);
        }
        if (verb is "/deletevalue" or "-deletevalue" && i < t.Count) return CatalogMatcher.ForBoot(line, text, t[i], null, delete: true, decoded);
        if (verb is "/enum" or "/v" or "-enum" or "/?") return null; // read-only listing
        return f(ScriptArea.BootSettings, "Changes the boot configuration", ScriptVerdict.Unknown, "Boot changes affect how Windows starts; a mistake can stop it starting.", false, null);
    }

    private static bool BadBootName(string name)
        => BadTweakCatalog.All.SelectMany(c => c.Signals).Select(s => s.Probe).OfType<BootValueProbe>().Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static ScriptFinding? NetshCommand(int line, string text, string args, bool decoded, Func<ScriptArea, string, ScriptVerdict, string, bool, string?, ScriptFinding> f)
    {
        var norm = CatalogMatcher.NormalizeCommand("netsh " + args);
        if (CatalogMatcher.ForCommand(line, text, norm, ScriptArea.Network, decoded) is { } fromTable) return fromTable;
        if (norm.Contains("autotuninglevel=normal", StringComparison.Ordinal))
            return f(ScriptArea.Network, "Sets TCP receive autotuning to normal", ScriptVerdict.Proven, "That's Windows' default.", false, null);
        if (norm.Contains("winsock reset", StringComparison.Ordinal) || norm.Contains("int ip reset", StringComparison.Ordinal))
            return f(ScriptArea.Network, "Resets the network stack", ScriptVerdict.Situational, "A standard repair for a broken connection; it needs a restart. PC care's \"Fix my network\" does this.", false, null);
        if (norm.Contains(" show ", StringComparison.Ordinal) || norm.EndsWith(" show", StringComparison.Ordinal)) return null; // read-only
        if (norm.Contains("int tcp", StringComparison.Ordinal) || norm.Contains("interface tcp", StringComparison.Ordinal) || norm.Contains("int ip", StringComparison.Ordinal))
            return f(ScriptArea.Network, "Changes a TCP/IP setting", ScriptVerdict.Unknown,
                "Most TCP \"gaming\" tweaks are placebo and some slow downloads down (Tweaks research). PC Tidy has no research on this exact one.", false, null);
        return f(ScriptArea.Network, "Changes a network setting (netsh)", ScriptVerdict.Unknown, "PC Tidy has no research on this change.", false, null);
    }

    private static ScriptFinding? PowercfgCommand(int line, string text, string args, bool decoded, Func<ScriptArea, string, ScriptVerdict, string, bool, string?, ScriptFinding> f)
    {
        var stray = BadTweakCatalog.Find("cpu.stray-power-plan");
        string StrayWhy() => (stray?.WhyBad ?? "Tests found no gain from tuning tools' own power plans over Balanced.")
                             + " If it becomes the active plan, Tweaks → Find bad tweaks offers to switch back to Balanced.";
        if (PowercfgImport().IsMatch(args))
            return f(ScriptArea.PowerPlan, "Imports its own power plan from a file", ScriptVerdict.Placebo, StrayWhy(), false, stray?.Id);
        if (PlanGuid().Match(args) is { Success: true } g && Guid.TryParse(g.Groups["g"].Value, out var scheme))
        {
            var plan = CatalogMatcher.ForPowerPlan(line, text, scheme, decoded);
            // Copying a plan PC Tidy doesn't know (not Windows' own, not in the research): the finder's "stray power plan".
            if (g.Value.Contains("duplicatescheme", StringComparison.OrdinalIgnoreCase) && plan is { MatchedId: null, Verdict: ScriptVerdict.Unknown })
                return f(ScriptArea.PowerPlan, $"Copies the power plan {scheme:B}", ScriptVerdict.Placebo, StrayWhy(), false, stray?.Id);
            return plan;
        }
        if (CatalogMatcher.ForCommand(line, text, "powercfg " + args, ScriptArea.PowerPlan, decoded) is { } fromTable) return fromTable;
        var norm = CatalogMatcher.NormalizeCommand(args);
        if (norm.StartsWith("-h off", StringComparison.Ordinal) || norm.StartsWith("/h off", StringComparison.Ordinal) || norm.Contains("hibernate off", StringComparison.Ordinal))
            return f(ScriptArea.PowerPlan, "Turns off hibernation (and Fast Startup)", ScriptVerdict.Situational, "It frees the space hiberfil.sys uses; laptops lose hibernation. \"powercfg -h on\" undoes it.", false, null);
        if (norm.StartsWith("/l", StringComparison.Ordinal) || norm.StartsWith("-l", StringComparison.Ordinal) || norm.StartsWith("/q", StringComparison.Ordinal)
            || norm.StartsWith("-q", StringComparison.Ordinal) || norm.StartsWith("/getactivescheme", StringComparison.Ordinal) || norm.StartsWith("-getactivescheme", StringComparison.Ordinal))
            return null; // read-only listing
        return f(ScriptArea.PowerPlan, "Changes a power setting", ScriptVerdict.Unknown, "PC Tidy has no research on this exact power setting.", false, null);
    }

    private static ScriptFinding? FsutilCommand(int line, string text, string name, string val, Func<ScriptArea, string, ScriptVerdict, string, bool, string?, ScriptFinding> f)
    {
        var n = name.ToLowerInvariant();
        return n switch
        {
            "disabledeletenotify" when val.Trim() == "1" => f(ScriptArea.Files, "Turns TRIM off", ScriptVerdict.Harmful,
                BadTweakCatalog.Find("storage.trim-off")?.WhyBad ?? "SSDs slow down and wear faster without TRIM.", false, "storage.trim-off"),
            "disabledeletenotify" => f(ScriptArea.Files, "Turns TRIM on", ScriptVerdict.Proven, "That's Windows' default.", false, null),
            "disablelastaccess" or "memoryusage" or "mftzone" or "disable8dot3" => f(ScriptArea.Files, $"Changes the NTFS setting '{name}'", ScriptVerdict.Placebo,
                "No measured gain on a modern PC (Community tweaks research).", false, null),
            _ => f(ScriptArea.Files, $"Changes the NTFS setting '{name}'", ScriptVerdict.Unknown, "PC Tidy has no research on this file-system setting.", false, null),
        };
    }

    private static ScriptFinding? SchtasksCommandFinding(int line, string text, string args, bool decoded, Func<ScriptArea, string, ScriptVerdict, string, bool, string?, ScriptFinding> f)
    {
        var t = ArgTokens.Split(args);
        string? name = null;
        bool create = false, change = false, delete = false, disable = false, enable = false;
        for (int i = 0; i < t.Count; i++)
        {
            switch (t[i].ToLowerInvariant())
            {
                case "/tn": name = i + 1 < t.Count ? t[++i] : null; break;
                case "/create": create = true; break;
                case "/change": change = true; break;
                case "/delete": delete = true; break;
                case "/disable": disable = true; break;
                case "/enable": enable = true; break;
            }
        }
        if (create)
            return f(ScriptArea.ScheduledTask, $"Creates the scheduled task '{name}'", ScriptVerdict.Unknown,
                "A task runs by itself later, even after you close the script. Optimizer packs use this to re-apply their changes.", false, null);
        if (delete) return f(ScriptArea.ScheduledTask, $"Deletes the scheduled task '{name}'", ScriptVerdict.Unknown, "A deleted task doesn't come back by itself.", false, null);
        if (change && name is not null && (disable || enable)) return CatalogMatcher.ForTask(line, text, name, disable, decoded);
        return null;
    }

    private static ScriptFinding FileDelete(string args, Func<ScriptArea, string, ScriptVerdict, string, bool, string?, ScriptFinding> f)
    {
        var a = args.ToLowerInvariant();
        if (a.Contains("prefetch"))
            return f(ScriptArea.Files, "Deletes the Prefetch folder", ScriptVerdict.Placebo, "Windows rebuilds it, and apps start slower until it has. No speed gain.", false, null);
        if (a.Contains("softwaredistribution") || a.Contains("catroot2"))
            return f(ScriptArea.Files, "Deletes Windows Update's working folder", ScriptVerdict.Unknown,
                "It can fix a stuck update, but deleting loses the update history. PC Tidy renames these folders instead, so they can come back.", false, null);
        if (a.Contains("system32") || a.Contains("syswow64") || a.Contains("winsxs") || a.Contains("\\drivers"))
            return f(ScriptArea.Files, "Deletes Windows system files", ScriptVerdict.Harmful, "Windows can stop starting or updating. There's no undo.", true, null);
        if (a.Contains("temp") || a.Contains("tmp"))
            return f(ScriptArea.Files, "Deletes temporary files", ScriptVerdict.Situational,
                "Harmless (files in use are skipped), but it can't be undone. Disk cleanup does the same with a quarantine.", false, null);
        return f(ScriptArea.Files, $"Deletes files ({Clip(args.Trim())})", ScriptVerdict.Unknown, "Deleted files don't go to the Recycle Bin: there's no undo.", false, null);
    }

    /// <summary><c>pnputil /disable-device</c> or <c>Disable-PnpDevice</c>: system devices are the finder's "System devices are disabled".</summary>
    private static ScriptFinding DeviceOff(string text, Match m, Func<ScriptArea, string, ScriptVerdict, string, bool, string?, ScriptFinding> f)
    {
        var name = m.Groups["id"].Success && !m.Groups["id"].Value.StartsWith('/') ? m.Groups["id"].Value
            : DeviceName().Match(text) is { Success: true } n ? n.Groups["n"].Value : null;
        name = name?.Trim('"', '\'', ' ');
        var what = name is { Length: > 0 } ? $"Disables the device '{Clip(name)}' in Device Manager" : "Disables a device in Device Manager";
        var check = BadTweakCatalog.Find("windows.system-devices-disabled");
        if (SystemDeviceHint().IsMatch(text))
            return f(ScriptArea.Other, what, ScriptVerdict.Harmful,
                check?.WhyBad ?? "Latency guides switch off system devices Windows or a driver may need, for no measured gain.", false, check?.Id);
        return f(ScriptArea.Other, what, ScriptVerdict.Unknown,
            "It stays off until someone turns it back on in Device Manager. PC Tidy can't tell which device this is; latency guides switch off system devices that Windows needs, for no measured gain.",
            false, null);
    }

    /// <summary>Which bad-tweak check a Set-MpPreference -Disable… switch belongs to.</summary>
    private static string DefenderCheckFor(string option) => option.ToLowerInvariant() switch
    {
        "disablerealtimemonitoring" or "disableonaccessprotection" or "disablescanonrealtimeenable" => "security.defender-off",
        "disableblockatfirstseen" => "security.defender-protection-reduced",
        _ => "security.defender-only-realtime", // behaviour monitoring, download (IOAV) and script scanning and the rest
    };

    /// <summary>Longest line the piece-joiner reads (longer ones are data blobs, flagged separately).</summary>
    private const int MaxJoinLine = 20_000;

    /// <summary>
    /// The line with command names put back together: PowerShell backticks inside a word (i`e`x), quoted pieces glued with
    /// + or &amp; mid-word ('Inv'+'oke'), batch carets inside a word (p^o^w^e^r). Returns the same instance when nothing changed.
    /// </summary>
    internal static string Unbroken(string text)
    {
        if (text.Length > MaxJoinLine || (text.IndexOf('`') < 0 && text.IndexOf('^') < 0 && text.IndexOf('+') < 0 && text.IndexOf('&') < 0)) return text;
        var t = TickInWord().Replace(text, "");
        t = QuoteJoin().Replace(t, "");
        t = CaretInWord().Replace(t, "");
        return t == text ? text : t;
    }

    /// <summary>Package names on an Appx removal line ("Microsoft.BingNews", "*xbox*"); empty = no filter at all.</summary>
    private static IReadOnlyList<string> AppxNames(string text)
    {
        var names = new List<string>();
        foreach (Match m in AppxNamePattern().Matches(text)) names.Add(m.Groups["n"].Value);
        foreach (Match m in AppxLikePattern().Matches(text)) names.Add(m.Groups["n"].Value);
        if (names.Count == 0 && text.Contains('$')) names.Add("(a list in the script)");
        return names.Select(n => n.Trim('"', '\'')).Where(n => n.Length > 0 && !n.StartsWith('-')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsMicrosoftDomain(string host)
    {
        var h = host.ToLowerInvariant();
        return new[] { "microsoft.com", "windows.com", "live.com", "msn.com", "bing.com", "windows.net", "office.com", "msftncsi.com" }
            .Any(d => h == d || h.EndsWith("." + d, StringComparison.Ordinal));
    }

    private static string From(string text) => Url().Match(text) is { Success: true } u ? $" from {u.Groups["host"].Value}" : " from the internet";

    private static string Clip(string s) => s.Length <= 60 ? s : s[..57] + "…";

    private static string? TryDecode(string b64)
    {
        try
        {
            var bytes = Convert.FromBase64String(b64.Length % 4 == 0 ? b64 : b64 + new string('=', 4 - b64.Length % 4));
            var text = Encoding.Unicode.GetString(bytes);
            // -EncodedCommand is UTF-16LE; anything else isn't readable text.
            return text.Count(c => c == '\0' || char.IsControl(c) && c is not '\r' and not '\n' and not '\t') > text.Length / 10 ? null : text;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ================================================================== patterns (text only)

    [GeneratedRegex(@"(?<![\w-])(?<cmd>Set-ItemProperty|New-ItemProperty|Remove-ItemProperty|Remove-Item|sp|rp|ri)\s+(?<args>[^|;]*(?:HK(?:LM|CU|CR|U|CC|EY_\w+)|Registry::)[^|;]*)", RegexOptions.IgnoreCase)]
    private static partial Regex PsRegistryCmdlet();

    [GeneratedRegex(@"(?<![\w-])reg(?:\.exe)?\s+(?<verb>add|delete|import)\s+(?<args>.+)", RegexOptions.IgnoreCase)]
    private static partial Regex RegExe();

    [GeneratedRegex(@"Registry\]::SetValue\(\s*['""](?<key>[^'""]+)['""]\s*,\s*['""](?<name>[^'""]*)['""]\s*,\s*(?<val>[^,)]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DotNetSetValue();

    [GeneratedRegex(@"\.RegWrite\s*\(?\s*""(?<path>[^""]+)""\s*,\s*(?<val>""[^""]*""|[^,)]+)(?:\s*,\s*""(?<type>REG_\w+)"")?", RegexOptions.IgnoreCase)]
    private static partial Regex WshRegWrite();

    [GeneratedRegex(@"\.RegDelete\s*\(?\s*""(?<path>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex WshRegDelete();

    [GeneratedRegex(@"(?<![\w-])Set-Service\s+(?<args>[^|;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SetService();

    [GeneratedRegex(@"(?<![\w-])sc(?:\.exe)?\s+(?:\\\\\S+\s+)?config\s+(?<name>""[^""]+""|\S+)\s+start\s*=\s*(?<type>[\w-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ScConfig();

    [GeneratedRegex(@"(?<![\w-])(?:sc(?:\.exe)?\s+stop|net(?:\.exe)?\s+stop)\s+(?<name>""[^""]+""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceStop();

    [GeneratedRegex(@"(?<![\w-])(?:Stop-Service|spsv)\b(?<args>[^|;]*)", RegexOptions.IgnoreCase)]
    private static partial Regex StopServiceCmdlet();

    [GeneratedRegex(@"Get-Service\s+(?:-Name\s+)?['""]?(?<name>[\w.-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex GetServiceName();

    [GeneratedRegex(@"(?<![\w-])sc(?:\.exe)?\s+delete\s+(?<name>""[^""]+""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ScDelete();

    [GeneratedRegex(@"(?<![\w-])bcdedit(?:\.exe)?\s+(?<args>.+)", RegexOptions.IgnoreCase)]
    private static partial Regex Bcdedit();

    [GeneratedRegex(@"(?<![\w-])netsh(?:\.exe)?\s+(?<args>.+)", RegexOptions.IgnoreCase)]
    private static partial Regex Netsh();

    [GeneratedRegex(@"netsh(?:\.exe)?\s+(?:advfirewall\s+set\s+\w+\s+state\s+off|firewall\s+set\s+opmode\s+(?:mode=)?disable)", RegexOptions.IgnoreCase)]
    private static partial Regex FirewallOff();

    [GeneratedRegex(@"Set-NetFirewallProfile\b[^|;]*-Enabled\s+(?:False|0|\$false)", RegexOptions.IgnoreCase)]
    private static partial Regex PsFirewallOff();

    [GeneratedRegex(@"(?<![\w-])powercfg(?:\.exe)?\s+(?<args>.+)", RegexOptions.IgnoreCase)]
    private static partial Regex Powercfg();

    [GeneratedRegex(@"(?:-|/)(?:setactive|s|duplicatescheme)\s+(?<g>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})", RegexOptions.IgnoreCase)]
    private static partial Regex PlanGuid();

    [GeneratedRegex(@"fsutil(?:\.exe)?\s+behavior\s+set\s+(?<name>\w+)\s+(?<val>\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex FsutilBehavior();

    [GeneratedRegex(@"fsutil(?:\.exe)?\s+8dot3name\s+strip", RegexOptions.IgnoreCase)]
    private static partial Regex Fsutil8dot3Strip();

    [GeneratedRegex(@"Disable-MMAgent\b", RegexOptions.IgnoreCase)]
    private static partial Regex DisableMmAgent();

    [GeneratedRegex(@"Remove-App(?:x)?(?:Provisioned)?Package\b", RegexOptions.IgnoreCase)]
    private static partial Regex AppxRemove();

    [GeneratedRegex(@"(?:Get-App(?:x)?(?:Provisioned)?Package(?:\s+-AllUsers)?\s+(?:-Name\s+)?|-PackageName\s+|-Package\s+)(?<n>['""]?[\w.*-]+['""]?)", RegexOptions.IgnoreCase)]
    private static partial Regex AppxNamePattern();

    [GeneratedRegex(@"-(?:like|match|eq)\s+(?<n>['""][^'""]+['""])", RegexOptions.IgnoreCase)]
    private static partial Regex AppxLikePattern();

    [GeneratedRegex(@"winget(?:\.exe)?\s+(?:uninstall|remove|rm)\s+(?:--id\s+|-e\s+--id\s+)?(?<id>""[^""]+""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex WingetUninstall();

    [GeneratedRegex(@"(?<![\w-])schtasks(?:\.exe)?\s+(?<args>.+)", RegexOptions.IgnoreCase)]
    private static partial Regex SchtasksCommand();

    [GeneratedRegex(@"Disable-ScheduledTask\s+(?<args>[^|;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DisableTaskCmdlet();

    [GeneratedRegex(@"Register-ScheduledTask\b", RegexOptions.IgnoreCase)]
    private static partial Regex RegisterTask();

    [GeneratedRegex(@"(?:Add|Set)-MpPreference\b[^|;]*?-Exclusion(?<kind>Path|Process|Extension|IpAddress)\s+(?<what>[^-|;][^|;]*?)(?=\s+-\w|$|[|;])", RegexOptions.IgnoreCase)]
    private static partial Regex DefenderExclusion();

    [GeneratedRegex(@"Set-MpPreference\b(?<args>[^|;]{0,2000})", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex MpPreference();

    [GeneratedRegex(@"-(?<opt>Disable\w{1,60})(?:\s{1,10}|:)(?<val>\$true|\$false|1|0)(?!\w)", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex DefenderDisableOption();

    [GeneratedRegex(@"\$PSScriptRoot|\$PWD\b|Get-Location|\$MyInvocation|Split-Path|%~dp0|%cd%|^\.\\?$|^\(?\s{0,3}pwd\s{0,3}\)?$", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex SelfFolder();

    [GeneratedRegex(@"\$env:(?:TEMP|TMP)\b|%(?:TEMP|TMP)%|GetTempPath|(?:^|\\)(?:Windows\\)?Temp(?:\\|$)|AppData\\Local\\Temp", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex TempFolder();

    [GeneratedRegex(@"Set-MpPreference\b[^|;]*?-(?:MAPSReporting\s+(?:0|Disabled)|SubmitSamplesConsent\s+(?:2|NeverSend))", RegexOptions.IgnoreCase)]
    private static partial Regex DefenderCloudOff();

    [GeneratedRegex(@"(?:Uninstall-WindowsFeature|Disable-WindowsOptionalFeature|dism(?:\.exe)?)\b[^|;]*Defender", RegexOptions.IgnoreCase)]
    private static partial Regex DefenderFeatureRemove();

    [GeneratedRegex(@"(?<![\w-])(?:Invoke-WebRequest|iwr|Invoke-RestMethod|irm|wget|curl(?:\.exe)?|Start-BitsTransfer)(?![\w-])|\.Download(?:String|File|Data)(?:Async)?\s*\(|Net\.WebClient|bitsadmin(?:\.exe)?\s+/transfer|certutil(?:\.exe)?\s+[^|;]*-urlcache|MSXML2\.(?:Server)?XMLHTTP|Microsoft\.XMLHTTP|WinHttp\.WinHttpRequest", RegexOptions.IgnoreCase)]
    internal static partial Regex Download();

    [GeneratedRegex(@"(?<![\w-])(?:iex|Invoke-Expression)(?![\w-])|\[scriptblock\]::Create|\bExecuteGlobal\b|\bExecute\s*\(|(?<![\w.])eval\s*\(", RegexOptions.IgnoreCase)]
    internal static partial Regex RunText();

    [GeneratedRegex(@"\|\s*(?:iex|Invoke-Expression)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PipeToIex();

    [GeneratedRegex(@"\s-(?:e|ec|en|enc|enco|encod|encode|encoded|encodedc\w*)\s+['""]?(?<b64>[A-Za-z0-9+/]{16,}={0,2})", RegexOptions.IgnoreCase)]
    private static partial Regex EncodedCommand();

    [GeneratedRegex(@"FromBase64String", RegexOptions.IgnoreCase)]
    private static partial Regex FromBase64();

    [GeneratedRegex(@"[A-Za-z0-9+/]{200,}={0,2}")]
    private static partial Regex LongBase64();

    [GeneratedRegex(@"-(?:WindowStyle|w|win)\s+(?:Hidden|1)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenWindow();

    [GeneratedRegex(@"Set-ExecutionPolicy\s+(?:-ExecutionPolicy\s+)?['""]?(?<policy>Unrestricted|Bypass)['""]?(?:[^|;]*?-Scope\s+['""]?(?<scope>\w+))?", RegexOptions.IgnoreCase)]
    private static partial Regex SetExecutionPolicy();

    [GeneratedRegex(@"-(?:ExecutionPolicy|ep|exec)\s+['""]?Bypass", RegexOptions.IgnoreCase)]
    private static partial Regex BypassPolicyArg();

    [GeneratedRegex(@"drivers[\\/]+etc[\\/]+hosts", RegexOptions.IgnoreCase)]
    private static partial Regex HostsPath();

    [GeneratedRegex(@"Add-Content|Set-Content|Out-File|(?<![\w-])ac\s|>>|>|(?<![\w-])echo\s|Copy-Item|(?<![\w-])copy\s|(?<![\w-])type\s|WriteAllText|AppendAllText", RegexOptions.IgnoreCase)]
    private static partial Regex HostsWriteVerb();

    [GeneratedRegex(@"(?:^|[\s""'(])(?:0\.0\.0\.0|127\.0\.0\.1)\s+[A-Za-z0-9-]+\.[A-Za-z0-9.-]+")]
    private static partial Regex HostsLine();

    [GeneratedRegex(@"(?<![\w.-])(?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,}(?![\w.-])")]
    private static partial Regex HostNames();

    [GeneratedRegex(@"(?<![\w-])(?:Remove-Item|ri|rm|del|erase|rd|rmdir)\s+(?<args>[^|;&]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteFiles();

    [GeneratedRegex(@"HK(?:LM|CU|CR|U|CC|EY_\w+):?\\|Registry::", RegexOptions.IgnoreCase)]
    private static partial Regex RegistryPathArg();

    [GeneratedRegex(@"(?<![\w-])takeown(?:\.exe)?\s+[^|;]*(?:windows|system32)", RegexOptions.IgnoreCase)]
    private static partial Regex TakeOwn();

    [GeneratedRegex(@"vssadmin(?:\.exe)?\s+delete\s+shadows|shadowcopy\s+delete|Disable-ComputerRestore", RegexOptions.IgnoreCase)]
    private static partial Regex ShadowDelete();

    [GeneratedRegex(@"wevtutil(?:\.exe)?\s+(?:cl|clear-log)\b|Clear-EventLog\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClearLogs();

    [GeneratedRegex(@"https?://(?<host>[A-Za-z0-9.-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    // ---- added rules: every quantifier is bounded and every pattern has a match timeout -------------------------

    /// <summary>Match timeout of the newer patterns. A line that takes longer is reported as unreadable, never guessed.</summary>
    internal const int RegexTimeoutMs = 250;

    [GeneratedRegex(@"(?<=[A-Za-z])`(?=[A-Za-z])", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex TickInWord();

    [GeneratedRegex(@"(?<=\w)['""]\s{0,3}[+&]\s{0,3}['""](?=\w)", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex QuoteJoin();

    [GeneratedRegex(@"(?<=\w)\^(?=\w)", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex CaretInWord();

    [GeneratedRegex(@"(?<![\w-])(?:iex|Invoke-Expression|Invoke-WebRequest|Invoke-RestMethod|DownloadString|DownloadFile|Net\.WebClient|Start-Process|Add-MpPreference|Set-MpPreference|FromBase64String|EncodedCommand|powershell|bitsadmin|certutil|WScript\.Shell|Shell\.Application)(?![\w-])", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex SensitiveName();

    [GeneratedRegex(@"(?:\[char\]\s{0,3}\(?\s{0,3}\d{2,3}\s{0,3}\)?\s{0,3}[+,]\s{0,3}){4,40}|\[char\[\]\]\s{0,3}\(\s{0,3}\d{2,3}(?:\s{0,3},\s{0,3}\d{2,3}){4,40}|(?:Chr[WB]?\(\s{0,3}\d{2,3}\s{0,3}\)\s{0,3}[&+]\s{0,3}){4,40}|fromCharCode\(\s{0,3}\d{2,3}(?:\s{0,3},\s{0,3}\d{2,3}){4,40}", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex CharCodes();

    [GeneratedRegex(@"(?<![\w-])copy(?:\.exe)?\s{1,10}/b\s{1,10}[^&|\r\n]{1,1000}?\+\s{0,5}(?![,\s])[^&|\r\n]{1,1000}", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex CopyJoin();

    [GeneratedRegex(@"\.(?:exe|dll|scr|ps1|bat|cmd|vbs|js|zip|7z|rar|msi|cab)\b|\bpart\d|\.part\b|\bchunk|\bpiece|\.0\d\d\b", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex JoinedPieceHint();

    [GeneratedRegex(@"(?<![\w-])pnputil(?:\.exe)?\s{1,10}[/-]disable-device\s{1,10}(?<id>""[^""\r\n]{1,300}""|[^\s""]{1,300})|(?<![\w-])Disable-PnpDevice\b", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex DisableDevice();

    [GeneratedRegex(@"-(?:FriendlyName|InstanceId|Class|Name)\s{1,5}(?<n>'[^'\r\n]{1,200}'|""[^""\r\n]{1,200}""|[^\s|;]{1,200})|/(?:class|deviceid)\s{1,5}(?<n>""[^""\r\n]{1,200}""|[^\s]{1,200})", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex DeviceName();

    [GeneratedRegex(@"High\s{0,3}precision\s{0,3}event|\bHPET\b|PNP0103|System\s{0,3}timer|PNP0100|Kernel\s{0,3}Debug|Composite\s{0,3}Bus|\bUMBus\b|NDIS\s{0,3}Virtual|Remote\s{0,3}Desktop\s{0,3}Device|Virtual\s{0,3}Drive\s{0,3}Enumerator|Programmable\s{0,3}interrupt|Direct\s{0,3}memory\s{0,3}access|GS\s{0,3}Wavetable|System\s{0,3}speaker|\bSM\s{0,3}Bus\b|Management\s{0,3}Engine|\bACPI\\|(?:-Class|/class)\s{1,5}['""]?System\b", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex SystemDeviceHint();

    [GeneratedRegex(@"(?<![\w-])attrib(?:\.exe)?\s{1,10}(?<args>[^|&;\r\n]{1,1000})", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex AttribCommand();

    [GeneratedRegex(@"(?<!\w)\+r(?!\w)", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex AttribPlusR();

    [GeneratedRegex(@"\.IsReadOnly\s{0,3}=\s{0,3}\$true|-Name\s{1,5}['""]?IsReadOnly['""]?\s{1,5}-Value\s{1,5}\$true|\.Attributes\s{0,3}(?:=|\+=|-bor)[^\r\n]{0,80}ReadOnly", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex PsReadOnly();

    [GeneratedRegex(@"""[^""\r\n]{1,260}\.(?:ini|cfg|json|xml|txt|vdf|conf)""|'[^'\r\n]{1,260}\.(?:ini|cfg|json|xml|txt|vdf|conf)'|[^\s""'|;()=]{1,260}\.(?:ini|cfg|json|xml|txt|vdf|conf)(?!\w)", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex ConfigFile();

    [GeneratedRegex(@"^\s{0,10}[/-]import\b", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex PowercfgImport();
}
