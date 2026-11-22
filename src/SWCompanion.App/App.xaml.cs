using System.Windows;

namespace SWCompanion.App;

public partial class App : Application
{
    /// <summary>Your saved choices (theme, window size, selected vault, last port tests), loaded once at start.</summary>
    internal static AppSettings Settings { get; } = AppSettings.Load();

    public static bool Dark => Settings.Theme == "dark";

    // SOLIDWORKS's API files come from the SOLIDWORKS installed on this PC, not from the Companion's own folder.
    static App() => SolidWorksApiFiles.Register();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Light or dark, as you last chose. The window is built after this, so it picks up the right colours.
        if (Dark)
            Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri("Themes/ModernDark.xaml", UriKind.Relative) };
        base.OnStartup(e);
    }
}
