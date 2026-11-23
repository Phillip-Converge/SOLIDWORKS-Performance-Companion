using System.Text.RegularExpressions;

namespace SWCompanion.Core.Files;

/// <summary>One file and the SOLIDWORKS year it was last saved in (null if the text couldn't be read).</summary>
public sealed record SavedIn(string Path, int? Year, string Raw);

/// <summary>What the saved-in versions of an opened file and its references say.</summary>
/// <param name="Old">Files last saved in an older SOLIDWORKS than the one running, oldest first.</param>
/// <param name="Unknown">Files whose version text couldn't be read.</param>
public sealed record OldVersionReport(string Opened, int RunningYear, int Total, IReadOnlyList<SavedIn> Old, int Unknown, double? OpenSeconds)
{
    public bool Any => Old.Count > 0;
    public int? OldestYear => Old.Count == 0 ? null : Old.Min(o => o.Year);
    /// <summary>The top file itself is old (not just some of what it references).</summary>
    public bool TopIsOld => Old.Any(o => string.Equals(o.Path, Opened, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Files saved in an older SOLIDWORKS version are converted in memory every time they're opened, until they're saved in
/// the current version. For an assembly or drawing with many such files that adds up. This reads SOLIDWORKS's own
/// "saved in" text for each file, finds the old ones and decides when it's worth flagging. Pure text work, tested.
/// </summary>
public static class OldVersions
{
    /// <summary>Flag in the "came and went" panel when the open took at least this long and old files were part of it.</summary>
    public const double SlowOpenSeconds = 5;
    /// <summary>...or when this many files are old, however long the open took.</summary>
    public const int ManyOld = 20;

    /// <summary>
    /// Reads the year from SOLIDWORKS's version text. SOLIDWORKS writes e.g. "11000[2018/1,2018/134,2018/211]": an internal
    /// number, then the year/build of each save in brackets (seen on SOLIDWORKS 2026 SP5.2). The newest year is taken.
    /// </summary>
    public static int? Year(string raw)
    {
        var years = Regex.Matches(raw ?? "", @"(?<!\d)(19[89]\d|20\d\d)(?!\d)").Select(m => int.Parse(m.Value)).ToList();
        return years.Count > 0 ? years.Max() : null;
    }

    /// <param name="note">The message's lines: full path, a tab, SOLIDWORKS's text for the last saved version.</param>
    public static OldVersionReport Read(string opened, string? note, int runningYear, double? openSeconds)
    {
        var files = (note ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 2)
            .Select(p => new SavedIn(p[0].Trim(), Year(p[1]), p[1].Trim()))
            .ToList();
        var old = files.Where(f => f.Year is { } y && y < runningYear).OrderBy(f => f.Year).ThenBy(f => System.IO.Path.GetFileName(f.Path)).ToList();
        return new OldVersionReport(opened, runningYear, files.Count, old, files.Count(f => f.Year is null), openSeconds);
    }

    /// <summary>Worth pulling out into the "came and went" panel: old files made a slow open slower, or there are a lot of them.</summary>
    public static bool WorthFlagging(OldVersionReport r) =>
        r.Any && ((r.OpenSeconds ?? 0) >= SlowOpenSeconds || r.Old.Count >= ManyOld);

    /// <summary>One line for the log and the panel title, e.g. "34 of 210 files in Gearbox.SLDASM were saved in older SOLIDWORKS (oldest 2018)".</summary>
    public static string Line(OldVersionReport r)
    {
        string name = System.IO.Path.GetFileName(r.Opened);
        if (r.Total <= 1 || (r.TopIsOld && r.Old.Count == 1))
            return $"{name} was last saved in SOLIDWORKS {r.OldestYear}, so it's converted every time it opens";
        return $"{r.Old.Count} of {r.Total} files in {name} were last saved in older SOLIDWORKS (oldest {r.OldestYear})";
    }

    /// <summary>The detail: which versions, the first few names, and what it likely cost.</summary>
    public static string Detail(OldVersionReport r)
    {
        var byYear = r.Old.GroupBy(o => o.Year).OrderBy(g => g.Key).Select(g => $"{g.Count()} from {g.Key}");
        var names = r.Old.Take(5).Select(o => System.IO.Path.GetFileName(o.Path)).ToList();
        string more = r.Old.Count > names.Count ? $" and {r.Old.Count - names.Count} more" : "";
        string cost = r.OpenSeconds is { } s
            ? $" The open took {s:0} s. The Companion can't measure how much of that was conversion; it's usually a noticeable share when many files are old."
            : "";
        return $"Running SOLIDWORKS {r.RunningYear}. Older files: {string.Join(", ", byYear)}. For example: {string.Join(", ", names)}{more}.{cost}";
    }
}
