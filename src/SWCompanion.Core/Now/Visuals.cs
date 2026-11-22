using SWCompanion.Core.AddIn;

namespace SWCompanion.Core.Now;

// =====================================================================================================
// The "brains" behind the main view's pictures. Pure bookkeeping (no Windows calls, no drawing), so each
// can be tested on its own. The window only draws what these decide.
// =====================================================================================================

public enum StageHealth { Idle, Ok, Busy, HoldUp }

/// <summary>One box in the pipeline picture: You, SOLIDWORKS, files, network and vault, Defender.</summary>
public sealed record Stage(string Name, string Detail, StageHealth Health);

/// <summary>What the pipeline picture looks at, once a second.</summary>
public sealed record PipelineInputs(
    bool Running, NowLevel Level, double? SwCores, double? TopThreadPercent, double? SwReadBytesPerSec, double? SwWriteBytesPerSec,
    double? NetworkBytesPerSec, bool VaultBusy, double? DefenderCores, IReadOnlyList<Cause> Causes);

/// <summary>
/// The pipeline: You -> SOLIDWORKS -> files -> network and vault -> Defender. Each box says what it's doing in a few
/// words; the box the Companion thinks is holding things up is marked. "Holding up" comes from the same causes the
/// rest of the Companion uses, so the picture never says more than the evidence does.
/// </summary>
public static class Pipeline
{
    public static IReadOnlyList<Stage> Build(PipelineInputs x)
    {
        if (!x.Running)
            return [new("You", "", StageHealth.Idle), new("SOLIDWORKS", "not open", StageHealth.Idle),
                    new("Files", "", StageHealth.Idle), new("Network and vault", "", StageHealth.Idle), new("Defender", "", StageHealth.Idle)];

        bool frozen = x.Level == NowLevel.Frozen;
        string? holdUp = HoldUp(x.Causes);
        StageHealth H(string stage, bool busy) => holdUp == stage ? StageHealth.HoldUp : busy ? StageHealth.Busy : StageHealth.Ok;

        double cores = x.SwCores ?? 0, top = x.TopThreadPercent ?? 0;
        string sw = frozen && cores < 0.1 ? "not responding, no work seen"
            : cores < 0.05 ? "idle, waiting for you"
            : top >= 85 && cores < 1.6 ? $"1 core at {top:0}%: step-by-step work"
            : $"{cores:0.0} cores busy";
        double read = x.SwReadBytesPerSec ?? 0, write = x.SwWriteBytesPerSec ?? 0;
        string files = read + write < 100_000 ? "quiet" : $"{(read >= write ? "reading" : "writing")} {Mb(Math.Max(read, write))}/s";
        double net = x.NetworkBytesPerSec ?? 0;
        string network = x.VaultBusy ? "vault traffic" : net < 100_000 ? "quiet" : $"{Mb(net)}/s";
        double av = x.DefenderCores ?? 0;
        string defender = x.DefenderCores is null ? "not seen" : av < 0.1 ? "quiet" : $"{av:0.0} cores scanning";

        return
        [
            new("You", frozen ? "waiting for SOLIDWORKS" : "working", frozen ? StageHealth.Busy : StageHealth.Ok),
            new("SOLIDWORKS", sw, H("SOLIDWORKS", cores >= 0.3)),
            new("Files", files, H("Files", read + write >= 2_000_000)),
            new("Network and vault", network, H("Network and vault", x.VaultBusy || net >= 2_000_000)),
            new("Defender", defender, H("Defender", av >= 0.3)),
        ];
    }

    /// <summary>Which box the strongest cause points at, or null when nothing outside the normal is holding things up.</summary>
    public static string? HoldUp(IReadOnlyList<Cause> causes)
    {
        foreach (var c in causes)
        {
            string? stage = c.Kind switch
            {
                "computing" or "computing-many" or "memory" or "commit" or "objects" or "graphics" => "SOLIDWORKS",
                "loading" or "saving" or "disk" or "files" => "Files",
                "vault-archive" or "vault-database" or "vault-trouble" => "Network and vault",
                "antivirus" or "defender-busy" => "Defender",
                _ => null,
            };
            if (stage is not null) return stage;
        }
        return null;
    }

    private static string Mb(double bytesPerSec) => bytesPerSec >= 1_000_000 ? $"{bytesPerSec / 1_000_000:0.#} MB" : $"{bytesPerSec / 1000:0} KB";
}

/// <summary>One second of the timeline.</summary>
public sealed record TimePoint(DateTime Time, double SwCorePercent, double DiskBytesPerSec, double MemoryGb, bool Frozen);

/// <summary>A moment worth marking on the timeline ("opened Pump", "Sheet3").</summary>
public sealed record TimeMark(DateTime Time, string Label);

/// <summary>The last few minutes, second by second, plus marked moments. Feeds the timeline picture.</summary>
public sealed class Timeline
{
    public static readonly TimeSpan Span = TimeSpan.FromMinutes(5);
    private readonly List<TimePoint> _points = new();
    private readonly List<TimeMark> _marks = new();

    public IReadOnlyList<TimePoint> Points => _points;
    public IReadOnlyList<TimeMark> Marks => _marks;

    public void Add(TimePoint p)
    {
        _points.Add(p);
        Trim(p.Time);
    }

    public void Mark(DateTime when, string label)
    {
        _marks.Add(new TimeMark(when, label));
        Trim(when);
    }

    /// <summary>Stretches of time SOLIDWORKS wasn't responding, for shading.</summary>
    public IReadOnlyList<(DateTime From, DateTime To)> Frozen()
    {
        var list = new List<(DateTime, DateTime)>();
        DateTime? start = null;
        foreach (var p in _points)
        {
            if (p.Frozen) start ??= p.Time;
            else if (start is { } s) { list.Add((s, p.Time)); start = null; }
        }
        if (start is { } open && _points.Count > 0) list.Add((open, _points[^1].Time));
        return list;
    }

    private void Trim(DateTime now)
    {
        var cutoff = now - Span;
        _points.RemoveAll(p => p.Time < cutoff);
        _marks.RemoveAll(m => m.Time < cutoff);
    }
}

/// <summary>A share of a wait: "30 s drawing views".</summary>
public sealed record TimeShare(string What, double Seconds);

/// <summary>Where the time went during one freeze or slow action.</summary>
public sealed record Breakdown(string Title, double TotalSeconds, IReadOnlyList<TimeShare> Shares);

/// <summary>
/// Remembers, second by second while SOLIDWORKS is busy or not responding, what the strongest cause was, so that
/// after a freeze or a slow action it can split the wait into its causes. Seconds with no outside cause count as
/// SOLIDWORKS's own work. An approximation from once-a-second readings, and labelled as such on screen.
/// </summary>
public sealed class TimeAccount
{
    public const string OwnWork = "SOLIDWORKS working on its own (no outside hold-up seen)";
    private static readonly HashSet<string> Skip = new() { "setup", "waiting", "inside", "busy-notice", "dialog-open" };
    private readonly List<(DateTime Time, string What)> _seconds = new();

    public void Record(DateTime now, NowReport report, bool swBusy)
    {
        if (report.Level != NowLevel.Frozen && !swBusy) return;
        var top = report.Causes.FirstOrDefault(c => !Skip.Contains(c.Kind));
        _seconds.Add((now, top is null ? OwnWork : Plain(top)));
        _seconds.RemoveAll(s => now - s.Time > TimeSpan.FromMinutes(15));
    }

    public Breakdown? For(string title, DateTime from, DateTime to)
    {
        var inside = _seconds.Where(s => s.Time >= from && s.Time <= to).ToList();
        if (inside.Count == 0) return null;
        double total = Math.Max((to - from).TotalSeconds, inside.Count);
        var shares = inside.GroupBy(s => s.What)
            .Select(g => new TimeShare(g.Key, g.Count() / (double)inside.Count * total))
            .OrderByDescending(s => s.Seconds).ToList();
        return new Breakdown(title, total, shares);
    }

    /// <summary>A cause in a few words for the breakdown ("Computing on one core" -> "computing on one core").</summary>
    private static string Plain(Cause c) =>
        c.Title.Length == 0 ? c.Kind
        : c.Title.StartsWith("Windows", StringComparison.Ordinal) || c.Title.StartsWith("SOLIDWORKS", StringComparison.Ordinal) ? c.Title // names keep their capitals
        : char.ToLowerInvariant(c.Title[0]) + c.Title[1..];
}

public enum Mood { Calm, Good, Working, Warn, Bad }

/// <summary>The guide's message at the top: one big friendly sentence, a line under it, and (maybe) one button.</summary>
public sealed record GuideMessage(string Title, string Body, Mood Mood, string? Action = null);

/// <summary>
/// Decides the one thing to say at the top, like a helpful person sitting next to you: the answer first, in plain
/// words, then the next step. Reassures when SOLIDWORKS is still working, says plainly when it looks stuck, and
/// offers to walk through fixes when there are some.
/// </summary>
public static class Guide
{
    public static GuideMessage Build(bool running, NowReport report, Breakdown? recent, int fixes)
    {
        string fixOffer = fixes == 0 ? "" : $" I found {fixes} thing{(fixes == 1 ? "" : "s")} that could make it run better.";
        string? action = fixes == 0 ? null : fixes == 1 ? "Show me the fix" : "Let's go through them";

        if (!running)
            return new("Open SOLIDWORKS and I'll keep an eye on it.",
                "While you work, I'll explain anything that slows it down, in plain words, and help you fix it." + fixOffer, Mood.Calm, action);

        if (report.Level == NowLevel.Frozen && report.Verdict is { } v)
        {
            string top = recent?.Shares.FirstOrDefault()?.What is { } t ? $" So far most of the time is going on {t}." : "";
            return v.Verdict switch
            {
                Verdict.Working or Verdict.Background => new("Hang on, SOLIDWORKS is still working.",
                    $"It isn't responding to clicks, but it's busy and making progress.{top} Ending it now would lose your unsaved work.", Mood.Working),
                Verdict.Quiet => new("SOLIDWORKS has gone quiet.",
                    $"No sign of work for a little while.{top} Give it a moment; the box below says when it's reasonable to end it.", Mood.Warn),
                Verdict.ProbablyStuck or Verdict.Stuck => new("SOLIDWORKS looks stuck.",
                    "No sign of work for minutes. The box below says what to check before you end it.", Mood.Bad),
                _ => new("SOLIDWORKS isn't responding yet.", "Watching for signs of work...", Mood.Working),
            };
        }

        if (recent is { TotalSeconds: >= 5 } b && b.Shares.FirstOrDefault() is { } first)
            return new($"{b.Title} took {AddIn.AddinTracker.Clock(b.TotalSeconds)}.",
                $"Most of it was {first.What}." + fixOffer, Mood.Warn, fixes > 0 ? "Show me how to speed it up" : null);

        if (report.Level == NowLevel.Busy && report.Causes.FirstOrDefault(c => c.Kind is not ("setup" or "waiting")) is { } c)
            return new("SOLIDWORKS is working, but something is slowing it down.", c.Title + ". " + LiveStory.FirstSentence(c.Evidence), Mood.Warn, action);

        return fixes == 0
            ? new("All good. SOLIDWORKS is running smoothly.", "Nothing to fix right now. I'll let you know if anything slows it down.", Mood.Good)
            : new("SOLIDWORKS is running smoothly.", fixOffer.Trim(), Mood.Good, action);
    }
}

/// <summary>A fix to try, with the steps and an idea of the gain.</summary>
public sealed record FixCard(string Title, string Gain, IReadOnlyList<string> Steps, IssueLevel Level, double GainSeconds);

/// <summary>Turns the issues into fix cards: biggest likely gain first, each with numbered steps.</summary>
public static class FixCards
{
    // What fixing each checkup item gives you, in plain words (no made-up numbers).
    private static readonly Dictionary<string, string> CheckGain = new()
    {
        ["power-plan"] = "The processor can run at full speed.",
        ["cpu-cap"] = "The processor can run at full speed.",
        ["battery"] = "Full speed while plugged in.",
        ["handles"] = "Fewer crashes and blank menus on big drawings and assemblies.",
        ["pagefile"] = "Fewer stalls when memory runs short.",
        ["graphics"] = "Smoother graphics, faster rotate and redraw.",
        ["explorer-opens"] = "Faster File Explorer and Open dialogs.",
        ["quick-access"] = "Faster File Explorer and Open dialogs.",
        ["search-paths"] = "Less background disk work while you're in SOLIDWORKS.",
        ["file-locations"] = "Faster opens: SOLIDWORKS stops looking in folders that don't exist.",
        ["ext-ref-search"] = "Faster opens of assemblies and drawings.",
        ["vpn"] = "Faster vault work: saves, check-ins and opens.",
        ["defender-exclusions"] = "Fewer scan delays when opening and saving.",
    };

    public static IReadOnlyList<FixCard> From(IReadOnlyList<Issue> issues, Func<Issue, string?>? checkIdOf = null)
    {
        return issues
            .Where(i => !string.IsNullOrWhiteSpace(i.Try) || i.Source == "crashes")
            .Select(i =>
            {
                double seconds = SecondsIn(i.Why);
                string gain = checkIdOf?.Invoke(i) is { } id && CheckGain.TryGetValue(id, out var g) ? g
                    : i.Source is "sheet switch" or "measured" && seconds > 0 ? $"Could save up to about {seconds:0} s each time."
                    : i.Source == "crashes" ? "Fewer crashes."
                    : LiveStory.FirstSentence(i.Why);
                return new FixCard(i.Title, gain, Steps(i.Try), i.Level, seconds);
            })
            .OrderBy(c => c.Level)
            .ThenByDescending(c => c.GainSeconds)
            .ToList();
    }

    /// <summary>"Close SOLIDWORKS. Open X. Untick Y." -> three numbered steps.</summary>
    public static IReadOnlyList<string> Steps(string text) =>
        text.Split(". ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.TrimEnd('.') + ".")
            .Where(s => s.Length > 2)
            .ToList();

    /// <summary>The biggest "N s" mentioned (e.g. "took about 31 s to load"), as a rough size of the gain.</summary>
    private static double SecondsIn(string text)
    {
        double best = 0;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"(\d+(?:\.\d+)?) s\b"))
            if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                best = Math.Max(best, v);
        return best;
    }
}
