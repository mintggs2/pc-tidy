using PcTidy.App;
using Xunit;

namespace PcTidy.UI.Tests;

/// <summary>
/// 3.1.2: --elevated and --scheduled-scan read their handler only AFTER the services are built, because building them is
/// what sets the handler. Up to 3.1.1 it was read first (always null), so the admin helper quit at once and every admin
/// step was skipped ("PC Tidy's admin helper closed before it started").
/// </summary>
public class WindowlessStartTests
{
    [Fact]
    public async Task The_admin_helper_uses_the_handler_that_building_the_services_sets()
    {
        var saved = AppEntry.ElevatedHandler;
        try
        {
            AppEntry.ElevatedHandler = null;
            var args = AppArgs.Parse(new[] { "--elevated", "pipe-name", "1234" });
            Assert.True(args.Elevated);

            int code = await AppEntry.RunWindowlessAsync(args, () =>
            {
                AppEntry.ElevatedHandler = (_, _, _) => Task.FromResult(42);
                return null!; // the handler here doesn't use the services
            }, CancellationToken.None);

            Assert.Equal(42, code);
        }
        finally { AppEntry.ElevatedHandler = saved; }
    }
}
