using Microsoft.Win32;
using SWCompanion.Core.Pdm;

namespace SWCompanion.Core.Network;

/// <summary>A SolidNetWork License server this PC is set to use: "25734@server" means port 25734 on "server".</summary>
public sealed record LicenseServer(string Host, int Port);

/// <summary>
/// The SolidNetWork License (SNL) servers SOLIDWORKS on this PC is set to use, and an opt-in test of their ports. The
/// servers are read from the SW_D_LICENSE_FILE setting (environment, then the FlexNet registry key): read only. The
/// test connects, times it and closes straight away, sending nothing to the licence server; it never touches licences.
/// </summary>
public static class LicenseServers
{
    /// <summary>The licence manager's usual port, and the usual fixed port of SOLIDWORKS's vendor daemon (sw_d) behind it.</summary>
    public const int ManagerPort = 25734, VendorPort = 25735;
    public const string SettingName = "SW_D_LICENSE_FILE";

    /// <summary>The servers from every place Windows keeps the setting, without repeats. Empty: this PC doesn't use a licence server.</summary>
    public static IReadOnlyList<LicenseServer> Read()
    {
        var values = new List<string?>
        {
            Environment.GetEnvironmentVariable(SettingName),
            Environment.GetEnvironmentVariable(SettingName, EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable(SettingName, EnvironmentVariableTarget.Machine),
            ReadRegistry(Registry.CurrentUser),
            ReadRegistry(Registry.LocalMachine),
        };
        return values.SelectMany(v => Parse(v)).DistinctBy(s => (s.Host.ToLowerInvariant(), s.Port)).ToList();
    }

    /// <summary>"25734@server1;25734@server2" -> two servers. A missing port ("@server") means the licence manager's usual one.</summary>
    public static IReadOnlyList<LicenseServer> Parse(string? value)
    {
        var list = new List<LicenseServer>();
        if (string.IsNullOrWhiteSpace(value)) return list;
        // Some installers pad the registry value with invisible "null" characters: everything from the first one is padding.
        value = value.Split('\0')[0];
        foreach (var raw in value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int at = raw.IndexOf('@');
            if (at < 0) continue; // a licence file path, not a server
            string host = raw[(at + 1)..].Trim();
            if (host.Length == 0) continue;
            int port = int.TryParse(raw[..at].Trim(), out var p) && p is > 0 and < 65536 ? p : ManagerPort;
            list.Add(new LicenseServer(host, port));
        }
        return list;
    }

    /// <summary>Opt-in: tests each server's licence manager port and the usual vendor port. Call from a background thread.</summary>
    public static IReadOnlyList<PortTest> Test(IEnumerable<LicenseServer> servers)
    {
        var results = new List<PortTest>();
        foreach (var s in servers)
        {
            results.Add(NetworkChecks.TestPort("licence manager", s.Host, s.Port));
            if (s.Port != VendorPort) results.Add(NetworkChecks.TestPort("vendor daemon", s.Host, VendorPort));
        }
        return results;
    }

    /// <summary>What the results mean, in plain words, one line per server.</summary>
    public static IReadOnlyList<string> Explain(IReadOnlyList<PortTest> results)
    {
        var lines = new List<string>();
        foreach (var server in results.GroupBy(r => r.Server, StringComparer.OrdinalIgnoreCase))
        {
            var manager = server.FirstOrDefault(r => r.Role == "licence manager");
            var vendor = server.FirstOrDefault(r => r.Role == "vendor daemon");
            string name = server.Key;
            if (manager is null) continue;
            string line = manager.Result switch
            {
                PortResult.Open when vendor is null or { Result: PortResult.Open } =>
                    $"{name}: the licence server answered{Time(manager)}{(vendor is null ? "" : $", and port {VendorPort} too")}. The connection to it looks fine.",
                PortResult.Open =>
                    $"{name}: the licence server answered on port {manager.Port}{Time(manager)}, but port {VendorPort} didn't. That's fine if your server " +
                    $"uses a different port for SOLIDWORKS's part of the licence service. If licences fail to check out, ask your IT team whether port " +
                    $"{VendorPort} is open on the firewall between this PC and the server.",
                PortResult.NameNotFound =>
                    $"{name}: Windows couldn't find a computer with that name. Check the name in the SolidNetWork License settings, or ask your IT team about the network name lookup (DNS).",
                PortResult.Refused =>
                    $"{name}: the computer answered but nothing is listening on port {manager.Port}. The SolidNetWork License Manager on the server may be stopped, or using another port. Ask whoever looks after the licence server.",
                _ =>
                    $"{name}: no answer on port {manager.Port} within 3 seconds. A firewall may be blocking it, the server may be off, or (away from the office) the VPN may not be connected.",
            };
            lines.Add(line);
        }
        return lines;
    }

    private static string Time(PortTest t) => t.Milliseconds is { } ms ? $" in {ms:0} ms" : "";

    private static string? ReadRegistry(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(@"SOFTWARE\FLEXlm License Manager");
            return key?.GetValue(SettingName) as string;
        }
        catch
        {
            return null;
        }
    }
}
