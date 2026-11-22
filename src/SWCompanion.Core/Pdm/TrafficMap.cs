using SWCompanion.Core.Io;
using SWCompanion.Core.Now;

namespace SWCompanion.Core.Pdm;

public enum LinkHealth { Idle, Good, Slow, VerySlow, Trouble }

public enum MapState { NotRunning, Responding, Busy, Working, Quiet, Stuck }

/// <summary>One line on the map, between this PC and a server, for one role.</summary>
/// <param name="Role">"database" or "archive".</param>
/// <param name="Dots">How many moving dots to show (0 to 4): more traffic, more dots.</param>
/// <param name="Thickness">Line thickness: more traffic, thicker line.</param>
/// <param name="Seconds">How long one dot takes to cross: faster replies, quicker dots.</param>
/// <param name="Label">Short text on the line.</param>
public sealed record MapLink(string Role, LinkHealth Health, int Dots, double Thickness, double Seconds, string Label);

/// <summary>A server box, with one line per role it serves (one box can be both database and archive).</summary>
public sealed record MapServer(string Name, string Roles, IReadOnlyList<MapLink> Links);

/// <param name="PcState">SOLIDWORKS's state, shown on this PC's box.</param>
/// <param name="Note">A sentence under the map (what it shows, or why it's empty).</param>
/// <param name="IsExample">True for the made-up example, which must be labelled clearly.</param>
public sealed record MapModel(MapState State, string PcState, IReadOnlyList<MapServer> Servers, string Note, bool IsExample = false);

/// <summary>
/// Works out what the live traffic map shows. Kept separate from the drawing so it
/// can be tested: the window only draws what this decides.
/// </summary>
public static class TrafficMap
{
    public static MapModel Build(IReadOnlyList<VaultView> vaults, IReadOnlyList<ServerTraffic> traffic, bool tracing,
                                 NowLevel level, Verdict verdict)
    {
        var (state, pc) = PcState(level, verdict);

        // Servers from the vault views, grouped by machine (one box when database and archive share a server).
        var roles = new List<(string Host, string Role, string Name)>();
        foreach (var v in vaults)
        {
            if (v.DatabaseHost is { } d) roles.Add((VaultView.ShortName(d), "database", d));
            if (v.ArchiveServer is { } a) roles.Add((VaultView.ShortName(a), "archive", a));
        }
        // Traffic to servers not in a vault view (standard ports) still gets a box.
        foreach (var t in traffic.Where(t => roles.All(r => !Same(r.Name, t.Server))))
            roles.Add((VaultView.ShortName(t.Server), t.Role, t.Server));

        var servers = roles
            .GroupBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var links = g.Select(r => r.Role).Distinct()
                    .Select(role => Link(role, traffic.FirstOrDefault(t => t.Role == role && g.Any(x => Same(x.Name, t.Server))), tracing))
                    .OrderBy(l => l.Role)
                    .ToList();
                return new MapServer(g.First().Name, string.Join(" + ", links.Select(l => l.Role)), links);
            })
            .ToList();

        string note = servers.Count == 0
            ? "No vault view on this PC, so there are no servers to show. Use \"show an example\" to see what the map looks like."
            : !tracing
                ? "Turn on tracing (restart as administrator in the files section) to see live traffic on these lines."
                : servers.SelectMany(s => s.Links).All(l => l.Health == LinkHealth.Idle)
                    ? "No traffic to the vault's servers in the last few seconds."
                    : "Dots are requests going out and replies coming back. Thicker line: more traffic. Colour: how long replies take.";
        return new MapModel(state, pc, servers, note);
    }

    /// <summary>A made-up example: one server for database and archive, a busy database line. Clearly labelled.</summary>
    public static MapModel Example() =>
        new(MapState.Working, "SOLIDWORKS: WORKING (example)",
        [
            new MapServer("pdmserver.example.local", "archive + database",
            [
                Link("archive", new ServerTraffic("archive", "pdmserver.example.local", 3030, 6, 6_000_000, 40_000, 22, 18, 0, 0, new Dictionary<string, int>(), null), true),
                Link("database", new ServerTraffic("database", "pdmserver.example.local", 1433, 140, 400_000, 120_000, 120, 35, 0, 0, new Dictionary<string, int>(), null), true),
            ]),
        ],
        "EXAMPLE with made-up servers, not your vault: a busy database line where each reply takes about 120 ms (network 35, server 85).",
        IsExample: true);

    public static MapLink Link(string role, ServerTraffic? t, bool tracing)
    {
        if (!tracing || t is null || t.Requests + t.Resent + t.Retried == 0 && t.BytesIn + t.BytesOut == 0)
            return new MapLink(role, LinkHealth.Idle, 0, 2, 2, tracing ? $"{role}: quiet" : role);

        var health = t.Resent + t.Retried >= VaultTrafficExplainer.TroubleCount ? LinkHealth.Trouble
                   : t.MedianReplyMs >= VaultTrafficExplainer.VerySlowReplyMs ? LinkHealth.VerySlow
                   : t.MedianReplyMs >= VaultTrafficExplainer.SlowReplyMs ? LinkHealth.Slow
                   : LinkHealth.Good;

        // Busier line: more dots and thicker. Archive traffic also counts its data volume.
        int amount = t.Requests + (int)Math.Min(60, (t.BytesIn + t.BytesOut) / (256 * 1024));
        int dots = amount switch { 0 => 0, < 5 => 1, < 20 => 2, < 60 => 3, _ => 4 };
        if (health == LinkHealth.Trouble) dots = Math.Max(dots, 1);
        double thickness = 2 + Math.Min(6, Math.Log2(amount + 1));
        double seconds = Math.Clamp((t.MedianReplyMs ?? 100) / 50.0, 0.6, 3.0); // slower replies, slower dots

        string label = role == "archive" && t.BytesIn + t.BytesOut >= 256 * 1024
            ? $"{role}: {IoExplainer.Size((ulong)(t.BytesIn + t.BytesOut))} moved"
            : $"{role}: {t.Requests} requests";
        if (t.MedianReplyMs is { } m) label += $" · {m:0} ms (network {t.FastestReplyMs:0}, server {t.ServerMs:0})";
        if (t.Resent + t.Retried > 0) label += $" · {t.Resent + t.Retried} resent/retried";
        return new MapLink(role, health, dots, thickness, seconds, label);
    }

    private static (MapState, string) PcState(NowLevel level, Verdict verdict) => level switch
    {
        NowLevel.NotRunning => (MapState.NotRunning, "SOLIDWORKS: not running"),
        NowLevel.Frozen => verdict switch
        {
            Verdict.Working or Verdict.Background => (MapState.Working, "SOLIDWORKS: not responding, WORKING"),
            Verdict.ProbablyStuck or Verdict.Stuck => (MapState.Stuck, verdict == Verdict.Stuck ? "SOLIDWORKS: STUCK" : "SOLIDWORKS: PROBABLY STUCK"),
            _ => (MapState.Quiet, "SOLIDWORKS: not responding, QUIET"),
        },
        NowLevel.Busy => (MapState.Busy, "SOLIDWORKS: responding, needs attention"),
        _ => (MapState.Responding, "SOLIDWORKS: responding"),
    };

    private static bool Same(string a, string b) =>
        string.Equals(VaultView.ShortName(a), VaultView.ShortName(b), StringComparison.OrdinalIgnoreCase);
}
