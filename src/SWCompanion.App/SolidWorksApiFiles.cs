using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace SWCompanion.App;

/// <summary>
/// The Companion talks to SOLIDWORKS through SOLIDWORKS's own API files (SolidWorks.Interop.*.dll). They are not shipped
/// with the Companion: they're loaded from the SOLIDWORKS installed on this PC (its api\redist folder), so the download
/// contains none of Dassault Systèmes' files and always matches the SOLIDWORKS you have.
/// </summary>
internal static class SolidWorksApiFiles
{
    private static string[]? _folders;

    /// <summary>Call once, first thing at start-up, before anything uses the SOLIDWORKS link.</summary>
    public static void Register() => AppDomain.CurrentDomain.AssemblyResolve += Resolve;

    /// <summary>The api\redist folders of the SOLIDWORKS versions installed here, newest first.</summary>
    public static IReadOnlyList<string> Folders => _folders ??= Find();

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name ?? "";
        if (!name.StartsWith("SolidWorks.Interop.", StringComparison.OrdinalIgnoreCase)) return null;
        // Next to the Companion first (a developer's build), then the installed SOLIDWORKS.
        foreach (var folder in new[] { AppContext.BaseDirectory }.Concat(Folders))
        {
            string path = Path.Combine(folder, name + ".dll");
            if (File.Exists(path))
                try { return Assembly.LoadFrom(path); } catch { }
        }
        return null;
    }

    private static string[] Find()
    {
        var found = new List<(int Year, string Folder)>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SolidWorks");
            foreach (var name in root?.GetSubKeyNames() ?? [])
            {
                if (!name.StartsWith("SOLIDWORKS ", StringComparison.OrdinalIgnoreCase) || !int.TryParse(name[11..], out int year)) continue;
                using var setup = root!.OpenSubKey(name + @"\Setup");
                if (setup?.GetValue("SolidWorks Folder") is string install)
                {
                    string redist = Path.Combine(install, "api", "redist");
                    if (Directory.Exists(redist)) found.Add((year, redist));
                }
            }
        }
        catch { }
        // The usual place, in case the registry doesn't say.
        string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SOLIDWORKS Corp", "SOLIDWORKS", "api", "redist");
        if (Directory.Exists(standard)) found.Add((0, standard));
        return found.OrderByDescending(f => f.Year).Select(f => f.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
