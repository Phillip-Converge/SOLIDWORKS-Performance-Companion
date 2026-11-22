using SWCompanion.Core.AddIn;
using SWCompanion.Core.Now;

namespace SWCompanion.Core.Tests;

public class MomentTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0, DateTimeKind.Local);
    private static readonly Cause Hog = new("other-program", "Another program is busy", "ExampleBackup.exe is using 3 cores.");
    private static readonly Cause Plugged = new("setup", "Also: plugged in", "Running on battery.");

    private static NowReport Calm(params Cause[] causes) => new(NowLevel.Calm, "ok", causes);
    private static NowReport Frozen(params Cause[] causes) => new(NowLevel.Frozen, "frozen", causes);
    private static readonly IReadOnlyList<SwWindow> NoBoxes = [new SwWindow(1, "SOLIDWORKS", "main", true, false)];

    /// <summary>Feeds one report per second from start to start+seconds.</summary>
    private static void Run(MomentCatcher c, int from, int seconds, Func<int, NowReport> report, Func<int, IReadOnlyList<SwWindow>>? windows = null, FreezeEpisode? ep = null)
    {
        for (int s = from; s < from + seconds; s++) c.Observe(T0.AddSeconds(s), report(s), windows?.Invoke(s) ?? NoBoxes, ep);
    }

    [Fact]
    public void Another_program_hogging_the_processor_for_a_few_seconds_is_caught_after_it_goes()
    {
        var c = new MomentCatcher();
        Run(c, 0, 6, _ => Calm(Hog, Plugged));
        Assert.Empty(c.Caught); // still going: nothing yet
        Run(c, 6, 1, _ => Calm(Plugged));

        var m = Assert.Single(c.Caught); // the always-there setup note is never "caught"
        Assert.Equal("Another program is busy (came and went)", m.Title);
        Assert.Equal(TimeSpan.FromSeconds(6), m.Duration);
        Assert.True(m.LookInto);
        Assert.Equal(1, c.Unseen);
        Assert.Contains("ExampleBackup.exe", m.Detail);
    }

    [Fact]
    public void A_one_second_blip_is_ignored_and_a_short_one_is_listed_but_not_flagged()
    {
        var c = new MomentCatcher();
        Run(c, 0, 1, _ => Calm(Hog));
        Run(c, 1, 1, _ => Calm());
        Assert.Empty(c.Caught);

        Run(c, 10, 3, _ => Calm(Hog));
        Run(c, 13, 1, _ => Calm());
        Assert.False(Assert.Single(c.Caught).LookInto);
        Assert.Equal(0, c.Unseen);
    }

    [Fact]
    public void A_box_that_popped_up_during_a_freeze_and_closed_is_flagged()
    {
        var c = new MomentCatcher();
        var withBox = new List<SwWindow>(NoBoxes) { new(2, "SOLIDWORKS", "#32770", false, true, Responding: true) };
        Run(c, 0, 4, _ => Frozen(), _ => withBox);
        Run(c, 4, 1, _ => Calm(), _ => NoBoxes);

        var m = Assert.Single(c.Caught);
        Assert.Equal("box", m.Kind);
        Assert.Contains("busy running a command", m.Title);
        Assert.Contains("while SOLIDWORKS wasn't responding", m.Detail);
        Assert.Contains("can't tell whether it closed by itself", m.Detail);
        Assert.True(m.LookInto);
    }

    [Fact]
    public void Untitled_windows_like_menus_are_not_listed_unless_they_came_during_a_freeze()
    {
        var c = new MomentCatcher();
        var withMenu = new List<SwWindow>(NoBoxes) { new(3, "", "#32768", false, true) };
        Run(c, 0, 5, _ => Calm(), _ => withMenu);
        Run(c, 5, 1, _ => Calm(), _ => NoBoxes);
        Assert.Empty(c.Caught);

        Run(c, 10, 3, _ => Frozen(), _ => withMenu);
        Run(c, 13, 1, _ => Calm(), _ => NoBoxes);
        Assert.Equal("A SOLIDWORKS window with no title popped up and closed", Assert.Single(c.Caught).Title);
    }

    [Fact]
    public void Causes_seen_only_during_a_freeze_are_left_to_the_freeze_summary()
    {
        var c = new MomentCatcher();
        var computing = new Cause("computing", "Computing on one core", "...");
        Run(c, 0, 8, _ => Frozen(computing));
        var episode = new FreezeEpisode(T0, TimeSpan.FromSeconds(8), [("Computing on one core", TimeSpan.FromSeconds(8))], []);
        Run(c, 8, 1, _ => Calm(), ep: episode);

        var m = Assert.Single(c.Caught);
        Assert.Equal("freeze", m.Kind);
        Assert.Equal("SOLIDWORKS stopped responding for 8 s", m.Title);
        Assert.StartsWith("What the Companion saw: 8 s at 09:00:00: 8 s computing on one core", m.Detail);
        Assert.True(m.LookInto);

        Run(c, 9, 3, _ => Calm(), ep: episode); // the same freeze isn't caught twice
        Assert.Single(c.Caught);
    }

    [Fact]
    public void Slow_actions_from_inside_SOLIDWORKS_are_caught_and_quick_ones_are_not()
    {
        var c = new MomentCatcher();
        c.AddAction(new AddinAction("sheet", @"C:\Example\Pump.SLDDRW", "Sheet3", T0, 0.4));
        c.AddAction(new AddinAction("open", @"C:\Example\Pump.SLDASM", null, T0, 4));
        Assert.Empty(c.Caught);

        c.AddAction(new AddinAction("sheet", @"C:\Example\Pump.SLDDRW", "Sheet3", T0, 12));
        var m = Assert.Single(c.Caught);
        Assert.Equal("Switched to sheet \"Sheet3\" in 12 s", m.Title);
        Assert.Contains("time each view", m.Detail);
        Assert.True(m.LookInto);
    }

    [Fact]
    public void Moments_are_saved_and_come_back_after_a_restart_as_already_seen()
    {
        var folder = Path.Combine(Path.GetTempPath(), "swc-moment-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MomentStore([folder]);
            var c = new MomentCatcher();
            c.NewMoment += store.Save;
            c.AddAction(new AddinAction("save", @"C:\Example\Frame.SLDASM", null, T0, 9.5));

            var again = new MomentCatcher();
            again.Restore(store.LoadDay(T0));
            var m = Assert.Single(again.Caught);
            Assert.Equal(("slow-save", "Saved Frame.SLDASM in 9.5 s", true), (m.Kind, m.Title, m.LookInto));
            Assert.Equal(9.5, m.Duration.TotalSeconds, 3);
            Assert.Equal(0, again.Unseen);
            Assert.Empty(new MomentStore([folder]).LoadDay(T0.AddDays(1)));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Marking_seen_clears_the_count_but_keeps_the_list()
    {
        var c = new MomentCatcher();
        c.AddAction(new AddinAction("open", @"C:\Example\Big.SLDASM", null, T0, 30));
        c.MarkSeen();
        Assert.Equal(0, c.Unseen);
        Assert.Single(c.Caught);
    }
}
