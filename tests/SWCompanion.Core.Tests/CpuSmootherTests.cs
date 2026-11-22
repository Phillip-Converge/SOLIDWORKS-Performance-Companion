using SWCompanion.Core.Cpu;

namespace SWCompanion.Core.Tests;

public class CpuSmootherTests
{
    private static CpuSnapshot Snap(double swCores, double topThread, double system = 5) =>
        new(system, Enumerable.Repeat(system, 20).ToList(),
            new SolidWorksCpu(swCores, swCores / 20 * 100, topThread >= 50 ? 1 : 0, topThread, true));

    private static CpuSnapshot NoSw(double system = 5) =>
        new(system, Enumerable.Repeat(system, 20).ToList(), null);

    [Fact]
    public void Real_flicker_from_the_recording_becomes_one_steady_sentence()
    {
        // From the 22:51 recording: the raw sentence went working -> idle -> working.
        var smoother = new CpuSmoother(3);
        var a = CpuInterpreter.Describe(smoother.Add(Snap(0.4, 38)));
        var b = CpuInterpreter.Describe(smoother.Add(Snap(0.0, 5)));
        var c = CpuInterpreter.Describe(smoother.Add(Snap(1.0, 72)));

        Assert.StartsWith("SOLIDWORKS is working lightly", a);
        Assert.StartsWith("SOLIDWORKS is working lightly", b); // raw reading alone would say "idle"
        Assert.StartsWith("SOLIDWORKS is working lightly", c);
    }

    [Fact]
    public void A_sustained_maxed_core_is_still_reported()
    {
        var smoother = new CpuSmoother(3);
        CpuSnapshot s = null!;
        for (int i = 0; i < 3; i++) s = smoother.Add(Snap(1.0, 98));
        Assert.Contains("One core is maxed", CpuInterpreter.Describe(s));
    }

    [Fact]
    public void Averages_only_the_last_three_readings()
    {
        var smoother = new CpuSmoother(3);
        smoother.Add(Snap(9, 100));               // falls out of the window
        smoother.Add(Snap(1, 10));
        smoother.Add(Snap(2, 20));
        var s = smoother.Add(Snap(3, 30));
        Assert.Equal(2.0, s.SolidWorks!.CoresWorth, 3);
        Assert.Equal(20.0, s.SolidWorks.TopThreadPercent, 3);
    }

    [Fact]
    public void Reports_SolidWorks_closing_immediately()
    {
        var smoother = new CpuSmoother(3);
        smoother.Add(Snap(1, 90));
        smoother.Add(Snap(1, 90));
        var s = smoother.Add(NoSw());
        Assert.Null(s.SolidWorks);
        Assert.StartsWith("SOLIDWORKS is not running", CpuInterpreter.Describe(s));
    }

    [Fact]
    public void Does_not_mix_readings_from_before_SolidWorks_started()
    {
        var smoother = new CpuSmoother(3);
        smoother.Add(NoSw(80));
        var s = smoother.Add(Snap(0.5, 40, system: 10));
        Assert.Equal(10, s.SystemPercent, 3);
    }
}
