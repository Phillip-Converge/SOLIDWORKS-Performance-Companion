using SWCompanion.Core.AddIn;
using SWCompanion.Core.Checkup;
using SWCompanion.Core.Now;

namespace SWCompanion.Core.Tests;

public class LiveViewTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0, DateTimeKind.Local);
    private static readonly NowReport Calm = new(NowLevel.Calm, "ok", []);

    private static NowReport Frozen(Verdict v, params Cause[] causes) =>
        new(NowLevel.Frozen, "frozen", causes, new VerdictReport(v, "t", "e", null, "a"));

    private static StoryInputs In(NowReport r, AddinAction? doing = null, AddinAction? done = null, SheetSwitch? sheet = null, string? doc = null) =>
        new(true, r, doing, done, sheet, doc, []);

    [Fact]
    public void The_story_tells_a_slow_sheet_switch_from_start_to_finish_in_plain_words()
    {
        var story = new LiveStory();
        var doing = new AddinAction("sheet", @"C:\Example\Pump.SLDDRW", "Sheet3", T0, null);
        var sheet = new SheetSwitch { Sheet = "Sheet3", Started = T0 };

        story.Update(T0, In(Calm, doc: @"C:\Example\Pump.SLDDRW"));
        story.Update(T0.AddSeconds(1), In(Calm, doing, sheet: sheet, doc: @"C:\Example\Pump.SLDDRW"));
        sheet.Loads.Add(new ModelLoad(@"C:\Example\Pump.SLDASM", 2));
        story.Update(T0.AddSeconds(3), In(Frozen(Verdict.Working), doing, sheet: sheet, doc: @"C:\Example\Pump.SLDDRW"));
        // Nothing new for a while: a "still" line every 10 s (never more than 15 s without news), not every second.
        for (int s = 4; s <= 20; s++) story.Update(T0.AddSeconds(s), In(Frozen(Verdict.Working), doing, sheet: sheet, doc: @"C:\Example\Pump.SLDDRW"));
        var done = doing with { Seconds = 21 };
        story.Update(T0.AddSeconds(22), In(Calm, null, done, sheet, @"C:\Example\Pump.SLDDRW"));

        var text = story.Lines.Select(l => l.Text).ToList();
        Assert.Equal(new[]
        {
            "SOLIDWORKS is running. Watching it now.",
            "Now working in Pump.SLDDRW.",
            "You switched to sheet \"Sheet3\". SOLIDWORKS is getting its views ready.",
            "Loading Pump.SLDASM for the views on this sheet.",
            "SOLIDWORKS stopped responding to clicks (Windows will call it \"Not Responding\").",
            "It's still working: SOLIDWORKS's main task is busy, step by step. Worth waiting.",
            "Still not responding, 10 s so far. Still on sheet \"Sheet3\". Its main task is still busy, so it's still going.",
            "Done: Switched to sheet \"Sheet3\" in 21 s.",
            "SOLIDWORKS is responding again, after 19 s.",
        }, text);
        Assert.Equal(StoryTone.Warn, story.Lines.Single(l => l.Text.StartsWith("Done")).Tone);
    }

    [Fact]
    public void The_story_says_when_it_looks_stuck_and_never_repeats_an_unchanged_cause()
    {
        var story = new LiveStory();
        var hog = new Cause("other-program", "Another program is busy", "ExampleBackup.exe is using 3 cores. More words.");
        story.Update(T0, In(Calm));
        story.Update(T0.AddSeconds(1), In(new NowReport(NowLevel.Busy, "b", [hog])));
        story.Update(T0.AddSeconds(2), In(new NowReport(NowLevel.Busy, "b", [hog])));
        Assert.Single(story.Lines, l => l.Text == "Another program is busy: ExampleBackup.exe is using 3 cores.");

        story.Update(T0.AddSeconds(3), In(Frozen(Verdict.Quiet)));
        story.Update(T0.AddSeconds(200), In(Frozen(Verdict.ProbablyStuck)));
        Assert.Contains(story.Lines, l => l.Text.StartsWith("No sign of work for a couple of minutes") && l.Tone == StoryTone.Bad);
    }

    [Fact]
    public void While_busy_the_log_adds_the_numbers_every_few_seconds_without_hiding_the_freeze_updates()
    {
        var story = new LiveStory();
        story.Update(T0, In(Calm));
        // Idle: no numbers.
        for (int s = 1; s <= 6; s++) story.Update(T0.AddSeconds(s), In(Calm));
        Assert.DoesNotContain(story.Lines, l => l.Text.StartsWith("Working"));

        // Frozen for 22 s, with the numbers available every second.
        for (int s = 10; s <= 32; s++)
            story.Update(T0.AddSeconds(s), In(Frozen(Verdict.Working)) with { Activity = "Working: SOLIDWORKS on 1 core." });
        var working = story.Lines.Where(l => l.Text.StartsWith("Working")).ToList();
        Assert.InRange(working.Count, 5, 8); // about every 3 s
        Assert.Equal(2, story.Lines.Count(l => l.Text.StartsWith("Still not responding"))); // at 20 s and 30 s
    }

    [Fact]
    public void Closing_SOLIDWORKS_is_told_and_the_next_one_starts_fresh()
    {
        var story = new LiveStory();
        story.Update(T0, In(Calm, doc: @"C:\Example\A.SLDPRT"));
        story.Update(T0.AddSeconds(1), new StoryInputs(false, Calm, null, null, null, null, []));
        story.Update(T0.AddSeconds(2), In(Calm, doc: @"C:\Example\A.SLDPRT"));
        Assert.Equal(new[] { "SOLIDWORKS is running. Watching it now.", "Now working in A.SLDPRT.", "SOLIDWORKS closed.",
                             "SOLIDWORKS is running. Watching it now.", "Now working in A.SLDPRT." },
            story.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Issues_put_big_slowdowns_first_then_the_slow_view_then_earlier_catches_and_setup()
    {
        var glaring = new Cause("antivirus", "Windows Defender is scanning Pump.SLDASM", "It held the open for 14 s. More.", Glaring: true,
            Fix: "Ask IT about excluding SOLIDWORKS files.");
        var report = new NowReport(NowLevel.Frozen, "f", [glaring]);
        var sheet = new SheetReport("h", [new Suspect("Section View B-B", 40, ["1,240 parts visible in it", "hidden lines removed in high quality on an assembly"], false, null)], "c", false);
        var caught = new List<Moment> { new(T0.AddMinutes(-10), TimeSpan.FromSeconds(9), "freeze", "SOLIDWORKS stopped responding for 9 s", "What the Companion saw: x. More.", true) };
        var checks = new List<CheckResult>
        {
            new("handles", "object limits", CheckStatus.Look, "GDI limit is 16,384. Recommended 65,536.", "Raise it in the registry. Carefully.", "Microsoft"),
            new("power", "power plan", CheckStatus.Good, "fine", "", "Microsoft"),
        };

        var issues = IssueBoard.Build(T0, report, sheet, caught, checks);
        Assert.Equal(new[]
        {
            "Windows Defender is scanning Pump.SLDASM",
            "Section View B-B is the likely reason the sheet was slow",
            "Earlier (08:50): SOLIDWORKS stopped responding for 9 s",
            "Windows limits how many drawing objects SOLIDWORKS can use",
        }, issues.Select(i => i.Title));
        Assert.Equal(IssueLevel.High, issues[1].Level);
        Assert.StartsWith("Try draft quality", issues[1].Try);
        Assert.Equal("1,240 parts visible in it; hidden lines removed in high quality on an assembly.", issues[1].Why);
        Assert.Equal("Raise it in the registry. Carefully.", issues[3].Try); // a short first step is never left on its own
    }

    [Fact]
    public void Old_catches_drop_off_the_issues_list_and_an_empty_board_is_empty()
    {
        var old = new List<Moment> { new(T0.AddHours(-2), TimeSpan.FromSeconds(9), "freeze", "old freeze", "x", true) };
        Assert.Empty(IssueBoard.Build(T0, Calm, null, old, []));
    }
}
