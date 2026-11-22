using SWCompanion.Core.Cpu;

namespace SWCompanion.Core.Tests;

public class CpuExplainerTests
{
    private static CpuTopology Laptop()
    {
        var cores = Enumerable.Range(0, 12).Select(i => new LogicalCore(i, i - i % 2, true))
            .Concat(Enumerable.Range(12, 8).Select(i => new LogicalCore(i, i, false)))
            .ToList();
        return new CpuTopology("Intel Core i9-13900H", cores, isHybrid: true);
    }

    private static CpuSnapshot Snap(double swCores, double top, int busyThreads = 0, params int[] busySlots)
    {
        var pct = Enumerable.Range(0, 20).Select(i => busySlots.Contains(i) ? 95.0 : 3.0).ToList();
        return new CpuSnapshot(8, pct, new SolidWorksCpu(swCores, swCores * 5, busyThreads, top, true));
    }

    [Fact]
    public void Single_core_work_is_explained_as_steps_in_order()
    {
        var text = CpuExplainer.WhatIsHappening(Snap(1.0, 98, 1, 4));
        Assert.Contains("one step at a time", text);
        Assert.Contains("normal, not a fault", text);
    }

    [Fact]
    public void Idle_says_waiting_for_you()
    {
        Assert.Contains("waiting for you", CpuExplainer.WhatIsHappening(Snap(0.0, 1)));
    }

    [Fact]
    public void These_cores_uses_the_real_core_layout_and_live_counts()
    {
        var text = CpuExplainer.Answer("these-cores", Snap(1.0, 98, 1, 4, 15), Laptop());
        Assert.Contains("Windows does", text);
        Assert.Contains("6 performance cores, shown as slots 00–11", text);
        Assert.Contains("8 efficiency cores, slots 12–19", text);
        Assert.Contains("1 of 12 performance slots and 1 of 8 efficiency slots are busy", text);
        Assert.Contains("can't see yet", text); // honest about the limit
    }

    [Fact]
    public void One_core_answer_adds_what_is_happening_now()
    {
        var text = CpuExplainer.Answer("one-core", Snap(1.0, 97, 1, 4), Laptop());
        Assert.Contains("recipe", text);
        Assert.Contains("97% of a core", text);
    }

    [Fact]
    public void Every_question_has_an_answer()
    {
        foreach (var q in CpuExplainer.Questions)
            Assert.False(string.IsNullOrWhiteSpace(CpuExplainer.Answer(q.Id, Snap(0.5, 40), Laptop())), q.Id);
    }

    [Fact]
    public void Non_hybrid_PCs_get_no_performance_efficiency_talk()
    {
        var plain = new CpuTopology("Some CPU", Enumerable.Range(0, 8).Select(i => new LogicalCore(i, i, true)).ToList(), isHybrid: false);
        var text = CpuExplainer.Answer("these-cores", Snap(0.5, 40), plain);
        Assert.DoesNotContain("efficiency", text);
    }
}
