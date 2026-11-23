using System.Reflection;

namespace SWCompanion.App;

/// <summary>About the Companion itself.</summary>
internal static class AppInfo
{
    /// <summary>
    /// Who created the Companion, shown small at the bottom of the sidebar for everyone. This is the one personal detail
    /// the owner chose to have in the code and in the public release (decided 2026-11-22); nothing else personal belongs here.
    /// The same address receives the reports users choose to send from their own e-mail ("send this to the developer").
    /// </summary>
    public const string Email = "Phillip.Lewis@converge.design";
    public const string Credit = "created by Phillip Lewis, " + Email;

    /// <summary>"0.1.1": the version set in Directory.Build.props.</summary>
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown")
        .Split('+')[0];
}
