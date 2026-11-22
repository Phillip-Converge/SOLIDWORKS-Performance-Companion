using SWCompanion.Core.Processes;

namespace SWCompanion.Core.Tests;

public class VersionTests
{
    [Theory]
    [InlineData("34.5.2.0604", 2026, 5, 2, 604, "SOLIDWORKS 2026 SP5.2", "34.5.2.604")]
    [InlineData("32.0.0.5032", 2024, 0, 0, 5032, "SOLIDWORKS 2024 SP0.0", "32.0.0.5032")]
    [InlineData("33.3.1", 2025, 3, 1, 0, "SOLIDWORKS 2025 SP3.1", "33.3.1.0")]
    public void Program_file_versions_become_release_names(string file, int year, int sp, int update, int build, string name, string number)
    {
        var v = SolidWorksVersion.FromFileVersion(file)!;
        Assert.Equal(new SolidWorksVersion(year, sp, update, build), v);
        Assert.Equal(name, v.Name);
        Assert.Equal(number, v.Number);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10.0.26100.1")] // a Windows file, not SOLIDWORKS
    [InlineData("not a version")]
    public void Other_versions_are_not_mistaken_for_SOLIDWORKS(string? file) => Assert.Null(SolidWorksVersion.FromFileVersion(file));

    [Theory]
    [InlineData("SOLIDWORKS PDM Client 2026 SP05.2", 2026, 5, 2)]
    [InlineData("SOLIDWORKS PDM Client 2025 SP3", 2025, 3, 0)]
    public void Installed_program_names_are_read(string name, int year, int sp, int update)
    {
        var v = SolidWorksVersion.FromDisplayName(name)!;
        Assert.Equal((year, sp, update), (v.Year, v.ServicePack, v.Update));
    }

    [Fact]
    public void A_PDM_client_on_a_different_service_pack_is_pointed_out_without_claiming_it_is_wrong()
    {
        var same = new InstalledVersions(new(2026, 5, 2, 604), null, new(2026, 5, 2, 0));
        var different = new InstalledVersions(new(2026, 5, 2, 604), null, new(2026, 4, 0, 0));
        Assert.DoesNotContain("different", same.Describe(running: true));
        Assert.Contains("different release or service pack", different.Describe(running: true));
        Assert.StartsWith("Running SOLIDWORKS 2026 SP5.2", same.Describe(true));
        Assert.StartsWith("Installed:", same.Describe(false));
    }

    [Fact]
    public void Reading_this_PC_never_throws_even_without_SOLIDWORKS()
    {
        var v = InstalledVersions.Read(runningExePath: null);
        Assert.NotNull(v.Describe(false));
    }
}
