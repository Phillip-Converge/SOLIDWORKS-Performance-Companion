using System.Net;
using System.Net.Sockets;
using SWCompanion.Core.Checkup;
using SWCompanion.Core.Pdm;

namespace SWCompanion.Core.Tests;

public class VaultDiscoveryTests
{
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> Copy(string vault, params (string, object)[] values) =>
        new Dictionary<string, IReadOnlyDictionary<string, object>>
        {
            [vault] = values.ToDictionary(v => v.Item1, v => v.Item2, StringComparer.OrdinalIgnoreCase),
        };

    private static readonly (string, object)[] OneServer =
    [
        ("DbServer", "pdmserver.example.local"), ("DbName", "ExampleVault"),
        ("ServerLoc", "pdmserver.example.local"), ("ServerPort", 3030), ("ShellRoot", @"C:\ExampleVault"),
    ];

    [Fact]
    public void Reads_a_vault_on_one_server()
    {
        var v = VaultDiscovery.Build(Copy("ExampleVault", OneServer), Copy("ExampleVault", OneServer)).Single();
        Assert.Equal("pdmserver.example.local", v.DatabaseHost);
        Assert.Equal(1433, v.SqlPort);
        Assert.True(v.SameServerByName);
        Assert.Empty(v.CopiesDisagree);
    }

    [Fact]
    public void Understands_named_instances_and_explicit_ports()
    {
        var named = VaultDiscovery.Build(Copy("V", ("DbServer", @"sql01\PDMINSTANCE"), ("ServerLoc", "archive01"), ("ServerPort", 3031)), Copy("V")).Single();
        Assert.Equal("sql01", named.DatabaseHost);
        Assert.Equal("PDMINSTANCE", named.SqlInstance);
        Assert.Null(named.SqlPort); // chosen by SQL Server
        Assert.Equal(3031, named.ArchivePortOrDefault);
        Assert.False(named.SameServerByName);

        var port = VaultDiscovery.Build(Copy("V", ("DbServer", "sql01,1500"), ("ServerLoc", "archive01")), Copy("V")).Single();
        Assert.Equal(1500, port.SqlPort);
        Assert.Equal(3030, port.ArchivePortOrDefault); // default when not stored
    }

    [Fact]
    public void Flags_registry_copies_that_disagree()
    {
        var v = VaultDiscovery.Build(
            Copy("V", ("DbServer", "pdmserver.example.local"), ("ServerLoc", "pdmserver.example.local")),
            Copy("V", ("DbServer", "pdmserver"), ("ServerLoc", "pdmserver.example.local"))).Single();
        var diff = Assert.Single(v.CopiesDisagree);
        Assert.Contains("DbServer", diff);
    }

    [Fact]
    public void Reads_this_PC_without_errors() => Assert.NotNull(VaultDiscovery.Read());
}

public class VaultChecksTests
{
    private static readonly VaultView OneServer = new("ExampleVault", "pdmserver.example.local", "ExampleVault",
        "pdmserver.example.local", 3030, @"C:\ExampleVault", []);
    private static readonly VaultView TwoServers = new("ExampleVault", "sql01.example.local", "ExampleVault",
        "archive01.example.local", 3030, null, []);

    private static Dictionary<string, NameLookup> Found(params string[] names) =>
        names.ToDictionary(n => n, n => new NameLookup(n, [IPAddress.Parse("10.0.0." + (n.Length % 200))], null, n.Contains('.')),
            StringComparer.OrdinalIgnoreCase);

    private static CheckResult Row(IReadOnlyList<CheckResult> rows, string prefix) => rows.Single(r => r.Id.StartsWith(prefix));

    [Fact]
    public void One_server_is_described_as_one()
    {
        var rows = VaultExplainer.Checks(OneServer, Found("pdmserver.example.local"), [], []);
        Assert.Contains("same server", Row(rows, "vault-servers").Finding);
        Assert.Equal(CheckStatus.Good, Row(rows, "vault-names").Status);
        Assert.All(rows, r => Assert.Equal("PDM client", r.Group));
    }

    [Fact]
    public void Two_servers_note_that_delays_add_up()
    {
        var rows = VaultExplainer.Checks(TwoServers, Found("sql01.example.local", "archive01.example.local"), [], []);
        Assert.Contains("adds up separately", Row(rows, "vault-servers").Finding);
    }

    [Fact]
    public void A_name_that_cannot_be_found_is_a_problem()
    {
        var lookups = new Dictionary<string, NameLookup>(StringComparer.OrdinalIgnoreCase)
        {
            ["pdmserver.example.local"] = new("pdmserver.example.local", [], "no such name was found", true),
        };
        var r = Row(VaultExplainer.Checks(OneServer, lookups, [], []), "vault-names");
        Assert.Equal(CheckStatus.Problem, r.Status);
        Assert.Contains("nslookup", r.HowToFix);
    }

    [Fact]
    public void A_hosts_file_pin_for_a_vault_server_is_flagged()
    {
        var hosts = NetworkChecks.ParseHosts([
            "# comment",
            "127.0.0.1 localhost",
            "10.0.0.25   pdmserver   # pinned years ago",
            "10.0.0.99 printer",
        ]);
        Assert.Equal(2, hosts.Count);
        var r = Row(VaultExplainer.Checks(OneServer, Found("pdmserver.example.local"), hosts, []), "vault-hosts");
        Assert.Equal(CheckStatus.Look, r.Status);
        Assert.Contains("pdmserver to 10.0.0.25", r.Finding);
    }

    [Fact]
    public void Port_results_are_explained_in_plain_English()
    {
        var now = DateTime.Now;
        var fast = Row(VaultExplainer.Checks(OneServer, Found("pdmserver.example.local"), [],
            [new PortTest("archive", "pdmserver.example.local", 3030, PortResult.Open, 1, now)]), "vault-port-archive");
        Assert.Equal(CheckStatus.Good, fast.Status);
        Assert.Contains("same building", fast.Finding);

        var far = VaultExplainer.Describe("x", "t", new PortTest("database", "sql01", 1433, PortResult.Open, 80, now), "");
        Assert.Equal(CheckStatus.Look, far.Status);
        Assert.Contains("about 32.0 s for the network alone", far.Finding); // 400 x 80 ms, clearly labelled as an example

        var blocked = VaultExplainer.Describe("x", "t", new PortTest("database", "sql01", 1433, PortResult.TimedOut, null, now), "");
        Assert.Equal(CheckStatus.Problem, blocked.Status);
        Assert.Contains("not which firewall", blocked.HowToFix); // honest about the limit

        var refused = VaultExplainer.Describe("x", "t", new PortTest("archive", "a", 3030, PortResult.Refused, null, now), "");
        Assert.Contains("service may be stopped", refused.Finding);
    }

    [Fact]
    public void Untested_ports_say_how_to_test_and_named_instances_say_why_not()
    {
        var rows = VaultExplainer.Checks(OneServer, Found("pdmserver.example.local"), [], []);
        Assert.Contains("test the server ports", Row(rows, "vault-port-archive").Finding);
        var named = OneServer with { DatabaseServer = @"pdmserver\PDM" };
        Assert.Contains("named SQL Server instance", Row(VaultExplainer.Checks(named, Found("pdmserver.example.local", "pdmserver"), [], []), "vault-port-database").Finding);
    }
}

public class PortTestTests
{
    [Fact]
    public void An_open_port_is_timed_and_a_closed_port_is_refused()
    {
        // A listener on this PC only (loopback): no traffic leaves the machine.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int open = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var ok = NetworkChecks.TestPort("archive", "127.0.0.1", open);
            Assert.Equal(PortResult.Open, ok.Result);
            Assert.NotNull(ok.Milliseconds);
        }
        finally
        {
            listener.Stop();
        }
        var closed = NetworkChecks.TestPort("archive", "127.0.0.1", open); // same port, now nobody listening
        Assert.Equal(PortResult.Refused, closed.Result);
    }
}

public class VaultNotesTests
{
    [Fact]
    public void Saves_and_reloads_notes_per_vault()
    {
        var file = Path.Combine(Path.GetTempPath(), "swc-notes-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new VaultNotesStore(file);
            store.Save("ExampleVault", new VaultNote("off-site data centre", "same building", "VPN", "yes", "SQL hosted by a provider"));
            store.Save("Other", new VaultNote());
            var all = store.Load();
            Assert.Equal(2, all.Count);
            Assert.Equal("VPN", all["ExampleVault"].Link);
            Assert.Equal("SQL hosted by a provider", all["ExampleVault"].Notes);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file); // the test's own temporary file only
        }
    }

    [Fact]
    public void Notes_live_in_the_users_app_data_folder()
    {
        Assert.StartsWith(AppPaths.LocalAppDataFolder, new VaultNotesStore().FilePath);
    }
}
