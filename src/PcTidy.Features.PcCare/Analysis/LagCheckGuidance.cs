namespace PcTidy.Features.PcCare.Analysis;

/// <summary>One tip under the lag check, with its measured number, the setup it was measured on and the source.</summary>
public sealed record LagTip(string Title, string Text, string Measured, string Source);

/// <summary>
/// The lag check's guidance block. Numbers come from "FPS boost tweaks - do they work" §4.4 (rows 26, 28 and 29) and the
/// F3-network appendix; each shows its setup and source. These are fixes for delay, not FPS: no FPS claim anywhere.
/// </summary>
public static class LagCheckGuidance
{
    public static readonly IReadOnlyList<LagTip> Tips = new LagTip[]
    {
        new("Use Ethernet if you can",
            "A cable avoids the extra delay and wobble Wi-Fi adds, most of all when other devices are busy.",
            "Wired: 16 ms average ping, 7 ms jitter. Wi-Fi 6: 60 ms average ping, 20 ms jitter (one home setup, their own test). "
            + "RTINGS tested 11 routers while another device downloaded at full speed: wired added almost no delay, Wi-Fi added a clear penalty.",
            "How-To Geek, \"Wi-Fi vs. Ethernet: how much better is a wired connection?\" (date not shown in the source); RTINGS router latency research (date not shown)."),
        new("Turn on Smart Queue (SQM) on your router",
            "Look for SQM, Smart Queue, CAKE or fq_codel in the router's settings. Set it to about 90% of your measured speed, then run this test again.",
            "Lab test bed (10 Mbit/s link, 50 ms base delay, 32 downloads against one real-time flow): CAKE kept the real-time flow at about 200 ms "
            + "while fq_codel swung between about 50 and 500 ms; with DiffServ, CAKE added no measurable delay. "
            + "One older Netgear router: about 2 s under a full upload without SQM, about +5 ms with fq_codel.",
            "Høiland-Jørgensen et al., \"Piece of CAKE\" (2018, arXiv 1804.07617); burntchrome blog (December 2016, one router)."),
        new("Pause uploads and cloud sync while you play or call",
            "Uploads fill a home line fastest. Pause OneDrive, Google Drive, Dropbox, backups and game clip uploads, then test again.",
            "Not measured separately: this is what the upload part of this test shows on your own line.",
            "PC Tidy's lag check (your result above)."),
    };
}
