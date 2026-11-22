namespace SWCompanion.Core.Now;

/// <summary>One line of a freeze's timeline.</summary>
public sealed record FreezeLine(DateTime Time, string Text);

/// <summary>A finished freeze: how long, and where the time went.</summary>
/// <param name="TimeByCause">Seconds attributed to each cause title, largest first.</param>
public sealed record FreezeEpisode(DateTime Start, TimeSpan Duration, IReadOnlyList<(string Cause, TimeSpan Time)> TimeByCause,
                                   IReadOnlyList<FreezeLine> Timeline)
{
    /// <summary>"Frozen 23 s at 23:41: 18 s computing on one core, 5 s loading files."</summary>
    public string Summary =>
        $"Last freeze: {NowEngine.Seconds(Duration)} at {Start:HH:mm:ss}" +
        (TimeByCause.Count == 0 ? "." : ": " + string.Join(", ", TimeByCause.Select(t => $"{NowEngine.Seconds(t.Time)} {t.Cause.ToLowerInvariant()}")) + ".");
}

/// <summary>
/// Follows SOLIDWORKS's responsiveness over time: when a freeze starts, what the
/// top cause was through it (a timestamped timeline), and a summary once it ends.
/// </summary>
public sealed class FreezeTracker
{
    private DateTime? _unresponsiveSince;
    private DateTime _lastUpdate;
    private string? _lastTopTitle;
    private readonly Dictionary<string, TimeSpan> _timeByCause = new();
    private readonly List<FreezeLine> _timeline = new();

    public FreezeEpisode? LastEpisode { get; private set; }
    public IReadOnlyList<FreezeLine> CurrentTimeline => _timeline;

    /// <summary>How long SOLIDWORKS has gone without answering (zero if it's answering).</summary>
    public TimeSpan UnresponsiveFor(DateTime now) => _unresponsiveSince is { } s ? now - s : TimeSpan.Zero;

    /// <summary>Records whether SOLIDWORKS answered at this moment.</summary>
    public void Observe(bool? responding, DateTime now)
    {
        if (responding == false)
        {
            _unresponsiveSince ??= now;
            return;
        }

        // Answered again (or gone): close the freeze if one was declared.
        if (_unresponsiveSince is { } start && now - start >= NowEngine.FrozenAfter)
        {
            AddTime(now);
            LastEpisode = new FreezeEpisode(start, now - start,
                _timeByCause.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList(),
                _timeline.ToList());
        }
        _unresponsiveSince = null;
        _timeByCause.Clear();
        _timeline.Clear();
        _lastTopTitle = null;
    }

    /// <summary>Call after each report while frozen: adds a timeline line when the top cause changes.</summary>
    public void Record(NowReport report, DateTime now)
    {
        if (report.Level != NowLevel.Frozen) return;
        AddTime(now);
        string title = report.Top?.Title ?? "Unknown";
        if (title != _lastTopTitle)
        {
            _timeline.Add(new FreezeLine(now, title));
            _lastTopTitle = title;
        }
    }

    private void AddTime(DateTime now)
    {
        if (_lastTopTitle is not null && _lastUpdate != default && now > _lastUpdate)
            _timeByCause[_lastTopTitle] = _timeByCause.GetValueOrDefault(_lastTopTitle) + (now - _lastUpdate);
        _lastUpdate = now;
    }
}
