using System.Diagnostics;
using SWCompanion.Core.Cpu;

namespace SWCompanion.Core.Tests;

public class CpuInterpreterTests
{
    private static CpuSnapshot Snap(double system, SolidWorksCpu? sw, int cores = 20, int busy = 0) =>
        new(system, Enumerable.Range(0, cores).Select(i => i < busy ? 100.0 : 2.0).ToList(), sw);

    [Fact]
    public void Not_running()
    {
        Assert.StartsWith("SOLIDWORKS is not running.", CpuInterpreter.Describe(Snap(5, null)));
    }

    [Fact]
    public void Idle()
    {
        var text = CpuInterpreter.Describe(Snap(3, new SolidWorksCpu(0.02, 0.1, 0, 2, true)));
        Assert.StartsWith("SOLIDWORKS is idle.", text);
    }

    [Fact]
    public void One_core_maxed_looks_like_rebuild()
    {
        var text = CpuInterpreter.Describe(Snap(8, new SolidWorksCpu(1.0, 5, 1, 99, true), busy: 1));
        Assert.Contains("One core is maxed", text);
        Assert.Contains("usually means", text); // an interpretation, never stated as fact
        Assert.Contains("1 of 20 cores busy", text);
    }

    [Fact]
    public void Many_cores_looks_multithreaded()
    {
        var text = CpuInterpreter.Describe(Snap(40, new SolidWorksCpu(6.5, 32, 6, 90, true), busy: 7));
        Assert.Contains("several cores (6.5 cores)", text);
    }

    [Fact]
    public void Says_when_threads_are_hidden()
    {
        var text = CpuInterpreter.Describe(Snap(10, new SolidWorksCpu(1.0, 5, 0, 0, false)));
        Assert.Contains("won't show its threads", text);
    }

    [Theory]
    [InlineData(0.42, "0.4 of a core")]
    [InlineData(1.0, "1.0 core")]
    [InlineData(3.24, "3.2 cores")]
    public void Core_wording(double cores, string expected)
    {
        Assert.Equal(expected, CpuInterpreter.Cores(cores));
    }
}

public class CpuSamplerTests
{
    [Fact]
    public void Reads_every_core_and_this_process()
    {
        using var sampler = new CpuSampler();
        int me = Environment.ProcessId;

        Assert.Null(sampler.Sample(me)); // first reading has nothing to compare with
        var until = Stopwatch.StartNew();
        while (until.ElapsedMilliseconds < 300) { } // burn some CPU so there is something to see
        var snap = sampler.Sample(me);

        Assert.NotNull(snap);
        Assert.Equal(Environment.ProcessorCount, snap.CoreCount);
        Assert.All(snap.CorePercent, c => Assert.InRange(c, 0, 100));
        Assert.NotNull(snap.SolidWorks);
        Assert.True(snap.SolidWorks.ThreadsVisible);
        Assert.True(snap.SolidWorks.CoresWorth > 0.3, $"expected busy, got {snap.SolidWorks.CoresWorth}");
    }
}
