using System.Diagnostics;
using System.Text.RegularExpressions;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

public enum ExternalTargetKind { SettingsPage, DefenderPage, SteamScreen, WebPage, Folder, StorePage, LauncherScreen }

/// <summary>
/// OpenExternal: opens a Settings page, Windows Security page, Microsoft Store page, a launcher screen (Steam open /
/// uninstall, Epic store/library, GOG Galaxy game view), an https page or an existing local folder.
/// Harmless and not undoable. Anything else is refused: no other URI schemes or verbs (no steam://run), no files,
/// no network shares or device paths, no arguments. Never opened from the elevated helper (a browser must never run as admin).
/// </summary>
public sealed class OpenExternalHandler : IActionHandler
{
    private static readonly Regex SettingsUri = new(@"^ms-settings:[A-Za-z0-9\-_.]*(\?[A-Za-z0-9\-_.=&%]*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex DefenderUri = new(@"^windowsdefender:(//[A-Za-z0-9\-_./]*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex SteamUri = new(@"^steam://(open/[A-Za-z0-9_\-]+|uninstall/\d{1,10})$", RegexOptions.CultureInvariant);
    private static readonly Regex StoreUri = new(@"^ms-windows-store://[A-Za-z]{1,32}/?(\?[A-Za-z0-9\-_.=&%]*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex EpicUri = new(@"^com\.epicgames\.launcher://store(/[A-Za-z0-9\-_]{1,80}){0,5}$", RegexOptions.CultureInvariant);
    private static readonly Regex GogUri = new(@"^goggalaxy://openGameView/[A-Za-z0-9_\-]{1,40}$", RegexOptions.CultureInvariant);

    public bool CanHandle(PlanItem item) => item.Kind == ActionKind.OpenExternal;

    /// <summary>Validates a target. Pure apart from checking whether a folder exists.</summary>
    public static bool TryValidate(string? target, out ExternalTargetKind kind, out string problem)
    {
        kind = default;
        problem = "";
        var t = target?.Trim() ?? "";
        if (t.Length == 0 || t.Length > 2048 || t.Any(char.IsControl)) { problem = "Nothing valid to open."; return false; }

        if (SettingsUri.IsMatch(t)) { kind = ExternalTargetKind.SettingsPage; return true; }
        if (DefenderUri.IsMatch(t)) { kind = ExternalTargetKind.DefenderPage; return true; }
        if (SteamUri.IsMatch(t)) { kind = ExternalTargetKind.SteamScreen; return true; }
        if (StoreUri.IsMatch(t)) { kind = ExternalTargetKind.StorePage; return true; }
        if (EpicUri.IsMatch(t)) { kind = ExternalTargetKind.LauncherScreen; return true; }
        if (GogUri.IsMatch(t)) { kind = ExternalTargetKind.LauncherScreen; return true; }

        if (t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
                && uri.Host.Length > 0 && !t.Any(c => char.IsWhiteSpace(c) || c is '"' or '<' or '>' or '^' or '`'))
            {
                kind = ExternalTargetKind.WebPage;
                return true;
            }
            problem = "This web address doesn't look right, so PC Tidy won't open it.";
            return false;
        }

        if (PathArgs.IsPlainLocal(t, allowDriveRoot: true) && Directory.Exists(t))
        {
            kind = ExternalTargetKind.Folder;
            return true;
        }

        problem = "PC Tidy only opens Settings pages, Windows Security, the Microsoft Store, game launchers, secure web pages and existing folders.";
        return false;
    }

    public Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (ctx.Mode == ExecutionMode.Simulate) return SimulateAsync(step, ctx, ct);
        var target = step.Item.Arg("target").Trim();
        if (!TryValidate(target, out var kind, out var problem)) return Task.FromResult(step.Skipped(problem));
        if (ctx.Locations.IsSandbox) return Task.FromResult(step.SandboxSimulated($"Would open {target}"));
        if (ctx.IsElevated) return Task.FromResult(step.Skipped("PC Tidy doesn't open pages from its admin helper."));

        try
        {
            var psi = kind == ExternalTargetKind.Folder
                ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false, ArgumentList = { target } }
                : new ProcessStartInfo(target) { UseShellExecute = true };
            using var _ = Process.Start(psi);
            return Task.FromResult(step.Done($"Opened {target}."));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return Task.FromResult(step.Skipped(kind == ExternalTargetKind.SteamScreen
                ? "Steam isn't installed, so it couldn't be opened."
                : "Windows couldn't open this."));
        }
    }

    public Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var target = step.Item.Arg("target").Trim();
        return Task.FromResult(TryValidate(target, out _, out var problem) ? step.Simulated($"Would open {target}.") : step.Skipped(problem));
    }

    public Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(step.Skipped("Opening a page changes nothing, so there's nothing to undo."));

    /// <summary>Never re-open windows on resume.</summary>
    public Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.FromResult(true);
}
