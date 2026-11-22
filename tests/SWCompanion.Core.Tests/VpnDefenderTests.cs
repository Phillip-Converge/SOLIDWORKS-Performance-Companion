using System.Net.NetworkInformation;
using SWCompanion.Core.AddIn;
using SWCompanion.Core.Antivirus;
using SWCompanion.Core.Checkup;
using SWCompanion.Core.Network;
using SWCompanion.Core.Now;

namespace SWCompanion.Core.Tests;

public class VpnDefenderTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0, DateTimeKind.Local);

    [Theory]
    [InlineData("Ethernet 3", "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter for Windows x64", NetworkInterfaceType.Ethernet, true)]
    [InlineData("Ethernet 2", "PANGP Virtual Ethernet Adapter Secure", NetworkInterfaceType.Ethernet, true)]
    [InlineData("WireGuard Tunnel", "WireGuard Tunnel", NetworkInterfaceType.Unknown, true)]
    [InlineData("Office VPN", "WAN Miniport (IKEv2)", NetworkInterfaceType.Ppp, true)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6E AX211 160MHz", NetworkInterfaceType.Wireless80211, false)]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet, false)]
    [InlineData("Teredo Tunneling Pseudo-Interface", "Teredo Tunneling Pseudo-Interface", NetworkInterfaceType.Tunnel, false)]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, false)]
    public void VPN_connections_are_recognised_and_ordinary_ones_are_not(string name, string description, NetworkInterfaceType type, bool vpn) =>
        Assert.Equal(vpn, VpnCheck.LooksLikeVpn(name, description, type));

    [Theory]
    [InlineData(@"\\fileserver\cad\Library", "fileserver")]
    [InlineData(@"\\10.0.0.20\share", "10.0.0.20")]
    [InlineData(@"C:\Example\Parts", null)]
    [InlineData(null, null)]
    public void The_server_is_read_from_a_network_path(string? path, string? server) => Assert.Equal(server, VpnCheck.ServerOf(path));

    private static readonly NetAdapter Vpn = new(12, "Ethernet 3", "ExampleCorp VPN Adapter", true, 0);
    private static readonly NetAdapter Wifi = new(7, "Wi-Fi", "Example Wi-Fi", false, 0);

    [Fact]
    public void Vault_traffic_through_the_VPN_is_worth_a_look_and_says_why()
    {
        var facts = new VpnFacts([Vpn], [new("archive", "pdmserver.example.local", "10.0.0.20", Vpn), new("network folder", "fileserver", "10.0.0.30", Wifi)],
            InternetVia: Vpn, VpnBytesPerSecond: 2_000_000);
        var r = VpnExplainer.Evaluate(facts);
        Assert.Equal(CheckStatus.Look, r.Status);
        Assert.Contains("the archive server pdmserver.example.local goes through the VPN (ExampleCorp VPN Adapter)", r.Finding);
        Assert.DoesNotContain("fileserver", r.Finding);
        Assert.Contains("full tunnel", r.Finding);
        Assert.Contains("/s is going through it right now", r.Finding);
        Assert.Contains("split tunnelling", r.HowToFix);
    }

    [Fact]
    public void A_VPN_that_SOLIDWORKS_traffic_avoids_and_no_VPN_at_all_are_both_fine()
    {
        var avoided = VpnExplainer.Evaluate(new VpnFacts([Vpn], [new("archive", "pdmserver", "10.0.0.20", Wifi)], Wifi, null));
        Assert.Equal(CheckStatus.Good, avoided.Status);
        Assert.Contains("doesn't go through it", avoided.Finding);
        Assert.Equal(CheckStatus.Good, VpnExplainer.Evaluate(new VpnFacts([], [], Wifi, null)).Status);
    }

    [Fact]
    public void Defender_exclusions_name_what_is_not_excluded_and_admit_when_they_cant_be_read()
    {
        var folders = new List<(string, string)> { ("the SOLIDWORKS program folder", @"C:\Program Files\Example CAD"), ("the ExampleVault vault folder", @"C:\ExampleVault") };
        var some = new DefenderExclusions(true, [@"C:\Program Files\Example CAD\"], ["SLDWORKS.exe"], true);
        var r = DefenderExplainer.Exclusions(some, folders);
        Assert.Equal(CheckStatus.Look, r.Status);
        Assert.Contains("Not excluded: the ExampleVault vault folder.", r.Finding);
        Assert.Contains("SLDWORKS.exe itself is excluded", r.Finding);
        Assert.Contains("security decision", r.HowToFix);

        Assert.True(some.Covers(@"C:\Program Files\Example CAD\sldworks"));
        Assert.False(some.Covers(@"C:\Program Files\Example CADX"));

        var hidden = DefenderExplainer.Exclusions(new DefenderExclusions(false, [], [], true), folders);
        Assert.Equal(CheckStatus.Unknown, hidden.Status);
        Assert.Contains("only shows Defender's exclusion list to administrators", hidden.Finding);
    }

    [Fact]
    public void Processor_time_is_read_from_Windows_process_list_without_opening_the_program()
    {
        string me = System.IO.Path.GetFileName(Environment.ProcessPath!);
        double before = ProcessTimes.CpuSeconds(me)!.Value;
        var end = DateTime.UtcNow.AddMilliseconds(300);
        while (DateTime.UtcNow < end) { } // keep a core busy for a moment
        Assert.True(ProcessTimes.CpuSeconds(me) > before, "our own processor time should have gone up");
        Assert.Null(ProcessTimes.CpuSeconds("no-such-program-example.exe"));
    }

    [Fact]
    public void Defender_processor_time_is_counted_between_two_moments()
    {
        var d = new DefenderSampler();
        d.Add(T0, 100);
        d.Add(T0.AddSeconds(2), 101);   // half a core
        Assert.Equal(0.5, d.CoresNow!.Value, 3);
        d.Add(T0.AddSeconds(4), 104);   // a core and a half
        d.Add(T0.AddSeconds(6), 104.2);
        Assert.Equal(4.2, d.CpuSecondsBetween(T0, T0.AddSeconds(4))!.Value, 3);
        Assert.Null(d.CpuSecondsBetween(T0.AddMinutes(-5), T0)); // before we were watching
    }

    [Fact]
    public void A_slow_sheet_switch_says_what_Defender_did_meanwhile()
    {
        Assert.Equal("Windows Defender scanned 12 files during it, about 6.4 s of scanning; Defender used 3.1 s of processor while it ran.",
            DefenderExplainer.DuringAction(3.1, 6.4, 12));
        Assert.Null(DefenderExplainer.DuringAction(0.2, null, 0)); // Defender was quiet: nothing to say

        var c = new MomentCatcher { ActionNote = (_, _) => "Defender used 3.1 s of processor while it ran." };
        c.AddAction(new AddinAction("sheet", @"C:\Example\Pump.SLDDRW", "Sheet3", T0, 12));
        Assert.StartsWith("Defender used 3.1 s of processor while it ran. Section 08", Assert.Single(c.Caught).Detail);
    }

    [Fact]
    public void Defender_busy_while_SOLIDWORKS_waits_shows_up_right_now_but_not_when_SOLIDWORKS_is_idle()
    {
        NowReport Run(double? defender, bool? responding) => NowEngine.Evaluate(new NowInputs(true, responding,
            responding == false ? TimeSpan.FromSeconds(10) : TimeSpan.Zero, null, null, null, null, null, [], [], DefenderCores: defender));
        Assert.Contains(Run(1.2, false).Causes, c => c.Kind == "defender-busy" && c.Evidence.Contains("most likely"));
        Assert.DoesNotContain(Run(1.2, true).Causes, c => c.Kind == "defender-busy");
        Assert.DoesNotContain(Run(0.1, false).Causes, c => c.Kind == "defender-busy");
    }
}
