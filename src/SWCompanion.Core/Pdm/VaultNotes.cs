using System.Text.Json;

namespace SWCompanion.Core.Pdm;

/// <summary>What the user knows about a vault's setup that the app can't detect.</summary>
/// <param name="DatabaseWhere">Where the SQL server is: on-site, off-site, cloud, or not known.</param>
/// <param name="ArchiveWhere">Where the archive server is.</param>
/// <param name="Link">How this PC reaches them: office network, VPN, internet, or not known.</param>
/// <param name="Replicated">Is the archive replicated between sites: yes, no, or not known.</param>
/// <param name="Notes">Anything else, in the user's words.</param>
public sealed record VaultNote(string DatabaseWhere = "not known", string ArchiveWhere = "not known", string Link = "not known",
                               string Replicated = "not known", string Notes = "");

/// <summary>
/// Saves the user's vault notes in their own app-data folder only
/// (%LOCALAPPDATA%\SWCompanion\vault-notes.json): never in the project, Git, or anything published.
/// </summary>
public sealed class VaultNotesStore
{
    public static readonly string[] Places = ["not known", "same building", "another office", "off-site data centre", "cloud"];
    public static readonly string[] Links = ["not known", "office network", "VPN", "internet"];
    public static readonly string[] YesNo = ["not known", "yes", "no"];

    private readonly string _path;

    public VaultNotesStore() : this(Path.Combine(AppPaths.LocalAppDataFolder, "vault-notes.json")) { }

    /// <param name="path">Where to keep the file. Separate so tests can use a temporary file.</param>
    public VaultNotesStore(string path) => _path = path;

    public string FilePath => _path;

    public IReadOnlyDictionary<string, VaultNote> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Dictionary<string, VaultNote>();
            return JsonSerializer.Deserialize<Dictionary<string, VaultNote>>(File.ReadAllText(_path)) ?? new();
        }
        catch
        {
            return new Dictionary<string, VaultNote>(); // unreadable: start empty rather than crash
        }
    }

    /// <summary>Saves one vault's notes, keeping the others. Writes to a temporary file first, then swaps it in.</summary>
    public void Save(string vault, VaultNote note)
    {
        var all = new Dictionary<string, VaultNote>(Load(), StringComparer.OrdinalIgnoreCase) { [vault] = note };
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }
}
