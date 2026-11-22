using System.Net;

namespace SWCompanion.Core.Pdm;

public enum TcpEventKind { Send, Receive, Connect, Retransmit, Reconnect }

/// <summary>One network event from Windows' tracing.</summary>
/// <param name="Remote">The other end's address.</param>
/// <param name="RemotePort">The other end's port (e.g. 1433 for SQL Server, 3030 for a PDM archive).</param>
/// <param name="LocalPort">This PC's port for the connection (tells connections apart).</param>
/// <param name="Bytes">Size of a send or receive.</param>
public sealed record TcpEvent(DateTime Time, int Pid, string Program, TcpEventKind Kind, IPAddress Remote, int RemotePort, int LocalPort, int Bytes);

/// <summary>A server worth watching: which role, its name, its addresses and port.</summary>
public sealed record VaultServerTarget(string Role, string Server, IReadOnlyList<IPAddress> Addresses, int Port);

/// <summary>Traffic to one vault server over a period.</summary>
/// <param name="Role">"database" or "archive".</param>
/// <param name="Requests">How many times something was asked and a reply came back.</param>
/// <param name="MedianReplyMs">Typical time from asking to the first byte of the reply.</param>
/// <param name="FastestReplyMs">The quickest reply: the best estimate of the network trip alone.</param>
/// <param name="Resent">Packets that had to be sent again (lost on the way).</param>
/// <param name="Retried">Connection attempts that had to be retried (no answer to the first knock).</param>
/// <param name="ByProgram">Requests per program, e.g. SLDWORKS 300, explorer 40.</param>
public sealed record ServerTraffic(
    string Role, string Server, int Port, int Requests, long BytesIn, long BytesOut,
    double? MedianReplyMs, double? FastestReplyMs, int Resent, int Retried,
    IReadOnlyDictionary<string, int> ByProgram, DateTime? LastActivity)
{
    /// <summary>Estimated server thinking time per reply: typical reply minus the network trip (never below zero).</summary>
    public double? ServerMs => MedianReplyMs is { } m && FastestReplyMs is { } f ? Math.Max(0, m - f) : null;
}

/// <summary>
/// Keeps a rolling picture of the traffic between this PC and the vault's servers.
/// Only traffic to the known servers, or to the standard SQL Server (1433) and PDM
/// archive (3030) ports, is kept; everything else is ignored. Fed by the tracing
/// session; separate so it can be tested with made-up events.
/// </summary>
public sealed class VaultTrafficTracker
{
    public const int StandardSqlPort = 1433, StandardArchivePort = 3030;
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(2);

    private readonly object _lock = new();
    private List<VaultServerTarget> _targets = new();
    private readonly List<(DateTime Time, string Key, string Program, double? ReplyMs, long In, long Out, TcpEventKind Kind)> _events = new();
    private readonly Dictionary<(int Pid, int LocalPort, string Remote, int RemotePort), DateTime?> _waiting = new();

    /// <summary>Sets the servers found from the vault views (role, name, addresses, port).</summary>
    public void SetTargets(IEnumerable<VaultServerTarget> targets)
    {
        lock (_lock) _targets = targets.ToList();
    }

    /// <summary>Quick filter for the tracing session: is this traffic worth keeping at all?</summary>
    public bool IsVaultTraffic(IPAddress remote, int remotePort)
    {
        lock (_lock) return Classify(remote, remotePort) is not null;
    }

    public void Add(TcpEvent e)
    {
        lock (_lock)
        {
            var target = Classify(e.Remote, e.RemotePort);
            if (target is null) return;
            string key = Key(target.Value.Role, target.Value.Server, e.RemotePort);
            var conn = (e.Pid, e.LocalPort, e.Remote.ToString(), e.RemotePort);

            switch (e.Kind)
            {
                case TcpEventKind.Send:
                    // A send while not already waiting starts a new request.
                    if (!_waiting.TryGetValue(conn, out var started) || started is null) _waiting[conn] = e.Time;
                    _events.Add((e.Time, key, e.Program, null, 0, e.Bytes, e.Kind));
                    break;
                case TcpEventKind.Receive:
                    double? reply = null;
                    if (_waiting.TryGetValue(conn, out var since) && since is { } s)
                    {
                        reply = (e.Time - s).TotalMilliseconds; // asked at s, first reply byte now
                        _waiting[conn] = null;
                    }
                    _events.Add((e.Time, key, e.Program, reply, e.Bytes, 0, e.Kind));
                    break;
                default:
                    _events.Add((e.Time, key, e.Program, null, 0, 0, e.Kind));
                    break;
            }
        }
    }

    /// <summary>Per-server totals over the last <paramref name="window"/>, busiest first.</summary>
    public IReadOnlyList<ServerTraffic> Snapshot(DateTime now, TimeSpan window)
    {
        lock (_lock)
        {
            _events.RemoveAll(x => now - x.Time > KeepFor);
            if (_waiting.Count > 5000) _waiting.Clear(); // safety valve
            return _events.Where(x => now - x.Time <= window)
                .GroupBy(x => x.Key)
                .Select(g =>
                {
                    var (role, server, port) = Split(g.Key);
                    var replies = g.Where(x => x.ReplyMs is not null).Select(x => x.ReplyMs!.Value).OrderBy(v => v).ToList();
                    return new ServerTraffic(role, server, port, replies.Count,
                        g.Sum(x => x.In), g.Sum(x => x.Out),
                        replies.Count == 0 ? null : replies[replies.Count / 2],
                        replies.Count == 0 ? null : replies[0],
                        g.Count(x => x.Kind == TcpEventKind.Retransmit),
                        g.Count(x => x.Kind == TcpEventKind.Reconnect),
                        g.Where(x => x.ReplyMs is not null).GroupBy(x => x.Program).ToDictionary(p => p.Key, p => p.Count()),
                        g.Max(x => x.Time));
                })
                .OrderByDescending(t => t.Requests + t.Resent + t.Retried)
                .ToList();
        }
    }

    private (string Role, string Server)? Classify(IPAddress remote, int port)
    {
        foreach (var t in _targets)
            if (t.Port == port && (t.Addresses.Count == 0 || t.Addresses.Any(a => a.Equals(remote))))
                return (t.Role, t.Server);
        // No vault view yet, or a server not listed: still recognise the standard ports.
        return port switch
        {
            StandardSqlPort => ("database", remote.ToString()),
            StandardArchivePort => ("archive", remote.ToString()),
            _ => null,
        };
    }

    private static string Key(string role, string server, int port) => $"{role}|{server}|{port}";

    private static (string, string, int) Split(string key)
    {
        var p = key.Split('|');
        return (p[0], p[1], int.Parse(p[2]));
    }
}
