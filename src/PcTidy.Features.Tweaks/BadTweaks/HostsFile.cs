namespace PcTidy.Features.Tweaks.BadTweaks;

/// <summary>
/// Reads the hosts file and finds lines that send Microsoft's update, Store, activation or sign-in servers
/// nowhere (0.0.0.0, 127.0.0.1…). Telemetry-only blocks are counted but not flagged (they don't break Windows).
/// </summary>
public static class HostsFile
{
    /// <summary>Servers Windows needs (Microsoft "manage Windows endpoints" + Community tweaks T6 §54). Suffix match.</summary>
    public static readonly IReadOnlyList<string> CriticalHosts = new[]
    {
        "windowsupdate.com", "windowsupdate.microsoft.com", "update.microsoft.com", "delivery.mp.microsoft.com",
        "dl.delivery.mp.microsoft.com", "tlu.dl.delivery.mp.microsoft.com", "emdl.ws.microsoft.com",
        "sls.microsoft.com", "activation.sls.microsoft.com", "validation.sls.microsoft.com", "licensing.mp.microsoft.com",
        "displaycatalog.mp.microsoft.com", "storeedgefd.dsx.mp.microsoft.com", "storecatalogrevocation.storequality.microsoft.com",
        "login.live.com", "login.microsoftonline.com", "account.live.com",
        "settings-win.data.microsoft.com", "definitionupdates.microsoft.com", "wdcp.microsoft.com", "wd.microsoft.com",
        "crl.microsoft.com", "ctldl.windowsupdate.com", "msftconnecttest.com", "www.msftconnecttest.com", "xboxlive.com",
    };

    /// <summary>Addresses that mean "block".</summary>
    private static readonly HashSet<string> SinkAddresses = new(StringComparer.OrdinalIgnoreCase) { "0.0.0.0", "127.0.0.1", "::1", "::", "0::0", "127.0.0.0" };

    public sealed record Finding(IReadOnlyList<string> CriticalLines, int TotalBlockLines);

    /// <summary>Null when the file can't be read (or doesn't exist).</summary>
    public static Finding? Scan(string path)
    {
        string[] lines;
        try
        {
            if (!File.Exists(path)) return null;
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Scan(lines);
    }

    public static Finding Scan(IEnumerable<string> lines)
    {
        var critical = new List<string>();
        var blocks = 0;
        foreach (var raw in lines)
        {
            var line = raw;
            var hash = line.IndexOf('#');
            var content = (hash >= 0 ? line[..hash] : line).Trim();
            if (content.Length == 0) continue;
            var parts = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !SinkAddresses.Contains(parts[0])) continue;
            blocks++;
            if (parts.Skip(1).Any(IsCritical)) critical.Add(raw);
        }
        return new Finding(critical, blocks);
    }

    public static bool IsCritical(string host)
    {
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        return CriticalHosts.Any(c => h == c || h.EndsWith("." + c, StringComparison.Ordinal));
    }
}
