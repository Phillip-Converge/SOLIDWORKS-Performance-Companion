using Microsoft.Win32;

namespace SWCompanion.Core.Pdm;

/// <summary>One vault view on this PC, as its registry entry describes it.</summary>
/// <param name="Name">The vault's name.</param>
/// <param name="DatabaseServer">DbServer: the SQL Server holding the vault's database ("server", "server\instance" or "server,port").</param>
/// <param name="DatabaseName">DbName: the database's name on that server.</param>
/// <param name="ArchiveServer">ServerLoc: the archive server holding the vault's files.</param>
/// <param name="ArchivePort">ServerPort: the archive server's port (3030 unless it was changed).</param>
/// <param name="LocalFolder">ShellRoot: the local vault view folder (the local cache).</param>
/// <param name="CopiesDisagree">Plain-English differences between the 64-bit and 32-bit registry copies, if any.</param>
public sealed record VaultView(
    string Name,
    string? DatabaseServer,
    string? DatabaseName,
    string? ArchiveServer,
    int? ArchivePort,
    string? LocalFolder,
    IReadOnlyList<string> CopiesDisagree)
{
    public const int DefaultArchivePort = 3030;
    public const int DefaultSqlPort = 1433;

    /// <summary>The archive port to use: the stored one, or PDM's default.</summary>
    public int ArchivePortOrDefault => ArchivePort ?? DefaultArchivePort;

    /// <summary>The SQL Server machine name, without any "\instance" or ",port".</summary>
    public string? DatabaseHost => DatabaseServer?.Split('\\', ',')[0].Trim();

    /// <summary>"server\instance": a named SQL Server instance, whose port is chosen by SQL Server.</summary>
    public string? SqlInstance => DatabaseServer is { } s && s.Contains('\\') ? s.Split('\\')[1].Split(',')[0].Trim() : null;

    /// <summary>The SQL port if known: an explicit ",port", 1433 for a default instance, or null for a named instance.</summary>
    public int? SqlPort =>
        DatabaseServer is null ? null
        : DatabaseServer.Contains(',') && int.TryParse(DatabaseServer.Split(',')[1].Trim(), out var p) ? p
        : SqlInstance is null ? DefaultSqlPort
        : null;

    /// <summary>True if the archive and database are on the same machine (same name, ignoring the domain part).</summary>
    public bool SameServerByName =>
        ArchiveServer is not null && DatabaseHost is not null &&
        string.Equals(ShortName(ArchiveServer), ShortName(DatabaseHost), StringComparison.OrdinalIgnoreCase);

    /// <summary>"pdmserver.company.local" -> "pdmserver". Addresses like 10.0.0.5 are kept whole.</summary>
    public static string ShortName(string host) =>
        System.Net.IPAddress.TryParse(host, out _) ? host : host.Split('.')[0];
}

/// <summary>
/// Finds the vault views on this PC. READ ONLY: reads the registry entries the PDM
/// client keeps for each vault view (both the 64-bit and 32-bit copies).
/// </summary>
public static class VaultDiscovery
{
    public const string Key64 = @"SOFTWARE\SolidWorks\Applications\PDMWorks Enterprise\Databases";
    public const string Key32 = @"SOFTWARE\WOW6432Node\SolidWorks\Applications\PDMWorks Enterprise\Databases";

    public static IReadOnlyList<VaultView> Read() => Build(ReadKey(Key64), ReadKey(Key32));

    /// <summary>
    /// Combines the two registry copies (vault name -> value name -> value). Separate from
    /// <see cref="Read"/> so it can be tested with made-up vaults.
    /// </summary>
    public static IReadOnlyList<VaultView> Build(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> copy64,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> copy32)
    {
        var names = copy64.Keys.Concat(copy32.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n);
        var views = new List<VaultView>();
        foreach (var name in names)
        {
            copy64.TryGetValue(name, out var a);
            copy32.TryGetValue(name, out var b);
            var main = a ?? b!;
            var differences = new List<string>();
            if (a is not null && b is not null)
            {
                foreach (var key in new[] { "DbServer", "ServerLoc", "ServerPort", "DbName" })
                {
                    string va = Text(a, key) ?? "(not set)", vb = Text(b, key) ?? "(not set)";
                    if (!string.Equals(va, vb, StringComparison.OrdinalIgnoreCase))
                        differences.Add($"{key} is \"{va}\" in one copy and \"{vb}\" in the other");
                }
            }
            views.Add(new VaultView(name, Text(main, "DbServer"), Text(main, "DbName"), Text(main, "ServerLoc"),
                Number(main, "ServerPort"), Text(main, "ShellRoot"), differences));
        }
        return views;
    }

    private static string? Text(IReadOnlyDictionary<string, object> values, string name) =>
        values.TryGetValue(name, out var v) && v is not null && v.ToString() is { Length: > 0 } s ? s.Trim() : null;

    private static int? Number(IReadOnlyDictionary<string, object> values, string name) =>
        values.TryGetValue(name, out var v) ? v switch { int i => i, string s when int.TryParse(s, out var n) => n, _ => null } : null;

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> ReadKey(string path)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is null) return result;
            foreach (var vault in key.GetSubKeyNames())
            {
                using var v = key.OpenSubKey(vault);
                if (v is null) continue;
                result[vault] = v.GetValueNames().ToDictionary(n => n, n => v.GetValue(n)!, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // unreadable: treat as no vault views
        }
        return result;
    }
}
