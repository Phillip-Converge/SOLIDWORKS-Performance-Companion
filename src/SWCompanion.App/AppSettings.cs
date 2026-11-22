using System.IO;
using System.Text.Json;
using SWCompanion.Core;
using SWCompanion.Core.Pdm;

namespace SWCompanion.App;

/// <summary>
/// Everything you choose or enter in the Companion that isn't saved elsewhere, kept for next time in
/// %LOCALAPPDATA%\SWCompanion\settings.json (on this PC only; never in the project or anything published).
/// Vault notes have their own file (vault-notes.json) and section folding is in layout.json.
/// </summary>
internal sealed class AppSettings
{
    public string Theme { get; set; } = "light";
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }
    public string? SelectedVault { get; set; }
    /// <summary>Last port test per vault, so the results are still there after a restart.</summary>
    public Dictionary<string, List<PortTest>> PortTests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>No longer used: the credit line is now part of the Companion itself (AppInfo.Credit). Kept so older settings files still load.</summary>
    public string? Credit { get; set; }
    /// <summary>Fixes you dismissed (by title). Kept out of the count and the guide until you bring them back.</summary>
    public HashSet<string> DismissedFixes { get; set; } = new();
    /// <summary>Whether you left the Companion as the slim strip, and where the strip was.</summary>
    public bool StripMode { get; set; }
    public double? StripLeft { get; set; }
    public double? StripTop { get; set; }
    /// <summary>"Ask AI": which service ("off", "claude", "openai", "local"), which model, where a model on this PC listens,
    /// and whether you said not to show "what will be sent" before each question. The key itself is NOT here (see AiKeyStore).</summary>
    public string AiProvider { get; set; } = "off";
    public string? AiModel { get; set; }
    public string AiLocalUrl { get; set; } = "http://localhost:11434";
    public bool AiSkipPreview { get; set; }

    private static string FilePath => Path.Combine(AppPaths.LocalAppDataFolder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) is { } s)
            {
                s.PortTests = new Dictionary<string, List<PortTest>>(s.PortTests ?? new(), StringComparer.OrdinalIgnoreCase);
                s.DismissedFixes ??= new();
                return s;
            }
        }
        catch (Exception) { /* a damaged file just means the defaults */ }
        return new AppSettings();
    }

    /// <summary>Saves straight away. Written to a temporary file first, so a crash mid-save can't leave half a file.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception) { /* not being able to save a preference is never worth an error box */ }
    }
}
