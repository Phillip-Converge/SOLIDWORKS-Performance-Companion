using System.Net;
using SWCompanion.Core.Cpu;
using SWCompanion.Core.Now;
using SWCompanion.Core.Pdm;

namespace SWCompanion.Core.Tests;

public class VaultTrafficTests
{
    private static readonly IPAddress Server = IPAddress.Parse("10.0.0.20");
    private static readonly IPAddress Elsewhere = IPAddress.Parse("93.184.216.34");
    private static readonly DateTime T0 = new(2026, 11, 21, 11, 0, 0);

    private static VaultTrafficTracker Tracker()
    {
        var t = new VaultTrafficTracker();
        t.SetTargets([
            new VaultServerTarget("database", "pdmserver.example.local", [Server], 1433),
            new VaultServerTarget("archive", "pdmserver.example.local", [Server], 3030),
        ]);
        return t;
    }

    /// <summary>One request/reply pair: send at <paramref name="at"/>, first reply byte <paramref name="replyMs"/> later.</summary>
    private static void Ask(VaultTrafficTracker t, DateTime at, double replyMs, string program = "SLDWORKS", int port = 1433, int bytesIn = 2000)
    {
        t.Add(new TcpEvent(at, 6000, program, TcpEventKind.Send, Server, port, 50000, 300));
        t.Add(new TcpEvent(at.AddMilliseconds(replyMs), 6000, program, TcpEventKind.Receive, Server, port, 50000, bytesIn));
    }

    [Fact]
    public void Times_each_request_and_splits_network_from_server()
    {
        var t = Tracker();
        // Network trip about 30 ms; most replies 80 ms, so the server thinks for about 50 ms.
        Ask(t, T0, 30);
        for (int i = 1; i <= 9; i++) Ask(t, T0.AddMilliseconds(i * 200), 80);
        var db = t.Snapshot(T0.AddSeconds(3), TimeSpan.FromMinutes(1)).Single();
        Assert.Equal("database", db.Role);
        Assert.Equal(10, db.Requests);
        Assert.Equal(30, db.FastestReplyMs!.Value, 3);
        Assert.Equal(80, db.MedianReplyMs!.Value, 3);
        Assert.Equal(50, db.ServerMs!.Value, 3);
        Assert.Equal(10, db.ByProgram["SLDWORKS"]);
    }

    [Fact]
    public void Ignores_traffic_that_has_nothing_to_do_with_the_vault()
    {
        var t = Tracker();
        t.Add(new TcpEvent(T0, 1, "chrome", TcpEventKind.Send, Elsewhere, 443, 51000, 500));
        Assert.False(t.IsVaultTraffic(Elsewhere, 443));
        Assert.Empty(t.Snapshot(T0, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Recognises_standard_PDM_ports_even_without_a_vault_view()
    {
        var t = new VaultTrafficTracker(); // no targets
        Assert.True(t.IsVaultTraffic(Elsewhere, 1433));
        Assert.True(t.IsVaultTraffic(Elsewhere, 3030));
        Assert.False(t.IsVaultTraffic(Elsewhere, 443));
    }

    [Fact]
    public void Counts_resent_and_retried_traffic()
    {
        var t = Tracker();
        for (int i = 0; i < 4; i++) t.Add(new TcpEvent(T0.AddMilliseconds(i), 6000, "SLDWORKS", TcpEventKind.Retransmit, Server, 1433, 50000, 0));
        t.Add(new TcpEvent(T0, 6000, "SLDWORKS", TcpEventKind.Reconnect, Server, 3030, 50001, 0));
        var all = t.Snapshot(T0.AddSeconds(1), TimeSpan.FromMinutes(1));
        Assert.Equal(4, all.Single(x => x.Role == "database").Resent);
        Assert.Equal(1, all.Single(x => x.Role == "archive").Retried);
    }

    [Fact]
    public void Forgets_old_traffic()
    {
        var t = Tracker();
        Ask(t, T0, 20);
        Assert.Empty(t.Snapshot(T0.AddMinutes(3), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Explains_a_slow_server_versus_a_slow_network()
    {
        var slowServer = new ServerTraffic("database", "pdmserver", 1433, 300, 1000, 1000, 180, 10, 0, 0, new Dictionary<string, int>(), T0);
        var text = VaultTrafficExplainer.WhatIsHappening([slowServer], tracing: true);
        Assert.Contains("roughly 54.0 s of waiting", text); // 300 x 180 ms
        Assert.Contains("server itself", text);

        var slowLink = slowServer with { MedianReplyMs = 150, FastestReplyMs = 140 };
        Assert.Contains("trip across the network", VaultTrafficExplainer.WhatIsHappening([slowLink], tracing: true));
        Assert.Contains("administrator", VaultTrafficExplainer.Summary([], tracing: false));
    }
}

public class NowWithVaultTrafficTests
{
    private static NowInputs Frozen(IReadOnlyList<ServerTraffic> traffic) =>
        new(true, false, TimeSpan.FromSeconds(12),
            new CpuSnapshot(5, Enumerable.Repeat(5.0, 20).ToList(), new SolidWorksCpu(0.05, 0.3, 0, 3, true)),
            null, null, null, null, [], [], null, null, traffic);

    [Fact]
    public void A_freeze_during_heavy_database_traffic_names_the_server()
    {
        var chatty = new ServerTraffic("database", "pdmserver.example.local", 1433, 120, 50_000, 30_000, 260, 240, 0, 0,
            new Dictionary<string, int> { ["SLDWORKS"] = 120 }, DateTime.Now);
        var r = NowEngine.Evaluate(Frozen([chatty]));
        Assert.Equal("Talking to the vault database server (pdmserver.example.local)", r.Top!.Title);
        Assert.True(r.Top.Glaring); // replies of 250 ms or more
        Assert.Contains("network trip", r.Top.Fix);
    }

    [Fact]
    public void Lost_packets_are_flagged_loudly()
    {
        var lossy = new ServerTraffic("archive", "pdmserver.example.local", 3030, 2, 1000, 1000, 20, 15, 6, 0,
            new Dictionary<string, int>(), DateTime.Now);
        var r = NowEngine.Evaluate(Frozen([lossy]));
        Assert.Contains(r.Glaring, c => c.Kind == "vault-trouble");
    }

    [Fact]
    public void A_big_archive_transfer_is_named()
    {
        var big = new ServerTraffic("archive", "pdmserver.example.local", 3030, 4, 40_000_000, 10_000, 20, 15, 0, 0,
            new Dictionary<string, int>(), DateTime.Now);
        Assert.Contains(NowEngine.Evaluate(Frozen([big])).Causes, c => c.Kind == "vault-archive");
    }
}
