using SWCompanion.Core.Ask;
using SWCompanion.Core.Now;

namespace SWCompanion.Core.Tests;

public class DeveloperReportTests
{
    private static ReportContext Sample(bool unexplained = true, IReadOnlyList<string>? log = null) => new(
        "SOLIDWORKS stopped responding",
        unexplained,
        "0.1.1",
        "SOLIDWORKS 2026 SP5.2",
        "Windows 11 (build 26100)",
        ["Opening \"Main Frame Assembly.SLDASM\" took 40 s"],
        [@"What was recorded: reading \\fileserver01\Engineering\Widget.SLDPRT on EXAMPLE-PC for someuser"],
        log ?? ["10:00:01  Opening Widget.SLDPRT", "10:00:41  SOLIDWORKS answered again"]);

    [Fact]
    public void PrivateNamesAreHiddenInSubjectAndBody()
    {
        var r = DeveloperReport.Build(Sample() with { About = "Slow open of Widget.SLDPRT" },
            new Scrubber("EXAMPLE-PC", "someuser"));
        string all = r.Subject + r.Body;
        Assert.DoesNotContain("Widget", all);
        Assert.DoesNotContain("Main Frame", all);
        Assert.DoesNotContain("fileserver01", all);
        Assert.DoesNotContain("Engineering", all);
        Assert.DoesNotContain("EXAMPLE-PC", all);
        Assert.DoesNotContain("someuser", all);
        Assert.Contains("file 1.SLDPRT", r.Subject);
        Assert.Contains("this PC", r.Body);
        Assert.Contains("the user", r.Body);
    }

    [Fact]
    public void ReportStartsWithTheUsersNoteAndHasVersions()
    {
        var r = DeveloperReport.Build(Sample(), new Scrubber());
        Assert.StartsWith("What happened, in my words:", r.Body);
        Assert.Contains(DeveloperReport.NotePlaceholder, r.Body);
        Assert.Contains("Companion version: 0.1.1", r.Body);
        Assert.Contains("SOLIDWORKS: SOLIDWORKS 2026 SP5.2", r.Body);
        Assert.Contains("Windows: Windows 11", r.Body);
    }

    [Fact]
    public void UnexplainedItemsAreMarkedInSubjectAndBody()
    {
        var r = DeveloperReport.Build(Sample(unexplained: true), new Scrubber());
        Assert.Contains("not explained", r.Subject);
        Assert.Contains("no specific explanation", r.Body);
        var known = DeveloperReport.Build(Sample(unexplained: false), new Scrubber());
        Assert.DoesNotContain("not explained", known.Subject);
        Assert.DoesNotContain("no specific explanation", known.Body);
    }

    [Fact]
    public void GeneralReportHasNoItemButStillWorks()
    {
        var r = DeveloperReport.Build(new ReportContext(null, false, "0.1.1", null, "Windows 11", [], [], []), new Scrubber());
        Assert.Equal("Companion for SOLIDWORKS report: a problem", r.Subject);
        Assert.Contains("SOLIDWORKS: not detected", r.Body);
        Assert.Contains("no longer in memory", r.Body);
    }

    [Fact]
    public void LogIsLimitedToTheLatestLines()
    {
        var log = Enumerable.Range(1, 100).Select(i => $"line {i:000}").ToList();
        var r = DeveloperReport.Build(Sample(log: log), new Scrubber());
        Assert.DoesNotContain("line 060", r.Body);
        Assert.Contains("line 061", r.Body);
        Assert.Contains("line 100", r.Body);
    }

    [Fact]
    public void ShortReportFitsInTheMailLink()
    {
        var (link, fits) = DeveloperReport.MailLink("someone@example.com", "Subject here", "Line one\nLine two");
        Assert.True(fits);
        Assert.StartsWith("mailto:someone@example.com?subject=Subject%20here&body=", link);
        Assert.Contains("Line%20one%0D%0ALine%20two", link);
    }

    [Fact]
    public void LongReportIsReplacedByAPasteLine()
    {
        var (link, fits) = DeveloperReport.MailLink("someone@example.com", "Subject", new string('x', 5000));
        Assert.False(fits);
        Assert.True(link.Length <= DeveloperReport.MaxMailtoLength);
        Assert.Contains(Uri.EscapeDataString(DeveloperReport.PasteLine), link);
    }

    [Fact]
    public void ExplanationsKnowWhenTheyAreOnlyGeneral()
    {
        var unknown = HangExplainer.For(new Moment(DateTime.Now, TimeSpan.FromSeconds(3), "something-new", "Something new", "", true), []);
        Assert.False(unknown.Specific);
        var freeze = HangExplainer.For(new Moment(DateTime.Now, TimeSpan.FromSeconds(12), "freeze", "SOLIDWORKS stopped responding", "", true), []);
        Assert.True(freeze.Specific);
    }
}
