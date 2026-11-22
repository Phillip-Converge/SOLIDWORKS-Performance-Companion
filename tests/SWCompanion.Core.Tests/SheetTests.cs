using SWCompanion.Contracts;
using SWCompanion.Core.AddIn;

namespace SWCompanion.Core.Tests;

public class SheetTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0, DateTimeKind.Local);
    private const string Drawing = @"C:\Example\Pump Assembly.SLDDRW";

    private static ViewFacts View(string name, string model, int? parts = null, string display = "hidden lines removed",
        bool draft = false, string type = "standard", bool outOfDate = false) =>
        new() { Name = name, Model = model, Components = parts, Display = display, DraftQuality = draft, Type = type, OutOfDate = outOfDate, Visible = true };

    [Fact]
    public void View_facts_survive_the_trip_through_JSON()
    {
        var sent = new AddinMessage
        {
            Kind = AddinMessage.Kinds.ViewTimes, Time = T0, Sheet = "Sheet3",
            Views = [View("Section View B-B", @"C:\Example\Pump.SLDASM", 1240, type: "section") , new ViewFacts { Name = "Drawing View1", LoadSeconds = 1.5, DrawSeconds = 0.25, Lightweight = true, Problem = "a \"quoted\" problem" }],
        };
        var got = AddinListener.Parse(sent.ToJson())!;
        Assert.Equal(2, got.Views!.Count);
        var a = got.Views[0];
        Assert.Equal(("Section View B-B", "section", 1240, "hidden lines removed", false), (a.Name, a.Type, a.Components!.Value, a.Display, a.DraftQuality!.Value));
        var b = got.Views[1];
        Assert.Equal((1.5, 0.25, true, "a \"quoted\" problem"), (b.LoadSeconds!.Value, b.DrawSeconds!.Value, b.Lightweight!.Value, b.Problem));
        Assert.Null(b.Components);
    }

    [Fact]
    public void The_view_whose_big_assembly_loads_slowly_in_high_quality_hidden_lines_comes_first()
    {
        var s = new SheetSwitch { Sheet = "Sheet3", Document = Drawing, Started = T0, Seconds = 45 };
        s.Loads.Add(new ModelLoad(@"C:\Example\Bracket.SLDPRT", 2));
        s.Loads.Add(new ModelLoad(@"C:\Example\Pump.SLDASM", 33));
        s.Views =
        [
            View("Drawing View1", @"C:\Example\Bracket.SLDPRT", display: "hidden lines removed"),
            View("Section View B-B", @"C:\Example\Pump.SLDASM", parts: 1240, type: "section"),
            View("Detail View C", @"C:\Example\Pump.SLDASM", parts: 1240, display: "hidden lines removed", draft: true, type: "detail"),
        ];

        var r = SheetAnalysis.Evidence(s);
        Assert.Contains("Sheet \"Sheet3\" took 45 s", r.Headline);
        Assert.Contains("Most likely holding it up: Section View B-B", r.Headline);
        Assert.False(r.Measured);
        Assert.Contains("not timed per view", r.Caveat);
        Assert.Contains(SheetAnalysis.MenuPath, r.Caveat);

        var top = r.Suspects[0];
        Assert.Contains(top.Reasons, x => x.Contains("about 31 s to load"));
        Assert.Contains(top.Reasons, x => x.Contains("1,240 parts"));
        Assert.Contains(top.Reasons, x => x.Contains("high quality"));
        Assert.Contains(top.Reasons, x => x.Contains("section view"));
        // The draft-quality detail view on the same assembly ranks lower and says why it's lighter.
        var detail = r.Suspects.Single(x => x.View == "Detail View C");
        Assert.Contains(detail.Reasons, x => x.Contains("draft quality"));
    }

    [Fact]
    public void Load_times_are_shared_out_between_views_in_the_order_their_models_finished()
    {
        var s = new SheetSwitch { Sheet = "S", Started = T0, Seconds = 20 };
        s.Loads.Add(new ModelLoad(@"C:\Example\Sub\Gear.SLDPRT", 3));   // a component, not shown in a view directly
        s.Loads.Add(new ModelLoad(@"C:\Example\Frame.SLDASM", 12));
        s.Loads.Add(new ModelLoad(@"C:\Example\Cover.SLDPRT", 15));
        s.Views = [View("A", @"C:\Example\Frame.SLDASM"), View("B", @"D:\Elsewhere\Cover.SLDPRT")]; // same file name, different folder
        var est = SheetAnalysis.EstimateLoadTimes(s);
        Assert.Equal(12, est["A"]);
        Assert.Equal(3, est["B"]);
    }

    [Fact]
    public void A_sheet_with_nothing_heavy_says_so_instead_of_blaming_a_view()
    {
        var s = new SheetSwitch { Sheet = "S", Started = T0, Seconds = 3, Views = [View("A", @"C:\Example\Plate.SLDPRT", display: "shaded")] };
        var r = SheetAnalysis.Evidence(s);
        Assert.Empty(r.Suspects);
        Assert.Contains("None of its views stands out", r.Headline);
    }

    [Fact]
    public void Measured_results_rank_by_real_seconds_and_mention_the_changed_marker()
    {
        var views = new List<ViewFacts>
        {
            new() { Name = "Fast", DrawSeconds = 0.4 },
            new() { Name = "Slow", LoadSeconds = 6, DrawSeconds = 9 },
            new() { Name = "Broken", Problem = "couldn't time this view: example" },
        };
        var r = SheetAnalysis.Measured("Sheet2", views, totalSeconds: 16, markedChanged: true);
        Assert.True(r.Measured);
        Assert.Equal("Slow", r.Suspects[0].View);
        Assert.Equal(15, r.Suspects[0].MeasuredSeconds);
        Assert.Contains("Slow is the slowest view at 15 s (94% of the test)", r.Headline);
        Assert.Contains("didn't change anything", r.Caveat);
        Assert.Null(r.Suspects.Single(x => x.View == "Broken").MeasuredSeconds);
    }

    [Fact]
    public void Tracker_puts_a_slow_sheet_switch_and_its_view_facts_together()
    {
        var t = new AddinTracker();
        t.Apply(new AddinMessage { Kind = AddinMessage.Kinds.SheetStart, Time = T0, Document = Drawing, Sheet = "Sheet3" });
        Assert.Equal("Switching to sheet \"Sheet3\": 10 s so far", t.NowLine(T0.AddSeconds(10)));
        t.Apply(new AddinMessage { Kind = AddinMessage.Kinds.ModelLoad, Time = T0.AddSeconds(30), Document = @"C:\Example\Pump.SLDASM" });
        t.Apply(new AddinMessage { Kind = AddinMessage.Kinds.SheetEnd, Time = T0.AddSeconds(40), Document = Drawing, Sheet = "Sheet3", Seconds = 40 });
        t.Apply(new AddinMessage
        {
            Kind = AddinMessage.Kinds.SheetViews, Time = T0.AddSeconds(41), Document = Drawing, Sheet = "Sheet3", Seconds = 40,
            Views = [View("Section View B-B", @"C:\Example\Pump.SLDASM", 900, type: "section")],
        });

        Assert.Null(t.InProgress);
        Assert.Equal("Switched to sheet \"Sheet3\" in 40 s", AddinTracker.Describe(t.Finished[0]));
        Assert.Equal(30, Assert.Single(t.LastSheet!.Loads).AtSeconds);
        Assert.StartsWith("Sheet \"Sheet3\" took 40 s. Most likely holding it up: Section View B-B", t.SheetReport!.Headline);

        // A quick switch afterwards clears the old report rather than leaving it up.
        t.Apply(new AddinMessage { Kind = AddinMessage.Kinds.SheetStart, Time = T0.AddSeconds(60), Sheet = "Sheet1" });
        t.Apply(new AddinMessage { Kind = AddinMessage.Kinds.SheetEnd, Time = T0.AddSeconds(60.5), Sheet = "Sheet1", Seconds = 0.5 });
        Assert.Null(t.SheetReport);
    }

    [Fact]
    public void Tracker_shows_the_view_test_while_it_runs_and_its_result_after()
    {
        var t = new AddinTracker();
        t.Apply(new AddinMessage { Kind = AddinMessage.Kinds.ViewTestStart, Time = T0, Document = Drawing, Note = "Sheet2" });
        Assert.Equal("Timing each view on sheet \"Sheet2\": 5.0 s so far", t.NowLine(T0.AddSeconds(5)));
        t.Apply(new AddinMessage
        {
            Kind = AddinMessage.Kinds.ViewTimes, Time = T0.AddSeconds(8), Document = Drawing, Sheet = "Sheet2", Seconds = 8,
            Views = [new ViewFacts { Name = "Drawing View3", DrawSeconds = 7 }],
        });
        Assert.Null(t.InProgress);
        Assert.True(t.SheetReport!.Measured);
        Assert.Equal("Timed each view on sheet \"Sheet2\" in 8.0 s", AddinTracker.Describe(t.Finished[0]));
    }
}
