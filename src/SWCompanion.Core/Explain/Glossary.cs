using System.Text.RegularExpressions;

namespace SWCompanion.Core.Explain;

/// <summary>One word or phrase, explained for someone who has never needed to know it.</summary>
public sealed record Term(string Word, string Plain);

/// <summary>
/// Plain explanations of the computer words the Companion uses, so nobody has to already know what memory, a processor
/// core or a graphics card is. <see cref="Find"/> picks the ones a piece of text uses, for "words used here".
/// Pure text, tested.
/// </summary>
public static class Glossary
{
    private static readonly (string Pattern, Term Term)[] Entries =
    [
        (@"\b(memory|RAM)\b", new("Memory (RAM)",
            "The computer's fast working space. Everything you have open, every model, drawing and program, sits in memory while you use it. More open means more memory used. When it fills up, the computer slows down.")),
        (@"\bmemory chips\b", new("Memory chips",
            "The physical memory (RAM) installed in the computer, measured in GB. This is the \"memory\" number you usually see.")),
        (@"\bpage file\b", new("Page file",
            "A reserved area of the disk that Windows uses as overflow when memory is full. It's much slower than memory, but it stops programs running out completely.")),
        (@"\b(memory limit|reserv(ed|ations?)|commit(ted)?)\b", new("Memory limit (reserved memory)",
            "Programs book memory before they use it. Windows only lets the bookings reach the memory chips plus the page file. That total is the memory limit; at the limit, programs are refused more.")),
        (@"\b(processor|CPU)\b", new("Processor (CPU)",
            "The part of the computer that does the calculating. Rebuilding a model, opening a file and drawing views all keep it busy.")),
        (@"\bcores?\b", new("Core",
            "A processor is made of several cores, each able to work on one job at a time. Many parts of SOLIDWORKS (most rebuilds, for example) can only use one core at once, so one busy core can mean SOLIDWORKS is flat out even when the processor overall looks quiet.")),
        (@"\b(graphics card|GPU|graphics)\b", new("Graphics card (GPU)",
            "The part of the computer that draws the picture on screen. SOLIDWORKS uses it to show and spin models; large assemblies and visual effects like RealView and shadows make it work harder.")),
        (@"\b(graphics )?driver\b", new("Driver",
            "The small piece of software that lets Windows talk to a piece of hardware, like the graphics card. SOLIDWORKS tests and certifies particular graphics drivers; others can cause glitches, freezes or crashes.")),
        (@"\b(disk|drive)s?\b", new("Disk (drive)",
            "Where files are stored, like C:. Opening and saving read and write the disk; a slow or very busy disk makes those slow.")),
        (@"\b(network|server)s?\b", new("Network and servers",
            "The connection to other computers. Files in shared folders or a PDM vault live on a server, so opening them depends on the network being quick and steady.")),
        (@"\b(vault|PDM)\b", new("PDM vault",
            "A managed store for SOLIDWORKS files on a server, with check-in, check-out and version history. Working with vault files talks to the vault's servers.")),
        (@"\b(Defender|antivirus)\b", new("Antivirus (Windows Defender)",
            "Security software that checks files for threats, usually as they're opened or saved. Every SOLIDWORKS file can be checked first, which adds a wait to each one.")),
        (@"\badd-ins?\b", new("Add-in",
            "An extra tool that loads inside SOLIDWORKS (for example Toolbox, PDM or a third-party tool). Add-ins can slow opens, saves and rebuilds, or cause crashes. You can switch them off under Tools > Add-Ins.")),
        (@"\bNot Responding\b|\bstopped responding\b", new("Not Responding",
            "Windows' way of saying a program hasn't answered for a few seconds. It's often still working on something long; it doesn't always mean it's broken.")),
        (@"\b(module|\.dll)\b", new("Module (.dll file)",
            "A piece of a program, stored in a .dll file. Crash reports name the module where the crash happened, which hints at whether it was SOLIDWORKS, an add-in, a driver or Windows.")),
        (@"\bTask Manager\b", new("Task Manager",
            "Windows' own window showing what's running and how busy the computer is. Open it with Ctrl+Shift+Esc.")),
        (@"\bEvent Viewer\b|\bWindows event\b|\bWindows recorded\b", new("Windows' event records",
            "Windows keeps its own diary of problems (programs hanging, drivers resetting, disk errors). Event Viewer shows it; the Companion reads it for you.")),
        (@"\bfirmware\b", new("Firmware",
            "The built-in software of the computer's hardware, which can slow the processor down to protect it from heat or to save power.")),
        (@"\bVPN\b", new("VPN",
            "A secure tunnel to the company network from outside the office. It adds a delay to everything that goes over it, like opening files from a server.")),
        (@"\b(name lookup|DNS)\b", new("Name lookup (DNS)",
            "How the computer turns a server's name into its address. If that's slow, every first connection to the server waits.")),
        (@"\badministrator\b", new("Administrator rights",
            "Permission to change system settings on the computer. Some fixes need it; at work, your IT team may hold it.")),
        (@"\b(blue screen)\b", new("Blue screen",
            "When Windows itself hits a problem it can't recover from, the whole computer stops with a blue screen and restarts. Usually a driver or hardware issue, not SOLIDWORKS.")),
        (@"\bpermission\b|\baccess denied\b|\bblocked\b", new("Permission (access)",
            "Windows decides which programs and people may read or change each file and folder. If SOLIDWORKS isn't allowed to write where it needs to, saving or opening can fail or slow down.")),
    ];

    /// <summary>All the explained words.</summary>
    public static IReadOnlyList<Term> All => Entries.Select(e => e.Term).ToList();

    /// <summary>The words this text uses, in the order of the glossary, each once.</summary>
    public static IReadOnlyList<Term> Find(string text, int max = 6)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return Entries.Where(e => Regex.IsMatch(text, e.Pattern, RegexOptions.IgnoreCase)).Select(e => e.Term)
                      .DistinctBy(t => t.Word).Take(max).ToList();
    }

    /// <summary>One word's explanation by its name (for tooltips), or null.</summary>
    public static string? Plain(string word) => All.FirstOrDefault(t => t.Word.StartsWith(word, StringComparison.OrdinalIgnoreCase))?.Plain;
}
