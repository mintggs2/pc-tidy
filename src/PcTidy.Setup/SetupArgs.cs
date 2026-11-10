namespace PcTidy.Setup;

/// <summary>
/// Command line of PC Tidy Setup / uninstall.exe:
/// <code>
///   (none)                      the install window
///   --silent [--desktop]        install with the defaults, no window (exit code 0 = installed; asks for admin once)
///   --verify                    check the built-in app files are complete; exit 0 = OK
///   --uninstall                 the uninstall window (what Settings → Apps runs)
///   --uninstall --quiet         uninstall without a window, KEEPING the data folder
///   --uninstall --quiet --remove-data   ... and delete the data folder too
/// </code>
/// Internal (the admin copy Setup starts of itself): <c>--install-elevated [--desktop] [--no-start-menu]</c> and
/// <c>--uninstall-elevated</c>. They take no folders: the admin part always works on %ProgramFiles%\PC Tidy, so nothing
/// on the command line can point it at another folder.
/// </summary>
public sealed record SetupArgs
{
    public bool Uninstall { get; init; }
    public bool Quiet { get; init; }
    public bool Silent { get; init; }
    public bool RemoveData { get; init; }
    public bool Desktop { get; init; }
    public bool NoStartMenu { get; init; }
    public bool Verify { get; init; }
    public bool ElevatedInstall { get; init; }
    public bool ElevatedUninstall { get; init; }

    public bool IsElevatedPart => ElevatedInstall || ElevatedUninstall;

    public static SetupArgs Parse(IReadOnlyList<string> args)
    {
        var a = new SetupArgs();
        foreach (var arg in args)
        {
            a = arg.ToLowerInvariant() switch
            {
                "--uninstall" => a with { Uninstall = true },
                "--quiet" => a with { Quiet = true },
                "--silent" or "/silent" or "/verysilent" => a with { Silent = true },
                "--remove-data" => a with { RemoveData = true },
                "--desktop" => a with { Desktop = true },
                "--no-start-menu" => a with { NoStartMenu = true },
                "--verify" => a with { Verify = true },
                Install.SetupFlow.ElevatedInstallSwitch => a with { ElevatedInstall = true },
                Install.SetupFlow.ElevatedUninstallSwitch => a with { ElevatedUninstall = true },
                _ => a, // unknown switches (and any folder) are ignored: never fail an uninstall over a typo
            };
        }
        return a;
    }
}
