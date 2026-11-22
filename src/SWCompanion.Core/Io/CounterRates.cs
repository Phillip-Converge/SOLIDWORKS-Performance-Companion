using System.Diagnostics;

namespace SWCompanion.Core.Io;

/// <summary>
/// Reads one Windows performance-counter category and works out each counter's
/// value since the previous read (rates, busy percentages, averages). READ ONLY.
/// </summary>
internal sealed class CounterRates
{
    private readonly string _category;
    private readonly string[] _counters;
    private Dictionary<(string Counter, string Instance), CounterSample> _previous = new();

    public CounterRates(string category, params string[] counters)
    {
        _category = category;
        _counters = counters;
    }

    /// <summary>
    /// Returns counter -> instance -> value. Empty on the first read (nothing to compare with)
    /// or if the category doesn't exist on this PC.
    /// </summary>
    public Dictionary<string, Dictionary<string, double>> Read()
    {
        var result = _counters.ToDictionary(c => c, _ => new Dictionary<string, double>());
        var current = new Dictionary<(string, string), CounterSample>();
        try
        {
            var data = new PerformanceCounterCategory(_category).ReadCategory();
            foreach (var counter in _counters)
            {
                if (data[counter] is not { } instances) continue;
                foreach (InstanceData i in instances.Values)
                {
                    var key = (counter, i.InstanceName);
                    current[key] = i.Sample;
                    if (_previous.TryGetValue(key, out var before))
                        result[counter][i.InstanceName] = CounterSample.Calculate(before, i.Sample);
                }
            }
        }
        catch
        {
            // category missing or unreadable: no values
        }
        _previous = current;
        return result;
    }
}
