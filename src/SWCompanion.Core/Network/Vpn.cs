using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SWCompanion.Core.Checkup;
using SWCompanion.Core.Explain;

namespace SWCompanion.Core.Network;

/// <summary>A network connection on this PC (Wi-Fi, cable, VPN...).</summary>
public sealed record NetAdapter(int Index, string Name, string Description, bool IsVpn, long BytesTotal);

/// <summary>A server SOLIDWORKS uses (vault server, network folder) and which connection Windows sends its traffic over.</summary>
public sealed record RoutedServer(string Role, string Server, string? Address, NetAdapter? Via);

/// <summary>What the VPN check found. Read only: Windows is asked which connection it *would* use; nothing is sent.</summary>
public sealed record VpnFacts(IReadOnlyList<NetAdapter> Vpns, IReadOnlyList<RoutedServer> Servers, NetAdapter? InternetVia,
    double? VpnBytesPerSecond)
{
    public IReadOnlyList<RoutedServer> ThroughVpn => Servers.Where(s => s.Via?.IsVpn == true).ToList();
}

/// <summary>
/// Finds VPN connections and checks whether SOLIDWORKS's network traffic (the vault's servers, network
/// folders) goes through one. A VPN wraps every request and sends it on a detour through the company's
/// VPN gateway, so it often adds delay, and the vault makes many small requests.
/// </summary>
public static class VpnCheck
{
    // Words that VPN connections use in their names, from the common VPN products and Windows' own VPN.
    private static readonly string[] VpnWords =
    [
        "vpn", "anyconnect", "globalprotect", "pangp", "fortinet", "forticlient", "wireguard", "wintun", "openvpn",
        "tap-windows", "tap adapter", "pulse secure", "juniper", "zscaler", "nordlynx", "sonicwall", "check point",
        "checkpoint", "cisco", "ivanti", "netextender", "watchguard", "openconnect", "tailscale", "zerotier",
        "wan miniport (sstp)", "wan miniport (ikev2)", "wan miniport (l2tp)", "wan miniport (pptp)",
    ];
    // Tunnels Windows uses for itself; not VPNs.
    private static readonly string[] NotVpnWords = ["teredo", "isatap", "6to4", "loopback", "hyper-v", "vethernet", "wsl"];

    /// <summary>Is this connection a VPN? Decided from its type and name (pure, so it can be tested).</summary>
    public static bool LooksLikeVpn(string name, string description, NetworkInterfaceType type)
    {
        string all = (name + " " + description).ToLowerInvariant();
        if (NotVpnWords.Any(all.Contains)) return false;
        if (type == NetworkInterfaceType.Ppp) return true; // dial-up style connections: Windows' built-in VPN
        return VpnWords.Any(all.Contains);
    }

    /// <summary>"\\fileserver\cad\..." -> "fileserver". Null for local paths.</summary>
    public static string? ServerOf(string? path) =>
        path is not null && path.StartsWith(@"\\", StringComparison.Ordinal) && path.Length > 2
            ? path[2..].Split('\\', 2)[0] : null;

    /// <param name="servers">Role and name (or address) of each server to check, e.g. ("archive", "pdmserver").</param>
    public static VpnFacts Read(IEnumerable<(string Role, string Server)> servers, IReadOnlyDictionary<string, IReadOnlyList<string>> knownAddresses)
    {
        var adapters = new List<NetAdapter>();
        foreach (var nic in SafeInterfaces())
        {
            int index;
            long bytes;
            try
            {
                index = nic.GetIPProperties().GetIPv4Properties()?.Index ?? -1;
                var st = nic.GetIPStatistics();
                bytes = st.BytesReceived + st.BytesSent;
            }
            catch (Exception) { continue; } // connections without IPv4
            if (index < 0) continue;
            adapters.Add(new NetAdapter(index, nic.Name, nic.Description, LooksLikeVpn(nic.Name, nic.Description, nic.NetworkInterfaceType), bytes));
        }

        NetAdapter? Route(string? address) =>
            address is not null && IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork &&
            GetBestInterface(BitConverter.ToUInt32(ip.GetAddressBytes(), 0), out uint idx) == 0
                ? adapters.FirstOrDefault(a => a.Index == (int)idx) : null;

        var routed = new List<RoutedServer>();
        foreach (var (role, server) in servers.DistinctBy(s => s.Server.ToLowerInvariant()))
        {
            string? address = IPAddress.TryParse(server, out _) ? server
                : knownAddresses.TryGetValue(server, out var list) ? list.FirstOrDefault(a => !a.Contains(':')) : null;
            routed.Add(new RoutedServer(role, server, address, Route(address)));
        }

        // Which connection ordinary internet traffic would take (a public address; asked, not contacted).
        var internet = Route("8.8.8.8");
        return new VpnFacts(adapters.Where(a => a.IsVpn).ToList(), routed, internet, null);
    }

    private static IEnumerable<NetworkInterface> SafeInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();
        }
        catch (NetworkInformationException) { return []; }
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);
}

/// <summary>Keeps two readings of the VPN's byte counter apart in time, to say how busy it is.</summary>
public sealed class VpnRate
{
    private long? _bytes;
    private DateTime _at;

    public double? Update(DateTime now, IReadOnlyList<NetAdapter> vpns)
    {
        if (vpns.Count == 0) { _bytes = null; return null; }
        long total = vpns.Sum(v => v.BytesTotal);
        double? rate = _bytes is { } b && now > _at && total >= b ? (total - b) / (now - _at).TotalSeconds : null;
        _bytes = total;
        _at = now;
        return rate;
    }
}

/// <summary>Turns the VPN facts into a checkup line in plain words.</summary>
public static class VpnExplainer
{
    public const string Source = "Microsoft documentation on VPN split tunnelling and general SOLIDWORKS PDM support practice";
    private const string Plain = "Think of it like this: a VPN is a private tunnel to the office. Every request goes in at your end, " +
                                 "travels to the office's tunnel entrance, and only then on to the server. Safe, but a detour, and " +
                                 "the vault makes hundreds of small trips for one save.";

    public static CheckResult Evaluate(VpnFacts f)
    {
        if (f.Vpns.Count == 0)
            return new("vpn", "VPN", CheckStatus.Good, "No VPN connection is on.", "", Source, Plain, "Windows");

        string names = string.Join(", ", f.Vpns.Select(v => v.Description.Length > 0 ? v.Description : v.Name).Distinct());
        string busy = f.VpnBytesPerSecond is { } r && r >= 50_000 ? $" About {Io.IoExplainer.Size((ulong)r)}/s is going through it right now." : "";
        bool fullTunnel = f.InternetVia?.IsVpn == true;
        string tunnel = fullTunnel ? " All your internet traffic goes through it too (a \"full tunnel\"), which adds to the load." : "";
        var through = f.ThroughVpn;

        if (through.Count == 0)
        {
            string checkedWhat = f.Servers.Count == 0 ? "No vault or network folders were found to check." :
                $"{f.Servers.Count} server{(f.Servers.Count == 1 ? "" : "s")} SOLIDWORKS uses {(f.Servers.Count == 1 ? "doesn't" : "don't")} go through it.";
            return new("vpn", "VPN", CheckStatus.Good, $"A VPN is on ({names}). {checkedWhat}{tunnel}{busy}", "", Source, Plain, "Windows");
        }

        string list = string.Join(", ", through.Select(s => $"the {s.Role} server {s.Server}"));
        return new("vpn", "VPN", CheckStatus.Look,
            $"Traffic to {list} goes through the VPN ({names}). Every vault or network-folder request takes the detour through the VPN, " +
            $"so saves, check-ins and opening files from there can be slower.{tunnel}{busy}",
            "If you can, work on the office network for heavy vault work. Otherwise ask IT whether the VPN can send vault traffic " +
            "directly (\"split tunnelling\") or whether a nearer replicated archive is available. The \"pdm vault\" section shows reply " +
            "times when file tracing is on.",
            Source, Plain, "Windows");
    }
}
