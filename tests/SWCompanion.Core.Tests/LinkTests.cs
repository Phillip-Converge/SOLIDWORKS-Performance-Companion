using SWCompanion.Link;

namespace SWCompanion.Core.Tests;

public class LinkTests
{
    [Fact]
    public void Connecting_to_a_SOLIDWORKS_that_isnt_there_explains_itself_and_never_starts_one()
    {
        SolidWorksLink.KeepDiary = false; // never write test lines into the real diary
        int before = System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length;
        using var link = new SolidWorksLink();
        link.Attach(999_999); // no such process
        var end = DateTime.UtcNow.AddSeconds(10);
        while (link.Problem is null && DateTime.UtcNow < end) Thread.Sleep(50);

        Assert.Null(link.ConnectedTo);
        Assert.False(string.IsNullOrWhiteSpace(link.Problem));
        Assert.Empty(link.Drain());
        Assert.Equal(before, System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length);
    }

    [Fact]
    public void The_view_test_warning_says_what_happens_and_lets_you_say_no()
    {
        Assert.Contains("doesn't change or save anything", SolidWorksLink.ViewTestWarning);
        Assert.Contains("Carry on?", SolidWorksLink.ViewTestWarning);
    }
}
