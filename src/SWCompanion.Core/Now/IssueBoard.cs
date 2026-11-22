using SWCompanion.Core.AddIn;
using SWCompanion.Core.Checkup;

namespace SWCompanion.Core.Now;

public enum IssueLevel { High, Medium }

/// <summary>An obvious issue, in plain words: what it is, why it matters, what to try.</summary>
public sealed record Issue(string Title, string Why, string Try, IssueLevel Level, string Source);

/// <summary>
/// Pulls the clear problems out of everything the Companion knows into one short, ranked list:
/// big slowdowns happening now, the views most likely holding up the last slow sheet, things
/// caught earlier that are worth a look, and setup problems from the checkup. Only obvious ones:
/// small, uncertain things stay in the details.
/// </summary>
public static class IssueBoard
{
    public const int Max = 8;
    /// <summary>Caught moments older than this drop off the list (they stay in section 00 and the log).</summary>
    public static readonly TimeSpan RecentCaught = TimeSpan.FromHours(1);

    public static IReadOnlyList<Issue> Build(DateTime now, NowReport? report, SheetReport? sheet,
        IReadOnlyList<Moment> caught, IReadOnlyList<CheckResult> checks, IReadOnlyList<Issue>? more = null)
    {
        var list = new List<Issue>();
        // Issues found elsewhere (e.g. a crash pattern) go in by their own level.
        if (more is not null) list.AddRange(more);

        // 1. Big slowdowns happening right now.
        foreach (var c in report?.Glaring ?? [])
            list.Add(new Issue(c.Title, LiveStory.FirstSentence(c.Evidence), c.Fix, IssueLevel.High, "right now"));

        // 2. The views most likely holding up the last slow sheet.
        if (sheet is not null)
        {
            foreach (var s in sheet.Suspects.Take(2))
            {
                bool big = (s.MeasuredSeconds ?? 0) >= 5 || s.Score >= 15;
                string title = s.Measured
                    ? $"{s.View} took {AddinTracker.Clock(s.MeasuredSeconds ?? 0)} to load and draw"
                    : $"{s.View} is the likely reason the sheet was slow";
                list.Add(new Issue(title, Capital(string.Join("; ", s.Reasons)) + ".", ViewTip(s), big ? IssueLevel.High : IssueLevel.Medium,
                    s.Measured ? "measured" : "sheet switch"));
            }
        }

        // 3. Things caught earlier that are worth a look.
        foreach (var m in caught.Where(m => m.LookInto && now - m.Start <= RecentCaught).Take(3))
            list.Add(new Issue($"Earlier ({m.Start:HH:mm}): {m.Title}", LiveStory.FirstSentence(m.Detail), "", IssueLevel.Medium, "caught"));

        // 4. Setup problems from the checkup: red ones first, then the "worth a look" ones.
        foreach (var c in checks.Where(c => c.Status is CheckStatus.Problem or CheckStatus.Look).OrderBy(c => c.Status == CheckStatus.Problem ? 0 : 1).Take(4))
            list.Add(new Issue(PlainTitle(c), LiveStory.FirstSentence(c.Finding), LiveStory.FirstSentence(c.HowToFix, minChars: 60), IssueLevel.Medium, "checkup"));

        // Same title twice (e.g. a glaring cause that was also caught) is said once.
        return list
            .GroupBy(i => i.Title, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(i => i.Level)
            .Take(Max)
            .ToList();
    }

    /// <summary>A plain "try this" for a view, from what made it heavy.</summary>
    private static string ViewTip(Suspect s)
    {
        string all = string.Join(" ", s.Reasons);
        if (all.Contains("high quality")) return "Try draft quality for this view (view properties), or a simpler display style.";
        if (all.Contains("shaded with edges")) return "Try hidden lines removed in draft quality, or plain shaded.";
        if (all.Contains("out of date")) return "Update the view once and save, so it doesn't redo it on every switch.";
        if (all.Contains("to load")) return "Its model takes long to load: check for old-version files, missing references or network paths.";
        return s.Measured ? "Look at this view's display style and what it shows." : "Use \"time each view\" to confirm before changing anything.";
    }

    /// <summary>A checkup item as a plain sentence for "worth fixing", instead of its short technical name.</summary>
    public static string PlainTitle(CheckResult c) => c.Id switch
    {
        "power-plan" => "Windows is set to save power, which slows the processor",
        "cpu-cap" => "Windows caps the processor below full speed",
        "battery" => "The laptop is on battery, so it runs slower",
        "handles" => "Windows limits how many drawing objects SOLIDWORKS can use",
        "pagefile" => "Windows' backup memory (the page file) needs a look",
        "other-antivirus" => "Another antivirus program is checking SOLIDWORKS's files",
        "folder-protection" => "Defender's folder protection can stop SOLIDWORKS saving",
        "work-folders" => "Windows won't let SOLIDWORKS save in a folder it needs",
        "graphics" => "SOLIDWORKS may not be using the dedicated graphics card",
        "explorer-opens" => "File Explorer opens to Quick access, which can be slow",
        "quick-access" => "File Explorer keeps a recent-files list that slows it down",
        "search-paths" => "Windows Search is indexing SOLIDWORKS folders",
        "file-locations" => "Some SOLIDWORKS File Locations point to folders that don't exist",
        "ext-ref-search" => "SOLIDWORKS searches extra places for referenced files",
        "pdm-options" => "The PDM add-in or vault view needs a look",
        "vpn" => "SOLIDWORKS traffic goes through the VPN",
        "defender-exclusions" => "Windows Defender scans the folders SOLIDWORKS uses most",
        _ => Capital(c.Title),
    };

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
