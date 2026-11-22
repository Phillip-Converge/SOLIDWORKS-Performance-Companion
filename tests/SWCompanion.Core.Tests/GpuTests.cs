using SWCompanion.Core.Gpu;

namespace SWCompanion.Core.Tests;

public class GpuTests
{
    private const ulong MB = 1024UL * 1024;
    private const long Intel = 0x16ee6, Nvidia = 0x172bc, Basic = 0x17287;

    /// <summary>An example laptop with built-in Intel graphics, an NVIDIA graphics card and Windows' software fallback.</summary>
    private static readonly IReadOnlyList<GpuAdapterInfo> Laptop =
    [
        new(Intel, "Intel Iris Xe Graphics", GpuKind.BuiltIn, 128 * MB, 16219 * MB),
        new(Nvidia, "NVIDIA GeForce RTX 4070 Laptop GPU", GpuKind.Dedicated, 7949 * MB, 16219 * MB),
        new(Basic, "Microsoft Basic Render Driver", GpuKind.Software, 0, 16219 * MB),
    ];

    private static string Eng(int pid, long luid, int eng, string type = "3D") =>
        $"pid_{pid}_luid_0x00000000_0x{luid:x8}_phys_0_eng_{eng}_engtype_{type}";
    private static string Mem(int pid, long luid) => $"pid_{pid}_luid_0x00000000_0x{luid:x8}_phys_0";
    private static string AdapterMem(long luid) => $"luid_0x00000000_0x{luid:x8}_phys_0";

    private static GpuSnapshot Build(Dictionary<string, double> engines, Dictionary<string, long> swDed, Dictionary<string, long> swShared,
                                     int? pid = 6096, string pref = "not set") =>
        GpuSampler.Aggregate(Laptop, engines,
            new Dictionary<string, long> { [AdapterMem(Nvidia)] = (long)(364 * MB) },
            new Dictionary<string, long>(),
            swDed, swShared, pid, pref);

    [Fact]
    public void Parses_counter_names()
    {
        var e = GpuSampler.ParseEngine("pid_6096_luid_0x00000000_0x000172BC_phys_0_eng_12_engtype_3D");
        Assert.NotNull(e);
        Assert.Equal(6096, e.Value.Pid);
        Assert.Equal(Nvidia, e.Value.Luid);
        Assert.Equal(Nvidia, GpuSampler.ParseLuid("luid_0x00000000_0x000172bc_phys_0"));
        Assert.Null(GpuSampler.ParseEngine("something else"));
    }

    [Fact]
    public void Finds_SolidWorks_on_the_NVIDIA_card_like_the_real_reading()
    {
        // Real reading 2026-11-20: 569 MB dedicated on NVIDIA, 118 MB shared on Intel.
        var g = Build(
            new() { [Eng(6096, Nvidia, 0)] = 20, [Eng(1234, Nvidia, 0)] = 15, [Eng(6096, Nvidia, 3, "Copy")] = 5 },
            new() { [Mem(6096, Nvidia)] = (long)(569 * MB) },
            new() { [Mem(6096, Intel)] = (long)(118 * MB), [Mem(6096, Nvidia)] = (long)(95 * MB) });

        var sw = g.SolidWorksAdapter!;
        Assert.Equal(Nvidia, sw.Adapter.Luid);
        Assert.Equal(35, sw.BusyPercent, 3);   // busiest engine, all programs: 20 + 15 on engine 0
        Assert.Equal(20, sw.SwBusyPercent, 3); // SOLIDWORKS's own share
        Assert.StartsWith("SOLIDWORKS is drawing with the NVIDIA GeForce RTX 4070 Laptop GPU: 35% busy", GpuExplainer.Describe(g));
        Assert.Contains("usually normal", GpuExplainer.Answer("which", g)); // the Intel share is explained
    }

    [Fact]
    public void Warns_when_SolidWorks_is_on_built_in_graphics()
    {
        var g = Build(new() { [Eng(6096, Intel, 0)] = 40 }, new(), new() { [Mem(6096, Intel)] = (long)(600 * MB) });
        Assert.Equal("SOLIDWORKS is drawing with the built-in Intel Iris Xe Graphics, not the NVIDIA GeForce RTX 4070 Laptop GPU.",
                     GpuExplainer.Describe(g));
        Assert.Contains("only watches", GpuExplainer.WhatIsHappening(g));
    }

    [Fact]
    public void Warns_when_SolidWorks_is_on_the_software_fallback()
    {
        var g = Build(new(), new(), new() { [Mem(6096, Basic)] = (long)(300 * MB) });
        Assert.StartsWith("Warning: SOLIDWORKS is drawing without a graphics card", GpuExplainer.Describe(g));
    }

    [Fact]
    public void Quiet_graphics_points_elsewhere()
    {
        var g = Build(new() { [Eng(6096, Nvidia, 0)] = 2 }, new() { [Mem(6096, Nvidia)] = (long)(569 * MB) }, new());
        Assert.Contains("the wait is somewhere else", GpuExplainer.WhatIsHappening(g));
    }

    [Fact]
    public void Not_running()
    {
        var g = Build(new() { [Eng(1234, Nvidia, 0)] = 12 }, new(), new(), pid: null);
        Assert.Null(g.SolidWorksAdapter);
        Assert.StartsWith("SOLIDWORKS is not running.", GpuExplainer.Describe(g));
    }

    [Fact]
    public void Every_question_has_an_answer()
    {
        var g = Build(new() { [Eng(6096, Nvidia, 0)] = 20 }, new() { [Mem(6096, Nvidia)] = (long)(569 * MB) }, new());
        foreach (var q in GpuExplainer.Questions)
            Assert.False(string.IsNullOrWhiteSpace(GpuExplainer.Answer(q.Id, g)), q.Id);
    }

    [Fact]
    public void Reads_this_PC()
    {
        var sampler = new GpuSampler();
        Assert.Null(sampler.Sample(null, null)); // first reading only sets a starting point
        Thread.Sleep(300);
        var g = sampler.Sample(null, null);
        Assert.NotNull(g);
        Assert.All(g.Adapters, a => Assert.InRange(a.BusyPercent, 0, 100));
    }
}
