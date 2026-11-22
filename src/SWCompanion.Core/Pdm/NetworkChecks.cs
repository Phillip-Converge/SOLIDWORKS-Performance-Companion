using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SWCompanion.Core.Pdm;

/// <summary>What a server's name turned into, or why it didn't.</summary>
/// <param name="Addresses">The IP addresses Windows found (empty if it couldn't).</param>
/// <param name="Error">Plain-English reason the lookup failed, or null.</param>
/// <param name="IsFullName">True for a fully qualified name such as "pdm.company.local"; false for a short name or an address.</param>
public sealed record NameLookup(string Name, IReadOnlyList<IPAddress> Addresses, string? Error, bool IsFullName);

/// <summary>A line in the Windows hosts file that pins a name to an address, bypassing DNS.</summary>
public sealed record HostsEntry(string Address, string Name);

public enum PortResult { Open, Refused, TimedOut, NameNotFound, NotTested }

/// <param name="Port">The port tested.</param>
/// <param name="Milliseconds">Median time to connect over the attempts that succeeded (null if none did).</param>
public sealed record PortTest(string Role, string Server, int Port, PortResult Result, double? Milliseconds, DateTime When);

/// <summary>
/// Network checks for the vault servers. Name lookups and the hosts file are read only.
/// The port test is opt-in: it opens a connection, times it, and closes it straight
/// away, sending nothing to PDM or SQL Server.
/// </summary>
public static class NetworkChecks
{
    public static readonly string HostsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

    /// <summary>Looks up a server name the way Windows does when connecting (DNS, plus the hosts file). Waits 3 s at most.</summary>
    public static NameLookup Lookup(string name)
    {
        bool full = name.Contains('.') && !IPAddress.TryParse(name, out _);
        if (IPAddress.TryParse(name, out var ip)) return new NameLookup(name, [ip], null, false);
        try
        {
            var task = Dns.GetHostAddressesAsync(name);
            if (!task.Wait(TimeSpan.FromSeconds(3)))
                return new NameLookup(name, [], "the name lookup didn't answer within 3 seconds", full);
            var addresses = task.Result.Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).ToList();
            return new NameLookup(name, addresses, addresses.Count == 0 ? "the name was found but has no address" : null, full);
        }
        catch (AggregateException ex) when (ex.InnerException is SocketException se)
        {
            return new NameLookup(name, [], se.SocketErrorCode == SocketError.HostNotFound ? "no such name was found" : se.Message, full);
        }
        catch (Exception ex)
        {
            return new NameLookup(name, [], ex.Message, full);
        }
    }

    /// <summary>Entries in the hosts file other than the usual "localhost" lines.</summary>
    public static IReadOnlyList<HostsEntry> ReadHostsFile() =>
        File.Exists(HostsPath) ? ParseHosts(File.ReadAllLines(HostsPath)) : [];

    public static IReadOnlyList<HostsEntry> ParseHosts(IEnumerable<string> lines)
    {
        var result = new List<HostsEntry>();
        foreach (var raw in lines)
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            foreach (var name in parts.Skip(1))
                if (!string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase))
                    result.Add(new HostsEntry(parts[0], name));
        }
        return result;
    }

    /// <summary>
    /// Opt-in port test: connects to the server's port up to three times (3 s limit each),
    /// closing each connection immediately, and reports the median connect time.
    /// Nothing is sent. Call from a background thread.
    /// </summary>
    public static PortTest TestPort(string role, string server, int port)
    {
        var when = DateTime.Now;
        var times = new List<double>();
        PortResult last = PortResult.TimedOut;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var client = new TcpClient();
            var sw = Stopwatch.StartNew();
            try
            {
                var connect = client.ConnectAsync(server, port);
                if (!connect.Wait(TimeSpan.FromSeconds(3))) { last = PortResult.TimedOut; continue; }
                times.Add(sw.Elapsed.TotalMilliseconds);
                last = PortResult.Open;
            }
            catch (AggregateException ex) when (ex.InnerException is SocketException se)
            {
                last = se.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => PortResult.Refused,
                    SocketError.HostNotFound or SocketError.NoData => PortResult.NameNotFound,
                    _ => PortResult.TimedOut,
                };
                if (last is PortResult.Refused or PortResult.NameNotFound) break; // a definite answer; don't keep knocking
            }
        }
        double? median = times.Count == 0 ? null : times.OrderBy(t => t).ElementAt(times.Count / 2);
        return new PortTest(role, server, port, times.Count > 0 ? PortResult.Open : last, median, when);
    }
}
