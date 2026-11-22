using SWCompanion.Core.Memory;

namespace SWCompanion.Core.Tests;

public class MemoryExplainerTests
{
    private const ulong GB = 1024UL * 1024 * 1024;

    private static MemorySnapshot Snap(double ramUsedGb = 14, double commitUsedGb = 18, int gdi = 1300, int user = 1700, bool sw = true) =>
        new((ulong)(31.7 * GB), (ulong)((31.7 - ramUsedGb) * GB), (ulong)(34.6 * GB), (ulong)(commitUsedGb * GB),
            sw ? new SolidWorksMemory((long)(0.1 * GB), (long)(1.1 * GB), (long)(1.3 * GB), gdi, 2045, user, 2161, 16384, 10000, 3165) : null);

    [Fact]
    public void Normal_says_memory_is_fine()
    {
        Assert.StartsWith("Memory is fine", MemoryExplainer.Describe(Snap()));
        Assert.Contains("isn't slowing SOLIDWORKS down", MemoryExplainer.WhatIsHappening(Snap()));
    }

    [Fact]
    public void Ram_getting_full_and_nearly_full()
    {
        Assert.StartsWith("Memory is getting full", MemoryExplainer.Describe(Snap(ramUsedGb: 26)));
        Assert.StartsWith("Memory is nearly full", MemoryExplainer.Describe(Snap(ramUsedGb: 29.5)));
    }

    [Fact]
    public void Commit_near_limit_warns_to_save()
    {
        Assert.Contains("Save your work", MemoryExplainer.Describe(Snap(commitUsedGb: 33)));
    }

    [Fact]
    public void Drawing_object_limit_beats_everything_else()
    {
        // 15,000 of this PC's 16,384 limit is over 90%
        var text = MemoryExplainer.Describe(Snap(ramUsedGb: 29.5, gdi: 15000));
        Assert.Contains("drawing objects (15,000 of 16,384)", text);
        Assert.Contains("usually crashes", MemoryExplainer.WhatIsHappening(Snap(gdi: 15000)));
    }

    [Fact]
    public void Objects_answer_notices_a_changed_limit()
    {
        var text = MemoryExplainer.Answer("objects", Snap());
        Assert.Contains("1,300 of 16,384 drawing objects", text);
        Assert.Contains("limits have been changed", text);
    }

    [Fact]
    public void Ram_vs_committed_uses_live_numbers()
    {
        var text = MemoryExplainer.Answer("ram-vs-committed", Snap());
        Assert.Contains("102 MB in RAM and 1.1 GB set aside", text); // 0.1 GB = 102 MB
    }

    [Fact]
    public void Every_question_has_an_answer_with_and_without_SolidWorks()
    {
        foreach (var q in MemoryExplainer.Questions)
        {
            Assert.False(string.IsNullOrWhiteSpace(MemoryExplainer.Answer(q.Id, Snap())), q.Id);
            Assert.False(string.IsNullOrWhiteSpace(MemoryExplainer.Answer(q.Id, Snap(sw: false))), q.Id);
        }
    }

    [Theory]
    [InlineData(1.5 * 1024 * 1024 * 1024, "1.5 GB")]
    [InlineData(820.0 * 1024 * 1024, "820 MB")]
    public void Size_wording(double bytes, string expected) => Assert.Equal(expected, MemoryExplainer.Gb((ulong)bytes));
}

public class MemorySamplerTests
{
    [Fact]
    public void Reads_PC_totals_and_this_process()
    {
        using var sampler = new MemorySampler();
        var m = sampler.Sample(Environment.ProcessId);
        Assert.True(m.TotalRam > 0);
        Assert.InRange(m.AvailableRam, 0UL, m.TotalRam);
        Assert.True(m.CommitLimit >= m.TotalRam / 2);
        Assert.NotNull(m.SolidWorks);
        Assert.True(m.SolidWorks.InRam > 0);
        Assert.True(m.SolidWorks.GdiLimit > 0 && m.SolidWorks.UserLimit > 0);
    }

    [Fact]
    public void No_SolidWorks_means_no_SolidWorks_readings()
    {
        using var sampler = new MemorySampler();
        Assert.Null(sampler.Sample(null).SolidWorks);
    }
}
