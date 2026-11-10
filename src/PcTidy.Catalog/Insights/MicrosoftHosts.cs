namespace PcTidy.Catalog.Insights;

/// <summary>What breaks when a Microsoft server is blocked in the hosts file.</summary>
public enum MicrosoftService { WindowsUpdate, Store, Activation, SignIn, DefenderUpdates, Certificates, ConnectivityCheck, Xbox }

/// <summary>
/// Servers Windows needs (Microsoft "manage connection endpoints" + Community tweaks T6), matched by suffix.
/// Same list the Tweaks bad-tweak finder uses for its hosts check; kept here as data because Insights only
/// reports and never references another feature.
/// </summary>
public static class MicrosoftHosts
{
    public static readonly IReadOnlyList<(string Host, MicrosoftService Service)> Critical = new[]
    {
        ("windowsupdate.com", MicrosoftService.WindowsUpdate), ("windowsupdate.microsoft.com", MicrosoftService.WindowsUpdate),
        ("update.microsoft.com", MicrosoftService.WindowsUpdate), ("delivery.mp.microsoft.com", MicrosoftService.WindowsUpdate),
        ("emdl.ws.microsoft.com", MicrosoftService.WindowsUpdate),
        ("displaycatalog.mp.microsoft.com", MicrosoftService.Store), ("storeedgefd.dsx.mp.microsoft.com", MicrosoftService.Store),
        ("storecatalogrevocation.storequality.microsoft.com", MicrosoftService.Store),
        ("sls.microsoft.com", MicrosoftService.Activation), ("licensing.mp.microsoft.com", MicrosoftService.Activation),
        ("login.live.com", MicrosoftService.SignIn), ("login.microsoftonline.com", MicrosoftService.SignIn), ("account.live.com", MicrosoftService.SignIn),
        ("definitionupdates.microsoft.com", MicrosoftService.DefenderUpdates), ("wdcp.microsoft.com", MicrosoftService.DefenderUpdates),
        ("wd.microsoft.com", MicrosoftService.DefenderUpdates), ("settings-win.data.microsoft.com", MicrosoftService.DefenderUpdates),
        ("crl.microsoft.com", MicrosoftService.Certificates), ("ctldl.windowsupdate.com", MicrosoftService.Certificates),
        ("msftconnecttest.com", MicrosoftService.ConnectivityCheck),
        ("xboxlive.com", MicrosoftService.Xbox),
    };

    /// <summary>Addresses that mean "send it nowhere".</summary>
    public static readonly IReadOnlySet<string> SinkAddresses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0.0.0.0", "127.0.0.1", "127.0.0.0", "::1", "::", "0::0" };

    /// <summary>The service a host belongs to, or null when Windows doesn't need it.</summary>
    public static MicrosoftService? ServiceOf(string host)
    {
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0) return null;
        foreach (var (critical, service) in Critical)
            if (h == critical || h.EndsWith("." + critical, StringComparison.Ordinal)) return service;
        return null;
    }

    public static string Describe(MicrosoftService service) => service switch
    {
        MicrosoftService.WindowsUpdate => "Windows Update",
        MicrosoftService.Store => "the Microsoft Store",
        MicrosoftService.Activation => "Windows activation",
        MicrosoftService.SignIn => "Microsoft account sign-in",
        MicrosoftService.DefenderUpdates => "Microsoft Defender updates",
        MicrosoftService.Certificates => "certificate checks",
        MicrosoftService.ConnectivityCheck => "the \"connected to the internet\" check",
        _ => "Xbox and Game Pass",
    };
}
