using SWCompanion.Core.Now;

namespace SWCompanion.Core.Tests;

public class ChatGuideTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0, DateTimeKind.Local);
    private static readonly NowReport Calm = new(NowLevel.Calm, "c", []);
    private static readonly FixCard Fix = new("Windows Search is indexing SOLIDWORKS folders", "Less background disk work.", ["Open Indexing Options."], IssueLevel.Medium, 0);

    private static NowReport Frozen(Verdict v) => new(NowLevel.Frozen, "f", [new Cause("computing", "Computing on one core", "e")],
        new VerdictReport(v, "t", "Its main task used 2.1 s of processor in the last 5 s. More.", DateTime.Now, "Give it time. More."));

    [Fact]
    public void A_new_situation_adds_a_message_and_the_same_one_only_updates_it()
    {
        var chat = new ChatGuide();
        chat.Update(T0, new GuideMessage("Hang on, SOLIDWORKS is still working.", "10 s so far.", Mood.Working));
        chat.Update(T0.AddSeconds(1), new GuideMessage("Hang on, SOLIDWORKS is still working.", "11 s so far.", Mood.Working));
        var m = Assert.Single(chat.Messages);
        Assert.Equal("11 s so far.", m.Text);

        chat.Update(T0.AddSeconds(5), new GuideMessage("All good. SOLIDWORKS is running smoothly.", "Nothing to fix.", Mood.Good));
        Assert.Equal(2, chat.Messages.Count);
    }

    [Fact]
    public void Reply_buttons_fit_the_moment()
    {
        var chat = new ChatGuide();
        var calm = chat.Replies(new ChatContext(Calm, null, [Fix], null));
        Assert.Equal(new[] { "Yes, show me", "Why?", "Not now" }, calm.Select(r => r.Label));

        var frozen = chat.Replies(new ChatContext(Frozen(Verdict.Working), null, [Fix], "SOLIDWORKS"));
        Assert.Equal(new[] { "Is it stuck?", "What's it doing?" }, frozen.Select(r => r.Label));

        Assert.Empty(chat.Replies(new ChatContext(Calm, null, [], null))); // nothing to talk about
    }

    [Fact]
    public void Answers_come_back_in_plain_words()
    {
        var chat = new ChatGuide();
        var ctx = new ChatContext(Calm, new Breakdown("Switching to sheet \"Sheet3\"", 48,
            [new TimeShare("drawing Section View B-B", 30), new TimeShare("Windows Defender checking files", 12)]), [Fix], null);
        chat.Reply(T0, new ChatReply("Why?", ReplyAction.Why), ctx);
        Assert.Equal(ChatFrom.You, chat.Messages[0].From);
        Assert.Equal("Switching to sheet \"Sheet3\" took 48 s: about 30 s drawing Section View B-B, about 12 s Windows Defender checking files.",
            chat.Messages[1].Text);

        var stuck = new ChatContext(Frozen(Verdict.Working), null, [], "SOLIDWORKS");
        chat.Reply(T0, new ChatReply("Is it stuck?", ReplyAction.IsItStuck), stuck);
        Assert.Equal("No, it's still working.", chat.Messages[^1].Title);
        Assert.Equal("Its main task used 2.1 s of processor in the last 5 s. Give it time.", chat.Messages[^1].Text);

        chat.Reply(T0, new ChatReply("What's it doing?", ReplyAction.WhatsItDoing), stuck);
        Assert.Equal("The hold-up looks like SOLIDWORKS. Computing on one core.", chat.Messages[^1].Text);
    }

    [Fact]
    public void Not_now_hides_the_buttons_until_something_new_happens()
    {
        var chat = new ChatGuide();
        var ctx = new ChatContext(Calm, null, [Fix], null);
        chat.Update(T0, new GuideMessage("SOLIDWORKS is running smoothly.", "I found 1 thing.", Mood.Good, "Show me the fix"));
        chat.Reply(T0, new ChatReply("Not now", ReplyAction.NotNow), ctx);
        Assert.Empty(chat.Replies(ctx));
        Assert.Equal("No problem.", chat.Messages[^1].Title);

        chat.Update(T0.AddMinutes(1), new GuideMessage("Switching to sheet \"Sheet3\" took 48 s.", "Most of it was one view.", Mood.Warn));
        Assert.NotEmpty(chat.Replies(ctx));
    }
}
