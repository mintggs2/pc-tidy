using System.Globalization;
using System.Text.RegularExpressions;

namespace PcTidy.Features.Search.Query;

/// <summary>
/// Parses <c>dm:</c> values into a UTC range, using the PC's local calendar days but no region-specific formats:
/// today, yesterday, thisweek, lastweek, thismonth, lastmonth, thisyear, lastyear, last&lt;N&gt;days|weeks|months|years,
/// ISO dates (2026, 2026-11, 2026-11-10), ranges (<c>2026-01..2026-03</c>) and comparisons (<c>&gt;2026-01-01</c>, <c>&lt;=lastmonth</c>).
/// Weeks start on Monday (ISO 8601) on every PC.
/// </summary>
public static partial class DateSpec
{
    [GeneratedRegex(@"^(?:last|past)(\d{1,4})(day|week|month|year)s?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LastN();

    [GeneratedRegex(@"^(\d{4})(?:-(\d{1,2})(?:-(\d{1,2}))?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    public static bool TryParse(string value, TimeProvider clock, out DateTime fromUtc, out DateTime toUtc)
    {
        fromUtc = DateTime.MinValue; toUtc = DateTime.MaxValue;
        var v = value.Trim();
        if (v.Length == 0) return false;
        var tz = clock.LocalTimeZone;
        var today = clock.GetLocalNow().Date;

        var range = v.IndexOf("..", StringComparison.Ordinal);
        if (range > 0)
        {
            if (!TryPeriod(v[..range], today, out var a1, out var a2) || !TryPeriod(v[(range + 2)..], today, out var b1, out var b2)) return false;
            var (start, end) = a1 <= b1 ? (a1, b2) : (b1, a2);
            (fromUtc, toUtc) = (ToUtc(start, tz), ToUtc(end, tz));
            return true;
        }

        string op = v.StartsWith(">=") || v.StartsWith("<=") ? v[..2] : v[0] is '>' or '<' or '=' ? v[..1] : "";
        if (!TryPeriod(v[op.Length..], today, out var s, out var e)) return false;
        switch (op)
        {
            case ">": fromUtc = ToUtc(e, tz); break;
            case ">=": fromUtc = ToUtc(s, tz); break;
            case "<": toUtc = ToUtc(s, tz); break;
            case "<=": toUtc = ToUtc(e, tz); break;
            default: (fromUtc, toUtc) = (ToUtc(s, tz), ToUtc(e, tz)); break;
        }
        return true;
    }

    /// <summary>A named or ISO period as local [start, end).</summary>
    private static bool TryPeriod(string text, DateTime today, out DateTime start, out DateTime end)
    {
        start = end = default;
        var t = text.Trim().ToLowerInvariant();
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var month = new DateTime(today.Year, today.Month, 1);
        var year = new DateTime(today.Year, 1, 1);
        switch (t)
        {
            case "today": (start, end) = (today, today.AddDays(1)); return true;
            case "yesterday": (start, end) = (today.AddDays(-1), today); return true;
            case "thisweek": (start, end) = (monday, monday.AddDays(7)); return true;
            case "lastweek": (start, end) = (monday.AddDays(-7), monday); return true;
            case "thismonth": (start, end) = (month, month.AddMonths(1)); return true;
            case "lastmonth": (start, end) = (month.AddMonths(-1), month); return true;
            case "thisyear": (start, end) = (year, year.AddYears(1)); return true;
            case "lastyear": (start, end) = (year.AddYears(-1), year); return true;
        }

        var last = LastN().Match(t);
        if (last.Success)
        {
            int n = int.Parse(last.Groups[1].Value, CultureInfo.InvariantCulture);
            end = today.AddDays(1);
            start = last.Groups[2].Value switch
            {
                "day" => today.AddDays(-(n - 1)),
                "week" => today.AddDays(-7 * n + 1),
                "month" => today.AddMonths(-n).AddDays(1),
                _ => today.AddYears(-n).AddDays(1),
            };
            return n > 0;
        }

        var iso = IsoDate().Match(t);
        if (!iso.Success) return false;
        int y = int.Parse(iso.Groups[1].Value, CultureInfo.InvariantCulture);
        if (y is < 1601 or > 9998) return false;
        if (!iso.Groups[2].Success) { (start, end) = (new DateTime(y, 1, 1), new DateTime(y + 1, 1, 1)); return true; }
        int m = int.Parse(iso.Groups[2].Value, CultureInfo.InvariantCulture);
        if (m is < 1 or > 12) return false;
        if (!iso.Groups[3].Success) { start = new DateTime(y, m, 1); end = start.AddMonths(1); return true; }
        int d = int.Parse(iso.Groups[3].Value, CultureInfo.InvariantCulture);
        if (d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        start = new DateTime(y, m, d);
        end = start.AddDays(1);
        return true;
    }

    /// <summary>Local midnight → UTC. A midnight skipped by a clock change moves to the first valid minute.</summary>
    private static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (int i = 0; i < 4 && tz.IsInvalidTime(unspecified); i++) unspecified = unspecified.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }
}
