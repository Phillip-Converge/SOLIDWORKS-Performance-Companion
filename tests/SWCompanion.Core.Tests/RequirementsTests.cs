using SWCompanion.Core.Checkup;

namespace SWCompanion.Core.Tests;

public class RequirementsTests
{
    private static RequirementsFacts PC(string edition = "Professional", int build = 26100, double ram = 32, bool? ssd = true, bool arm = false, int? year = 2026) =>
        new(edition, build, ram, ssd, arm, year);

    [Fact]
    public void SupportedPcIsGoodAndSaysWhatIsFine()
    {
        var c = Requirements.Evaluate(PC());
        Assert.Equal(CheckStatus.Good, c.Status);
        Assert.Contains("Windows 11", c.Finding);
        Assert.Contains("32 GB of memory", c.Finding);
        Assert.Contains("an SSD", c.Finding);
    }

    [Theory]
    [InlineData("Core")]
    [InlineData("CoreSingleLanguage")]
    [InlineData("CoreN")]
    public void WindowsHomeIsFlagged(string edition)
    {
        var c = Requirements.Evaluate(PC(edition: edition));
        Assert.Equal(CheckStatus.Look, c.Status);
        Assert.Contains("Windows 11 Home", c.Finding);
        Assert.Contains("doesn't support Windows Home", c.Finding);
        Assert.Contains("Upgrade your edition of Windows", c.HowToFix);
    }

    [Theory]
    [InlineData("Professional")]
    [InlineData("Enterprise")]
    [InlineData("Education")]
    [InlineData("ProfessionalWorkstation")]
    public void ProAndEnterpriseAreNotHome(string edition) => Assert.False(Requirements.IsHome(edition));

    [Fact]
    public void Windows10WithSolidWorks2026IsAProblem()
    {
        var c = Requirements.Evaluate(PC(build: 19045, year: 2026));
        Assert.Equal(CheckStatus.Problem, c.Status);
        Assert.Contains("isn't supported on Windows 10", c.Finding);
        Assert.Contains("Windows 11", c.HowToFix);
    }

    [Fact]
    public void Windows10WithOlderSolidWorksIsWorthALook()
    {
        var c = Requirements.Evaluate(PC(build: 19045, year: 2024));
        Assert.Equal(CheckStatus.Look, c.Status);
        Assert.Contains("SOLIDWORKS 2026 and later need Windows 11", c.Finding);
    }

    [Theory]
    [InlineData(8, CheckStatus.Problem)]
    [InlineData(15, CheckStatus.Problem)]
    [InlineData(15.9, CheckStatus.Look)] // a 16 GB PC can report a little under 16
    [InlineData(16, CheckStatus.Look)]
    [InlineData(24, CheckStatus.Look)]
    [InlineData(31.7, CheckStatus.Good)]
    [InlineData(64, CheckStatus.Good)]
    public void MemoryThresholds(double ram, CheckStatus expected) => Assert.Equal(expected, Requirements.Evaluate(PC(ram: ram)).Status);

    [Fact]
    public void HardDiskIsWorthALookAndUnknownDiskIsNotMentioned()
    {
        var hdd = Requirements.Evaluate(PC(ssd: false));
        Assert.Equal(CheckStatus.Look, hdd.Status);
        Assert.Contains("spinning hard disk", hdd.Finding);
        var unknown = Requirements.Evaluate(PC(ssd: null));
        Assert.Equal(CheckStatus.Good, unknown.Status);
        Assert.DoesNotContain("SSD", unknown.Finding);
    }

    [Fact]
    public void ArmProcessorIsAProblem() =>
        Assert.Equal(CheckStatus.Problem, Requirements.Evaluate(PC(arm: true)).Status);

    [Fact]
    public void SeveralIssuesAreAllListedWithNumberedFixes()
    {
        var c = Requirements.Evaluate(PC(edition: "Core", ram: 16, ssd: false));
        Assert.Equal(CheckStatus.Look, c.Status);
        Assert.Contains("Home", c.Finding);
        Assert.Contains("16 GB of memory", c.Finding);
        Assert.Contains("hard disk", c.Finding);
        Assert.Contains("1. ", c.HowToFix);
        Assert.Contains("3. ", c.HowToFix);
    }

    [Fact]
    public void UnreadableIsUnknown() => Assert.Equal(CheckStatus.Unknown, Requirements.Evaluate(null).Status);
}
