using SWCompanion.Core.Ask;

namespace SWCompanion.Core.Tests;

public class AskTests
{
    [Fact]
    public void FileNamesBecomeNumberedPlaceholdersKeepingTheType()
    {
        var s = new Scrubber();
        string t = s.Hide("Opened Bracket_V2-A.SLDPRT in 12 s, then Bracket_V2-A.SLDPRT again and Housing.SLDASM.");
        Assert.DoesNotContain("Bracket", t);
        Assert.DoesNotContain("Housing", t);
        Assert.Contains("file 1.SLDPRT in 12 s, then file 1.SLDPRT again and file 2.SLDASM", t);
    }

    [Fact]
    public void QuotedNamesWithSpacesAreHiddenWhole()
    {
        var s = new Scrubber();
        string t = s.Hide("Switching to \"Main Frame Assembly.SLDDRW\" took 8 s");
        Assert.Equal("Switching to \"file 1.SLDDRW\" took 8 s", t);
    }

    [Fact]
    public void FoldersServersAndAddressesAreHidden()
    {
        var s = new Scrubber();
        string t = s.Hide(@"Loading \\fileserver01\Engineering\Projects\Widget.SLDPRT from C:\Users\someone\Documents\ via vault.example.com (10.1.2.3)");
        Assert.DoesNotContain("fileserver01", t);
        Assert.DoesNotContain("Engineering", t);
        Assert.DoesNotContain("someone", t);
        Assert.DoesNotContain("example.com", t);
        Assert.DoesNotContain("10.1.2.3", t);
        Assert.Contains("folder 1\\file 1.SLDPRT", t);
        Assert.Contains("server A", t);
        Assert.Contains("server B", t);
    }

    [Fact]
    public void ThisPcTheUserAndEmailsAreHidden()
    {
        var s = new Scrubber(pcName: "WS-ENG-042", userName: "jdoe", otherNames: ["Example Corp"]);
        string t = s.Hide("WS-ENG-042 is signed in as jdoe; contact someone@example.org at Example Corp.");
        Assert.Equal("this PC is signed in as the user; contact [email] at private name 1.", t);
    }

    [Fact]
    public void OrdinaryWordsAndVersionsAreLeftAlone()
    {
        var s = new Scrubber();
        const string text = "SOLIDWORKS 2026 SP5.2 stopped responding for 7 s. Defender used 1.1 cores. Module example.dll.";
        Assert.Equal(text, s.Hide(text));
    }

    [Fact]
    public void VersionNumbersAreNotMistakenForAddresses()
    {
        var s = new Scrubber();
        const string text = "Running SOLIDWORKS 2026 SP5.2 (version 34.5.2.604), driver v31.0.15.5222, module 10.0.26100.1.";
        Assert.Equal(text, s.Hide(text));
        Assert.Equal("ping server A", s.Hide("ping 192.168.1.20"));
    }

    [Fact]
    public void TheAnswerGetsTheRealNamesBack()
    {
        var s = new Scrubber();
        s.Hide("Opened Bracket.SLDPRT and Cover.SLDPRT ... and 10 more: A1.SLDPRT A2.SLDPRT A3.SLDPRT A4.SLDPRT A5.SLDPRT A6.SLDPRT A7.SLDPRT A8.SLDPRT A9.SLDPRT A10.SLDPRT");
        Assert.Equal("Check A10.SLDPRT first, then Bracket.SLDPRT.", s.Restore("Check file 12.SLDPRT first, then file 1.SLDPRT."));
    }

    [Fact]
    public void TheMessageHoldsTheFactsWithNamesHidden()
    {
        var s = new Scrubber(pcName: "WS-ENG-042");
        var ctx = new AskContext("SOLIDWORKS 2026 SP5.2", "Not responding for 12 s", "CPU 12%, memory 18 of 32 GB",
            ["13:01:02  Opening Gearbox.SLDASM", "13:01:05  WS-ENG-042 disk busy"], ["SOLIDWORKS stopped responding for 12 s"],
            ["Power plan is Balanced"], "SOLIDWORKS stopped responding for 12 s. What the Companion saw: opening Gearbox.SLDASM.");
        var r = AskPrompt.Build("Why did it freeze?", ctx, s);
        string all = r.AsText();
        Assert.Contains("Why did it freeze?", all);
        Assert.Contains("file 1.SLDASM", all);
        Assert.DoesNotContain("Gearbox", all);
        Assert.DoesNotContain("WS-ENG-042", all);
        Assert.Contains("Power plan is Balanced", all);
        Assert.Contains("placeholders", r.System);
    }

    [Fact]
    public void OnlyTheLastFewEarlierQuestionsAreSent()
    {
        var earlier = Enumerable.Range(1, 6).Select(i => new AskTurn($"q{i}", $"a{i}")).ToList();
        var r = AskPrompt.Build("next?", new AskContext(null, "ok", "-", [], [], [], null), new Scrubber(), earlier);
        Assert.Equal(AskPrompt.MaxEarlier, r.Earlier.Count);
        Assert.Equal("q6", r.Earlier[^1].Question);
    }
}
