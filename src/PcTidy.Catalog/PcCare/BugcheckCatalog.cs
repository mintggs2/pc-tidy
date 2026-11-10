namespace PcTidy.Catalog.PcCare;

/// <summary>Where PC Tidy points you after a crash.</summary>
public enum CrashFixArea
{
    /// <summary>A driver is the usual cause: the Drivers section (problem devices, rollback, official updates).</summary>
    Drivers,
    /// <summary>The graphics driver in particular.</summary>
    GraphicsDriver,
    /// <summary>Damaged Windows files: PC care's Windows repair (DISM then SFC).</summary>
    WindowsRepair,
    /// <summary>A drive or its cable: check drive health, back up.</summary>
    Disk,
    /// <summary>Memory: Windows Memory Diagnostic, undo RAM overclocks (XMP/EXPO).</summary>
    Memory,
    /// <summary>Hardware, heat, power or overclocking.</summary>
    Hardware,
    /// <summary>No clear single cause.</summary>
    Unknown,
}

public sealed record BugcheckInfo(uint Code, string Name, string PlainCause, CrashFixArea Fix);

/// <summary>
/// Common blue-screen stop codes mapped to a plain cause (Microsoft's bug check code reference). Codes with the 0x10000000
/// "has parameters" bit (0x1000007E) map to their base code.
/// </summary>
public static class BugcheckCatalog
{
    public static readonly IReadOnlyList<BugcheckInfo> All = new BugcheckInfo[]
    {
        new(0x0A, "IRQL_NOT_LESS_OR_EQUAL", "A driver used memory it wasn't allowed to.", CrashFixArea.Drivers),
        new(0x19, "BAD_POOL_HEADER", "A driver damaged Windows' memory bookkeeping.", CrashFixArea.Drivers),
        new(0x1A, "MEMORY_MANAGEMENT", "A memory problem: often RAM, sometimes a driver.", CrashFixArea.Memory),
        new(0x1E, "KMODE_EXCEPTION_NOT_HANDLED", "A driver hit an error it couldn't handle.", CrashFixArea.Drivers),
        new(0x3B, "SYSTEM_SERVICE_EXCEPTION", "A driver or system file hit an error.", CrashFixArea.Drivers),
        new(0x50, "PAGE_FAULT_IN_NONPAGED_AREA", "Something read memory that wasn't there: a driver or RAM.", CrashFixArea.Drivers),
        new(0x7A, "KERNEL_DATA_INPAGE_ERROR", "Windows couldn't read data back from the drive.", CrashFixArea.Disk),
        new(0x77, "KERNEL_STACK_INPAGE_ERROR", "Windows couldn't read data back from the drive.", CrashFixArea.Disk),
        new(0x7E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "A driver hit an error it couldn't handle.", CrashFixArea.Drivers),
        new(0x9C, "MACHINE_CHECK_EXCEPTION", "The processor reported a hardware error.", CrashFixArea.Hardware),
        new(0x9F, "DRIVER_POWER_STATE_FAILURE", "A driver didn't respond while the PC slept or woke.", CrashFixArea.Drivers),
        new(0xA0, "INTERNAL_POWER_ERROR", "A power-management problem, often sleep or hibernation.", CrashFixArea.Drivers),
        new(0xC2, "BAD_POOL_CALLER", "A driver misused Windows' memory.", CrashFixArea.Drivers),
        new(0xD1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL", "A driver (often network or storage) used memory it wasn't allowed to.", CrashFixArea.Drivers),
        new(0xEF, "CRITICAL_PROCESS_DIED", "A core Windows process stopped. Damaged system files are a common cause.", CrashFixArea.WindowsRepair),
        new(0xF4, "CRITICAL_OBJECT_TERMINATION", "A core process stopped, often because the drive stopped answering.", CrashFixArea.Disk),
        new(0xFC, "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "A driver tried to run code from protected memory.", CrashFixArea.Drivers),
        new(0x101, "CLOCK_WATCHDOG_TIMEOUT", "A processor core stopped answering: overclocking, heat or power.", CrashFixArea.Hardware),
        new(0x116, "VIDEO_TDR_FAILURE", "The graphics driver stopped responding and couldn't recover.", CrashFixArea.GraphicsDriver),
        new(0x117, "VIDEO_TDR_TIMEOUT_DETECTED", "The graphics driver stopped responding.", CrashFixArea.GraphicsDriver),
        new(0x119, "VIDEO_SCHEDULER_INTERNAL_ERROR", "The graphics driver hit an internal error.", CrashFixArea.GraphicsDriver),
        new(0x124, "WHEA_UNCORRECTABLE_ERROR", "The hardware reported an error it couldn't fix: overclocking, heat or power.", CrashFixArea.Hardware),
        new(0x133, "DPC_WATCHDOG_VIOLATION", "A driver (often storage or SSD firmware) took too long.", CrashFixArea.Drivers),
        new(0x139, "KERNEL_SECURITY_CHECK_FAILURE", "Windows found damaged data, usually from a driver or RAM.", CrashFixArea.Drivers),
        new(0x13A, "KERNEL_MODE_HEAP_CORRUPTION", "A driver damaged Windows' memory.", CrashFixArea.Drivers),
        new(0x154, "UNEXPECTED_STORE_EXCEPTION", "The drive or its driver had a problem.", CrashFixArea.Disk),
        new(0x1D5, "DRIVER_PNP_WATCHDOG", "A device driver took too long to start or stop.", CrashFixArea.Drivers),
    };

    private static readonly Dictionary<uint, BugcheckInfo> ByCode = All.ToDictionary(b => b.Code);

    /// <summary>The entry for a stop code (the 0x10000000 "extended" bit is ignored), or null.</summary>
    public static BugcheckInfo? Find(uint code)
        => ByCode.TryGetValue(code & ~0x10000000u, out var info) ? info : null;

    /// <summary>"0x0000009F" style, as Windows shows it.</summary>
    public static string Format(uint code) => "0x" + code.ToString("X8");
}
