using SWCompanion.Core.Checkup;

namespace SWCompanion.Core.Tests;

public class ChecklistTests
{
    private const string Dx = @"C:\Program Files\Dassault Systemes\SOLIDWORKS 3DEXPERIENCE R2026x\SOLIDWORKS\lang\english";

    /// <summary>An example PC: Explorer already tidy, one extra indexed folder, File Locations left behind by a removed install.</summary>
    private static ChecklistFacts ExamplePc(IReadOnlyList<FileLocationEntry>? locations = null, int? launchTo = 1, int? recent = 0,
                                           int? frequent = 0, bool pdm = true, IReadOnlyList<string>? views = null) =>
        new(launchTo, recent, frequent,
            [(@"c:\users\public\documents\solidworks\solidworks data\", true)],
            "2026",
            locations ??
            [
                new("Document Template Folders", @"C:\ProgramData\SolidWorks\SOLIDWORKS 2026\templates\", true, false),
                new("BOM Template Folders", Dx, false, false),
                new("Title Block Template Folders", Dx, false, false),
                new("Bend Table Folders", Dx + @"\Sheetmetal Bend Tables", false, false),
            ],
            pdm, views ?? []);

    private static CheckResult Get(ChecklistFacts f, string id) => ChecklistEvaluator.Evaluate(f).Single(r => r.Id == id);

    [Fact]
    public void Explorer_settings_that_already_follow_the_checklist_are_good()
    {
        Assert.Equal(CheckStatus.Good, Get(ExamplePc(), "explorer-opens").Status);
        Assert.Equal(CheckStatus.Good, Get(ExamplePc(), "quick-access").Status);
    }

    [Fact]
    public void Windows_defaults_are_flagged()
    {
        var f = ExamplePc(launchTo: null, recent: null, frequent: null);
        Assert.Equal(CheckStatus.Look, Get(f, "explorer-opens").Status);
        var qa = Get(f, "quick-access");
        Assert.Equal(CheckStatus.Look, qa.Status);
        Assert.Contains("recently used files and frequently used folders", qa.Finding);
    }

    [Fact]
    public void Extra_indexed_folders_are_listed_as_search_paths()
    {
        var r = Get(ExamplePc(), "search-paths");
        Assert.Equal(CheckStatus.Look, r.Status);
        Assert.Contains("solidworks data", r.Finding);
        Assert.Contains("Indexing Options", r.HowToFix);
    }

    [Fact]
    public void Missing_file_locations_name_the_old_install()
    {
        var r = Get(ExamplePc(), "file-locations");
        Assert.Equal(CheckStatus.Look, r.Status);
        Assert.Contains("3 of 4 File Locations point to folders that don't exist", r.Finding);
        Assert.Contains(@"under C:\Program Files\Dassault Systemes\SOLIDWORKS 3DEXPERIENCE R2026x", r.Finding);
        Assert.Contains("Find/Replace", r.HowToFix);
    }

    [Fact]
    public void A_missing_network_location_is_a_problem()
    {
        var f = ExamplePc([new("Sheet Format Folders", @"\\oldserver\cad\sheetformats", false, true)]);
        Assert.Equal(CheckStatus.Problem, Get(f, "file-locations").Status);
    }

    [Fact]
    public void Old_year_paths_are_flagged_even_if_they_exist()
    {
        var f = ExamplePc([new("Document Template Folders", @"C:\ProgramData\SolidWorks\SOLIDWORKS 2018\templates\", true, false)]);
        var r = Get(f, "file-locations");
        Assert.Equal(CheckStatus.Look, r.Status);
        Assert.Contains("older SOLIDWORKS year", r.Finding);
    }

    [Fact]
    public void Settings_the_app_cannot_read_say_check_by_hand_instead_of_guessing()
    {
        var ext = Get(ExamplePc(), "ext-ref-search");
        Assert.Equal(CheckStatus.Unknown, ext.Status);
        Assert.StartsWith("Check by hand", ext.Finding);
        Assert.Equal(CheckStatus.Unknown, Get(ExamplePc(views: ["TestVault"]), "pdm-options").Status);
        Assert.Contains("no vault view", Get(ExamplePc(), "pdm-options").Finding);
    }

    [Fact]
    public void Every_check_is_grouped_and_explained()
    {
        foreach (var r in ChecklistEvaluator.Evaluate(ExamplePc()))
        {
            Assert.Contains(r.Group, new[] { "Windows", "SOLIDWORKS", "PDM client" });
            Assert.StartsWith("Think of it like this", r.Plain);
        }
    }

    [Theory]
    [InlineData(@"file:///C:\[bd4c205a-c9d8-473d-9df1-a7fcdaaefcfa]\Users\Public\x\", @"C:\Users\Public\x\")]
    [InlineData(@"file:///D:\Projects\", @"D:\Projects\")]
    public void Turns_index_rules_into_paths(string url, string expected) =>
        Assert.Equal(expected, ChecklistReader.IndexUrlToPath(url));

    [Fact]
    public void Reads_this_PC_without_errors()
    {
        var f = ChecklistReader.Read();
        Assert.All(ChecklistEvaluator.Evaluate(f), r => Assert.False(string.IsNullOrWhiteSpace(r.Finding)));
    }
}
