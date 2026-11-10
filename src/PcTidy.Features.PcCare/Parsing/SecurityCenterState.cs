namespace PcTidy.Features.PcCare.Parsing;

public enum ProductRunState { Off, On, Snoozed, Expired, Unknown }

/// <summary>A decoded Security Center <c>productState</c>.</summary>
public sealed record DecodedProductState(ProductRunState State, bool IsMicrosoft, bool SignaturesOutOfDate)
{
    /// <summary>Actively protecting right now (on, and not snoozed or expired).</summary>
    public bool IsActive => State == ProductRunState.On;
}

/// <summary>
/// Decodes WSC <c>productState</c> (root\SecurityCenter2 AntiVirusProduct). The value packs three fields:
/// bits 12-15 = run state (0x1000 on, 0x0000 off, 0x2000 snoozed, 0x3000 expired), bits 8-11 = owner
/// (0x100 = Microsoft), bits 4-7 = signature status (0x10 = out of date). The top byte is the provider type.
/// </summary>
public static class SecurityCenterState
{
    public static DecodedProductState Decode(uint productState)
    {
        var state = (productState & 0xF000) switch
        {
            0x0000 => ProductRunState.Off,
            0x1000 => ProductRunState.On,
            0x2000 => ProductRunState.Snoozed,
            0x3000 => ProductRunState.Expired,
            _ => ProductRunState.Unknown,
        };
        bool microsoft = (productState & 0x0F00) == 0x0100;
        bool outOfDate = (productState & 0x00F0) == 0x0010;
        return new DecodedProductState(state, microsoft, outOfDate);
    }

    /// <summary>Windows Defender registers itself under this executable (the display name is localized).</summary>
    public static bool IsDefender(string? reportingExe, string? name)
        => (reportingExe?.EndsWith("MsMpeng.exe", StringComparison.OrdinalIgnoreCase) ?? false)
           || (reportingExe?.Contains("windowsdefender://", StringComparison.OrdinalIgnoreCase) ?? false)
           || string.Equals(name, "Windows Defender", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "Microsoft Defender Antivirus", StringComparison.OrdinalIgnoreCase);
}
