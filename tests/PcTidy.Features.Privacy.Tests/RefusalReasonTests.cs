using PcTidy.Features.Privacy.Notifications;

namespace PcTidy.Features.Privacy.Tests;

/// <summary>Review CR-17: a guard refusal only says "restart PC Tidy" when the app really is new since PC Tidy started.</summary>
public class RefusalReasonTests
{
    [Fact]
    public void An_app_missing_from_the_start_up_list_is_told_to_restart()
    {
        var reason = AppNotificationsScanner.RefusalReason("New.App", SafetyVerdict.Block("Not in the catalog."), _ => false);
        Assert.Contains("Restart PC Tidy", reason);
    }

    [Fact]
    public void Any_other_refusal_shows_the_real_reason_and_no_restart()
    {
        var reason = AppNotificationsScanner.RefusalReason("Known.App", SafetyVerdict.Block("Only the Enabled value may be changed."), _ => true);
        Assert.DoesNotContain("Restart", reason);
        Assert.Contains("Only the Enabled value may be changed", reason);
        Assert.Contains("Settings → Notifications", reason);
    }
}
