using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using PcTidy.Setup.Install;
using PcTidy.Setup.Payload;
using PcTidy.Setup.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using Application = System.Windows.Application;

namespace PcTidy.Setup;

/// <summary>
/// Entry point for both PC Tidy Setup (install) and uninstall.exe (the same program without the payload).
/// See <see cref="SetupArgs"/> for the command line. The window runs with normal rights (asInvoker manifest); the
/// Program Files / HKLM part runs in a copy started with admin rights (<see cref="SetupFlow"/>), which shows no UI,
/// writes no log file (nothing outside Program Files, the all-users Start menu and HKLM) and only returns an exit code.
/// </summary>
public static class Program
{
    public const int ExitOk = 0, ExitFailed = 1, ExitNoPayload = 2, ExitDamaged = 3, ExitAppOpen = 4, ExitNotElevated = 5, ExitDeclined = 6, ExitLeftovers = 7;
    /// <summary>Something on this PC would let another program run inside the admin part (see <see cref="ElevationGuard"/>).</summary>
    public const int ExitUnsafe = 8;
    /// <summary>This Setup has no compiled-in payload hash (a developer build), so its admin part installs nothing.</summary>
    public const int ExitUntrusted = 9;
    /// <summary>An update failed part-way and every replaced file was put back: the previous version is intact.</summary>
    public const int ExitRolledBack = 10;
    /// <summary>An update failed part-way and some files couldn't be put back: Setup must be run again to repair.</summary>
    public const int ExitNeedsRepair = 11;
    /// <summary>PC Tidy is open only in another account on this PC; nothing was changed.</summary>
    public const int ExitAppOpenElsewhere = 12;

    private static string SelfPath => Environment.ProcessPath ?? throw new InvalidOperationException("Can't tell where Setup is running from.");

    [STAThread]
    public static int Main(string[] args)
    {
        var a = SetupArgs.Parse(args);
        if (a.IsElevatedPart)
        {
            // First thing in the admin copy, before any other DLL is loaded by name: System32 only. No WPF from here on.
            ElevationGuard.RestrictDllSearch();
            if (ElevationGuard.InElevatedPart(SelfPath) is not null) return ExitUnsafe;
            return RunElevatedPart(a, InstallEnvironment.Real(), new RunAsSetupElevation().IsElevated, SelfPath);
        }

        SetupLog.Start(a.Uninstall ? "uninstall" : a.Verify ? "verify" : "setup");
        try
        {
            if (a.Verify) return Verify();
            if (a.Uninstall) return RunUninstall(a);
            return RunInstall(a);
        }
        catch (Exception ex)
        {
            SetupLog.Error("Unhandled", ex);
            if (!a.Quiet && !a.Silent && !a.Verify)
                NativeMessage("PC Tidy Setup", "Something went wrong: " + ex.Message + (SetupLog.FilePath is { } log ? "\n\nDetails: " + log : ""));
            return ExitFailed;
        }
    }

    // ------------------------------------------------------------------ the admin part

    /// <summary>
    /// The copy started with admin rights. Install: the payload is copied out of this program file (one handle that
    /// denies writes) into an admin-only folder and must match the hash compiled into this program, not the one in the
    /// file's own trailer (<see cref="StagedPayload"/>); the folder is always the environment's (Program Files) and
    /// uninstall.exe must come from the verified payload. Uninstall: the install folder from the environment, this
    /// program left for Windows to delete at the next restart.
    /// </summary>
    /// <param name="testRequest">Tests: install this request directly (no staging).</param>
    /// <param name="staging">Tests: where to stage and how to check it (real: an admin-only folder in %WINDIR%\Temp).</param>
    /// <param name="expectedSha256">Tests: the "compiled-in" payload hash (real: <see cref="PayloadTrust.Embedded"/>).</param>
    internal static int RunElevatedPart(SetupArgs a, InstallEnvironment env, bool elevated, string selfPath, InstallRequest? testRequest = null,
        StagingOptions? staging = null, byte[]? expectedSha256 = null)
    {
        if (!elevated) return ExitNotElevated;
        try
        {
            if (a.ElevatedUninstall)
            {
                var running = FolderSafety.IsSameOrUnder(selfPath, env.InstallDir) ? selfPath : null;
                var removed = new Uninstaller(env).RemoveProgram(new UninstallRequest { RunningUninstaller = running });
                return removed.Ok ? ExitOk
                    : removed.Outcome == SetupOutcome.AppOpenElsewhere ? ExitAppOpenElsewhere
                    : removed.Notes.Count == 0 ? ExitAppOpen : ExitLeftovers;
            }

            if (testRequest is not null) return Code(new Installer(env).InstallAsync(testRequest).GetAwaiter().GetResult());
            var (staged, problem) = StagedPayload.Create(selfPath, expectedSha256 ?? PayloadTrust.Embedded, staging ?? StagingOptions.Real());
            if (staged is null) return problem;
            using (staged)
            {
                var request = new InstallRequest
                {
                    OpenPayload = staged.Open,
                    UninstallerSource = "", // never the stub of a file in a user folder: the payload's own uninstall.exe
                    StartMenuShortcuts = !a.NoStartMenu,
                    DesktopShortcut = a.Desktop,
                };
                return Code(new Installer(env).InstallAsync(request).GetAwaiter().GetResult());
            }
        }
        catch (Exception)
        {
            return ExitFailed;
        }

        static int Code(SetupResult r) => r.Ok ? ExitOk : r.Outcome switch
        {
            SetupOutcome.AppOpen => ExitAppOpen,
            SetupOutcome.AppOpenElsewhere => ExitAppOpenElsewhere,
            SetupOutcome.RolledBack => ExitRolledBack,
            SetupOutcome.NeedsRepair => ExitNeedsRepair,
            _ => ExitFailed,
        };
    }

    // ------------------------------------------------------------------ install

    private static int Verify()
    {
        AttachConsole(-1);
        var payload = PayloadFile.Find(SelfPath);
        // The admin part installs only a payload that matches the hash compiled into this program, so check that too.
        var code = payload is null ? ExitNoPayload
            : PayloadTrust.Embedded is null ? ExitUntrusted
            : !PayloadTrust.TrailerMatchesEmbedded(payload) ? ExitDamaged
            : payload.Verify() ? ExitOk : ExitDamaged;
        var version = payload is null ? null : ReadPayloadVersion(payload);
        var text = code switch
        {
            ExitOk => $"OK: PC Tidy {version} payload, {payload!.Length:N0} bytes, SHA-256 matches the hash built into Setup.",
            ExitNoPayload => "This exe has no PC Tidy payload (developer build or uninstaller).",
            ExitUntrusted => "This Setup has no payload hash built in (publish it with -p:PcTidyPayloadSha256, as build-release.ps1 does), so it can't install.",
            _ => "The payload is damaged. Please download PC Tidy Setup again.",
        };
        SetupLog.Info(text);
        try { Console.WriteLine(text); } catch { /* no console */ }
        return code;
    }

    internal static string? ReadPayloadVersion(PayloadInfo payload)
    {
        try
        {
            using var zip = new ZipArchive(payload.Open(), ZipArchiveMode.Read);
            if (zip.GetEntry(PayloadDescription.FileName) is not { } e) return null;
            using var r = new StreamReader(e.Open());
            return PayloadDescription.Parse(r.ReadToEnd())?.Version;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return null; }
    }

    private static int RunInstall(SetupArgs a)
    {
        var payload = PayloadFile.Find(SelfPath);
        if (payload is null)
        {
            const string none = "This copy of PC Tidy Setup doesn't contain PC Tidy. Download the full PC Tidy Setup, or build it with build-release.ps1.";
            SetupLog.Info(none);
            if (!a.Silent) NativeMessage("PC Tidy Setup", none);
            return ExitNoPayload;
        }

        if (a.Silent)
        {
            if (!payload.Verify()) { SetupLog.Info("Payload damaged."); return ExitDamaged; }
            var result = new SetupFlow(InstallEnvironment.Real(), new RunAsSetupElevation())
                .InstallAsync(Request(payload, startMenu: true, a.Desktop)).GetAwaiter().GetResult();
            SetupLog.Result(result);
            return result.Ok ? ExitOk : ExitFailed;
        }

        var app = CreateApp();
        return app.Run(SetupWindow.ForInstall(payload, ReadPayloadVersion(payload) ?? "?"));
    }

    internal static InstallRequest Request(PayloadInfo payload, bool startMenu, bool desktop) => new()
    {
        OpenPayload = payload.Open,
        UninstallerSource = payload.ExePath,
        UninstallerLength = payload.StubLength,
        StartMenuShortcuts = startMenu,
        DesktopShortcut = desktop,
    };

    // ------------------------------------------------------------------ uninstall

    private static int RunUninstall(SetupArgs a)
    {
        var env = InstallEnvironment.Real();
        // Runs from Program Files\PC Tidy\uninstall.exe: it can't delete itself, so Windows does at the next restart.
        var running = FolderSafety.IsSameOrUnder(SelfPath, env.InstallDir) ? SelfPath : null;
        if (a.Quiet)
        {
            var result = new SetupFlow(env, new RunAsSetupElevation()).UninstallAsync(keepData: !a.RemoveData, running).GetAwaiter().GetResult();
            SetupLog.Result(result);
            return result.Ok ? ExitOk : ExitFailed;
        }
        var app = CreateApp();
        return app.Run(SetupWindow.ForUninstall(env, running));
    }

    // ------------------------------------------------------------------ UI plumbing

    private static Application CreateApp()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        app.DispatcherUnhandledException += (_, e) =>
        {
            SetupLog.Error("UI", e.Exception);
            e.Handled = true;
            System.Windows.MessageBox.Show("Something went wrong: " + e.Exception.Message, "PC Tidy Setup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        };
        ApplyTheme();
        return app;
    }

    /// <summary>Follows Windows' light/dark setting with PC Tidy's green accent (same colours as the app).</summary>
    private static void ApplyTheme()
    {
        var dark = ApplicationThemeManager.GetSystemTheme() is SystemTheme.Dark or SystemTheme.Glow or SystemTheme.CapturedMotion or SystemTheme.HCBlack;
        var theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        void Accent()
        {
            if (dark) ApplicationAccentColorManager.Apply(Color.FromRgb(0x3C, 0xCB, 0x86), Color.FromRgb(0x4F, 0xCB, 0x8E), Color.FromRgb(0x6F, 0xD9, 0xA4), Color.FromRgb(0x94, 0xE6, 0xBD));
            else ApplicationAccentColorManager.Apply(Color.FromRgb(0x2B, 0xB6, 0x73), Color.FromRgb(0x1E, 0x9A, 0x5E), Color.FromRgb(0x17, 0x80, 0x4D), Color.FromRgb(0x10, 0x66, 0x3C));
        }
        Accent();
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: false);
        Accent();
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    internal static void NativeMessage(string caption, string text)
    {
        try { MessageBoxW(IntPtr.Zero, text, caption, 0x30); } catch { /* nothing more we can do */ }
    }
}
