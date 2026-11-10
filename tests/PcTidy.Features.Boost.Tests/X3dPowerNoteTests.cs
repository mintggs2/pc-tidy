using PcTidy.Catalog.Tweaks;
using PcTidy.Features.Boost.Planning;
using Xunit;

namespace PcTidy.Features.Boost.Tests;

/// <summary>Community tweaks §9.6: on dual-CCD Ryzen X3D, Game boost says to keep Balanced, Game Mode, Game Bar and the chipset driver.</summary>
public class X3dPowerNoteTests
{
    [Fact]
    public void Dual_ccd_x3d_gets_the_keep_balanced_advice()
    {
        var note = BoostPlanner.PowerNote(CpuKind.DualCcdX3D);
        foreach (var word in new[] { "Balanced", "Game Mode", "Game Bar", "chipset driver", "never switches" })
            Assert.Contains(word, note);
    }

    [Fact]
    public void Other_cpus_get_the_plain_note()
        => Assert.Equal(BoostPlanner.PowerPlanNote, BoostPlanner.PowerNote(CpuKind.None));
}
