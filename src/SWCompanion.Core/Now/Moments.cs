using System.Text.Json;
using SWCompanion.Core.AddIn;
using SWCompanion.Core.Files;

namespace SWCompanion.Core.Now;

/// <summary>
/// Something that came and went while you were working: a box that popped up and closed, a short
/// freeze, another program hogging the processor for a few seconds, a slow file, a slow sheet switch.
/// </summary>
/// <param name="LookInto">True when it's worth your attention: it held SOLIDWORKS up, lasted a while, or was flagged as a big slowdown.</param>
public sealed record Moment(DateTime Start, TimeSpan Duration, string Kind, string Title, string Detail, bool LookInto)
{
    /// <summary>"17:51:18   12 s   SOLIDWORKS stopped responding"</summary>
    public string Line => $"{Start:HH:mm:ss}   {NowEngine.Seconds(Duration)}   {Title}";
}

/// <summary>
/// Catches things that appear and disappear between glances, so they aren't missed: watches what
/// the right-now panel sees every second, the boxes SOLIDWORKS opens, freezes, slow actions the
/// add-in or outside link report, and slow files. When something goes away, it becomes a "moment"
/// that stays on screen and is saved. Pure bookkeeping (no Windows calls), so it can be tested.
/// </summary>
public sealed class MomentCatcher
{
    /// <summary>Shorter than this and a passing cause isn't worth a line (the screen flickers more than that).</summary>
    public static readonly TimeSpan MinCause = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MinBox = TimeSpan.FromSeconds(1);
    /// <summary>Lasting at least this long makes it "look into this".</summary>
    public static readonly TimeSpan LookIntoAfter = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan FreezeLookInto = TimeSpan.FromSeconds(3);
    public const int Keep = 100;

    // Causes that are always there (setup), handled elsewhere (boxes, actions), or just filler.
    private static readonly HashSet<string> IgnoredKinds = new() { "setup", "inside", "waiting", "busy-notice", "dialog-open" };

    private readonly Dictionary<string, OpenCause> _causes = new();
    private readonly Dictionary<IntPtr, OpenBox> _boxes = new();
    private readonly List<Moment> _caught = new();
    private FreezeEpisode? _lastEpisode;
    private DateTime? _lastTick;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<Moment> Caught => _caught;
    /// <summary>How many "look into this" moments arrived since you last marked them seen.</summary>
    public int Unseen { get; private set; }
    /// <summary>Optional: an extra sentence for a slow action, from its start and end (e.g. what Defender did meanwhile).</summary>
    public Func<DateTime, DateTime, string?>? ActionNote { get; set; }

    /// <summary>Raised for each new moment (to save it and write it to the activity log).</summary>
    public event Action<Moment>? NewMoment;

    /// <summary>Called every second with what the right-now panel decided and the SOLIDWORKS windows it saw.</summary>
    public void Observe(DateTime now, NowReport report, IReadOnlyList<SwWindow> windows, FreezeEpisode? lastEpisode)
    {
        var tick = _lastTick is { } last && now - last < TimeSpan.FromSeconds(5) ? now - last : TimeSpan.FromSeconds(1);
        _lastTick = now;
        bool frozen = report.Level == NowLevel.Frozen;

        // 1. Causes in the right-now panel.
        var seen = new HashSet<string>();
        foreach (var c in report.Causes.Where(c => !IgnoredKinds.Contains(c.Kind)))
        {
            if (!seen.Add(c.Kind)) continue;
            if (!_causes.TryGetValue(c.Kind, out var open)) _causes[c.Kind] = open = new OpenCause { First = now };
            open.Last = now;
            open.Title = c.Title;
            open.Evidence = c.Evidence;
            open.Glaring |= c.Glaring;
            if (!frozen) open.AllFrozen = false;
            open.Tick = tick;
        }
        foreach (var kind in _causes.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var gone = _causes[kind];
            _causes.Remove(kind);
            var duration = gone.Last - gone.First + gone.Tick;
            // Causes seen only during a freeze are summed up in the freeze's own moment instead.
            if (gone.AllFrozen) continue;
            if (duration < MinCause && !gone.Glaring) continue;
            Add(new Moment(gone.First, duration, kind, gone.Title + " (came and went)",
                gone.Evidence, LookInto: gone.Glaring || duration >= LookIntoAfter));
        }

        // 2. Boxes SOLIDWORKS opened and closed.
        var present = new HashSet<IntPtr>();
        foreach (var w in windows.Where(w => w.IsDialog && !w.IsMain))
        {
            present.Add(w.Handle);
            if (!_boxes.TryGetValue(w.Handle, out var box)) _boxes[w.Handle] = box = new OpenBox { First = now, Title = w.Title };
            box.Last = now;
            box.Tick = tick;
            if (!string.IsNullOrWhiteSpace(w.Title)) box.Title = w.Title;
            box.DuringFreeze |= frozen;
            box.BusyNotice |= frozen && w.Responding == true;
        }
        foreach (var handle in _boxes.Keys.Where(h => !present.Contains(h)).ToList())
        {
            var box = _boxes[handle];
            _boxes.Remove(handle);
            var duration = box.Last - box.First + box.Tick;
            if (duration < MinBox) continue;
            bool untitled = string.IsNullOrWhiteSpace(box.Title);
            // A box that was open while SOLIDWORKS answered normally was just waiting for you: not a hang-up.
            // (How long it took to be ready is timed separately, from your click; see LoadTracker.)
            if (!box.DuringFreeze) continue;
            string title = box.BusyNotice
                ? "SOLIDWORKS's \"busy running a command\" box popped up and closed"
                : untitled ? "A SOLIDWORKS window with no title popped up and closed"
                : $"A SOLIDWORKS box popped up and closed: \"{box.Title}\"";
            string detail = $"It was open for {NowEngine.Seconds(duration)}" +
                            (box.DuringFreeze ? " while SOLIDWORKS wasn't responding. " : ". ") +
                            "The Companion can't tell whether it closed by itself or someone answered it.";
            Add(new Moment(box.First, duration, "box", title, detail, LookInto: box.DuringFreeze));
        }

        // 3. Freezes, once they end.
        if (lastEpisode is not null && !ReferenceEquals(lastEpisode, _lastEpisode))
        {
            _lastEpisode = lastEpisode;
            Add(new Moment(lastEpisode.Start, lastEpisode.Duration, "freeze",
                $"SOLIDWORKS stopped responding for {NowEngine.Seconds(lastEpisode.Duration)}",
                lastEpisode.Summary.Replace("Last freeze: ", "What the Companion saw: "),
                LookInto: lastEpisode.Duration >= FreezeLookInto));
        }
    }

    /// <summary>A finished action from inside SOLIDWORKS. Slow ones become moments.</summary>
    public void AddAction(AddinAction a)
    {
        if (a.Seconds is not { } s) return;
        (double limit, string tip) = a.What switch
        {
            "open" => (10.0, "Large files, files from older SOLIDWORKS versions (converted on every open), missing references and network paths are the usual reasons."),
            "sheet" => (3.0, "Section 08 lists the views on that sheet most likely to blame; \"time each view\" measures them."),
            "save" => (5.0, "Saving to a network folder or a PDM vault, and Defender scanning the file, are the usual reasons."),
            "rebuild" => (5.0, "A long rebuild usually comes down to a few slow features."),
            _ => (double.MaxValue, ""),
        };
        if (s < limit) return;
        string? note = ActionNote?.Invoke(a.Started, a.Started.AddSeconds(s));
        Add(new Moment(a.Started, TimeSpan.FromSeconds(s), "slow-" + a.What, AddinTracker.Describe(a),
            note is null ? tip : note + " " + tip, LookInto: true));
    }

    /// <summary>A file SOLIDWORKS finished with (file tracing). Long waits and long virus scans become moments.</summary>
    public void AddFile(FileActivity f)
    {
        bool slow = f.WaitTime >= TimeSpan.FromSeconds(2);
        bool scanned = f.ScannedByAntivirus && f.AntivirusTime >= TimeSpan.FromSeconds(1);
        if (!slow && !scanned) return;
        var duration = f.WaitTime > f.AntivirusTime ? f.WaitTime : f.AntivirusTime;
        string title = scanned ? $"Windows Defender held up {f.Name}" : $"SOLIDWORKS waited on {f.Name}";
        Add(new Moment(f.FirstSeen, duration, scanned ? "antivirus" : "slow-file", title, FileExplainer.Line(f), LookInto: true));
    }

    /// <summary>Moments saved earlier today, shown again after a restart (already seen).</summary>
    public void Restore(IEnumerable<Moment> saved)
    {
        // Boxes saved before 2026-11-22 that were only waiting for you (not during a freeze) are no longer hang-ups.
        // Also: late Windows crash reports that were wrongly shown as new graphics resets before that was fixed (same day).
        foreach (var m in saved.Where(m => !(m.Kind == "box" && !m.LookInto) &&
                                           !(m.Kind == "win-gpu" && m.Detail.Contains("Windows Error Reporting", StringComparison.Ordinal)))
                               .OrderByDescending(m => m.Start))
            if (_caught.Count < Keep) _caught.Add(m);
    }

    public void MarkSeen() => Unseen = 0;

    /// <summary>Adds a moment found elsewhere (e.g. a crash).</summary>
    public void Report(Moment m) => Add(m);

    private void Add(Moment m)
    {
        _caught.Insert(0, m);
        if (_caught.Count > Keep) _caught.RemoveAt(_caught.Count - 1);
        if (m.LookInto) Unseen++;
        NewMoment?.Invoke(m);
    }

    private sealed class OpenCause
    {
        public DateTime First, Last;
        public TimeSpan Tick;
        public string Title = "", Evidence = "";
        public bool Glaring;
        public bool AllFrozen = true;
    }

    private sealed class OpenBox
    {
        public DateTime First, Last;
        public TimeSpan Tick;
        public string Title = "";
        public bool DuringFreeze, BusyNotice;
    }
}

/// <summary>
/// Saves moments one per line (JSON) in caught-YYYY-MM-DD.jsonl, in the same folders as the activity
/// log, and reads today's back after a restart. File names stay on this PC.
/// </summary>
public sealed class MomentStore
{
    private readonly IReadOnlyList<string> _folders;

    public MomentStore() : this(AppPaths.DataFolders.Select(f => Path.Combine(f, "logs")).ToList()) { }
    public MomentStore(IReadOnlyList<string> folders) => _folders = folders;

    public static string FileNameFor(DateTime day) => $"caught-{day:yyyy-MM-dd}.jsonl";

    public void Save(Moment m)
    {
        string line = JsonSerializer.Serialize(new Saved(m.Start, m.Duration.TotalSeconds, m.Kind, m.Title, m.Detail, m.LookInto));
        foreach (var folder in _folders)
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, FileNameFor(m.Start)), line + Environment.NewLine);
            }
            catch (Exception) { /* one unwritable folder mustn't stop the other */ }
        }
    }

    public List<Moment> LoadDay(DateTime day)
    {
        foreach (var folder in _folders)
        {
            var file = Path.Combine(folder, FileNameFor(day));
            if (!File.Exists(file)) continue;
            var list = new List<Moment>();
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<Saved>(line) is { } s)
                            list.Add(new Moment(s.Start, TimeSpan.FromSeconds(s.Seconds), s.Kind, s.Title, s.Detail, s.LookInto));
                    }
                    catch (JsonException) { /* skip a damaged line */ }
                }
            }
            catch (IOException) { continue; }
            return list;
        }
        return [];
    }

    private sealed record Saved(DateTime Start, double Seconds, string Kind, string Title, string Detail, bool LookInto);
}
