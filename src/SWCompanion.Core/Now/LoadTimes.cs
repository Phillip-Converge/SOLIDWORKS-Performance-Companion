using System.Text.Json;

namespace SWCompanion.Core.Now;

/// <summary>
/// How long a window or command took to be ready: from the click (SOLIDWORKS's "command started" notice) or, without
/// one, from the moment the window was created, until it answered Windows. Time spent waiting for you afterwards is
/// never part of it.
/// </summary>
/// <param name="What">The window's title, or the command's name when no window opened (e.g. a PropertyManager page).</param>
/// <param name="FromClick">True when timed from SOLIDWORKS's "command started" notice; false when from the window appearing.</param>
public sealed record LoadTiming(DateTime Start, DateTime Ready, string What, bool IsWindow, bool FromClick, string? Command)
{
    public double Seconds => (Ready - Start).TotalSeconds;
    /// <summary>What "normal" is remembered under: one entry per window title or command.</summary>
    public string Key => IsWindow ? "window:" + LoadHistory.Normalize(What) : "command:" + What;
}

/// <summary>Whether a load was slower than normal for this PC, and what normal is.</summary>
public sealed record LoadVerdict(bool Slow, double? Usual, int Samples, string Reason);

/// <summary>
/// "Normal" for each window and command, learned on this PC: the last 20 timings of each, saved between runs.
/// A load is slow when it took about twice as long as usual and at least 1 second more (once there are 3 timings);
/// until then, anything over 2 seconds is slow. The owner chose this rule on 2026-11-22.
/// </summary>
public sealed class LoadHistory
{
    public const int Keep = 20, MinSamples = 3;
    public const double FirstLimit = 2.0, Factor = 2.0, Margin = 1.0;

    private readonly Dictionary<string, List<double>> _times = new();
    private readonly string? _path;

    public LoadHistory(string? path = null)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, List<double>>>(File.ReadAllText(path));
            if (saved is not null) foreach (var (k, v) in saved) _times[k] = v.TakeLast(Keep).ToList();
        }
        catch { /* a damaged file just means starting to learn again */ }
    }

    /// <summary>The usual place: %LOCALAPPDATA%\SWCompanion\load-times.json (stays on this PC).</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Contracts.ProductInfo.FolderName, "load-times.json");

    /// <summary>Judges a timing against what's normal so far, then remembers it.</summary>
    public LoadVerdict Judge(LoadTiming t)
    {
        double s = t.Seconds;
        _times.TryGetValue(t.Key, out var past);
        int n = past?.Count ?? 0;
        LoadVerdict v;
        if (n < MinSamples)
        {
            v = new(s > FirstLimit, n == 0 ? null : Median(past!), n,
                s > FirstLimit ? $"over {FirstLimit:0} s, and there aren't enough earlier timings yet to know what's normal here"
                               : "still learning what's normal here");
        }
        else
        {
            double usual = Median(past!);
            bool slow = s >= usual * Factor && s >= usual + Margin;
            v = new(slow, usual, n, slow ? $"about {s / Math.Max(usual, 0.05):0.#} times as long as usual (usually {usual:0.0} s)" : $"normal here (usually {usual:0.0} s)");
        }
        if (past is null) _times[t.Key] = past = new();
        past.Add(Math.Round(s, 2));
        if (past.Count > Keep) past.RemoveAt(0);
        Save();
        return v;
    }

    /// <summary>"Properties - Bracket.SLDPRT" → "Properties": the same box for different files counts as one.</summary>
    public static string Normalize(string title)
    {
        string t = title.Trim();
        int dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) t = t[..dash];
        return t.TrimEnd('.', ' ');
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_times));
        }
        catch { }
    }
}

/// <summary>
/// Times windows and commands from the click until they're ready. Fed by SOLIDWORKS's "command started" notice, by
/// Windows' "window created / shown" notices, and ten times a second by whether each waiting window (and SOLIDWORKS's
/// main window) answers. Pure bookkeeping, so it can be tested; thread-safe because the notices come from different threads.
/// </summary>
public sealed class LoadTracker
{
    /// <summary>A window created this soon after a command belongs to that command's click.</summary>
    public static readonly TimeSpan CommandWindow = TimeSpan.FromSeconds(10);
    /// <summary>A command with no window is timed until SOLIDWORKS answers again, but only after this long (to let a window appear first).</summary>
    public static readonly TimeSpan NoWindowGrace = TimeSpan.FromMilliseconds(300);
    /// <summary>Stop waiting after this long (it's a freeze by then, which the freeze watcher reports).</summary>
    public static readonly TimeSpan GiveUp = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly List<(DateTime Time, string Name)> _commands = new();
    private readonly Dictionary<IntPtr, DateTime> _created = new();
    private readonly Dictionary<IntPtr, (DateTime Start, string Title, bool FromClick, string? Command)> _waiting = new();
    private (DateTime Time, string Name)? _commandNoWindow;

    /// <summary>SOLIDWORKS said a command started (you clicked a button or menu item).</summary>
    public void CommandStarted(DateTime time, string name)
    {
        lock (_lock)
        {
            _commands.Add((time, name));
            _commands.RemoveAll(c => time - c.Time > CommandWindow);
            _commandNoWindow = (time, name);
        }
    }

    /// <summary>Windows created a top-level window in SOLIDWORKS (it may still be filling itself in).</summary>
    public void WindowCreated(DateTime time, IntPtr handle)
    {
        lock (_lock) _created[handle] = time;
    }

    /// <summary>A titled SOLIDWORKS window became visible. Timing starts at the click if there was one, else at its creation.</summary>
    public void WindowShown(DateTime time, IntPtr handle, string title)
    {
        lock (_lock)
        {
            if (_waiting.ContainsKey(handle) || string.IsNullOrWhiteSpace(title)) return;
            var created = _created.Remove(handle, out var c) ? c : time;
            // The latest command shortly before the window was created is the click that opened it.
            var cmd = _commands.LastOrDefault(x => x.Time <= created + TimeSpan.FromMilliseconds(50) && created - x.Time <= CommandWindow);
            bool fromClick = cmd.Name is not null;
            if (fromClick)
            {
                _commands.Remove(cmd);
                if (_commandNoWindow is { } n && n.Time == cmd.Time) _commandNoWindow = null;
            }
            _waiting[handle] = (fromClick ? cmd.Time : created, title.Trim(), fromClick, cmd.Name);
        }
    }

    /// <summary>The window closed (before or after it was ready).</summary>
    public void WindowGone(IntPtr handle)
    {
        lock (_lock) { _waiting.Remove(handle); _created.Remove(handle); }
    }

    /// <summary>Windows that still need checking for "ready".</summary>
    public IReadOnlyList<IntPtr> Waiting { get { lock (_lock) return _waiting.Keys.ToList(); } }
    /// <summary>True while a command with no window yet needs SOLIDWORKS's main window checked.</summary>
    public bool WaitingOnMain { get { lock (_lock) return _commandNoWindow is not null; } }

    /// <summary>
    /// Ten times a second: which waiting windows answered (true), didn't (false) or are gone (null), and whether the
    /// main window answered. Returns the timings that just finished.
    /// </summary>
    public List<LoadTiming> Check(DateTime now, IReadOnlyDictionary<IntPtr, bool?> windows, bool? mainAnswers)
    {
        var done = new List<LoadTiming>();
        lock (_lock)
        {
            foreach (var (h, w) in _waiting.ToList())
            {
                windows.TryGetValue(h, out var ok);
                if (!windows.ContainsKey(h)) continue;
                if (ok is null || now - w.Start > GiveUp) { _waiting.Remove(h); continue; }
                if (ok == true)
                {
                    _waiting.Remove(h);
                    done.Add(new LoadTiming(w.Start, now, w.Title, IsWindow: true, w.FromClick, w.Command));
                }
            }
            if (_commandNoWindow is { } c)
            {
                if (now - c.Time > GiveUp) _commandNoWindow = null;
                else if (now - c.Time >= NoWindowGrace && mainAnswers == true && _waiting.Count == 0)
                {
                    _commandNoWindow = null;
                    _commands.RemoveAll(x => x.Time == c.Time);
                    done.Add(new LoadTiming(c.Time, now, c.Name, IsWindow: false, FromClick: true, c.Name));
                }
            }
            foreach (var old in _created.Where(kv => now - kv.Value > GiveUp).Select(kv => kv.Key).ToList()) _created.Remove(old);
        }
        return done;
    }
}
