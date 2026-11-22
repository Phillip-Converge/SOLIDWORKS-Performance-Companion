using SWCompanion.Contracts;

namespace SWCompanion.Core.AddIn;

/// <summary>A model that finished loading during a sheet switch, and when (seconds after the switch began).</summary>
public sealed record ModelLoad(string Path, double AtSeconds);

/// <summary>One sheet switch as the add-in reported it.</summary>
public sealed class SheetSwitch
{
    public required string Sheet { get; init; }
    public string? Document { get; init; }
    public DateTime Started { get; init; }
    public double? Seconds { get; set; }
    public List<ModelLoad> Loads { get; } = new();
    public List<ViewFacts>? Views { get; set; }
}

/// <summary>A view that may be holding a sheet up, with the reasons in plain words.</summary>
public sealed record Suspect(string View, double Score, IReadOnlyList<string> Reasons, bool Measured, double? MeasuredSeconds);

/// <summary>The plain-English result: a headline, the views most likely to blame, and what we can't know.</summary>
public sealed record SheetReport(string Headline, IReadOnlyList<Suspect> Suspects, string Caveat, bool Measured);

/// <summary>
/// Ranks the views on a sheet by how likely each is to be what made the switch slow.
/// SOLIDWORKS doesn't time individual views during a sheet switch, so unless the owner runs the
/// "time each view" test, this is a ranking from evidence: how long each view's model took to load,
/// how big it is, and how it is displayed. The report always says which kind of answer it is.
/// </summary>
public static class SheetAnalysis
{
    public const double ManyParts = 100, VeryManyParts = 500;
    public const string MenuPath = "Tools > Companion: Time Each View on This Sheet";

    public const string ButtonHint = "click \"time each view\" in section 08 of the Companion";

    /// <param name="howToMeasure">How the owner starts the "time each view" test, e.g. the SOLIDWORKS menu or the Companion's button.</param>
    public static SheetReport Evidence(SheetSwitch s, string? howToMeasure = null)
    {
        string took = $"Sheet \"{s.Sheet}\" took {AddinTracker.Clock(s.Seconds ?? 0)}.";
        if (s.Views is null || s.Views.Count == 0)
            return new SheetReport(took, [], s.Seconds >= 2
                ? "The add-in couldn't read the views on this sheet."
                : "Quick enough that the add-in didn't look closer.", Measured: false);

        var loadEstimate = EstimateLoadTimes(s);
        var suspects = new List<Suspect>();
        foreach (var v in s.Views.Where(v => v.Visible != false))
        {
            double score = 0;
            var reasons = new List<string>();
            if (loadEstimate.TryGetValue(v.Name, out var load) && load >= 0.5)
            {
                score += load;
                reasons.Add($"its model ({FileName(v.Model)}) took about {AddinTracker.Clock(load)} to load during the switch");
            }
            if (v.Components is { } c && c >= ManyParts)
            {
                score += Math.Min(c, 5000) / 100.0;
                reasons.Add($"{c:N0} parts visible in it");
            }
            bool heavyLines = v.Display is "hidden lines removed" or "hidden lines visible";
            if (heavyLines && v.DraftQuality != true && (v.Components ?? 0) >= 50)
            {
                score += 5;
                reasons.Add($"{v.Display} in high quality on an assembly: SOLIDWORKS works out every hidden edge exactly, which is heavy on big assemblies");
            }
            else if (v.Display == "shaded with edges" && (v.Components ?? 0) >= 50)
            {
                score += 3;
                reasons.Add("shaded with edges on an assembly: both shading and edges have to be drawn");
            }
            if (v.Type == "section") { score += 2; reasons.Add("a section view, which cuts through the whole model"); }
            if (v.OutOfDate == true) { score += 2; reasons.Add("out of date, so it has to be updated"); }
            if (v.Lightweight == true) reasons.Add("lightweight (lighter to load, but may need resolving later)");
            if (v.DraftQuality == true && heavyLines) reasons.Add("already draft quality (the lighter setting)");
            if (score > 0) suspects.Add(new Suspect(v.Name, score, reasons, Measured: false, MeasuredSeconds: null));
        }
        suspects.Sort((a, b) => b.Score.CompareTo(a.Score));

        string head = suspects.Count == 0
            ? took + " None of its views stands out from what the add-in can see."
            : took + $" Most likely holding it up: {suspects[0].View}.";
        if (s.Loads.Count > 0) head += $" {s.Loads.Count} model{(s.Loads.Count == 1 ? "" : "s")} loaded during the switch.";
        return new SheetReport(head, suspects.Take(5).ToList(),
            "Ranked from evidence, not timed per view: SOLIDWORKS doesn't report how long each view takes. " +
            "To measure each view, open the drawing on this sheet and " + (howToMeasure ?? $"choose {MenuPath} in SOLIDWORKS") + ".", Measured: false);
    }

    /// <summary>The "time each view" test results, slowest first.</summary>
    public static SheetReport Measured(string sheet, IReadOnlyList<ViewFacts> views, double totalSeconds, bool markedChanged)
    {
        var suspects = views
            .Select(v =>
            {
                double secs = (v.LoadSeconds ?? 0) + (v.DrawSeconds ?? 0);
                var reasons = new List<string>();
                if (v.Problem is not null) reasons.Add(v.Problem);
                if (v.LoadSeconds is { } l) reasons.Add($"loading its model: {AddinTracker.Clock(l)}");
                if (v.DrawSeconds is { } d) reasons.Add($"redrawing: {AddinTracker.Clock(d)}");
                if (v.Components is { } c && c >= ManyParts) reasons.Add($"{c:N0} parts visible");
                if (v.Display is not null) reasons.Add(v.Display + (v.DraftQuality == true ? ", draft quality" : ""));
                return new Suspect(v.Name, secs, reasons, Measured: v.Problem is null, MeasuredSeconds: v.Problem is null ? secs : null);
            })
            .OrderByDescending(s => s.Score)
            .ToList();

        var top = suspects.FirstOrDefault(s => s.Measured);
        string head = top is null
            ? $"The view test on sheet \"{sheet}\" couldn't time any views."
            : $"Measured on sheet \"{sheet}\": {top.View} is the slowest view at {AddinTracker.Clock(top.MeasuredSeconds ?? 0)}" +
              (totalSeconds > 0 && top.MeasuredSeconds is { } t ? $" ({t / totalSeconds:P0} of the test)." : ".");
        string caveat = "Measured one view at a time. Each view is timed on its own, so the total can differ from a real sheet switch " +
                        "(models already in memory load faster the second time).";
        if (markedChanged) caveat += " SOLIDWORKS now marks the drawing as changed; the test didn't change anything in it.";
        return new SheetReport(head, suspects.Take(8).ToList(), caveat, Measured: true);
    }

    /// <summary>
    /// Rough load time per view: models load one after another, so the time between one view's model
    /// finishing and the next view's model finishing is put down to the second. An estimate, and labelled so.
    /// </summary>
    public static Dictionary<string, double> EstimateLoadTimes(SheetSwitch s)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (s.Views is null) return result;
        var marks = new List<(string View, double At)>();
        foreach (var v in s.Views)
        {
            if (v.Model is null) continue;
            var load = s.Loads.FirstOrDefault(l => SamePath(l.Path, v.Model));
            if (load is not null) marks.Add((v.Name, load.AtSeconds));
        }
        double previous = 0;
        foreach (var (view, at) in marks.OrderBy(m => m.At))
        {
            result[view] = Math.Max(0, at - previous);
            previous = Math.Max(previous, at);
        }
        return result;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);

    private static string FileName(string? path) => path is null ? "?" : Path.GetFileName(path);
}
