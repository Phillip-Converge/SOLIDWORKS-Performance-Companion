using SWCompanion.Core.Now;

namespace SWCompanion.Core.Tests;

public class VisualsTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0, DateTimeKind.Local);
    private static readonly Cause Defender = new("antivirus", "Windows Defender is scanning Pump.SLDASM", "e");
    private static readonly Cause Computing = new("computing", "Computing on one core", "e");

    private static PipelineInputs Inputs(NowLevel level, params Cause[] causes) =>
        new(true, level, 1.0, 98, 4_000_000, 0, 0, false, 0.6, causes);

    [Fact]
    public void The_pipeline_marks_the_box_the_strongest_cause_points_at()
    {
        var stages = Pipeline.Build(Inputs(NowLevel.Frozen, Defender, Computing));
        Assert.Equal(new[] { "You", "SOLIDWORKS", "Files", "Network and vault", "Defender" }, stages.Select(s => s.Name));
        Assert.Equal(StageHealth.HoldUp, stages.Single(s => s.Name == "Defender").Health);
        Assert.Equal(StageHealth.Busy, stages.Single(s => s.Name == "SOLIDWORKS").Health); // busy, but not the one holding it up
        Assert.Equal("waiting for SOLIDWORKS", stages[0].Detail);
        Assert.Equal("1 core at 98%: step-by-step work", stages[1].Detail);
        Assert.Equal("reading 4 MB/s", stages[2].Detail);
        Assert.Single(stages, s => s.Health == StageHealth.HoldUp);
    }

    [Fact]
    public void Nothing_is_marked_when_nothing_outside_the_normal_is_happening()
    {
        var stages = Pipeline.Build(new PipelineInputs(true, NowLevel.Calm, 0, 0, 0, 0, 0, false, 0, []));
        Assert.DoesNotContain(stages, s => s.Health == StageHealth.HoldUp);
        Assert.Equal("idle, waiting for you", stages[1].Detail);
        Assert.All(Pipeline.Build(new PipelineInputs(false, NowLevel.NotRunning, null, null, null, null, null, false, null, [])),
            s => Assert.Equal(StageHealth.Idle, s.Health));
    }

    [Fact]
    public void The_timeline_keeps_five_minutes_and_finds_the_frozen_stretches()
    {
        var t = new Timeline();
        for (int s = 0; s < 400; s++) t.Add(new TimePoint(T0.AddSeconds(s), 50, 0, 1, Frozen: s is >= 350 and < 370));
        t.Mark(T0.AddSeconds(10), "too old");
        t.Mark(T0.AddSeconds(349), "Sheet3");
        Assert.InRange(t.Points.Count, 299, 302);
        Assert.Equal("Sheet3", Assert.Single(t.Marks).Label);
        var (from, to) = Assert.Single(t.Frozen());
        Assert.Equal((T0.AddSeconds(350), T0.AddSeconds(370)), (from, to));
    }

    [Fact]
    public void The_wait_is_split_into_its_causes()
    {
        var a = new TimeAccount();
        var frozenDefender = new NowReport(NowLevel.Frozen, "f", [Defender]);
        var frozenComputing = new NowReport(NowLevel.Frozen, "f", [new Cause("setup", "plugged in", "e"), Computing]);
        var frozenNothing = new NowReport(NowLevel.Frozen, "f", []);
        for (int s = 0; s < 12; s++) a.Record(T0.AddSeconds(s), frozenDefender, swBusy: true);
        for (int s = 12; s < 42; s++) a.Record(T0.AddSeconds(s), frozenComputing, swBusy: true);
        for (int s = 42; s < 48; s++) a.Record(T0.AddSeconds(s), frozenNothing, swBusy: true);
        a.Record(T0.AddSeconds(60), new NowReport(NowLevel.Calm, "c", [Computing]), swBusy: false); // idle seconds aren't counted

        var b = a.For("The freeze at 09:00", T0, T0.AddSeconds(47))!;
        Assert.Equal(new[] { "computing on one core", "Windows Defender is scanning Pump.SLDASM", TimeAccount.OwnWork }, b.Shares.Select(s => s.What));
        Assert.Equal(30, b.Shares[0].Seconds, 0);
        Assert.Null(a.For("nothing", T0.AddMinutes(5), T0.AddMinutes(6)));
    }

    [Fact]
    public void The_guide_reassures_while_it_works_says_when_it_looks_stuck_and_offers_fixes()
    {
        VerdictReport V(Verdict v) => new(v, "t", "e", null, "a");
        var working = Guide.Build(true, new NowReport(NowLevel.Frozen, "f", [], V(Verdict.Working)),
            new Breakdown("This freeze so far", 20, [new TimeShare("computing on one core", 20)]), fixes: 0);
        Assert.Equal("Hang on, SOLIDWORKS is still working.", working.Title);
        Assert.Contains("most of the time is going on computing on one core", working.Body);
        Assert.Contains("would lose your unsaved work", working.Body);

        Assert.Equal(Mood.Bad, Guide.Build(true, new NowReport(NowLevel.Frozen, "f", [], V(Verdict.Stuck)), null, 0).Mood);

        var calm = Guide.Build(true, new NowReport(NowLevel.Calm, "c", []), null, fixes: 3);
        Assert.Equal("SOLIDWORKS is running smoothly.", calm.Title);
        Assert.Equal("I found 3 things that could make it run better.", calm.Body);
        Assert.Equal("Let's go through them", calm.Action);

        var afterSlow = Guide.Build(true, new NowReport(NowLevel.Calm, "c", []),
            new Breakdown("Switching to sheet \"Sheet3\"", 48, [new TimeShare("computing on one core", 30)]), fixes: 1);
        Assert.Equal("Switching to sheet \"Sheet3\" took 48 s.", afterSlow.Title);
        Assert.Equal("Show me how to speed it up", afterSlow.Action);

        Assert.Null(Guide.Build(true, new NowReport(NowLevel.Calm, "c", []), null, 0).Action);
    }

    [Fact]
    public void Fix_cards_put_the_biggest_likely_gain_first_with_numbered_steps()
    {
        var issues = new List<Issue>
        {
            new("Windows Search is indexing SOLIDWORKS folders", "x", "Open Indexing Options. Untick the folder. Click OK.", IssueLevel.Medium, "checkup"),
            new("Section View B-B is the likely reason the sheet was slow", "its model took about 31 s to load", "Try draft quality for this view.", IssueLevel.High, "sheet switch"),
            new("No advice", "x", "", IssueLevel.Medium, "caught"),
        };
        var cards = FixCards.From(issues, i => i.Source == "checkup" ? "search-paths" : null);
        Assert.Equal(2, cards.Count); // nothing to do means no card
        Assert.Equal("Section View B-B is the likely reason the sheet was slow", cards[0].Title);
        Assert.Equal("Could save up to about 31 s each time.", cards[0].Gain);
        Assert.Equal(new[] { "Open Indexing Options.", "Untick the folder.", "Click OK." }, cards[1].Steps);
        Assert.Equal("Less background disk work while you're in SOLIDWORKS.", cards[1].Gain);
    }
}
