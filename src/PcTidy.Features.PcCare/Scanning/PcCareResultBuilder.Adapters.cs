using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Analysis;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Planning;
using T = PcTidy.Features.PcCare.Scanning.PcCareTabs;

namespace PcTidy.Features.PcCare.Scanning;

/// <summary>Network tab: "Adapter power saving" (build list B6), one row per matched physical adapter.</summary>
public static partial class PcCareResultBuilder
{
    /// <summary>
    /// Evidence (FPS boost tweaks §4.4 row 21, SITUATIONAL, strength C): one user's note on Linux with Intel Wi-Fi (iwlwifi)
    /// saw 50-300 ms ping spikes on a 1-5 ms base from Wi-Fi power saving, gone when it was off. Not measured on Windows.
    /// </summary>
    internal const string AdapterPowerEvidence =
        "Situational: it helps when a link drops or lags after going quiet. One user's note (Linux, Intel Wi-Fi, iwlwifi driver; "
        + "slightfuture.com technote, date not given) saw 50-300 ms ping spikes on a 1-5 ms base from Wi-Fi power saving, gone when it was off. "
        + "Not measured on Windows. No effect on FPS.";

    /// <summary>
    /// The plain side effects every apply row states (safety review M3): it also turns off waking the PC over the network,
    /// it needs a restart, and newer drivers may ignore it (see the VERIFY note on <see cref="Catalog.PcCare.AdapterPowerAllowances"/>).
    /// </summary>
    internal const string AdapterPowerSideEffects =
        "This also turns off \"wake on LAN\" and \"wake on wireless\" (the adapter can no longer wake the PC). "
        + "It needs a restart before it takes effect. Some newer drivers ignore this setting, so it may change nothing.";

    private static void AddAdapterPower(PcCareSnapshot s, List<ScanGroup> groups)
    {
        if (s.AdapterPower is not { } facts) return;
        var candidates = AdapterPowerRules.Candidates(facts);
        if (candidates.Count == 0) return;

        var rows = candidates.Select(AdapterRow).ToList();
        groups.Add(Group(T.Network, "adapterpower", "Adapter power saving",
            "Stops Windows turning a network adapter off to save power (Device Manager: \"Allow the computer to turn off this device to save power\"). "
            + "Physical Wi-Fi and Ethernet adapters only. Nothing is ticked for you: the evidence is weak. " + AdapterPowerEvidence + " "
            + AdapterPowerSideEffects,
            Badge.Check, rows));
    }

    private static ScanItem AdapterRow(AdapterPowerCandidate c)
    {
        bool wifi = c.Medium == NetAdapterMedium.WiFi;
        string kind = wifi ? "Wi-Fi" : "Ethernet";
        string name = string.IsNullOrWhiteSpace(c.Adapter.Description) ? c.Adapter.Name : c.Adapter.Description;
        string title = $"{kind}: {name}";
        var details = new Dictionary<string, string>
        {
            ["Adapter"] = c.Adapter.Name,
            ["Registry"] = @"HKLM\" + AdapterPowerRules.KeyPath(c),
            ["PnPCapabilities now"] = c.BeforeValue == "absent" ? "not set (Windows' default)" : c.BeforeValue,
            ["Evidence"] = AdapterPowerEvidence,
        };
        switch (c.State)
        {
            case AdapterPowerState.AlreadyOff:
                return Info(title, "Windows already won't turn this adapter off to save power.", detail: "PnPCapabilities = 24", state: "Already off", details: details);
            case AdapterPowerState.CustomValue:
                return Disabled(title, "This adapter's power setting was set to something else by its driver or another tool.",
                    "PC Tidy leaves values it doesn't know alone. Change it in Device Manager > the adapter > Power Management if you need to.",
                    detail: "PnPCapabilities = " + c.BeforeValue, state: "Left as it is");
        }

        var item = Pick(title,
            "Situational, weak evidence: only worth trying if this " + (wifi ? "Wi-Fi" : "network") + " connection drops or lags after going quiet. "
            + "Unticks \"Allow the computer to turn off this device to save power\". "
            + (wifi
                ? "This also turns off \"wake on wireless\": the PC can't be woken over Wi-Fi any more. "
                : "This also turns off \"wake on LAN\": the PC can't be woken over the network any more. ")
            + "You need to restart before it takes effect. Newer drivers may ignore this setting, so it may change nothing. "
            + "Needs admin; a restore point is made first. Undo from History puts the exact old value back.",
            Badge.Check,
            new[] { PcCareActions.AdapterPowerSavingOff(c.Entry.Subkey, name, c.BeforeValue) },
            detail: "Now: " + (c.BeforeValue == "absent" ? "Windows may turn it off (not set)" : "Windows may turn it off (PnPCapabilities = " + c.BeforeValue + ")")
                    + " · needs a restart",
            state: kind,
            details: details,
            restart: true);
        // Safety review M3: never ticked for you, Wi-Fi included (SITUATIONAL, one Linux note, side effects, and the
        // VERIFY on newer drivers is still open). Like every other new v3.1 apply path, the user ticks it.
        return item with { DefaultSelected = false };
    }
}
