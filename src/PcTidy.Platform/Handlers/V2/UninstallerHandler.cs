using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>One app as Windows lists it under ...\CurrentVersion\Uninstall (HKLM 64/32-bit or HKCU).</summary>
public sealed record UninstallEntry(string Hive, string KeyName, string? DisplayName, string? UninstallString, string? QuietUninstallString)
{
    public bool SameEntry(UninstallEntry other)
        => Hive.Equals(other.Hive, StringComparison.OrdinalIgnoreCase) && KeyName.Equals(other.KeyName, StringComparison.OrdinalIgnoreCase);
}

public enum UninstallVia { Msi, Winget, Vendor }

/// <summary>
/// An exact uninstall command: an absolute program path plus either an argument list (msiexec, winget) or, for a
/// vendor uninstaller, its registered argument text handed to Windows as-is (never through a shell).
/// <see cref="WithoutAdmin"/>: the admin helper hands it to Explorer to start with the user's normal rights (the
/// uninstaller then asks for admin rights itself, under its own name).
/// </summary>
public sealed record UninstallCommand(UninstallVia Via, string ExePath, IReadOnlyList<string>? Arguments, string? RawArguments)
{
    public bool WithoutAdmin { get; init; }

    public override string ToString() => (Arguments is not null
        ? $"{ExePath} {string.Join(' ', Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}"
        : $"\"{ExePath}\" {RawArguments}".TrimEnd()) + (WithoutAdmin ? " (started without admin rights)" : "");
}

/// <summary>Installed-app facts and the uninstaller run. Real: registry + processes. Tests: a fake.</summary>
public interface IUninstallBackend
{
    bool TouchesRealSystem { get; }
    bool FileExists(string path);
    /// <summary>Where winget would be (the App Installer alias); may not exist.</summary>
    string WingetPath { get; }
    IReadOnlyList<UninstallEntry> Entries();

    /// <summary>
    /// Why this program must not run with admin rights (null = it may): an account without admin rights could change it,
    /// a file beside it, or a folder above it (<see cref="AclSafety.CheckProgram"/>).
    /// </summary>
    string? AdminRunProblem(string exePath);

    /// <summary>
    /// Runs and waits. With <paramref name="asAdmin"/> (a vendor uninstaller in the admin helper) the program file is
    /// held open read-only and <see cref="AdminRunProblem"/> is checked again right before it starts; an unsafe answer
    /// throws <see cref="UnsafeProgramException"/> and nothing runs.
    /// </summary>
    Task<ProcessResult> RunAsync(UninstallCommand command, bool asAdmin, TimeSpan timeout, string? logPath, CancellationToken ct);

    /// <summary>Starts the command through Explorer with the user's normal rights, without waiting (no handle comes back).</summary>
    void LaunchWithoutAdmin(UninstallCommand command);

    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class WindowsUninstallBackend : IUninstallBackend
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public bool TouchesRealSystem => true;
    public bool FileExists(string path) => File.Exists(path);
    public string WingetPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");

    public IReadOnlyList<UninstallEntry> Entries()
    {
        var list = new List<UninstallEntry>();
        Read(list, "HKLM", RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry64));
        Read(list, "HKLM", RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry32));
        Read(list, "HKCU", RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.CurrentUser, RegistryView.Registry64));
        return list;
    }

    private static void Read(List<UninstallEntry> list, string hive, RegistryKey root)
    {
        using (root)
        using (var key = root.OpenSubKey(UninstallKey))
        {
            foreach (var name in key?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                try
                {
                    using var app = key!.OpenSubKey(name);
                    if (app is null) continue;
                    list.Add(new UninstallEntry(hive, name, app.GetValue("DisplayName") as string, app.GetValue("UninstallString") as string, app.GetValue("QuietUninstallString") as string));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
    }

    public string? AdminRunProblem(string exePath) => AclSafety.CheckProgram(exePath);

    public Task<ProcessResult> RunAsync(UninstallCommand command, bool asAdmin, TimeSpan timeout, string? logPath, CancellationToken ct)
    {
        if (command.Arguments is not null) return ProcessRunner.RunAsync(command.ExePath, command.Arguments, timeout, logPath, ct);
        return asAdmin
            ? ProcessRunner.RunRawVerifiedAsync(command.ExePath, command.RawArguments ?? "", () => AdminRunProblem(command.ExePath), timeout, logPath, ct)
            : ProcessRunner.RunRawAsync(command.ExePath, command.RawArguments ?? "", timeout, logPath, ct);
    }

    public void LaunchWithoutAdmin(UninstallCommand command)
    {
        var arguments = command.Arguments is not null
            ? string.Join(' ', command.Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))
            : command.RawArguments ?? "";
        ShellInterop.ShellExecuteAsExplorer(command.ExePath, arguments, Path.GetDirectoryName(command.ExePath) ?? Environment.SystemDirectory);
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}

/// <summary>
/// RunUninstaller. Args: "msiProductCode" ({GUID}) and/or "wingetId" and/or "uninstallString", plus "displayName".
/// <para>
/// Everything must point at ONE app Windows really lists under Uninstall (in the admin helper: a machine-wide HKLM
/// entry): the MSI code is that entry's key name, the uninstall string is that entry's own command. Details that point
/// at different entries, or at nothing listed, are refused, and only that entry's command is ever run.
/// </para>
/// Preference: <c>msiexec.exe /x {GUID} /norestart</c>, then <c>winget uninstall --id X --exact --silent --disable-interactivity</c>
/// (never in the admin helper: winget lives in a folder the user can change; it asks for admin rights itself per
/// installer), then the vendor's command split safely into program + arguments (no cmd.exe, no script hosts). In the
/// admin helper a vendor uninstaller runs with admin rights only if no account without admin rights can change it or
/// its folders (checked again, with the file held open, right before it starts); otherwise it is started through
/// Explorer without admin rights and asks for them itself. The next option is only tried when the previous one says
/// "not found". It waits, then decides by exit code plus "is it still listed?". NOT reversible.
/// </summary>
public sealed partial class UninstallerHandler(IUninstallBackend backend) : SystemActionHandler
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromHours(1);
    internal static readonly TimeSpan ListingGrace = TimeSpan.FromSeconds(60);
    /// <summary>How long PC Tidy watches for an uninstaller it started without admin rights (the user answers its prompts).</summary>
    internal static readonly TimeSpan WithoutAdminWait = TimeSpan.FromMinutes(15);

    private const int MsiUnknownProduct = 1605;
    private const int MsiUserCancelled = 1602;
    private const int WingetNoApplicationsFound = unchecked((int)0x8A150014);
    private static readonly int[] SuccessCodes = { 0, 3010, 1641 };

    /// <summary>Programs an uninstall string may never start: shells and script hosts.</summary>
    private static readonly string[] ForbiddenPrograms = { "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "bash", "wsl", "conhost", "explorer" };

    internal const string NotSameApp = "These uninstall details don't all belong to the same installed app, so PC Tidy won't run them.";
    internal const string WingetNotInHelper = "PC Tidy's admin helper never runs winget. Run this uninstall without admin rights (winget asks for them itself).";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_+]{0,127}$", RegexOptions.CultureInvariant)] private static partial Regex WingetId();
    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", RegexOptions.CultureInvariant)] private static partial Regex GuidPattern();

    public UninstallerHandler() : this(new WindowsUninstallBackend()) { }

    protected override ActionKind Kind => ActionKind.RunUninstaller;
    protected override bool TouchesRealSystem => backend.TouchesRealSystem;
    protected override bool NeedsElevation(PlanItem item) => item.NeedsAdmin;
    protected override bool PerUserOnly(PlanItem item) => !item.NeedsAdmin;

    private static string Display(PlanItem item) => item.Arg("displayName").Trim() is { Length: > 0 } d ? d : "this app";
    private static string Code(PlanItem item) => item.Arg("msiProductCode").Trim();
    private static string Winget(PlanItem item) => item.Arg("wingetId").Trim();
    private static string Vendor(PlanItem item) => item.Arg("uninstallString").Trim();

    protected override string? Validate(PlanItem item)
    {
        if (item.Reversible) return "An uninstall can't be undone, so the step must say so.";
        var code = Code(item);
        if (code.Length > 0 && !Guid.TryParseExact(code, "B", out _)) return "The app's installer code is not valid.";
        if (Winget(item).Length > 0 && !WingetId().IsMatch(Winget(item))) return "The app's winget id is not valid.";
        var vendor = Vendor(item);
        if (vendor.Length > 2048 || vendor.Any(char.IsControl)) return "The app's uninstall command is not valid.";
        if (code.Length == 0 && Winget(item).Length == 0 && vendor.Length == 0) return "This app didn't tell Windows how to uninstall it.";
        return null;
    }

    protected override string Describe(PlanItem item)
    {
        var (commands, problem) = Plan(item, elevated: item.NeedsAdmin, userProfile: null);
        return commands.Count > 0 ? $"Would run: {commands[0]}" : $"Would not uninstall {Display(item)}: {problem}";
    }

    private static bool Same(string? a, string b) => a is not null && a.Trim().Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The one listed entry this item refers to, or null with the reason. Without an MSI code or uninstall string
    /// (winget only) there is no entry to find and no problem.
    /// </summary>
    internal (UninstallEntry? Entry, string? Problem) Resolve(PlanItem item, bool elevated)
    {
        var code = Code(item);
        var vendor = Vendor(item);
        if (code.Length == 0 && vendor.Length == 0) return (null, null);

        var listed = backend.Entries().Where(e => !elevated || e.Hive == "HKLM").ToList();
        var byCode = code.Length > 0 ? listed.FirstOrDefault(e => e.KeyName.Equals(code, StringComparison.OrdinalIgnoreCase)) : null;
        var byVendor = vendor.Length > 0 ? listed.Where(e => Same(e.UninstallString, vendor) || Same(e.QuietUninstallString, vendor)).ToList() : new List<UninstallEntry>();

        if (code.Length > 0 && vendor.Length > 0)
        {
            if (byCode is not null && byVendor.Any(byCode.SameEntry)) return (byCode, null);
            if (byCode is null && byVendor.Count == 0) return (null, NotListed(item, elevated, vendor.Length > 0));
            return (null, NotSameApp);
        }
        if (code.Length > 0) return byCode is not null ? (byCode, null) : (null, NotListed(item, elevated, false));
        return byVendor.Count > 0 ? (byVendor[0], null) : (null, NotListed(item, elevated, true));
    }

    private static string NotListed(PlanItem item, bool elevated, bool hasCommand) => hasCommand
        ? elevated
            ? "PC Tidy's admin helper only runs uninstall commands that Windows lists for an app installed for everyone."
            : "PC Tidy only runs uninstall commands that Windows lists for an installed app."
        : $"{Display(item)} isn't listed as installed any more.";

    /// <summary>The commands to try, in order. Empty with a reason when nothing may run.</summary>
    internal (IReadOnlyList<UninstallCommand> Commands, string? Problem) Plan(PlanItem item, bool elevated, string? userProfile)
    {
        var (entry, problem) = Resolve(item, elevated);
        if (problem is not null) return (Array.Empty<UninstallCommand>(), problem);

        var commands = new List<UninstallCommand>();
        var code = Code(item);
        if (entry is not null && code.Length > 0)
            commands.Add(new UninstallCommand(UninstallVia.Msi, Path.Combine(Environment.SystemDirectory, "msiexec.exe"), new[] { "/x", code.ToUpperInvariant(), "/norestart" }, null));

        if (Winget(item).Length > 0)
        {
            if (elevated) problem = WingetNotInHelper;
            else if (backend.FileExists(backend.WingetPath))
                commands.Add(new UninstallCommand(UninstallVia.Winget, backend.WingetPath,
                    new[] { "uninstall", "--id", Winget(item), "--exact", "--silent", "--disable-interactivity" }, null));
        }

        var vendor = Vendor(item);
        if (entry is not null && vendor.Length > 0)
        {
            // The entry's own registered text (it equals the item's, compared above).
            var registered = Same(entry.UninstallString, vendor) ? entry.UninstallString! : entry.QuietUninstallString!;
            if (!ParseVendor(registered.Trim(), backend.FileExists, out var command, out var why)) problem = why;
            else if (command!.Via == UninstallVia.Msi) commands.Add(command);
            else commands.Add(elevated && AdminProblem(command.ExePath, userProfile) is not null ? command with { WithoutAdmin = true } : command);
        }

        if (commands.Count == 0)
            problem ??= Winget(item).Length > 0 && code.Length == 0 && vendor.Length == 0 ? "winget isn't installed on this PC." : $"{Display(item)} isn't listed as installed any more.";
        return (commands.DistinctBy(c => c.ToString()).ToList(), commands.Count == 0 ? problem : null);
    }

    /// <summary>
    /// Splits a registered uninstall command into program + argument text. A quoted program path is taken as is;
    /// an unquoted one is the shortest prefix ending in ".exe" that exists. MsiExec commands become "/x {GUID}" (never /I).
    /// </summary>
    internal static bool ParseVendor(string commandLine, Func<string, bool> fileExists, out UninstallCommand? command, out string? problem)
    {
        command = null;
        problem = "PC Tidy couldn't read this app's uninstall command safely. Use Settings > Apps instead.";
        var s = commandLine.Trim();
        string exe, rest;
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end <= 1) return false;
            exe = s[1..end];
            rest = s[(end + 1)..].Trim();
        }
        else
        {
            exe = "";
            rest = "";
            for (var i = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); i >= 0; i = s.IndexOf(".exe", i + 4, StringComparison.OrdinalIgnoreCase))
            {
                var end = i + 4;
                if (end < s.Length && s[end] != ' ') continue;
                var candidate = s[..end];
                var bare = !candidate.Contains('\\');
                if (bare || fileExists(candidate)) { exe = candidate; rest = s[end..].Trim(); break; }
            }
            if (exe.Length == 0) return false;
        }

        var name = Path.GetFileNameWithoutExtension(exe);
        if (name.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            var guid = GuidPattern().Match(rest);
            if (!guid.Success) return false;
            command = new UninstallCommand(UninstallVia.Msi, Path.Combine(Environment.SystemDirectory, "msiexec.exe"), new[] { "/x", guid.Value.ToUpperInvariant(), "/norestart" }, null);
            problem = null;
            return true;
        }
        if (ForbiddenPrograms.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            problem = "This app's uninstall command runs a command shell or script, so PC Tidy won't run it. Use Settings > Apps instead.";
            return false;
        }
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !PathArgs.IsPlainLocal(exe) || !fileExists(exe))
        {
            problem = "This app's uninstaller isn't on this PC any more. Use \"Find leftovers\" instead.";
            return false;
        }
        command = new UninstallCommand(UninstallVia.Vendor, exe, null, rest);
        problem = null;
        return true;
    }

    /// <summary>
    /// Why a vendor uninstaller may not run with admin rights (null = it may): it is in the user's profile or a temp
    /// folder, or an account without admin rights could change it, a file beside it, or a folder above it.
    /// </summary>
    private string? AdminProblem(string exe, string? userProfile)
    {
        var temp = Path.GetTempPath();
        var windowsTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        if ((userProfile is not null && PathUtil.IsUnder(exe, userProfile)) || PathUtil.IsUnder(exe, temp) || PathUtil.IsUnder(exe, windowsTemp))
            return "it is in a folder your account can change";
        return backend.AdminRunProblem(exe);
    }

    protected override async Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var item = step.Item;
        var elevated = ctx.IsElevated();
        var (commands, problem) = Plan(item, elevated, ctx.Locations.UserProfile);
        if (commands.Count == 0) return step.Skipped(problem!);
        var entry = Resolve(item, elevated).Entry;
        var name = Display(item);

        var log = Path.Combine(DataPaths.Logs(ctx.Locations.DataRoot), "tools", $"{SafeRunId(ctx)}-{step.Index}-uninstall.log");
        ProcessResult? last = null;
        foreach (var planned in commands)
        {
            ct.ThrowIfCancellationRequested();
            var command = planned;
            if (!command.WithoutAdmin)
            {
                try
                {
                    last = await backend.RunAsync(command, elevated && command.Via == UninstallVia.Vendor, Timeout, log, ct).ConfigureAwait(false);
                }
                catch (UnsafeProgramException)
                {
                    // It became changeable between the plan and the start: never with admin rights.
                    command = command with { WithoutAdmin = true };
                }
            }
            if (command.WithoutAdmin) return await HandOffAsync(step, command, entry, elevated, name, ct).ConfigureAwait(false);

            var notFound = (command.Via == UninstallVia.Winget && last!.ExitCode == WingetNoApplicationsFound)
                           || (command.Via == UninstallVia.Msi && last!.ExitCode == MsiUnknownProduct);
            if (!notFound) break;
            if (entry is not null && !await StillListedAsync(entry, TimeSpan.Zero, ct).ConfigureAwait(false)) break;
        }

        if (last!.TimedOut) return step.Failed($"The uninstaller for {name} is still running. It was left to finish on its own.");
        if (last.ExitCode == MsiUserCancelled) return step.Skipped($"The uninstaller for {name} was cancelled. Nothing was removed.");
        if (entry is not null)
        {
            // Many uninstallers hand over to a copy of themselves and return at once: give Windows' list a moment.
            if (!await StillListedAsync(entry, ListingGrace, ct).ConfigureAwait(false)) return step.Done($"Uninstalled {name}.");
            return SuccessCodes.Contains(last.ExitCode)
                ? step.Failed($"The uninstaller finished, but {name} is still installed. It may have been cancelled or need a restart.")
                : step.Failed($"The uninstaller for {name} reported a problem (code {last.ExitCode}). Details are in the log.");
        }
        return SuccessCodes.Contains(last.ExitCode)
            ? step.Done($"Uninstalled {name}.")
            : step.Failed($"The uninstaller for {name} reported a problem (code {last.ExitCode}). Details are in the log.");
    }

    /// <summary>
    /// The admin helper won't run this uninstaller with admin rights: it is started through Explorer with the user's
    /// normal rights (it asks for admin rights itself, showing its own name), and PC Tidy watches Windows' list.
    /// </summary>
    private async Task<StepRecord> HandOffAsync(StepRecord step, UninstallCommand command, UninstallEntry? entry, bool elevated, string name, CancellationToken ct)
    {
        if (entry is null) return step.Skipped($"PC Tidy won't run the uninstaller for {name} with admin rights, and can't tell when it would be done.");
        backend.LaunchWithoutAdmin(command);
        if (!await StillListedAsync(entry, WithoutAdminWait, ct).ConfigureAwait(false))
            return step.Done($"Uninstalled {name}. Its uninstaller was started without admin rights because other programs could change its folder.");
        return step.Failed($"PC Tidy started the uninstaller for {name} without admin rights (other programs could change its folder), " +
                           "but it's still installed. Finish it in its own window, or it may have been cancelled.");
    }

    private bool IsListed(UninstallEntry entry) => backend.Entries().Any(entry.SameEntry);

    private async Task<bool> StillListedAsync(UninstallEntry entry, TimeSpan grace, CancellationToken ct)
    {
        var poll = TimeSpan.FromSeconds(3);
        for (var waited = TimeSpan.Zero; ; waited += poll)
        {
            if (!IsListed(entry)) return false;
            if (waited >= grace) return true;
            await backend.DelayAsync(poll, ct).ConfigureAwait(false);
        }
    }

    protected override Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(Code(step.Item).Length + Vendor(step.Item).Length > 0 && Resolve(step.Item, ctx.IsElevated()) is { Entry: null, Problem: not NotSameApp });
}
