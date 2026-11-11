using PcTidy.Core.Model;

namespace PcTidy.Features.PcCare.Planning;

/// <summary>
/// One card on the Fixes tab: a standard repair with plain text about what it changes and whether it can be undone.
/// <see cref="CreateItem"/> builds a fresh PlanItem each time; Run turns it into a one-item plan that always goes through
/// the standard confirm. No FPS or speed claims on any of these: they are repairs.
/// </summary>
/// <param name="Id">Stable id ("wifi-reset").</param>
/// <param name="Description">The one-line description.</param>
/// <param name="WhatItChanges">What it changes on this PC, in plain words.</param>
/// <param name="Undo">Whether undo exists, and what that means here.</param>
/// <param name="Guidance">Steps to try by hand, shown under the card (may be empty).</param>
/// <param name="RunLabel">The Run button's text.</param>
public sealed record FixRecipe(
    string Id,
    string Title,
    string Description,
    string WhatItChanges,
    string Undo,
    IReadOnlyList<string> Guidance,
    string RunLabel,
    Func<PlanItem> CreateItem)
{
    /// <summary>A sample item, for the card's flags (admin, restore point). Not run.</summary>
    public PlanItem Preview => CreateItem();
}

/// <summary>The Fixes tab's recipes (build list B5). Only emits PlanItems; PcTidy.Platform runs them after the confirm.</summary>
public static class FixRecipes
{
    public const string WifiReset = "wifi-reset";
    public const string BluetoothReset = "bluetooth-reset";
    public const string WindowsUpdateReset = "windows-update-reset";
    public const string StoreReset = "store-reset";
    public const string InstallerErrors = "installer-errors";
    public const string GameLauncherRepair = "game-launcher-repair";

    /// <summary>Service names the ServiceRestart recipes use (the Platform handler's allow-list must contain them).</summary>
    public const string WlanService = "WlanSvc";
    public const string BluetoothService = "bthserv";

    public static readonly IReadOnlyList<FixRecipe> All = new FixRecipe[]
    {
        new(WifiReset, "Wi-Fi reset",
            "For Wi-Fi that won't connect, keeps dropping or can't see networks.",
            "Restarts Windows' WLAN AutoConfig service (WlanSvc). Wi-Fi drops for a few seconds and then reconnects. Its start type stays the same.",
            "Nothing to undo: the service only restarts. Your saved networks stay.",
            new[]
            {
                "Still stuck? Forget the network (Settings > Network & internet > Wi-Fi > Manage known networks), then join it again with its password.",
                "Restart the router: unplug it for 30 seconds, plug it back in and wait for its lights to settle.",
            },
            "Reset Wi-Fi",
            () => PcCareActions.ServiceRestart(WlanService, "Restart the Wi-Fi service (WLAN AutoConfig, WlanSvc)")),

        new(BluetoothReset, "Bluetooth reset",
            "For Bluetooth devices that won't pair, connect or show up.",
            "Restarts Windows' Bluetooth Support Service (bthserv). Connected Bluetooth devices drop for a moment and reconnect. Its start type stays the same.",
            "Nothing to undo: the service only restarts. Paired devices stay paired.",
            new[] { "Still stuck? Remove the device in Settings > Bluetooth & devices, then pair it again." },
            "Reset Bluetooth",
            () => PcCareActions.ServiceRestart(BluetoothService, "Restart the Bluetooth Support Service (bthserv)")),

        new(WindowsUpdateReset, "Windows Update reset",
            "For updates that fail, stay stuck downloading or show the same error again and again.",
            "Stops the update services, renames the SoftwareDistribution and catroot2 folders (the old ones are kept, with \".old\" and the date added), then starts the services again. The first update check afterwards is slower, because Windows rebuilds its update list.",
            "Not from History. The old folders are kept next to the new ones, so nothing is lost. A restore point is made first.",
            new[] { "Afterwards, open Settings > Windows Update and select Check for updates." },
            "Reset Windows Update",
            PcCareActions.WindowsUpdateReset),

        new(StoreReset, "Microsoft Store reset",
            "For a Store that won't open, download or update.",
            "Runs Windows' wsreset, which clears the Store's cache. Your apps and purchases stay. The Store opens when it's done.",
            "Nothing to undo: the cache refills by itself.",
            new[] { "More Store fixes (re-register the Store) are on the Repair tab." },
            "Reset the Store",
            PcCareActions.StoreReset),

        new(InstallerErrors, "Windows Installer errors 2502 / 2503",
            "For setup programs (.msi) that stop with error 2502, 2503 or \"Windows Installer service could not be accessed\".",
            "Registers Windows Installer again (msiexec /unregister, then /regserver). Installed programs aren't touched.",
            "Nothing to undo: Windows Installer is registered the same way as before.",
            new[] { "Then run the installer as admin: right-click the .msi or setup file and pick \"Run as administrator\"." },
            "Repair Windows Installer",
            PcCareActions.MsiReregister),

        // steam://validate/<appid> is listed on Valve's community wiki (developer.valvesoftware.com, "Steam browser protocol"),
        // which says some commands no longer work; it also needs one game's app id and OpenExternal only allows https: and
        // ms-settings: targets. So this card opens Steam's own help page and gives the steps; Epic is steps only.
        new(GameLauncherRepair, "Game launcher repair",
            "For a game that crashes at start, won't launch or has missing files.",
            "Opens Steam's own help page on verifying game files. The launcher then checks the game's files and downloads any damaged or missing ones. Saves aren't touched.",
            "Nothing to undo: PC Tidy only opens a web page.",
            new[]
            {
                "Steam: Library > right-click the game > Properties > Installed Files > Verify integrity of game files.",
                "Epic Games Launcher: Library > the three dots on the game > Manage > Verify.",
                "Verify one game at a time; large games on a hard drive can take a while.",
            },
            "Open Steam's guide",
            () => PcCareActions.Open(PcCareLinks.SteamVerifyHelp, "Open Steam's help page: Verify Integrity of Game Files")),
    };

    /// <summary>Clearing DNS and resetting Winsock are on the Network tab already, so the Fixes tab points there.</summary>
    public const string NetworkResetNote = "Network reset (clear the DNS cache, reset Winsock) is on the Network tab.";

    public static FixRecipe Get(string id) => All.First(r => r.Id == id);

    /// <summary>The one-item plan Run sends to the standard confirm.</summary>
    public static ActionPlan PlanFor(FixRecipe recipe) => new()
    {
        FeatureId = PcCareActions.FeatureId,
        Title = recipe.Title,
        Items = new[] { recipe.CreateItem() },
    };

    /// <summary>The card's flag pills: "Needs admin", "Restore point first".</summary>
    public static IReadOnlyList<string> Flags(FixRecipe recipe)
    {
        var i = recipe.Preview;
        var flags = new List<string>();
        if (i.NeedsAdmin) flags.Add("Needs admin");
        if (i.IsSystemChange) flags.Add("Restore point first");
        return flags;
    }
}
