using SWCompanion.Core;

namespace SWCompanion.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void Always_writes_to_the_users_own_app_data_folder()
    {
        Assert.EndsWith(@"AppData\Local\SWCompanion", AppPaths.LocalAppDataFolder);
        Assert.Contains(AppPaths.LocalAppDataFolder, AppPaths.DataFolders);
    }

    [Fact]
    public void A_development_copy_also_uses_the_projects_data_folder()
    {
        // The tests run from inside the source folder, so the solution file is found.
        var found = AppPaths.FindProjectDataFolder(AppContext.BaseDirectory);
        Assert.NotNull(found);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(found)!, AppPaths.SolutionFileName)));
        Assert.EndsWith(@"\data", found);
    }

    [Fact]
    public void An_installed_copy_uses_app_data_only()
    {
        Assert.Null(AppPaths.FindProjectDataFolder(Path.GetTempPath()));
    }
}
