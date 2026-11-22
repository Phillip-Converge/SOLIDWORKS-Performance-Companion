using SWCompanion.Core.Now;

namespace SWCompanion.Core.Tests;

public class LoadTimesTests
{
    private static readonly DateTime T = new(2026, 3, 4, 10, 0, 0);
    private static readonly IntPtr Box = new(101);

    private static LoadTiming Window(double seconds, string title = "Open") =>
        new(T, T.AddSeconds(seconds), title, IsWindow: true, FromClick: true, "Open");

    private static Dictionary<IntPtr, bool?> Answers(bool? box) => new() { [Box] = box };

    [Fact]
    public void FirstTimesUseTheTwoSecondLimit()
    {
        var h = new LoadHistory();
        Assert.False(h.Judge(Window(1.5)).Slow);
        Assert.True(h.Judge(Window(2.5)).Slow);
    }

    [Fact]
    public void AfterThreeTimingsSlowMeansTwiceUsualAndASecondMore()
    {
        var h = new LoadHistory();
        foreach (var s in new[] { 0.4, 0.5, 0.6 }) h.Judge(Window(s));
        Assert.False(h.Judge(Window(1.4)).Slow);           // more than twice 0.5, but not a second more
        var v = h.Judge(Window(1.6));
        Assert.True(v.Slow);
        Assert.Contains("usually", v.Reason);
    }

    [Fact]
    public void AWindowThatIsAlwaysSlowIsNormalOnceLearned()
    {
        var h = new LoadHistory();
        foreach (var s in new[] { 4.0, 4.2, 3.9 }) h.Judge(Window(s));
        Assert.False(h.Judge(Window(4.5)).Slow);
    }

    [Fact]
    public void TheSameBoxForDifferentFilesSharesOneNormal()
    {
        Assert.Equal("Properties", LoadHistory.Normalize("Properties - Bracket.SLDPRT"));
        Assert.Equal(Window(1, "Properties - A.SLDPRT").Key, Window(1, "Properties - B.SLDPRT").Key);
    }

    [Fact]
    public void HistoryIsSavedAndReadBack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"load-times-test-{Guid.NewGuid():N}.json");
        try
        {
            var h = new LoadHistory(path);
            foreach (var s in new[] { 0.4, 0.5, 0.6 }) h.Judge(Window(s));
            Assert.True(new LoadHistory(path).Judge(Window(1.6)).Slow); // learned before the restart
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WindowIsTimedFromTheClickUntilItAnswers()
    {
        var t = new LoadTracker();
        t.CommandStarted(T, "Open");
        t.WindowCreated(T.AddSeconds(0.5), Box);
        t.WindowShown(T.AddSeconds(2.5), Box, "Open");
        Assert.Empty(t.Check(T.AddSeconds(2.6), Answers(false), true));   // still filling itself in
        var done = Assert.Single(t.Check(T.AddSeconds(3.0), Answers(true), true));
        Assert.Equal(3.0, done.Seconds, 2);
        Assert.True(done.FromClick);
        Assert.Equal("Open", done.Command);
    }

    [Fact]
    public void WaitingForYouAfterItIsReadyIsNeverCounted()
    {
        var t = new LoadTracker();
        t.CommandStarted(T, "Options");
        t.WindowCreated(T.AddSeconds(0.1), Box);
        t.WindowShown(T.AddSeconds(0.2), Box, "System Options");
        var done = Assert.Single(t.Check(T.AddSeconds(0.3), Answers(true), true));
        Assert.Equal(0.3, done.Seconds, 2);
        // A minute later you're still choosing: nothing more is timed.
        Assert.Empty(t.Check(T.AddMinutes(1), Answers(true), true));
        Assert.Empty(t.Waiting);
    }

    [Fact]
    public void WithoutAClickItIsTimedFromWhenTheWindowWasCreated()
    {
        var t = new LoadTracker();
        t.WindowCreated(T, Box);
        t.WindowShown(T.AddSeconds(1), Box, "Find References");
        var done = Assert.Single(t.Check(T.AddSeconds(1.5), Answers(true), true));
        Assert.False(done.FromClick);
        Assert.Equal(1.5, done.Seconds, 2);
    }

    [Fact]
    public void ACommandWithNoWindowIsTimedUntilSolidWorksAnswersAgain()
    {
        var t = new LoadTracker();
        t.CommandStarted(T, "Fillet");
        Assert.True(t.WaitingOnMain);
        Assert.Empty(t.Check(T.AddSeconds(1), new Dictionary<IntPtr, bool?>(), false));
        var done = Assert.Single(t.Check(T.AddSeconds(2.2), new Dictionary<IntPtr, bool?>(), true));
        Assert.False(done.IsWindow);
        Assert.Equal("command:Fillet", done.Key);
        Assert.False(t.WaitingOnMain);
    }

    [Fact]
    public void AWindowClosedBeforeItWasReadyIsDropped()
    {
        var t = new LoadTracker();
        t.WindowCreated(T, Box);
        t.WindowShown(T, Box, "Rebuilding");
        Assert.Empty(t.Check(T.AddSeconds(1), Answers(null), true));
        Assert.Empty(t.Waiting);
    }

    [Fact]
    public void UntitledWindowsAreIgnored()
    {
        var t = new LoadTracker();
        t.WindowShown(T, Box, "");
        Assert.Empty(t.Waiting);
    }

    [Fact]
    public void SlowLoadHasItsOwnExplanationAndMoreDetail()
    {
        var x = HangExplainer.For(new Moment(T, TimeSpan.FromSeconds(4), "slow-load", "The \"Open\" window took 4.0 s to be ready", "Detail.", true), []);
        Assert.Contains("longer than it normally does", x.WhatHappened);
        Assert.NotEmpty(x.More);
        Assert.Contains(x.Technical, l => l.StartsWith("Kind of record: slow-load"));
    }
}
