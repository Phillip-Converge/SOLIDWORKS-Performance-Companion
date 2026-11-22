using SWCompanion.Contracts;

namespace SWCompanion.Core.AddIn;

/// <summary>Something SOLIDWORKS started and (maybe) finished, as reported by the add-in.</summary>
public sealed record AddinAction(string What, string? Document, string? Sheet, DateTime Started, double? Seconds)
{
    public bool Finished => Seconds is not null;
    public string FileName => Document is null ? "" : Path.GetFileName(Document);
}

/// <summary>
/// Turns the add-in's notes into "what SOLIDWORKS is doing right now" and "how long things took".
/// Pure bookkeeping, no Windows calls, so it can be tested on its own.
/// </summary>
public sealed class AddinTracker
{
    public const int Keep = 20;

    private readonly List<AddinAction> _finished = new();

    public string? ActiveDocument { get; private set; }
    public string? ActiveDocType { get; private set; }
    /// <summary>What SOLIDWORKS says it is in the middle of, if anything.</summary>
    public AddinAction? InProgress { get; private set; }
    public IReadOnlyList<AddinAction> Finished => _finished;
    public DateTime? LastHeard { get; private set; }
    public string? Version { get; private set; }
    /// <summary>The sheet switch under way, or the last one finished.</summary>
    public SheetSwitch? LastSheet { get; private set; }
    /// <summary>What we can say about the last slow sheet switch, or the last "time each view" test.</summary>
    public SheetReport? SheetReport { get; private set; }
    /// <summary>How the owner starts the "time each view" test right now (set by the screen).</summary>
    public string? HowToMeasure { get; set; }

    public void Apply(AddinMessage m, DateTime? receivedAt = null)
    {
        var at = m.Time == default ? receivedAt ?? DateTime.Now : m.Time.ToLocalTime();
        LastHeard = receivedAt ?? DateTime.Now;
        switch (m.Kind)
        {
            case AddinMessage.Kinds.Connected:
                Version = m.Note;
                break;
            case AddinMessage.Kinds.ActiveDocument:
                ActiveDocument = m.Document;
                ActiveDocType = m.DocType;
                break;
            case AddinMessage.Kinds.Close:
                if (m.Document is not null && string.Equals(m.Document, ActiveDocument, StringComparison.OrdinalIgnoreCase))
                {
                    ActiveDocument = null;
                    ActiveDocType = null;
                }
                break;
            case AddinMessage.Kinds.OpenStart:
                InProgress = new AddinAction("open", m.Document, null, at, null);
                break;
            case AddinMessage.Kinds.SheetStart:
                InProgress = new AddinAction("sheet", m.Document, m.Sheet, at, null);
                LastSheet = new SheetSwitch { Sheet = m.Sheet ?? "", Document = m.Document, Started = at };
                break;
            case AddinMessage.Kinds.ModelLoad:
                if (InProgress?.What == "sheet" && LastSheet is { Seconds: null } sw && m.Document is not null)
                    sw.Loads.Add(new ModelLoad(m.Document, Math.Max(0, (at - sw.Started).TotalSeconds)));
                break;
            case AddinMessage.Kinds.SheetViews:
                if (LastSheet is { } last && last.Sheet == (m.Sheet ?? ""))
                {
                    last.Views = m.Views;
                    last.Seconds ??= m.Seconds;
                    SheetReport = SheetAnalysis.Evidence(last, HowToMeasure);
                }
                break;
            case AddinMessage.Kinds.ViewTestStart:
                InProgress = new AddinAction("view-test", m.Document, m.Note, at, null);
                break;
            case AddinMessage.Kinds.ViewTimes:
                End("view-test", m, at);
                SheetReport = SheetAnalysis.Measured(m.Sheet ?? "", m.Views ?? [], m.Seconds ?? 0, m.Note == "marked-changed");
                break;
            case AddinMessage.Kinds.SheetEnd:
                End("sheet", m, at);
                if (LastSheet is { } ended && ended.Sheet == (m.Sheet ?? ended.Sheet))
                {
                    ended.Seconds = m.Seconds;
                    // Quick switches get no closer look from the add-in; say so rather than show an old report.
                    if (m.Seconds < 2) SheetReport = null;
                }
                break;
            case AddinMessage.Kinds.RebuildStart:
                InProgress = new AddinAction("rebuild", m.Document, null, at, null);
                break;
            case AddinMessage.Kinds.SaveStart:
                InProgress = new AddinAction("save", m.Document, null, at, null);
                break;
            case AddinMessage.Kinds.OpenEnd:
                End("open", m, at);
                break;
            case AddinMessage.Kinds.RebuildEnd:
                End("rebuild", m, at);
                break;
            case AddinMessage.Kinds.SaveEnd:
                End("save", m, at);
                break;
        }
    }

    private void End(string what, AddinMessage m, DateTime at)
    {
        var started = InProgress is { } p && p.What == what ? p.Started : at;
        double seconds = m.Seconds ?? Math.Max(0, (at - started).TotalSeconds);
        _finished.Insert(0, new AddinAction(what, m.Document ?? InProgress?.Document, m.Sheet ?? InProgress?.Sheet, started, seconds));
        if (_finished.Count > Keep) _finished.RemoveAt(_finished.Count - 1);
        if (InProgress?.What == what) InProgress = null;
    }

    /// <summary>"Opening Bracket.SLDASM: 14 s so far", or null when nothing is in progress.</summary>
    public string? NowLine(DateTime now)
    {
        if (InProgress is not { } p) return null;
        var secs = Math.Max(0, (now - p.Started).TotalSeconds);
        return $"{Verb(p)}: {Clock(secs)} so far";
    }

    /// <summary>"Opened Bracket.SLDASM in 4.2 s".</summary>
    public static string Describe(AddinAction a) => a.What switch
    {
        "open" => $"Opened {a.FileName} in {Clock(a.Seconds ?? 0)}",
        "sheet" => $"Switched to sheet \"{a.Sheet}\" in {Clock(a.Seconds ?? 0)}",
        "rebuild" => $"Rebuilt {a.FileName} in {Clock(a.Seconds ?? 0)}",
        "save" => $"Saved {a.FileName} in {Clock(a.Seconds ?? 0)}",
        "view-test" => $"Timed each view on sheet \"{a.Sheet}\" in {Clock(a.Seconds ?? 0)}",
        _ => $"{a.What} {a.FileName} in {Clock(a.Seconds ?? 0)}",
    };

    private static string Verb(AddinAction a) => a.What switch
    {
        "open" => $"Opening {a.FileName}",
        "sheet" => $"Switching to sheet \"{a.Sheet}\"",
        "rebuild" => $"Rebuilding {a.FileName}",
        "save" => $"Saving {a.FileName}",
        "view-test" => $"Timing each view on sheet \"{a.Sheet}\"",
        _ => a.What,
    };

    /// <summary>How the Companion is hearing from inside SOLIDWORKS.</summary>
    public enum Line { None, AddIn, Outside }

    /// <summary>The plain-English paragraph under the section, depending on how (or whether) we hear from SOLIDWORKS.</summary>
    public static string Explain(bool solidWorksRunning, Line line, string? problem = null) =>
        !solidWorksRunning
            ? "When SOLIDWORKS is running, this shows what SOLIDWORKS itself says it is doing: opening files, switching drawing " +
              "sheets, and how long each took. No add-in is needed; the Companion connects to SOLIDWORKS from outside."
            : line switch
            {
                Line.AddIn => "This comes from inside SOLIDWORKS through the Companion add-in. It only listens: it notes when " +
                              "SOLIDWORKS starts and finishes an action and passes the times here. It never changes your models or settings. " + Analogies_Intercom,
                Line.Outside => "The Companion is connected to SOLIDWORKS from outside (no add-in needed). It only listens: SOLIDWORKS " +
                                "tells it when an action starts and finishes. It never changes your models or settings. " + Analogies_Intercom,
                _ => (problem ?? "Not connected to SOLIDWORKS yet; the Companion keeps trying every few seconds.") +
                     " Everything else on this screen still works without it.",
            };
    private const string Analogies_Intercom =
        "Think of it like this: the rest of this screen watches the machine shop through the window; the add-in is the foreman " +
        "on the shop floor calling out \"starting the big job now\" and \"done, took 40 seconds\".";

    public static string Clock(double seconds) =>
        seconds < 10 ? $"{seconds:0.0} s" :
        seconds < 120 ? $"{seconds:0} s" :
        $"{(int)(seconds / 60)} min {(int)(seconds % 60):00} s";

    public static string? TypeFromPath(string? path) => Path.GetExtension(path ?? "").ToUpperInvariant() switch
    {
        ".SLDPRT" => "part",
        ".SLDASM" => "assembly",
        ".SLDDRW" => "drawing",
        _ => null,
    };
}
