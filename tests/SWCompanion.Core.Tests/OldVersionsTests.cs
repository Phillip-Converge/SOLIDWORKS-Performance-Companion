using SWCompanion.Core.Files;

namespace SWCompanion.Core.Tests;

public class OldVersionsTests
{
    [Theory]
    [InlineData("11000[2018/1,2018/134,2018/211]", 2018)]   // the real format, seen on SOLIDWORKS 2026 SP5.2
    [InlineData("9000[2016/286]", 2016)]
    [InlineData("23000[2025/12,2026/4]", 2026)]
    [InlineData("SOLIDWORKS 2023", 2023)]
    [InlineData("2023 [SolidWorks 2023]", 2023)]
    [InlineData("SolidWorks 2018 SP5.0", 2018)]
    [InlineData("4000", null)]
    public void ReadsTheYear(string raw, int? year) => Assert.Equal(year, OldVersions.Year(raw));

    private static string Note(params (string Path, string Raw)[] files) => string.Join("\n", files.Select(f => f.Path + "\t" + f.Raw));

    [Fact]
    public void FindsOldFilesOldestFirst()
    {
        var r = OldVersions.Read(@"C:\x\Top.SLDASM", Note(
            (@"C:\x\Top.SLDASM", "SOLIDWORKS 2026"), (@"C:\x\A.SLDPRT", "SOLIDWORKS 2021"), (@"C:\x\B.SLDPRT", "SOLIDWORKS 2018"),
            (@"C:\x\C.SLDPRT", "SOLIDWORKS 2026"), (@"C:\x\D.SLDPRT", "something odd")), 2026, 12);
        Assert.Equal(5, r.Total);
        Assert.Equal(2, r.Old.Count);
        Assert.Equal(2018, r.OldestYear);
        Assert.Equal(1, r.Unknown);
        Assert.False(r.TopIsOld);
        Assert.Equal("2 of 5 files in Top.SLDASM were last saved in older SOLIDWORKS (oldest 2018)", OldVersions.Line(r));
        Assert.Contains("1 from 2018, 1 from 2021", OldVersions.Detail(r));
        Assert.Contains("B.SLDPRT, A.SLDPRT", OldVersions.Detail(r));
    }

    [Fact]
    public void FlagsWhenTheOpenWasSlowOrThereAreMany()
    {
        string note = Note((@"C:\x\Top.SLDASM", "SOLIDWORKS 2026"), (@"C:\x\A.SLDPRT", "SOLIDWORKS 2021"));
        Assert.True(OldVersions.WorthFlagging(OldVersions.Read(@"C:\x\Top.SLDASM", note, 2026, 8)));
        Assert.False(OldVersions.WorthFlagging(OldVersions.Read(@"C:\x\Top.SLDASM", note, 2026, 1.5)));
        var many = string.Join("\n", Enumerable.Range(1, 25).Select(i => $@"C:\x\P{i}.SLDPRT" + "\tSOLIDWORKS 2020"));
        Assert.True(OldVersions.WorthFlagging(OldVersions.Read(@"C:\x\Top.SLDASM", many, 2026, 1)));
    }

    [Fact]
    public void ASingleOldPartReadsNaturally()
    {
        var r = OldVersions.Read(@"C:\x\Bracket.SLDPRT", Note((@"C:\x\Bracket.SLDPRT", "SOLIDWORKS 2019")), 2026, 3);
        Assert.True(r.TopIsOld);
        Assert.Equal("Bracket.SLDPRT was last saved in SOLIDWORKS 2019, so it's converted every time it opens", OldVersions.Line(r));
    }

    [Fact]
    public void NothingOldNothingFlagged()
    {
        var r = OldVersions.Read(@"C:\x\Top.SLDASM", Note((@"C:\x\Top.SLDASM", "SOLIDWORKS 2026")), 2026, 30);
        Assert.False(r.Any);
        Assert.False(OldVersions.WorthFlagging(r));
    }
}
