using SWCompanion.Contracts;
using SWCompanion.Core.AddIn;

namespace SWCompanion.Core.Tests;

public class AddinTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0, DateTimeKind.Local);

    [Fact]
    public void A_message_survives_the_trip_through_JSON_including_awkward_characters()
    {
        var sent = new AddinMessage
        {
            Kind = AddinMessage.Kinds.OpenEnd,
            Time = new DateTime(2026, 1, 5, 14, 3, 7, 250, DateTimeKind.Utc),
            Document = @"C:\Example Projects\Pump ""A""\Housing\tab	and\newline.SLDASM".Replace("\\newline", "\nnewline"),
            DocType = "assembly",
            Seconds = 12.345,
            Note = "ünïcode ✓",
        };
        string json = sent.ToJson();
        Assert.DoesNotContain('\n', json); // one message per line

        var got = AddinListener.Parse(json)!;
        Assert.Equal(sent.Kind, got.Kind);
        Assert.Equal(sent.Time, got.Time.ToUniversalTime());
        Assert.Equal(sent.Document, got.Document);
        Assert.Equal("assembly", got.DocType);
        Assert.Equal(12.345, got.Seconds!.Value, 3);
        Assert.Equal(sent.Note, got.Note);
        Assert.Null(got.Sheet);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"time\":\"2026-01-05T00:00:00Z\"}")]
    public void Garbage_is_ignored_not_crashed_on(string line) => Assert.Null(AddinListener.Parse(line));

    [Fact]
    public void Tracker_times_an_open_and_reports_it_in_plain_words()
    {
        var t = new AddinTracker();
        t.Apply(Msg(AddinMessage.Kinds.OpenStart, @"C:\Example\Bracket.SLDASM", T0));
        Assert.Equal("Opening Bracket.SLDASM: 14 s so far", t.NowLine(T0.AddSeconds(14)));

        t.Apply(Msg(AddinMessage.Kinds.OpenEnd, @"C:\Example\Bracket.SLDASM", T0.AddSeconds(4.2), seconds: 4.2));
        Assert.Null(t.InProgress);
        Assert.Null(t.NowLine(T0.AddSeconds(20)));
        Assert.Equal("Opened Bracket.SLDASM in 4.2 s", AddinTracker.Describe(Assert.Single(t.Finished)));
    }

    [Fact]
    public void Tracker_follows_the_active_document_and_forgets_it_on_close()
    {
        var t = new AddinTracker();
        t.Apply(Msg(AddinMessage.Kinds.ActiveDocument, @"C:\Example\Sheet Metal Cover.SLDDRW", T0, "drawing"));
        Assert.Equal("drawing", t.ActiveDocType);
        t.Apply(Msg(AddinMessage.Kinds.Close, @"c:\example\sheet metal cover.slddrw", T0));
        Assert.Null(t.ActiveDocument);
    }

    [Fact]
    public void Tracker_keeps_only_the_most_recent_actions()
    {
        var t = new AddinTracker();
        for (int i = 0; i < AddinTracker.Keep + 5; i++)
            t.Apply(Msg(AddinMessage.Kinds.OpenEnd, $@"C:\Example\Part{i}.SLDPRT", T0.AddSeconds(i), seconds: 1));
        Assert.Equal(AddinTracker.Keep, t.Finished.Count);
        Assert.Equal($"Part{AddinTracker.Keep + 4}.SLDPRT", t.Finished[0].FileName); // newest first
    }

    [Theory]
    [InlineData(4.24, "4.2 s")]
    [InlineData(42, "42 s")]
    [InlineData(185, "3 min 05 s")]
    public void Clock_reads_naturally(double seconds, string expected) => Assert.Equal(expected, AddinTracker.Clock(seconds));

    [Fact]
    public void Explanation_says_how_we_hear_from_SOLIDWORKS_and_why_not_when_we_dont()
    {
        Assert.Contains("add-in", AddinTracker.Explain(true, AddinTracker.Line.AddIn));
        Assert.Contains("from outside (no add-in needed)", AddinTracker.Explain(true, AddinTracker.Line.Outside));
        Assert.All(new[] { AddinTracker.Line.AddIn, AddinTracker.Line.Outside },
            l => Assert.Contains("never changes", AddinTracker.Explain(true, l)));
        // When not connected, the reason the link gave is passed on as is.
        Assert.StartsWith("Example reason.", AddinTracker.Explain(true, AddinTracker.Line.None, "Example reason."));
        Assert.Contains("keeps trying", AddinTracker.Explain(true, AddinTracker.Line.None));
        Assert.Contains("No add-in is needed", AddinTracker.Explain(false, AddinTracker.Line.None));
    }

    [Fact]
    public void Without_the_add_in_the_report_points_to_the_Companion_button_instead_of_the_menu()
    {
        var t = new AddinTracker { HowToMeasure = SheetAnalysis.ButtonHint };
        t.Apply(Msg(AddinMessage.Kinds.SheetStart, @"C:\Example\Frame.SLDDRW", T0, sheet: "Sheet2"));
        t.Apply(new AddinMessage { Kind = AddinMessage.Kinds.SheetEnd, Time = T0.AddSeconds(5), Sheet = "Sheet2", Seconds = 5 });
        t.Apply(new AddinMessage { Kind = AddinMessage.Kinds.SheetViews, Time = T0.AddSeconds(6), Sheet = "Sheet2", Seconds = 5,
            Views = [new ViewFacts { Name = "Drawing View1", Components = 800, Display = "hidden lines removed", DraftQuality = false, Visible = true }] });
        Assert.Contains("time each view\" in section 08", t.SheetReport!.Caveat);
        Assert.DoesNotContain("Tools >", t.SheetReport.Caveat);
    }

    [Fact]
    public void Notes_travel_from_the_add_in_out_tray_to_the_Companion_over_a_real_pipe()
    {
        // A made-up "process ID" so this never clashes with a real SOLIDWORKS on the PC.
        int fakePid = 900_000 + Random.Shared.Next(99_999);
        using var outbox = new AddinOutbox(AddinPipe.NameFor(fakePid));
        // Sent before anyone is listening: should be replayed when the Companion connects.
        outbox.Post(Msg(AddinMessage.Kinds.ActiveDocument, @"C:\Example\Frame.SLDASM", T0, "assembly"));

        using var listener = new AddinListener(retry: TimeSpan.FromMilliseconds(100));
        listener.Watch([fakePid]);
        Assert.True(WaitFor(() => listener.ConnectedTo.Contains(fakePid)), "listener should connect");

        outbox.Post(Msg(AddinMessage.Kinds.OpenStart, @"C:\Example\Frame.SLDASM", T0));
        outbox.Post(Msg(AddinMessage.Kinds.OpenEnd, @"C:\Example\Frame.SLDASM", T0, seconds: 2.5));

        var got = new List<AddinMessage>();
        Assert.True(WaitFor(() => { got.AddRange(listener.Drain().Select(x => x.Message)); return got.Count >= 3; }), $"got {got.Count} of 3");
        Assert.Equal([AddinMessage.Kinds.ActiveDocument, AddinMessage.Kinds.OpenStart, AddinMessage.Kinds.OpenEnd], got.Select(m => m.Kind));
        Assert.Equal(2.5, got[2].Seconds);
    }

    [Fact]
    public void The_out_tray_never_blocks_when_no_one_is_listening()
    {
        using var outbox = new AddinOutbox(AddinPipe.NameFor(900_000 + Random.Shared.Next(99_999)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 5_000; i++) outbox.Post(Msg(AddinMessage.Kinds.OpenEnd, $@"C:\Example\P{i}.SLDPRT", T0, seconds: 0.1));
        Assert.True(sw.ElapsedMilliseconds < 2_000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.False(outbox.Connected);
    }

    private static AddinMessage Msg(string kind, string doc, DateTime time, string? type = null, double? seconds = null, string? sheet = null) =>
        new() { Kind = kind, Document = doc, Time = time, DocType = type, Seconds = seconds, Sheet = sheet };


    private static bool WaitFor(Func<bool> condition, int ms = 5_000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }
}
