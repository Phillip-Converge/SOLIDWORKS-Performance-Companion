using SWCompanion.Core.Now;
using SWCompanion.Core.Pdm;

namespace SWCompanion.Core.Tests;

public class TrafficMapTests
{
    private static readonly VaultView OneServer = new("ExampleVault", "pdmserver.example.local", "ExampleVault",
        "pdmserver.example.local", 3030, null, []);
    private static readonly VaultView TwoServers = new("ExampleVault", "sql01.example.local", "ExampleVault",
        "archive01.example.local", 3030, null, []);

    private static ServerTraffic Db(string server, int requests, double median, double fastest, int resent = 0) =>
        new("database", server, 1433, requests, 100_000, 50_000, median, fastest, resent, 0, new Dictionary<string, int>(), DateTime.Now);

    [Fact]
    public void One_machine_for_database_and_archive_is_one_box_with_two_lines()
    {
        var m = TrafficMap.Build([OneServer], [], tracing: true, NowLevel.Calm, Verdict.None);
        var box = Assert.Single(m.Servers);
        Assert.Equal(2, box.Links.Count);
        Assert.Equal("archive + database", box.Roles);
        Assert.All(box.Links, l => Assert.Equal(LinkHealth.Idle, l.Health));
        Assert.Equal("SOLIDWORKS: responding", m.PcState);
    }

    [Fact]
    public void Two_machines_are_two_boxes()
    {
        var m = TrafficMap.Build([TwoServers], [], tracing: true, NowLevel.Calm, Verdict.None);
        Assert.Equal(2, m.Servers.Count);
    }

    [Fact]
    public void Busy_slow_and_troubled_lines_look_different()
    {
        var good = TrafficMap.Link("database", Db("s", 10, 8, 5), true);
        var slow = TrafficMap.Link("database", Db("s", 10, 150, 30), true);
        var verySlow = TrafficMap.Link("database", Db("s", 80, 300, 30), true);
        var trouble = TrafficMap.Link("database", Db("s", 2, 10, 5, resent: 5), true);

        Assert.Equal(LinkHealth.Good, good.Health);
        Assert.Equal(LinkHealth.Slow, slow.Health);
        Assert.Equal(LinkHealth.VerySlow, verySlow.Health);
        Assert.Equal(LinkHealth.Trouble, trouble.Health);
        Assert.True(verySlow.Dots > good.Dots, "more traffic, more dots");
        Assert.True(verySlow.Thickness > good.Thickness, "more traffic, thicker line");
        Assert.True(verySlow.Seconds > good.Seconds, "slower replies, slower dots");
        Assert.Contains("network 30, server 270", verySlow.Label);
    }

    [Fact]
    public void A_frozen_SolidWorks_shows_its_verdict_on_the_PC()
    {
        Assert.Equal("SOLIDWORKS: STUCK", TrafficMap.Build([OneServer], [], true, NowLevel.Frozen, Verdict.Stuck).PcState);
        Assert.Equal(MapState.Working, TrafficMap.Build([OneServer], [], true, NowLevel.Frozen, Verdict.Background).State);
    }

    [Fact]
    public void Traffic_to_a_server_not_in_any_vault_view_still_gets_a_box()
    {
        var m = TrafficMap.Build([], [Db("10.0.0.20", 30, 20, 10)], tracing: true, NowLevel.Calm, Verdict.None);
        Assert.Equal("10.0.0.20", Assert.Single(m.Servers).Name);
    }

    [Fact]
    public void Empty_and_untraced_maps_explain_themselves()
    {
        Assert.Contains("No vault view", TrafficMap.Build([], [], true, NowLevel.Calm, Verdict.None).Note);
        Assert.Contains("administrator", TrafficMap.Build([OneServer], [], false, NowLevel.Calm, Verdict.None).Note);
    }

    [Fact]
    public void The_example_is_clearly_labelled_and_uses_made_up_servers()
    {
        var m = TrafficMap.Example();
        Assert.True(m.IsExample);
        Assert.StartsWith("EXAMPLE", m.Note);
        Assert.All(m.Servers, s => Assert.EndsWith(".example.local", s.Name));
    }
}
