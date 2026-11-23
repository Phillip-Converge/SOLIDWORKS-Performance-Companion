using SWCompanion.Core.Now;
using SWCompanion.Core.Pdm;

namespace SWCompanion.Core.Tests;

public class PdmGuideTests
{
    private static Moment M(string kind, string title, string detail = "") => new(DateTime.Now, TimeSpan.FromSeconds(3), kind, title, detail, true);

    [Fact]
    public void VaultWaitsAndPdmProgramsCount()
    {
        Assert.True(PdmGuide.IsPdm(M("vault-database", "Talking to the vault database server (came and went)")));
        Assert.True(PdmGuide.IsPdm(M("win-crash", "Windows: File Explorer (where the PDM vault view lives) crashed")));
        Assert.True(PdmGuide.IsPdm(M("win-hang", "Windows: EdmServer.exe stopped responding")));
        Assert.True(PdmGuide.IsPdm(M("slow-load", "The \"Check In\" window took 6.0 s to be ready")));
    }

    [Fact]
    public void EverythingElseDoesNot()
    {
        Assert.False(PdmGuide.IsPdm(M("freeze", "SOLIDWORKS stopped responding for 7 s")));
        Assert.False(PdmGuide.IsPdm(M("slow-load", "The \"Open\" window took 3.0 s to be ready")));
        Assert.False(PdmGuide.IsPdm(M("commit", "Windows is close to its memory limit")));
    }

    [Fact]
    public void TheGuideCoversTheMainWaysPdmSlowsThingsDown()
    {
        var titles = PdmGuide.Points.Select(p => p.Title).ToList();
        Assert.Contains("Two servers do the work", titles);
        Assert.Contains("Files come to your PC first", titles);
        Assert.All(PdmGuide.Points, p => Assert.False(string.IsNullOrWhiteSpace(p.Text)));
    }
}
