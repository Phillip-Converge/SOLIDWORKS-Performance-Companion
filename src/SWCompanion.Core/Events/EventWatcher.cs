using System.Diagnostics.Eventing.Reader;

namespace SWCompanion.Core.Events;

/// <summary>
/// Watches Windows' Application and System event logs as new records arrive, and reads the last few minutes once at
/// the start. Read only, and works without administrator rights (those two logs are open to every user). Only
/// events <see cref="WinEventReader"/> understands are passed on. Events arrive on a background thread.
/// </summary>
public sealed class EventWatcher : IDisposable
{
    private readonly List<EventLogWatcher> _watchers = new();

    /// <summary>A Windows event that matters, in plain words.</summary>
    public event Action<EventFinding>? Found;
    /// <summary>Why watching couldn't start, if it couldn't (shown in the log; the rest of the Companion carries on).</summary>
    public string? Problem { get; private set; }

    /// <summary>Starts watching. Events from the last <paramref name="lookBack"/> are reported first, oldest first.</summary>
    public void Start(TimeSpan lookBack)
    {
        foreach (var (log, query) in new[] { ("Application", WinEventReader.ApplicationQuery), ("System", WinEventReader.SystemQuery),
                                             (WinEventReader.DefenderLog, WinEventReader.DefenderQuery) })
        {
            try
            {
                ReadRecent(log, query, lookBack);
                var w = new EventLogWatcher(new EventLogQuery(log, PathType.LogName, query));
                w.EventRecordWritten += (_, e) =>
                {
                    if (e.EventRecord is { } r) using (r) Pass(log, r);
                };
                w.Enabled = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                Problem = (Problem is null ? "" : Problem + " ") + $"Couldn't watch the {log} log: {ex.Message}";
            }
        }
    }

    private void ReadRecent(string log, string query, TimeSpan lookBack)
    {
        // Narrow to recent records in the query itself, so a large log isn't read from the beginning.
        string recent = query.Replace("*[System[", $"*[System[TimeCreated[timediff(@SystemTime) <= {(long)lookBack.TotalMilliseconds}] and (")
                             .Replace("]]", ")]]");
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, recent));
        for (var r = reader.ReadEvent(); r is not null; r = reader.ReadEvent())
            using (r) Pass(log, r);
    }

    private void Pass(string log, EventRecord r)
    {
        string message;
        try { message = r.FormatDescription() ?? ""; }
        catch { message = ""; } // some drivers ship no message text; the provider and ID are still enough
        var e = new WinEvent(r.TimeCreated ?? DateTime.Now, log, r.ProviderName ?? "", r.Id, r.Level ?? 4, message);
        if (WinEventReader.Read(e) is { } f) Found?.Invoke(f);
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            try { w.Enabled = false; w.Dispose(); } catch { }
        }
        _watchers.Clear();
    }
}
