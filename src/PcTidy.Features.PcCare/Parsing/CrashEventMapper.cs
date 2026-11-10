using System.Globalization;
using PcTidy.Features.PcCare.Model;

namespace PcTidy.Features.PcCare.Parsing;

/// <summary>One event-log (or Reliability Monitor) record, reduced to what the mapper needs: provider, id, time, properties.</summary>
public sealed record RawEvent(string Provider, int EventId, DateTime TimeUtc, IReadOnlyList<object?> Properties);

/// <summary>
/// Turns event records into crash facts using only provider names, event ids and structured properties (never the
/// localized message text):
/// <list type="bullet">
/// <item>BugCheck / WER-SystemErrorReporting 1001: property 0 is "0x0000009f (0x..., ...)", hex is never localized.</item>
/// <item>Kernel-Power 41: property 0 = BugcheckCode (0 = power loss or hard reset), 1-4 = parameters.</item>
/// <item>WHEA-Logger 18 = fatal hardware error, 19 = corrected hardware error, 47 = corrected memory error.</item>
/// <item>Application Error 1000: 0 = app, 3 = faulting module, 6 = exception code. Application Hang 1002: 0 = app.</item>
/// </list>
/// </summary>
public static class CrashEventMapper
{
    public const string BugCheckProvider = "Microsoft-Windows-WER-SystemErrorReporting";
    public const string LegacyBugCheckProvider = "BugCheck";
    public const string KernelPowerProvider = "Microsoft-Windows-Kernel-Power";
    public const string WheaProvider = "Microsoft-Windows-WHEA-Logger";
    public const string AppErrorProvider = "Application Error";
    public const string AppHangProvider = "Application Hang";

    /// <summary>The (log, provider, ids) the real source queries.</summary>
    public static readonly IReadOnlyList<(string Log, string Provider, int[] Ids)> Queries = new[]
    {
        ("System", BugCheckProvider, new[] { 1001 }),
        ("System", LegacyBugCheckProvider, new[] { 1001 }),
        ("System", KernelPowerProvider, new[] { 41 }),
        ("System", WheaProvider, new[] { 18, 19, 47 }),
        ("Application", AppErrorProvider, new[] { 1000 }),
        ("Application", AppHangProvider, new[] { 1002 }),
    };

    public static CrashEvent? Map(RawEvent e)
    {
        var p = e.Provider ?? "";
        if (e.EventId == 1001 && (Eq(p, BugCheckProvider) || Eq(p, LegacyBugCheckProvider)))
            return new CrashEvent(CrashKind.BlueScreen, e.TimeUtc, BugcheckCode: ParseLeadingHex(Prop(e, 0)));

        if (e.EventId == 41 && Eq(p, KernelPowerProvider))
        {
            uint? code = ToUInt(Prop(e, 0));
            // A Kernel-Power 41 with a stop code is the same blue screen seen from the power side; keep the code.
            return new CrashEvent(CrashKind.UnexpectedShutdown, e.TimeUtc, BugcheckCode: code is 0 ? null : code);
        }

        if (Eq(p, WheaProvider))
            return e.EventId switch
            {
                18 => new CrashEvent(CrashKind.HardwareError, e.TimeUtc),
                19 or 47 => new CrashEvent(CrashKind.HardwareCorrected, e.TimeUtc),
                _ => null,
            };

        if (e.EventId == 1000 && Eq(p, AppErrorProvider))
            return new CrashEvent(CrashKind.AppCrash, e.TimeUtc, AppName: Text(Prop(e, 0)), ModuleName: Text(Prop(e, 3)), ExceptionCode: Hex(Prop(e, 6)));

        if (e.EventId == 1002 && Eq(p, AppHangProvider))
            return new CrashEvent(CrashKind.AppHang, e.TimeUtc, AppName: Text(Prop(e, 0)));

        return null;
    }

    /// <summary>Removes the second copy when the event log and Reliability Monitor report the same thing within a minute.</summary>
    public static IReadOnlyList<CrashEvent> Dedupe(IEnumerable<CrashEvent> events)
    {
        var kept = new List<CrashEvent>();
        foreach (var e in events.OrderByDescending(x => x.TimeUtc))
        {
            bool dup = kept.Any(k => k.Kind == e.Kind && Math.Abs((k.TimeUtc - e.TimeUtc).TotalSeconds) < 60
                                     && string.Equals(k.AppName, e.AppName, StringComparison.OrdinalIgnoreCase));
            if (!dup) kept.Add(e);
        }
        return kept;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static object? Prop(RawEvent e, int index) => index < e.Properties.Count ? e.Properties[index] : null;

    private static string? Text(object? v)
    {
        var s = v?.ToString()?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>"0x0000009f (0x...)" → 0x9F. Null when the text doesn't start with a hex number.</summary>
    internal static uint? ParseLeadingHex(object? v)
    {
        var s = v?.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        int end = 0;
        while (end < s.Length && Uri.IsHexDigit(s[end])) end++;
        if (end == 0 || end > 16) return null;
        return ulong.TryParse(s[..end], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) && n <= uint.MaxValue ? (uint)n : null;
    }

    private static uint? ToUInt(object? v) => v switch
    {
        null => null,
        uint u => u,
        int i => unchecked((uint)i),
        ulong ul => ul <= uint.MaxValue ? (uint)ul : null,
        long l => l is >= 0 and <= uint.MaxValue ? (uint)l : null,
        ushort us => us,
        byte b => b,
        string s => uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : ParseLeadingHex(s),
        _ => null,
    };

    /// <summary>Exception codes arrive as "c0000005" (string) or an integer; normalised to "0xC0000005".</summary>
    private static string? Hex(object? v)
    {
        uint? n = v switch
        {
            string s => ParseLeadingHex(s),
            null => null,
            _ => ToUInt(v),
        };
        return n is { } x ? "0x" + x.ToString("X8", CultureInfo.InvariantCulture) : null;
    }
}
