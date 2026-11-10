namespace PcTidy.Catalog.Drivers;

/// <summary>How a Device Manager problem code is shown.</summary>
public enum ProblemKind
{
    /// <summary>Something is wrong and the user can probably fix it.</summary>
    Problem,
    /// <summary>Shown so you know (disabled on purpose, not plugged in): not counted as a problem.</summary>
    Info,
}

/// <summary>One Device Manager error code (CM_PROB_*) in plain English.</summary>
public sealed record ProblemCodeInfo(int Code, string Name, string Cause, string Fix, ProblemKind Kind = ProblemKind.Problem);

/// <summary>
/// Device Manager problem codes PC Tidy explains (Research: "Drivers and system files", Problem codes).
/// The fix text never suggests turning off driver signature enforcement or editing filter values by hand.
/// </summary>
public static class ProblemCodes
{
    /// <summary>"Disabled by you": information, not a problem.</summary>
    public const int Disabled = 22;
    /// <summary>"Not connected": a ghost device; handled in Old devices, not Problems.</summary>
    public const int NotPresent = 45;
    /// <summary>"No driver installed".</summary>
    public const int NoDriver = 28;

    public static readonly IReadOnlyList<ProblemCodeInfo> All = new ProblemCodeInfo[]
    {
        new(1, "Not set up", "Windows didn't finish setting up this device.", "Reinstall the driver: get it from Windows Update or the maker's site."),
        new(3, "Driver may be damaged", "The driver may be damaged, or the PC was low on memory when it loaded.", "Restart your PC. If it comes back, reinstall the driver."),
        new(10, "Can't start", "The device or its driver couldn't start.", "Unplug and replug it (if you can), then update the driver from Windows Update or the maker's tool."),
        new(12, "Not enough resources", "Two devices want the same resources, or the BIOS gave it none.", "Update the BIOS from your motherboard or PC maker, or remove a device you just added."),
        new(14, "Needs a restart", "Windows needs a restart before this device works.", "Restart your PC."),
        new(18, "Needs reinstalling", "The driver needs to be installed again.", "Reinstall the driver from Windows Update or the maker's site."),
        new(19, "Settings damaged", "This device's driver settings in the registry are damaged.", "Reinstall the driver. PC Tidy never edits these settings by hand."),
        new(21, "Being removed", "Windows is removing this device.", "Restart your PC."),
        new(Disabled, "Turned off", "This device was turned off (in Device Manager or by an app).", "Nothing to fix if you turned it off on purpose. Turn it back on in Device Manager if you need it.", ProblemKind.Info),
        new(24, "Not found", "The device isn't there, isn't working properly, or is missing a driver.", "Replug it or install its driver."),
        new(NoDriver, "No driver", "No driver is installed for this device.", "Check Windows Update (optional updates) or the maker's official tool or site."),
        new(31, "Driver won't load", "Windows couldn't load the driver for this device.", "Reinstall the driver from Windows Update or the maker's site."),
        new(32, "Driver turned off", "The driver's service is set to not start.", "Reinstall the driver. If an app or tweak turned it off, undo that change."),
        new(37, "Driver failed", "The driver reported a failure while starting.", "Reinstall or update the driver."),
        new(39, "Driver missing or damaged", "The driver file is missing or damaged.", "Reinstall the driver. If it keeps happening, check your antivirus didn't remove it."),
        new(41, "Driver loaded, device not found", "The driver loaded but couldn't find the hardware.", "Replug the device, then reinstall its driver."),
        new(43, "Device reported a problem", "Windows stopped this device because it reported a problem.", "Unplug and replug it, shut the PC down fully and start it again, then update the driver. It can be a hardware fault."),
        new(NotPresent, "Not connected", "This device isn't plugged in right now.", "Nothing to fix. Old devices can be tidied in Old devices.", ProblemKind.Info),
        new(48, "Blocked driver", "Windows blocked this driver because it's known to cause problems.", "Get a newer driver from Windows Update or the maker's official tool."),
        new(52, "Signature can't be checked", "Windows can't verify the driver's digital signature.", "Get a properly signed driver from Windows Update or the maker. Never turn off driver signature checks."),
    };

    private static readonly IReadOnlyDictionary<int, ProblemCodeInfo> ByCode = All.ToDictionary(p => p.Code);

    /// <summary>The entry for a code, or a generic plain-English entry for codes not in the table.</summary>
    public static ProblemCodeInfo Describe(int code)
        => ByCode.TryGetValue(code, out var info)
            ? info
            : new ProblemCodeInfo(code, $"Problem code {code}", "Windows reported a problem with this device.",
                "Restart your PC, then update or reinstall the driver from Windows Update or the maker's site.");

    /// <summary>True when the code means something is actually wrong (not 0, not "turned off", not "unplugged").</summary>
    public static bool IsProblem(int code) => code != 0 && Describe(code).Kind == ProblemKind.Problem;
}
