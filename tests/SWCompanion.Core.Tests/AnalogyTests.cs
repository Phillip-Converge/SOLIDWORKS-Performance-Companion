using SWCompanion.Core.Checkup;
using SWCompanion.Core.Cpu;
using SWCompanion.Core.Explain;
using SWCompanion.Core.Gpu;
using SWCompanion.Core.Io;
using SWCompanion.Core.Memory;
using SWCompanion.Core.Now;

namespace SWCompanion.Core.Tests;

/// <summary>The owner asked for an everyday comparison with every technical answer.</summary>
public class AnalogyTests
{
    private const ulong MB = 1024UL * 1024;

    [Fact]
    public void Every_ask_why_answer_ends_with_an_everyday_comparison()
    {
        var topo = new CpuTopology("test", Enumerable.Range(0, 4).Select(i => new LogicalCore(i, i - i % 2, true)).ToList(), false);
        var cpu = new CpuSnapshot(5, [5, 5, 5, 5], new SolidWorksCpu(0.2, 5, 0, 20, true));
        var mem = new MemorySnapshot(32UL * 1024 * MB, 16UL * 1024 * MB, 34UL * 1024 * MB, 18UL * 1024 * MB,
            new SolidWorksMemory(100 * (long)MB, 1000 * (long)MB, 1200 * (long)MB, 1300, 2000, 1700, 2100, 16384, 10000, 3000));
        var gpu = new GpuSnapshot([new AdapterReading(new GpuAdapterInfo(1, "NVIDIA RTX", GpuKind.Dedicated, 8000 * MB, 16000 * MB),
            5, 400 * MB, 0, 2, 500 * MB, 0)], true, "not set");
        var io = new IoSnapshot([new DiskReading("0 C:", 2, 0.4, 0, 0)], [new NetworkReading("Wi-Fi", 0, 0, 1e9)],
            new SolidWorksIo(0, 0, 0, 0, 0, 0));

        var answers = CpuExplainer.Questions.Select(q => (q.Id, CpuExplainer.Answer(q.Id, cpu, topo)))
            .Concat(MemoryExplainer.Questions.Select(q => (q.Id, MemoryExplainer.Answer(q.Id, mem))))
            .Concat(GpuExplainer.Questions.Select(q => (q.Id, GpuExplainer.Answer(q.Id, gpu))))
            .Concat(IoExplainer.Questions.Select(q => (q.Id, IoExplainer.Answer(q.Id, io))));

        foreach (var (id, text) in answers)
            Assert.True(text.Contains(Analogies.Lead), $"answer '{id}' has no everyday comparison");
    }

    [Fact]
    public void Page_file_and_object_answers_use_the_workshop_picture()
    {
        var mem = new MemorySnapshot(32UL * 1024 * MB, 16UL * 1024 * MB, 34UL * 1024 * MB, 18UL * 1024 * MB, null);
        Assert.Contains("storeroom", MemoryExplainer.Answer("ram-vs-committed", mem));
        Assert.Contains("pagefile.sys", MemoryExplainer.Answer("ram-vs-committed", mem));
        Assert.Contains("tool crib", MemoryExplainer.Answer("objects", mem));
    }

    [Fact]
    public void Every_checkup_row_explains_itself_in_plain_terms()
    {
        var facts = new CheckupFacts("High performance", null, null, 100, 100, true, false, 90, 16384, 10000, 10000, 10000, true,
            "?:\\pagefile.sys", 2);
        foreach (var r in CheckupEvaluator.Evaluate(facts, null))
            Assert.StartsWith(Analogies.Lead, r.Plain);
    }

    [Fact]
    public void Main_freeze_causes_carry_a_short_comparison()
    {
        var inputs = new NowInputs(true, false, TimeSpan.FromSeconds(9),
            new CpuSnapshot(5, Enumerable.Repeat(5.0, 20).ToList(), new SolidWorksCpu(0, 0, 0, 1, true)),
            null, null, null, null, [], []);
        Assert.Contains("standing by the phone", NowEngine.Evaluate(inputs).Top!.Evidence);
    }
}
