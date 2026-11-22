using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SWCompanion.Core.Gpu;

/// <summary>
/// Reads graphics-card use from Windows' performance counters, the same ones
/// Task Manager uses. READ ONLY. Needs no administrator rights.
/// Call <see cref="Sample"/> on a regular interval (every 2 seconds is plenty).
/// </summary>
public sealed partial class GpuSampler
{
    private readonly IReadOnlyList<GpuAdapterInfo> _adapters = GpuAdapters.Read();
    private Dictionary<string, CounterSample> _previous = new();

    public IReadOnlyList<GpuAdapterInfo> Adapters => _adapters;

    /// <param name="solidWorksPid">SOLIDWORKS's process ID, or null if it isn't running.</param>
    /// <param name="solidWorksExePath">Full path of SLDWORKS.exe, used to look up Windows' graphics setting.</param>
    /// <returns>Null on the first call, which only records a starting point.</returns>
    public GpuSnapshot? Sample(int? solidWorksPid, string? solidWorksExePath)
    {
        // Engine busy-ness is a running total; compare with the previous reading.
        var current = ReadSamples("GPU Engine", "Utilization Percentage");
        bool first = _previous.Count == 0;
        var enginePercent = new Dictionary<string, double>();
        foreach (var (name, now) in current)
        {
            if (_previous.TryGetValue(name, out var before))
                enginePercent[name] = CounterSample.Calculate(before, now);
        }
        _previous = current;
        if (first) return null;

        var adapterDedicated = ReadRaw("GPU Adapter Memory", "Dedicated Usage");
        var adapterShared = ReadRaw("GPU Adapter Memory", "Shared Usage");
        string prefix = solidWorksPid is null ? "\0" : $"pid_{solidWorksPid}_";
        var swDedicated = ReadRaw("GPU Process Memory", "Dedicated Usage", prefix);
        var swShared = ReadRaw("GPU Process Memory", "Shared Usage", prefix);

        return Aggregate(_adapters, enginePercent, adapterDedicated, adapterShared, swDedicated, swShared,
            solidWorksPid, ReadWindowsPreference(solidWorksExePath));
    }

    /// <summary>
    /// Turns raw counter values into per-adapter readings. Separate from
    /// <see cref="Sample"/> so it can be tested with made-up numbers.
    /// </summary>
    public static GpuSnapshot Aggregate(
        IReadOnlyList<GpuAdapterInfo> adapters,
        IReadOnlyDictionary<string, double> enginePercent,
        IReadOnlyDictionary<string, long> adapterDedicated,
        IReadOnlyDictionary<string, long> adapterShared,
        IReadOnlyDictionary<string, long> swDedicated,
        IReadOnlyDictionary<string, long> swShared,
        int? solidWorksPid,
        string windowsPreference)
    {
        // Each engine (3D, copy, video...) of each adapter: add up all programs, and SOLIDWORKS alone.
        var engineTotal = new Dictionary<(long Luid, string Engine), double>();
        var engineSw = new Dictionary<(long Luid, string Engine), double>();
        foreach (var (name, pct) in enginePercent)
        {
            var e = ParseEngine(name);
            if (e is null) continue;
            var key = (e.Value.Luid, e.Value.Engine);
            engineTotal[key] = engineTotal.GetValueOrDefault(key) + pct;
            if (e.Value.Pid == solidWorksPid)
                engineSw[key] = engineSw.GetValueOrDefault(key) + pct;
        }

        // Adapters known to counters but not to DirectX still get a line.
        var known = adapters.ToList();
        foreach (var luid in engineTotal.Keys.Select(k => k.Luid).Distinct())
            if (known.All(a => a.Luid != luid))
                known.Add(new GpuAdapterInfo(luid, "unnamed graphics adapter", GpuKind.BuiltIn, 0, 0));

        var readings = known.Select(a =>
        {
            double busy = engineTotal.Where(kv => kv.Key.Luid == a.Luid).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
            double swBusy = engineSw.Where(kv => kv.Key.Luid == a.Luid).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
            return new AdapterReading(
                a,
                Math.Clamp(busy, 0, 100),
                (ulong)Math.Max(0, SumFor(adapterDedicated, a.Luid)),
                (ulong)Math.Max(0, SumFor(adapterShared, a.Luid)),
                Math.Clamp(swBusy, 0, 100),
                (ulong)Math.Max(0, SumFor(swDedicated, a.Luid)),
                (ulong)Math.Max(0, SumFor(swShared, a.Luid)));
        }).ToList();

        return new GpuSnapshot(readings, solidWorksPid is not null, windowsPreference);
    }

    private static long SumFor(IReadOnlyDictionary<string, long> values, long luid) =>
        values.Where(kv => ParseLuid(kv.Key) == luid).Sum(kv => kv.Value);

    // ---- counter names look like "pid_6096_luid_0x00000000_0x000172BC_phys_0_eng_0_engtype_3D" ----

    [GeneratedRegex(@"^pid_(\d+)_luid_0x([0-9a-f]+)_0x([0-9a-f]+)_phys_(\d+)_eng_(\d+)_engtype_(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex EngineName();

    [GeneratedRegex(@"luid_0x([0-9a-f]+)_0x([0-9a-f]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LuidPart();

    public static (int Pid, long Luid, string Engine)? ParseEngine(string name)
    {
        var m = EngineName().Match(name);
        if (!m.Success) return null;
        long luid = (long.Parse(m.Groups[2].Value, NumberStyles.HexNumber) << 32) | long.Parse(m.Groups[3].Value, NumberStyles.HexNumber);
        return (int.Parse(m.Groups[1].Value), luid, $"phys_{m.Groups[4].Value}_eng_{m.Groups[5].Value}");
    }

    public static long? ParseLuid(string name)
    {
        var m = LuidPart().Match(name);
        if (!m.Success) return null;
        return (long.Parse(m.Groups[1].Value, NumberStyles.HexNumber) << 32) | long.Parse(m.Groups[2].Value, NumberStyles.HexNumber);
    }

    // ---- reading Windows ----

    private static Dictionary<string, CounterSample> ReadSamples(string category, string counter)
    {
        var result = new Dictionary<string, CounterSample>();
        try
        {
            var data = new PerformanceCounterCategory(category).ReadCategory();
            if (data[counter] is { } instances)
                foreach (InstanceData i in instances.Values)
                    result[i.InstanceName] = i.Sample;
        }
        catch
        {
            // category missing (very old Windows or driver): no readings
        }
        return result;
    }

    private static Dictionary<string, long> ReadRaw(string category, string counter, string? instancePrefix = null)
    {
        var result = new Dictionary<string, long>();
        try
        {
            var data = new PerformanceCounterCategory(category).ReadCategory();
            if (data[counter] is { } instances)
                foreach (InstanceData i in instances.Values)
                    if (instancePrefix is null || i.InstanceName.StartsWith(instancePrefix, StringComparison.OrdinalIgnoreCase))
                        result[i.InstanceName] = i.RawValue;
        }
        catch
        {
            // no readings
        }
        return result;
    }

    /// <summary>Windows Settings > Display > Graphics: the per-app choice for SOLIDWORKS, if any.</summary>
    public static string ReadWindowsPreference(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return "not set";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            var name = key?.GetValueNames().FirstOrDefault(n => string.Equals(n, exePath, StringComparison.OrdinalIgnoreCase));
            if (name is null) return "not set";
            var value = key!.GetValue(name) as string ?? "";
            return Regex.Match(value, @"GpuPreference=(\d)").Groups[1].Value switch
            {
                "0" => "let Windows decide",
                "1" => "power saving",
                "2" => "high performance",
                _ => "not set",
            };
        }
        catch
        {
            return "not set";
        }
    }
}
