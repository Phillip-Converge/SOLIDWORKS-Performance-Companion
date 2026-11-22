using System.Text;

namespace SWCompanion.Core.Ask;

/// <summary>What the Companion knows right now, gathered for one question (before names are hidden).</summary>
public sealed record AskContext(
    string? SolidWorksVersion,
    string Status,
    string Vitals,
    IReadOnlyList<string> RecentLog,
    IReadOnlyList<string> HungUp,
    IReadOnlyList<string> Fixes,
    string? Focus);

/// <summary>One earlier question and answer in this conversation (already with names hidden).</summary>
public sealed record AskTurn(string Question, string Answer);

/// <summary>Everything that will be sent for one question: exactly this, nothing else.</summary>
public sealed record AskRequest(string System, IReadOnlyList<AskTurn> Earlier, string Message)
{
    /// <summary>The whole thing as plain text, for the "what will be sent" preview and to count its size.</summary>
    public string AsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- Instructions for the AI ---").AppendLine(System).AppendLine();
        foreach (var t in Earlier)
            sb.AppendLine("--- Earlier question ---").AppendLine(t.Question).AppendLine("--- Earlier answer ---").AppendLine(t.Answer).AppendLine();
        sb.AppendLine("--- This question, with what the Companion sees ---").AppendLine(Message);
        return sb.ToString();
    }
}

/// <summary>
/// Builds what is sent to the AI for a question: fixed instructions, then the question and the Companion's current
/// view (status, numbers, the recent running log, hang-ups, fixes, and the item you asked about), all with private
/// names hidden by the conversation's <see cref="Scrubber"/>. The AI gets no tools: it can only answer.
/// </summary>
public static class AskPrompt
{
    public const int MaxLogLines = 40, MaxHungUp = 12, MaxEarlier = 3;

    public const string Instructions =
        """
        You are the helper inside a free Windows app that watches SOLIDWORKS from outside and explains slowdowns, freezes and "Not Responding" in plain English. The person asking uses SOLIDWORKS; they are not a programmer.

        How to answer:
        - Use plain, friendly English. Explain any technical word you have to use.
        - Keep it short: a few sentences, or a short numbered list of steps. Most likely cause first.
        - Base your answer on the facts below plus well-established SOLIDWORKS and Windows knowledge. Say plainly when you are not sure, and never present a guess as a fact. The app watches from outside, so it cannot see inside SOLIDWORKS.
        - The app is read only and cannot change anything. Tell the person what they can check or do themselves, step by step, and say when something is a job for their IT team or PDM administrator.
        - Do not tell them to end SOLIDWORKS, licensing or PDM processes from Task Manager, unless SOLIDWORKS has clearly been stuck for a long time; then remind them unsaved work will be lost.
        - Do not name or recommend any SOLIDWORKS reseller. Point to SOLIDWORKS's own documentation, Microsoft's documentation, or the person's own support channel instead.
        - Names of files, folders, servers, this PC and the user have been replaced with placeholders such as "file 1.SLDPRT", "folder 2" or "server A". Use the placeholders exactly as written; don't guess the real names.
        """;

    public static AskRequest Build(string question, AskContext c, Scrubber scrub, IReadOnlyList<AskTurn>? earlier = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Question: " + question.Trim()).AppendLine();
        if (c.Focus is { Length: > 0 } focus)
            sb.AppendLine("The question is about this item:").AppendLine(focus.Trim()).AppendLine();
        sb.AppendLine("What the app sees right now:");
        if (c.SolidWorksVersion is { Length: > 0 } v) sb.AppendLine("- Version: " + v);
        sb.AppendLine("- Status: " + c.Status);
        sb.AppendLine("- Numbers: " + c.Vitals);
        Section(sb, "Hang-ups the app caught today (newest first)", c.HungUp.Take(MaxHungUp));
        Section(sb, "Things the app thinks are worth fixing on this PC", c.Fixes);
        Section(sb, "The app's running log (oldest first)", c.RecentLog.TakeLast(MaxLogLines));
        return new AskRequest(Instructions, (earlier ?? []).TakeLast(MaxEarlier).ToList(), scrub.Hide(sb.ToString().TrimEnd()));
    }

    private static void Section(StringBuilder sb, string title, IEnumerable<string> lines)
    {
        var list = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (list.Count == 0) return;
        sb.AppendLine().AppendLine(title + ":");
        foreach (var l in list) sb.AppendLine("- " + l.Trim());
    }
}
