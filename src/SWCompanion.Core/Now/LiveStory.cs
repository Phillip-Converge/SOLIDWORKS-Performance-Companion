using SWCompanion.Core.AddIn;
using SWCompanion.Core.Files;

namespace SWCompanion.Core.Now;

public enum StoryTone { Normal, Good, Warn, Bad }

/// <summary>One line of the "what's happening now" story.</summary>
public sealed record StoryLine(DateTime Time, string Text, StoryTone Tone = StoryTone.Normal)
{
    public string Clock => Time.ToString("HH:mm:ss");
}

/// <summary>Everything the story looks at, once a second.</summary>
public sealed record StoryInputs(
    bool SolidWorksRunning,
    NowReport Report,
    AddinAction? InProgress,
    AddinAction? LastFinished,
    SheetSwitch? Sheet,
    string? ActiveDocument,
    IReadOnlyList<FileActivity> ActiveFiles,
    // While SOLIDWORKS is busy: a one-line snapshot of the numbers ("Working: 1.2 cores, ..."); null when it's idle.
    string? Activity = null);

/// <summary>
/// Tells what SOLIDWORKS is doing as a running story in everyday words. A line is added only when
/// something changes (or every so often during a long freeze, so you can see time passing), and lines
/// never move once written, so it reads calmly while things are happening. It only says what the
/// Companion can actually see; where it can't tell, it says so.
/// </summary>
public sealed class LiveStory
{
    public const int Keep = 200;
    /// <summary>During a freeze with nothing new to say, a "still ..." line this often.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(10); // the owner's rule: never more than 15 s without news
    /// <summary>Models listed one by one during a sheet switch; after that, a running count.</summary>
    public const int LoadsByName = 40;
    /// <summary>While SOLIDWORKS is busy, a "working" line with the numbers this often.</summary>
    public static readonly TimeSpan ActivityEvery = TimeSpan.FromSeconds(3);
    private DateTime _lastActivity = DateTime.MinValue;

    private static readonly HashSet<string> QuietKinds = new() { "setup", "waiting", "inside", "busy-notice" };

    private readonly List<StoryLine> _lines = new();
    private bool? _running;
    private NowLevel? _level;
    private Verdict _verdict = Verdict.None;
    private DateTime? _frozenSince;
    private DateTime _lastLine = DateTime.MinValue;
    private DateTime _lastHeartbeat = DateTime.MinValue; // "still not responding" has its own clock, so busy lines never hide it
    private AddinAction? _inProgress;
    private AddinAction? _lastFinished;
    private bool _seenFinished; // the first finished action we see may be old history (before we were watching)
    private string? _activeDoc;
    private readonly HashSet<string> _causes = new();
    private readonly HashSet<string> _filesTold = new(StringComparer.OrdinalIgnoreCase);
    private SheetSwitch? _sheet;
    private int _loadsTold;
    private DateTime _lastLoadCount = DateTime.MinValue;

    public IReadOnlyList<StoryLine> Lines => _lines;
    /// <summary>Raised for every new line (the screen appends it; nothing above it moves).</summary>
    public event Action<StoryLine>? NewLine;

    public void Update(DateTime now, StoryInputs x)
    {
        // SOLIDWORKS starting and closing.
        if (x.SolidWorksRunning != _running)
        {
            if (_running is not null || x.SolidWorksRunning)
                Say(now, x.SolidWorksRunning ? "SOLIDWORKS is running. Watching it now." : "SOLIDWORKS closed.");
            _running = x.SolidWorksRunning;
            if (!x.SolidWorksRunning) { ResetWork(); return; }
        }
        if (!x.SolidWorksRunning) return;

        // Which document you're working in.
        if (x.ActiveDocument is { } doc && !string.Equals(doc, _activeDoc, StringComparison.OrdinalIgnoreCase))
        {
            _activeDoc = doc;
            Say(now, $"Now working in {Path.GetFileName(doc)}.");
        }

        // Actions SOLIDWORKS reports starting and finishing.
        if (x.InProgress is { } p && !SameAction(p, _inProgress))
        {
            Say(now, Starting(p));
            _startedSeen.Add((p.What, p.Started));
        }
        _inProgress = x.InProgress;
        if (x.LastFinished is { } f && !SameAction(f, _lastFinished))
        {
            if (_seenFinished || _inProgressSeen(f)) // not old history from before we were watching
                if ((f.Seconds ?? 0) >= 0.05)
                    Say(now, "Done: " + AddinTracker.Describe(f) + ".", (f.Seconds ?? 0) >= 10 ? StoryTone.Warn : StoryTone.Good);
        }
        _lastFinished = x.LastFinished;
        _seenFinished = true;

        // Models loading during a sheet switch: which view's model is being fetched.
        if (x.Sheet is { Seconds: null } sheet && x.InProgress?.What == "sheet")
        {
            if (!ReferenceEquals(sheet, _sheet)) { _sheet = sheet; _loadsTold = 0; }
            while (_loadsTold < sheet.Loads.Count && _loadsTold < LoadsByName)
            {
                Say(now, $"Loading {Path.GetFileName(sheet.Loads[_loadsTold].Path)} for the views on this sheet.");
                _loadsTold++;
            }
            if (sheet.Loads.Count > LoadsByName && sheet.Loads.Count > _loadsTold && now - _lastLoadCount >= TimeSpan.FromSeconds(5))
            {
                _lastLoadCount = now;
                _loadsTold = sheet.Loads.Count;
                Say(now, $"{sheet.Loads.Count} models loaded for this sheet so far. Big assemblies load part by part.");
            }
        }

        // Responding, frozen, and the "still working?" verdict.
        var level = x.Report.Level;
        if (level != _level)
        {
            if (level == NowLevel.Frozen)
            {
                _frozenSince = now;
                _lastHeartbeat = now;
                Say(now, "SOLIDWORKS stopped responding to clicks (Windows will call it \"Not Responding\").", StoryTone.Warn);
            }
            else if (_level == NowLevel.Frozen && _frozenSince is { } since)
            {
                Say(now, $"SOLIDWORKS is responding again, after {NowEngine.Seconds(now - since)}.", StoryTone.Good);
                _frozenSince = null;
            }
            _level = level;
        }
        var verdict = x.Report.Verdict?.Verdict ?? Verdict.None;
        if (level == NowLevel.Frozen && verdict != _verdict && verdict != Verdict.None)
            Say(now, VerdictLine(verdict, x.Report.Verdict!), Tone(verdict));
        _verdict = level == NowLevel.Frozen ? verdict : Verdict.None;

        // What the right-now panel sees, as it appears.
        var current = new HashSet<string>();
        foreach (var c in x.Report.Causes.Where(c => !QuietKinds.Contains(c.Kind)))
        {
            current.Add(c.Kind);
            if (_causes.Contains(c.Kind)) continue;
            Say(now, c.Title + ": " + FirstSentence(c.Evidence), c.Glaring ? StoryTone.Bad : StoryTone.Warn);
        }
        _causes.Clear();
        _causes.UnionWith(current);

        // Files SOLIDWORKS is waiting on (file tracing), each told once while it's busy.
        foreach (var file in x.ActiveFiles.Where(fl => fl.ScannedByAntivirus || fl.WaitTime >= TimeSpan.FromSeconds(1)).Take(3))
        {
            if (!_filesTold.Add(file.Path)) continue;
            Say(now, file.ScannedByAntivirus
                ? $"Windows Defender is checking {file.Name} before SOLIDWORKS can use it."
                : $"SOLIDWORKS is waiting on {file.Name} ({file.Where}).", StoryTone.Warn);
        }
        // Every SOLIDWORKS file it reads (with file tracing on), told once while it's in use.
        foreach (var file in x.ActiveFiles.Where(fl => fl.IsSolidWorksDocument && !fl.ScannedByAntivirus && fl.WaitTime < TimeSpan.FromSeconds(1)).Take(5))
        {
            if (!_filesTold.Add(file.Path)) continue;
            Say(now, $"Reading {file.Name}" + (file.BytesRead > 0 ? $" ({Io.IoExplainer.Size((ulong)file.BytesRead)} so far, {file.Where})." : $" ({file.Where})."));
        }
        if (x.ActiveFiles.Count == 0) _filesTold.Clear();

        // While it's busy, the numbers every few seconds, so the log keeps moving with what it's doing.
        if (x.Activity is { } activity && now - _lastActivity >= ActivityEvery && now - _lastLine >= TimeSpan.FromSeconds(1))
        {
            _lastActivity = now;
            Say(now, activity);
        }

        // During a long freeze, show time passing even when nothing else changes.
        if (level == NowLevel.Frozen && _frozenSince is { } frozen && now - _lastHeartbeat >= Heartbeat)
        {
            _lastHeartbeat = now;
            Say(now, $"Still not responding, {NowEngine.Seconds(now - frozen)} so far. " + StillLine(verdict, x.InProgress) +
                     LifeLine(now, x.Report.Verdict), Tone(verdict));
        }
    }

    /// <summary>An action we watched start is never "old history", however long it took.</summary>
    private bool _inProgressSeen(AddinAction finished) => _startedSeen.Contains((finished.What, finished.Started));
    private readonly HashSet<(string, DateTime)> _startedSeen = new();

    private void ResetWork()
    {
        _seenFinished = false;
        _startedSeen.Clear();
        _inProgress = _lastFinished = null;
        _activeDoc = null;
        _level = null;
        _verdict = Verdict.None;
        _frozenSince = null;
        _causes.Clear();
        _filesTold.Clear();
        _sheet = null;
    }

    private static bool SameAction(AddinAction a, AddinAction? b) =>
        b is not null && a.What == b.What && a.Started == b.Started && a.Document == b.Document && a.Sheet == b.Sheet;

    private static string Starting(AddinAction a) => a.What switch
    {
        "open" => $"Opening {a.FileName}.",
        "sheet" => $"You switched to sheet \"{a.Sheet}\". SOLIDWORKS is getting its views ready.",
        "save" => $"Saving {a.FileName}.",
        "rebuild" => $"Rebuilding {a.FileName}.",
        "view-test" => $"Timing each view on sheet \"{a.Sheet}\", one at a time.",
        _ => $"{a.What} {a.FileName}.",
    };

    private static string VerdictLine(Verdict v, VerdictReport r) => v switch
    {
        Verdict.Working => "It's still working: SOLIDWORKS's main task is busy, step by step. Worth waiting.",
        Verdict.Background => "Its main task is waiting, but other parts of SOLIDWORKS are still busy or data is still moving. Worth waiting.",
        Verdict.Quiet => "It's gone quiet: no sign of work for a little while. Keep an eye on it.",
        Verdict.ProbablyStuck => "No sign of work for a couple of minutes. It may be stuck. The box at the top says what to check before ending it.",
        Verdict.Stuck => "No sign of work for five minutes. It looks stuck. The box at the top says what to do.",
        _ => r.Title,
    };

    private static string StillLine(Verdict v, AddinAction? doing)
    {
        string what = doing is null ? "" : doing.What switch
        {
            "sheet" => $"Still on sheet \"{doing.Sheet}\". ",
            "open" => $"Still opening {doing.FileName}. ",
            "save" => $"Still saving {doing.FileName}. ",
            "rebuild" => $"Still rebuilding {doing.FileName}. ",
            _ => "",
        };
        return what + v switch
        {
            Verdict.Working => "Its main task is still busy, so it's still going.",
            Verdict.Background => "Background work is still going.",
            Verdict.Quiet or Verdict.ProbablyStuck or Verdict.Stuck => "No sign of work right now.",
            _ => "",
        };
    }

    /// <summary>" Last sign of work 2 s ago." The plain fact behind the verdict, so you can judge it yourself.</summary>
    private static string LifeLine(DateTime now, VerdictReport? v) =>
        v?.LastSignOfLife is { } last ? $" Last sign of work: {(now - last < TimeSpan.FromSeconds(2) ? "just now" : NowEngine.Seconds(now - last) + " ago")}." : "";

    private static StoryTone Tone(Verdict v) => v switch
    {
        Verdict.Working or Verdict.Background => StoryTone.Warn,
        Verdict.Quiet => StoryTone.Warn,
        Verdict.ProbablyStuck or Verdict.Stuck => StoryTone.Bad,
        _ => StoryTone.Normal,
    };

    /// <summary>The start of a text, whole sentences only, at least <paramref name="minChars"/> long so a short first step ("Close SOLIDWORKS.") isn't left on its own.</summary>
    internal static string FirstSentence(string text, int minChars = 0)
    {
        int end = -1;
        do
        {
            int next = text.IndexOf(". ", end + 1, StringComparison.Ordinal);
            if (next < 0) return text;
            end = next + 1;
        } while (end < minChars);
        return text[..end];
    }

    /// <summary>Adds a line from outside the story's own watching (e.g. a crash).</summary>
    public void Note(DateTime now, string text, StoryTone tone = StoryTone.Good) => Say(now, text, tone);

    private void Say(DateTime now, string text, StoryTone tone = StoryTone.Normal)
    {
        var line = new StoryLine(now, text.Trim(), tone);
        _lines.Add(line);
        if (_lines.Count > Keep) _lines.RemoveAt(0);
        _lastLine = now;
        NewLine?.Invoke(line);
    }
}
