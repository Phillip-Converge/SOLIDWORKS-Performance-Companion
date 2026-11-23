using System.Text;

namespace SWCompanion.Core.Ask;

/// <summary>What a report to the developer is about, gathered before names are hidden.</summary>
/// <param name="About">One line: the item the report is about ("SOLIDWORKS stopped responding", "SOLIDWORKS crashed in xyz.dll"), or null for a general report.</param>
/// <param name="Unexplained">True when the Companion had no specific explanation for it (the most useful reports).</param>
/// <param name="Shown">What the Companion told the user about it (title, what happened, what to try).</param>
/// <param name="Recorded">The raw facts the Companion recorded (kind, times, details, nearby Windows events).</param>
/// <param name="Log">The running log around the time it happened, oldest first.</param>
public sealed record ReportContext(
    string? About,
    bool Unexplained,
    string CompanionVersion,
    string? SolidWorksVersion,
    string WindowsVersion,
    IReadOnlyList<string> Shown,
    IReadOnlyList<string> Recorded,
    IReadOnlyList<string> Log);

/// <summary>A report ready for the user to read, change and send from their own e-mail.</summary>
public sealed record DeveloperReportText(string Subject, string Body);

/// <summary>
/// Builds the "send this to the developer" report: a short plain-text summary of one item (or a general problem) with
/// the versions, what the Companion showed, what it recorded, and the running log around it. Private names are hidden
/// with the same <see cref="Scrubber"/> Ask AI uses. The Companion never sends it: the user reads it, can change
/// anything, and sends it from their own e-mail. Pure text, so it can be tested.
/// </summary>
public static class DeveloperReport
{
    public const int MaxLogLines = 40;
    public const string NotePlaceholder = "(Please write here what you were doing in SOLIDWORKS when this happened, and anything else you noticed.)";

    public static DeveloperReportText Build(ReportContext c, Scrubber scrub)
    {
        string about = string.IsNullOrWhiteSpace(c.About) ? "a problem" : c.About.Trim();
        string subject = "Companion for SOLIDWORKS report: " + (c.Unexplained ? "not explained - " : "") + about;

        var sb = new StringBuilder();
        sb.AppendLine("What happened, in my words:");
        sb.AppendLine(NotePlaceholder);
        sb.AppendLine();
        sb.AppendLine("----- Filled in by the Companion -----");
        sb.AppendLine("About: " + about);
        if (c.Unexplained) sb.AppendLine("The Companion had no specific explanation for this.");
        sb.AppendLine("Companion version: " + c.CompanionVersion);
        sb.AppendLine("SOLIDWORKS: " + (string.IsNullOrWhiteSpace(c.SolidWorksVersion) ? "not detected" : c.SolidWorksVersion.Trim()));
        sb.AppendLine("Windows: " + c.WindowsVersion);
        Section(sb, "What the Companion showed", c.Shown);
        Section(sb, "What the Companion recorded", c.Recorded);
        var log = c.Log.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(MaxLogLines).ToList();
        Section(sb, "Running log around that time (oldest first)",
            log.Count > 0 ? log : ["(The log from that time is no longer in memory. It is in the activity log file, in the Companion's logs folder.)"]);
        sb.AppendLine();
        sb.AppendLine("Names of files, folders, servers, this PC and the user were replaced with placeholders such as \"file 1.SLDPRT\" before this was shown.");

        return new DeveloperReportText(scrub.Hide(subject), scrub.Hide(sb.ToString().TrimEnd()));
    }

    /// <summary>
    /// A "mailto:" link that opens the user's e-mail program with the address, subject and report filled in. E-mail
    /// programs only accept links up to about 2,000 characters, so a longer report is replaced by a line asking the
    /// user to paste it (the caller puts the full report on the clipboard first).
    /// </summary>
    public const int MaxMailtoLength = 1900;
    public const string PasteLine = "The full report is on your clipboard: click here and press Ctrl+V to paste it, then send.";

    public static (string Link, bool Fits) MailLink(string to, string subject, string body)
    {
        string Make(string b) => $"mailto:{to}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(b.Replace("\r\n", "\n").Replace("\n", "\r\n"))}";
        string full = Make(body);
        return full.Length <= MaxMailtoLength ? (full, true) : (Make(PasteLine), false);
    }

    private static void Section(StringBuilder sb, string title, IEnumerable<string> lines)
    {
        var list = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (list.Count == 0) return;
        sb.AppendLine().AppendLine(title + ":");
        foreach (var l in list) sb.AppendLine("  " + l.Trim());
    }
}
