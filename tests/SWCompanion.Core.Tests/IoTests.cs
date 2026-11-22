using SWCompanion.Core.Io;

namespace SWCompanion.Core.Tests;

public class IoTests
{
    private const double MB = 1024 * 1024;

    private static IoSnapshot Snap(double swRead = 0, double swWrite = 0, double diskBusy = 5, double diskMs = 0.4,
                                   double netIn = 10_000, bool sw = true)
    {
        var disk = new Dictionary<string, Dictionary<string, double>>
        {
            ["% Idle Time"] = new() { ["0 C:"] = 100 - diskBusy, ["_Total"] = 100 - diskBusy },
            ["Avg. Disk sec/Transfer"] = new() { ["0 C:"] = diskMs / 1000 },
            ["Disk Read Bytes/sec"] = new() { ["0 C:"] = swRead },
            ["Disk Write Bytes/sec"] = new() { ["0 C:"] = swWrite },
        };
        var net = new Dictionary<string, Dictionary<string, double>>
        {
            ["Bytes Received/sec"] = new() { ["Intel[R] Wi-Fi 6E AX211 160MHz"] = netIn, ["Realtek USB GbE Family Controller"] = 0 },
            ["Bytes Sent/sec"] = new() { ["Intel[R] Wi-Fi 6E AX211 160MHz"] = 2000 },
            ["Current Bandwidth"] = new() { ["Intel[R] Wi-Fi 6E AX211 160MHz"] = 1_200_000_000 },
        };
        return IoSampler.Build(disk, net, sw ? new SolidWorksIo(swRead, swWrite, 10, 1, 181UL * 1024 * 1024, 4UL * 1024 * 1024) : null);
    }

    [Fact]
    public void Ignores_the_total_line_and_works_out_busy_and_response_time()
    {
        var s = Snap(diskBusy: 30, diskMs: 2);
        Assert.Single(s.Disks);
        Assert.Equal("0 C:", s.BusiestDisk!.Name);
        Assert.Equal(30, s.BusiestDisk.BusyPercent, 3);
        Assert.Equal(2, s.BusiestDisk.ResponseMs, 3);
    }

    [Fact]
    public void Quiet_SolidWorks()
    {
        Assert.StartsWith("SOLIDWORKS isn't reading or writing much.", IoExplainer.Describe(Snap()));
        Assert.Contains("aren't what's holding it up", IoExplainer.WhatIsHappening(Snap()));
    }

    [Fact]
    public void Reading_with_network_traffic_points_to_the_network()
    {
        var text = IoExplainer.WhatIsHappening(Snap(swRead: 40 * MB, netIn: 30 * MB));
        Assert.Contains("probably coming over the network", text);
    }

    [Fact]
    public void Reading_without_network_traffic_is_local()
    {
        var text = IoExplainer.WhatIsHappening(Snap(swRead: 40 * MB));
        Assert.Contains("SOLIDWORKS is reading files.", text);
        Assert.StartsWith("SOLIDWORKS is reading 40.0 MB/s", IoExplainer.Describe(Snap(swRead: 40 * MB)));
    }

    [Fact]
    public void A_struggling_disk_comes_first()
    {
        var text = IoExplainer.WhatIsHappening(Snap(swRead: 40 * MB, diskBusy: 98, diskMs: 120));
        Assert.Contains("struggling to keep up", text);
        Assert.Contains("120 ms", text);
    }

    [Fact]
    public void Network_percent_uses_link_speed()
    {
        var wifi = Snap(netIn: 15 * MB).Networks.First();
        Assert.Equal(15 * MB * 8 * 100 / 1_200_000_000, wifi.UsedPercent, 3);
    }

    [Fact]
    public void Answers_are_honest_about_what_needs_tracing()
    {
        Assert.Contains("Step 7", IoExplainer.Answer("disk-or-network", Snap()));
        Assert.Contains("not per program", IoExplainer.Answer("network-who", Snap()));
        foreach (var q in IoExplainer.Questions)
        {
            Assert.False(string.IsNullOrWhiteSpace(IoExplainer.Answer(q.Id, Snap())), q.Id);
            Assert.False(string.IsNullOrWhiteSpace(IoExplainer.Answer(q.Id, Snap(sw: false))), q.Id);
        }
    }

    [Fact]
    public void Reads_this_PC_and_this_process()
    {
        var sampler = new IoSampler();
        sampler.Sample(Environment.ProcessId); // starting point
        Thread.Sleep(300);
        var s = sampler.Sample(Environment.ProcessId);
        Assert.NotNull(s);
        Assert.NotEmpty(s.Disks);
        Assert.NotNull(s.SolidWorks); // here "SOLIDWORKS" is this test program
        Assert.True(s.SolidWorks.TotalRead > 0);
    }
}
