namespace SWCompanion.Core.Now;

public enum ChatFrom { Companion, You }

/// <summary>One bubble in the conversation.</summary>
public sealed record ChatMessage(DateTime Time, ChatFrom From, string Title, string Text, Mood Mood);

/// <summary>What a reply button does.</summary>
public enum ReplyAction { ShowFixes, Why, NotNow, IsItStuck, WhatsItDoing }

/// <summary>A reply button under the conversation. The first one is the suggested answer.</summary>
public sealed record ChatReply(string Label, ReplyAction Action);

/// <summary>What the conversation knows about right now, to answer "why?" and "is it stuck?".</summary>
public sealed record ChatContext(NowReport Report, Breakdown? Recent, IReadOnlyList<FixCard> Fixes, string? HoldUpStage);

/// <summary>
/// The guide as a conversation: the Companion says something only when there's something new to say (a new
/// situation, not every changing number), you answer with a button, and it answers back in plain words. Keeps the
/// last few messages so you can see how things went. Pure bookkeeping, so it can be tested.
/// </summary>
public sealed class ChatGuide
{
    public const int Keep = 30;
    private readonly List<ChatMessage> _messages = new();
    private string? _lastTitle;
    private bool _dismissed;

    public IReadOnlyList<ChatMessage> Messages => _messages;
    public event Action? Changed;

    /// <summary>Once a second with the guide's current message. A new situation adds a bubble; the same one just updates it.</summary>
    public void Update(DateTime now, GuideMessage msg)
    {
        if (msg.Title == _lastTitle)
        {
            // Same situation: refresh the wording in place (e.g. "so far most of the time...") without a new bubble.
            int i = _messages.FindLastIndex(m => m.From == ChatFrom.Companion && m.Title == msg.Title);
            if (i >= 0 && _messages[i].Text != msg.Body)
            {
                _messages[i] = _messages[i] with { Text = msg.Body, Mood = msg.Mood };
                Changed?.Invoke();
            }
            return;
        }
        _lastTitle = msg.Title;
        _dismissed = false;
        Add(new ChatMessage(now, ChatFrom.Companion, msg.Title, msg.Body, msg.Mood));
    }

    /// <summary>The reply buttons that fit what was last said. None after "not now", until something new happens.</summary>
    public IReadOnlyList<ChatReply> Replies(ChatContext c)
    {
        if (_dismissed) return [];
        var list = new List<ChatReply>();
        if (c.Report.Level == NowLevel.Frozen)
        {
            list.Add(new("Is it stuck?", ReplyAction.IsItStuck));
            list.Add(new("What's it doing?", ReplyAction.WhatsItDoing));
            return list;
        }
        if (c.Fixes.Count > 0) list.Add(new(c.Recent is { TotalSeconds: >= 5 } ? "Yes, show me how" : "Yes, show me", ReplyAction.ShowFixes));
        if (c.Fixes.Count > 0 || c.Recent is not null || c.Report.Level == NowLevel.Busy) list.Add(new("Why?", ReplyAction.Why));
        if (list.Count > 0) list.Add(new("Not now", ReplyAction.NotNow));
        return list;
    }

    /// <summary>You pressed a reply: your answer goes in, and the Companion answers back.</summary>
    public void Reply(DateTime now, ChatReply reply, ChatContext c)
    {
        Add(new ChatMessage(now, ChatFrom.You, "", reply.Label, Mood.Calm));
        switch (reply.Action)
        {
            case ReplyAction.NotNow:
                _dismissed = true;
                Add(Say(now, "No problem.", "I'll keep watching and let you know if anything changes. The fixes stay on the Fixes page.", Mood.Calm));
                break;
            case ReplyAction.ShowFixes:
                Add(Say(now, "Here's the first one.", c.Fixes.Count == 1 ? "One fix, step by step, on the Fixes page."
                    : $"{c.Fixes.Count} fixes, biggest gain first, one at a time on the Fixes page.", Mood.Good));
                break;
            case ReplyAction.Why:
                Add(Say(now, "Here's why.", Why(c), Mood.Calm));
                break;
            case ReplyAction.IsItStuck:
                Add(Say(now, IsItStuckTitle(c), IsItStuckText(c), c.Report.Verdict?.Verdict is Verdict.ProbablyStuck or Verdict.Stuck ? Mood.Bad : Mood.Working));
                break;
            case ReplyAction.WhatsItDoing:
                Add(Say(now, "Here's what I can see.", WhatsItDoing(c), Mood.Working));
                break;
        }
    }

    // ---- the answers ----

    internal static string Why(ChatContext c)
    {
        if (c.Recent is { } b && b.Shares.Count > 0)
            return $"{b.Title} took {AddIn.AddinTracker.Clock(b.TotalSeconds)}: " +
                   string.Join(", ", b.Shares.Take(3).Select(s => $"about {s.Seconds:0} s {s.What}")) + ".";
        if (c.Report.Level == NowLevel.Busy && c.Report.Causes.FirstOrDefault(x => x.Kind is not ("setup" or "waiting")) is { } cause)
            return cause.Title + ". " + LiveStory.FirstSentence(cause.Evidence, 80);
        if (c.Fixes.Count > 0)
            return "These are known to slow SOLIDWORKS down on this PC: " + string.Join("; ", c.Fixes.Take(3).Select(f => f.Title)) +
                   (c.Fixes.Count > 3 ? $"; and {c.Fixes.Count - 3} more." : ".");
        return "Nothing is slowing SOLIDWORKS down right now.";
    }

    private static string IsItStuckTitle(ChatContext c) => c.Report.Verdict?.Verdict switch
    {
        Verdict.Working or Verdict.Background => "No, it's still working.",
        Verdict.Quiet => "Not yet. It's gone quiet.",
        Verdict.ProbablyStuck => "Probably, yes.",
        Verdict.Stuck => "It looks stuck.",
        _ => "Too soon to say.",
    };

    private static string IsItStuckText(ChatContext c)
    {
        var v = c.Report.Verdict;
        if (v is null) return "It only just stopped responding. Give it a few seconds and ask again.";
        return LiveStory.FirstSentence(v.Evidence) + (string.IsNullOrWhiteSpace(v.Advice) ? "" : " " + LiveStory.FirstSentence(v.Advice));
    }

    private static string WhatsItDoing(ChatContext c)
    {
        var parts = new List<string>();
        if (c.HoldUpStage is { } stage) parts.Add($"The hold-up looks like {stage}.");
        var causes = c.Report.Causes.Where(x => x.Kind is not ("setup" or "waiting" or "busy-notice")).Take(2).ToList();
        foreach (var x in causes) parts.Add(x.Title + ".");
        if (c.Recent is { } b && b.Shares.FirstOrDefault() is { } top) parts.Add($"So far most of the time is {top.What}.");
        return parts.Count == 0 ? "No single cause stands out yet; SOLIDWORKS is working on its own. The log below shows each step." : string.Join(" ", parts);
    }

    /// <summary>Adds a message from outside the guide's own script (your typed question, an AI's answer).</summary>
    public void Post(ChatMessage m) => Add(m);

    /// <summary>Swaps a message for another (e.g. "Asking..." for the answer once it arrives). Adds it if the old one is gone.</summary>
    public void Replace(ChatMessage old, ChatMessage with)
    {
        int i = _messages.IndexOf(old);
        if (i < 0) { Add(with); return; }
        _messages[i] = with;
        Changed?.Invoke();
    }

    private static ChatMessage Say(DateTime now, string title, string text, Mood mood) => new(now, ChatFrom.Companion, title, text, mood);

    private void Add(ChatMessage m)
    {
        _messages.Add(m);
        while (_messages.Count > Keep) _messages.RemoveAt(0);
        Changed?.Invoke();
    }
}
