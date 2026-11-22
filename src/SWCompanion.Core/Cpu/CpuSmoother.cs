namespace SWCompanion.Core.Cpu;

/// <summary>
/// Averages the last few CPU readings so the plain-English sentence doesn't
/// flicker between "idle" and "working" every second. The live numbers and
/// core bars still use each raw reading; only the sentence is smoothed.
/// </summary>
public sealed class CpuSmoother
{
    private readonly int _window;
    private readonly Queue<CpuSnapshot> _recent = new();

    /// <param name="window">How many readings to average (3 = the last 3 seconds).</param>
    public CpuSmoother(int window = 3)
    {
        if (window < 1) throw new ArgumentOutOfRangeException(nameof(window));
        _window = window;
    }

    public int Window => _window;

    /// <summary>Adds a reading and returns the average of the last <see cref="Window"/> readings.</summary>
    public CpuSnapshot Add(CpuSnapshot latest)
    {
        // If SOLIDWORKS just started, stopped or restarted, start fresh so we
        // never mix readings from before and after.
        if (_recent.Count > 0 && (_recent.Last().SolidWorks is null) != (latest.SolidWorks is null))
            _recent.Clear();

        _recent.Enqueue(latest);
        while (_recent.Count > _window) _recent.Dequeue();

        var all = _recent.ToList();
        int cores = latest.CorePercent.Count;
        var corePercent = Enumerable.Range(0, cores)
            .Select(i => all.Where(s => s.CorePercent.Count > i).Average(s => s.CorePercent[i]))
            .ToList();

        SolidWorksCpu? sw = null;
        if (latest.SolidWorks is not null)
        {
            var sws = all.Select(s => s.SolidWorks!).ToList();
            sw = new SolidWorksCpu(
                sws.Average(x => x.CoresWorth),
                sws.Average(x => x.PercentOfPc),
                (int)Math.Round(sws.Average(x => x.BusyThreads)),
                sws.Average(x => x.TopThreadPercent),
                latest.SolidWorks.ThreadsVisible);
        }

        return new CpuSnapshot(all.Average(s => s.SystemPercent), corePercent, sw);
    }
}
