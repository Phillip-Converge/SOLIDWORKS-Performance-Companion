using SWCompanion.Core.Explain;
using SWCompanion.Core.Io;

namespace SWCompanion.Core.Pdm;

/// <summary>
/// Plain-English wording for traffic to the vault's servers. The network/server split
/// is an estimate: the fastest reply stands in for the network trip, and anything
/// above it is counted as the server thinking. The text says so.
/// </summary>
public static class VaultTrafficExplainer
{
    /// <summary>In 5 seconds: this many database round trips is "chatty".</summary>
    public const int ChattyRequests = 20;
    public const double SlowReplyMs = 100;
    public const double VerySlowReplyMs = 250;
    public const long BigTransferBytes = 1024 * 1024;
    public const int TroubleCount = 3;

    /// <summary>"database server pdmserver (port 1433): 340 requests, typical reply 12 ms (network about 10 ms, server about 2 ms)..."</summary>
    public static string Line(ServerTraffic t)
    {
        var parts = new List<string> { $"{t.Requests} request{(t.Requests == 1 ? "" : "s")}" };
        if (t.MedianReplyMs is { } m)
            parts.Add($"typical reply {m:0} ms (network about {t.FastestReplyMs:0} ms, server about {t.ServerMs:0} ms)");
        if (t.BytesIn + t.BytesOut > 0)
            parts.Add($"{IoExplainer.Size((ulong)t.BytesIn)} in, {IoExplainer.Size((ulong)t.BytesOut)} out");
        if (t.Resent > 0) parts.Add($"{t.Resent} packet{(t.Resent == 1 ? "" : "s")} resent");
        if (t.Retried > 0) parts.Add($"{t.Retried} connection attempt{(t.Retried == 1 ? "" : "s")} retried");
        string who = t.ByProgram.Count == 0 ? "" :
            " Mostly " + string.Join(", ", t.ByProgram.OrderByDescending(p => p.Value).Take(3).Select(p => $"{p.Key} ({p.Value})")) + ".";
        return $"{t.Role} server {t.Server} (port {t.Port}): {string.Join(", ", parts)}.{who}";
    }

    public static string Summary(IReadOnlyList<ServerTraffic> lastMinute, bool tracing)
    {
        if (!tracing) return "Traffic to the vault's servers is watched when file tracing is on (restart as administrator in the files section).";
        if (lastMinute.Count == 0) return "No traffic to the vault's servers in the last minute.";
        return $"Traffic to the vault's servers in the last minute: {lastMinute.Sum(t => t.Requests)} requests across {lastMinute.Count} server connection{(lastMinute.Count == 1 ? "" : "s")}.";
    }

    /// <summary>What the traffic means, most important first.</summary>
    public static string WhatIsHappening(IReadOnlyList<ServerTraffic> lastMinute, bool tracing)
    {
        if (!tracing)
            return "With tracing on, this shows how often SOLIDWORKS, PDM in File Explorer and the PDM client ask the vault's servers " +
                   "something, how long each answer takes, and whether the time goes on the network trip or on the server itself.";
        var trouble = lastMinute.Where(t => t.Resent + t.Retried >= TroubleCount).ToList();
        if (trouble.Count > 0)
            return $"Traffic to {string.Join(" and ", trouble.Select(t => t.Server))} is being resent or retried. That usually means " +
                   "packets are being lost or blocked: a weak Wi-Fi or VPN link, or a firewall or security software interfering. " +
                   "Each resend adds a delay you can feel.";
        var db = lastMinute.Where(t => t.Role == "database" && t.MedianReplyMs is not null).OrderByDescending(t => t.Requests).FirstOrDefault();
        if (db is not null && db.MedianReplyMs >= SlowReplyMs)
        {
            bool serverSlow = db.ServerMs > db.FastestReplyMs;
            return $"Each request to the database server takes about {db.MedianReplyMs:0} ms to answer, and there were {db.Requests} in the " +
                   $"last minute: roughly {db.Requests * db.MedianReplyMs!.Value / 1000:0.0} s of waiting. " +
                   (serverSlow
                       ? $"Most of each wait looks like the server itself thinking (about {db.ServerMs:0} ms), not the network: server performance is the thing to look at."
                       : $"Most of each wait looks like the trip across the network (about {db.FastestReplyMs:0} ms): distance or the link (VPN, internet) is the thing to look at.");
        }
        if (lastMinute.Count > 0)
            return "The vault's servers are answering quickly. Nothing here is slowing SOLIDWORKS down.";
        return "No traffic to the vault's servers in the last minute.";
    }

    /// <summary>A comparison for the whole idea.</summary>
    public const string Plain = Analogies.Lead +
        "every request is a phone call to the vault office: you ask, you wait, they answer. The wait is the time on the line " +
        "(the network) plus the time the clerk spends looking it up (the server). A save can mean hundreds of short calls, so a " +
        "long line or a slow clerk adds up fast.";
}
