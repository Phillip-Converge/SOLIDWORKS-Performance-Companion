using System.Text.RegularExpressions;

namespace SWCompanion.Core.Ask;

/// <summary>
/// Hides private names before anything is sent to an AI service, and puts them back in the answer you see.
/// File names become "file 1.SLDPRT" (the type is kept, it matters for the answer), folders "folder 1", servers and
/// web addresses "server A", this PC's name "this PC", your Windows user name "the user", e-mail addresses "[email]".
/// One scrubber is kept per conversation, so the same file always gets the same placeholder. Pure text work, tested.
/// </summary>
public sealed class Scrubber
{
    private static readonly string Extensions =
        "sldprt|sldasm|slddrw|slddot|prtdot|asmdot|drwdot|sldlfp|sldblk|sldftp|sldmat|sldstd|eprt|easm|edrw|eprtx|easmx|edrwx|" +
        "step|stp|igs|iges|x_t|x_b|sat|stl|3mf|dwg|dxf|pdf|xlsx|xls|xlsm|docx|doc|txt|csv|log|json|xml|zip|bmp|png|jpg|jpeg|tif|tiff|cwr|swp|swb|dmp";

    // Quoted file names may contain spaces; unquoted ones can't (we can't tell where a spaced name starts).
    private static readonly Regex QuotedFile = new($"\"([^\"\\r\\n]+\\.(?:{Extensions}))\"", RegexOptions.IgnoreCase);
    private static readonly Regex BareFile = new($@"(?<![\w\-.~$#&@+()])(?<!\bfile )[\w\-.~$#&@+()]+\.(?:{Extensions})\b", RegexOptions.IgnoreCase);
    // Folders: a drive or \\server start, then whole folder names, up to the last backslash.
    private static readonly Regex Folder = new(@"(?:\b[A-Za-z]:|\\\\[^\\\s""<>|]+)\\(?:[^\\\r\n""<>|:]+\\)*"); // folder names can't hold ":", so one path never runs into the next
    private static readonly Regex Email = new(@"\b[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+\b");
    // Network addresses (each part 0-255), but not version numbers like "version 34.5.2.604" or "v30.1.0.75".
    private static readonly Regex Ip = new(@"(?<!version\s)(?<![vV])(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\d.])", RegexOptions.IgnoreCase);
    private static readonly Regex Host = new(
        @"\b(?:[a-z0-9](?:[a-z0-9\-]*[a-z0-9])?\.)+(?:com|net|org|local|lan|corp|internal|intra|intranet|io|co|uk|us|de|ca|au|eu|edu|gov|biz|info|cloud|ad|home|site|tech)\b",
        RegexOptions.IgnoreCase);

    private readonly Dictionary<string, string> _hide = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _show = new(StringComparer.Ordinal);
    private readonly List<(string Name, string As)> _known = new();
    private int _files, _folders, _servers;

    /// <param name="pcName">This computer's name (Environment.MachineName).</param>
    /// <param name="userName">The Windows user name (Environment.UserName).</param>
    /// <param name="otherNames">Anything else known to be private, e.g. the Windows domain or vault names.</param>
    public Scrubber(string? pcName = null, string? userName = null, IEnumerable<string>? otherNames = null)
    {
        if (!string.IsNullOrWhiteSpace(pcName)) _known.Add((pcName.Trim(), "this PC"));
        if (!string.IsNullOrWhiteSpace(userName)) _known.Add((userName.Trim(), "the user"));
        int n = 0;
        foreach (var o in otherNames ?? [])
            if (!string.IsNullOrWhiteSpace(o) && o.Trim().Length >= 3) _known.Add((o.Trim(), $"private name {++n}"));
        // Longest first, so "SMITH-PC2" is hidden before "SMITH".
        _known.Sort((a, b) => b.Name.Length.CompareTo(a.Name.Length));
    }

    /// <summary>The text with private names replaced by placeholders.</summary>
    public string Hide(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string t = Email.Replace(text, "[email]");
        t = QuotedFile.Replace(t, m => "\"" + FileName(m.Groups[1].Value) + "\"");
        t = Folder.Replace(t, m => Placeholder(m.Value, () => $"folder {++_folders}") + "\\");
        t = BareFile.Replace(t, m => FileName(m.Value));
        t = Ip.Replace(t, m => Placeholder(m.Value, NextServer));
        t = Host.Replace(t, m => Placeholder(m.Value, NextServer));
        foreach (var (name, @as) in _known)
            t = Regex.Replace(t, $@"(?<![\w-]){Regex.Escape(name)}(?![\w-])", @as, RegexOptions.IgnoreCase);
        return t;
    }

    /// <summary>The AI's answer with the placeholders turned back into the real names (only on your screen).</summary>
    public string Restore(string answer)
    {
        if (string.IsNullOrEmpty(answer)) return answer;
        // Longest placeholder first, so "file 12" isn't read as "file 1" followed by "2".
        foreach (var (placeholder, original) in _show.OrderByDescending(kv => kv.Key.Length))
            answer = answer.Replace(placeholder, original, StringComparison.OrdinalIgnoreCase);
        return answer;
    }

    /// <summary>How many different names were hidden so far (for the preview and the log).</summary>
    public int HiddenCount => _show.Count;

    private string FileName(string original)
    {
        string ext = Path.GetExtension(original);
        return Placeholder(original, () => $"file {++_files}{ext.ToUpperInvariant()}");
    }

    private string NextServer() => "server " + Letters(++_servers);

    private string Placeholder(string original, Func<string> make)
    {
        string key = original.TrimEnd('\\');
        if (_hide.TryGetValue(key, out var p)) return p;
        p = make();
        _hide[key] = p;
        _show[p] = key;
        return p;
    }

    private static string Letters(int n)
    {
        string s = "";
        for (; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }
}
