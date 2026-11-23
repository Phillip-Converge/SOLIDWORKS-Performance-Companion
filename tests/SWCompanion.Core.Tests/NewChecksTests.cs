using SWCompanion.Core.Checkup;
using SWCompanion.Core.Network;
using SWCompanion.Core.Now;
using SWCompanion.Core.Pdm;

namespace SWCompanion.Core.Tests;

/// <summary>Symbol library, free space, licence server and slow drawing saves.</summary>
public class NewChecksTests
{
    // ---- symbol library (gtol.sym) ----

    private static ChecklistFacts Facts(IReadOnlyList<(string, bool)>? symbols, string? foundAt = null, string? year = "2026") =>
        new(1, 0, 0, [], year, [new FileLocationEntry("Symbol Library Folders", @"C:\Symbols", true, false)], false, [], symbols, foundAt);

    private static CheckResult Symbol(ChecklistFacts f) => ChecklistEvaluator.Evaluate(f).Single(c => c.Id == "symbol-library");

    [Fact]
    public void SymbolLibraryWithGtolIsGood() =>
        Assert.Equal(CheckStatus.Good, Symbol(Facts([(@"C:\Symbols", true)])).Status);

    [Fact]
    public void SymbolLibraryWithoutGtolSaysWhereItIs()
    {
        var c = Symbol(Facts([(@"C:\Old\lang\english", false)], foundAt: @"C:\ProgramData\SolidWorks\SOLIDWORKS 2026\lang\english"));
        Assert.Equal(CheckStatus.Look, c.Status);
        Assert.Contains("<MOD-DIAM>", c.Finding);
        Assert.Contains(@"C:\Old\lang\english", c.Finding);
        Assert.Contains(@"On this PC it is in C:\ProgramData\SolidWorks\SOLIDWORKS 2026\lang\english", c.HowToFix);
        Assert.Contains("Symbol Library File", c.HowToFix);
    }

    [Fact]
    public void SymbolLibraryNotSetIsWorthALook() =>
        Assert.Equal(CheckStatus.Look, Symbol(Facts([])).Status);

    [Fact]
    public void SymbolLibraryUnreadableIsUnknown()
    {
        Assert.Equal(CheckStatus.Unknown, Symbol(Facts(null)).Status);
        Assert.Equal(CheckStatus.Unknown, Symbol(Facts([(@"C:\Symbols", true)], year: null)).Status);
    }

    [Fact]
    public void SymbolLibraryWithoutKnownLocationGivesTheUsualPlace() =>
        Assert.Contains(@"ProgramData\SOLIDWORKS\SOLIDWORKS 2026\lang\english", Symbol(Facts([(@"C:\X", false)])).HowToFix);

    // ---- free space on the Windows drive ----

    private static CheckResult Space(double? free, double? total = 500) =>
        CheckupEvaluator.Evaluate(new CheckupFacts(null, null, null, null, null, false, false, null, null, null, null, null, true, null, null,
            "C:", free, total), null).Single(c => c.Id == "free-space");

    [Theory]
    [InlineData(5.0, CheckStatus.Problem)]
    [InlineData(9.9, CheckStatus.Problem)]
    [InlineData(10.0, CheckStatus.Look)]
    [InlineData(19.9, CheckStatus.Look)]
    [InlineData(20.0, CheckStatus.Good)]
    [InlineData(250.0, CheckStatus.Good)]
    public void FreeSpaceThresholds(double free, CheckStatus expected) => Assert.Equal(expected, Space(free).Status);

    [Fact]
    public void FreeSpaceSaysTheAmountAndHowToFix()
    {
        var c = Space(7.25, 476);
        Assert.Contains("C: has 7.3 GB free of 476 GB", c.Finding);
        Assert.Contains("Disk Cleanup", c.HowToFix);
        Assert.Contains("keep the current one", c.HowToFix);
        Assert.Contains("checking in your files", c.HowToFix);
    }

    [Fact]
    public void FreeSpaceUnreadableIsUnknown() => Assert.Equal(CheckStatus.Unknown, Space(null, null).Status);

    // ---- licence server ----

    [Fact]
    public void LicenceSettingIsParsedIntoServers()
    {
        var s = LicenseServers.Parse("25734@lic-server; 27000@backup.example.local ;@third;C:\\licences\\sw.lic");
        Assert.Equal(3, s.Count);
        Assert.Equal(new LicenseServer("lic-server", 25734), s[0]);
        Assert.Equal(new LicenseServer("backup.example.local", 27000), s[1]);
        Assert.Equal(new LicenseServer("third", LicenseServers.ManagerPort), s[2]);
    }

    [Fact]
    public void NullPaddingAfterTheSettingIsIgnored()
    {
        var s = LicenseServers.Parse("25734@lic-server;25734@localhost\0\0\0\0   \0");
        Assert.Equal(2, s.Count);
        Assert.Equal("localhost", s[1].Host);
    }

    [Fact]
    public void EmptyLicenceSettingMeansNoServer()
    {
        Assert.Empty(LicenseServers.Parse(null));
        Assert.Empty(LicenseServers.Parse("  "));
        Assert.Empty(LicenseServers.Parse("not-a-server"));
    }

    private static PortTest T(string role, int port, PortResult r, double? ms = 12) => new(role, "lic-server", port, r, r == PortResult.Open ? ms : null, DateTime.Now);

    [Fact]
    public void BothPortsOpenLooksFine()
    {
        var line = Assert.Single(LicenseServers.Explain([T("licence manager", 25734, PortResult.Open), T("vendor daemon", 25735, PortResult.Open)]));
        Assert.Contains("looks fine", line);
        Assert.Contains("12 ms", line);
    }

    [Fact]
    public void VendorPortClosedIsExplainedCarefully()
    {
        var line = Assert.Single(LicenseServers.Explain([T("licence manager", 25734, PortResult.Open), T("vendor daemon", 25735, PortResult.TimedOut)]));
        Assert.Contains("port 25735 didn't", line);
        Assert.Contains("That's fine if", line);
    }

    [Theory]
    [InlineData(PortResult.NameNotFound, "couldn't find a computer")]
    [InlineData(PortResult.Refused, "nothing is listening")]
    [InlineData(PortResult.TimedOut, "no answer")]
    public void ManagerPortProblemsAreExplained(PortResult result, string expected) =>
        Assert.Contains(expected, Assert.Single(LicenseServers.Explain([T("licence manager", 25734, result)])));

    // ---- slow drawing saves ----

    private static HangExplanation Save(string file) =>
        HangExplainer.For(new Moment(DateTime.Now, TimeSpan.FromSeconds(9), "slow-save", $"Saved {file} in 9 s", "", true), []);

    [Fact]
    public void SlowDrawingSaveSuggestsTheDetailingModeOption()
    {
        var x = Save("Frame.SLDDRW");
        Assert.Contains("Include Detailing Mode data when saving", x.TryThis[0]);
        Assert.Contains("2021 or earlier", x.TryThis[1]);
        Assert.Contains("Detailing mode", x.WhyItMatters);
        Assert.True(x.Specific);
    }

    [Fact]
    public void SlowPartSaveDoesNotMentionDetailingMode()
    {
        var x = Save("Bracket.SLDPRT");
        Assert.DoesNotContain(x.TryThis, t => t.Contains("Detailing", StringComparison.Ordinal));
        Assert.DoesNotContain("Detailing", x.WhyItMatters);
    }
}
