using SWCompanion.Core.Events;

namespace SWCompanion.Core.Now;

/// <summary>What a caught hang-up means, in plain words: what happened, why it matters, what to try, and how to look further.</summary>
/// <param name="More">"Explain more": a fuller, still plain, account of what goes on behind it.</param>
/// <param name="Technical">"Still unclear?": the raw facts the Companion recorded, for whoever helps you look into it.</param>
public sealed record HangExplanation(string Title, string When, string WhatHappened, string WhyItMatters,
    IReadOnlyList<string> TryThis, IReadOnlyList<string> LookFurther, IReadOnlyList<string> WindowsSaw,
    IReadOnlyList<string> More, IReadOnlyList<string> Technical)
{
    /// <summary>"Words used here": plain explanations of the computer words in this explanation.</summary>
    public IReadOnlyList<Explain.Term> Words => Explain.Glossary.Find(string.Join(" ", [Title, WhatHappened, WhyItMatters, .. TryThis]));
}

/// <summary>
/// Explains a caught moment (a freeze, a slow open, a Windows event...) for the "hung up" panel. Advice is general
/// SOLIDWORKS support practice and Microsoft's documentation; where the Companion can't know the cause, it says so.
/// Pure text, so it can be tested.
/// </summary>
public static class HangExplainer
{
    /// <summary>Windows events this close to a moment are shown with it ("what Windows saw at the same time").</summary>
    public static readonly TimeSpan Nearby = TimeSpan.FromSeconds(30);

    /// <summary>The Windows events that happened during the moment or within <see cref="Nearby"/> of it, oldest first.</summary>
    public static IReadOnlyList<EventFinding> Around(Moment m, IEnumerable<EventFinding> events) =>
        events.Where(e => e.Time >= m.Start - Nearby && e.Time <= m.Start + m.Duration + Nearby && !SameEvent(m, e))
              .OrderBy(e => e.Time).ToList();

    public static HangExplanation For(Moment m, IEnumerable<EventFinding> events)
    {
        string when = m.Duration >= TimeSpan.FromSeconds(1)
            ? $"{m.Start:ddd HH:mm:ss}, lasted {NowEngine.Seconds(m.Duration)}"
            : $"{m.Start:ddd HH:mm:ss}";
        var saw = Around(m, events).Select(e => $"{e.Time:HH:mm:ss}  {e.Line}").ToList();
        var (what, why, tryThis, look) = Advice(m);
        if (saw.Count > 0 && m.Kind is "freeze" or "slow-open" or "slow-sheet" or "slow-save" or "slow-rebuild" or "box")
            tryThis = ["Start with what Windows recorded at the same time (below): it is the most likely cause.", .. tryThis];
        var technical = new List<string>
        {
            $"Kind of record: {m.Kind}",
            $"Started: {m.Start:yyyy-MM-dd HH:mm:ss.fff}",
            $"Lasted: {m.Duration.TotalSeconds:0.00} s",
            $"Flagged to look into: {(m.LookInto ? "yes" : "no")}",
        };
        if (!string.IsNullOrWhiteSpace(m.Detail)) technical.Add("What was recorded: " + m.Detail.Trim());
        technical.AddRange(Around(m, events).Select(e => $"Windows event at {e.Time:HH:mm:ss.fff}: {e.Source}. {e.Detail}".Trim()));
        technical.Add("The same record is in the activity log and in caught-<date>.jsonl, in the logs folder (Settings shows where).");
        return new HangExplanation(m.Title, when, what, why, tryThis, look, saw, Deeper(m.Kind), technical);
    }

    private static readonly string[] EventViewer =
    [
        "Open Event Viewer (right-click Start > Event Viewer), then Windows Logs > Application and System. Look at the minute this happened.",
        "Reliability Monitor gives a simpler day-by-day view: press Start, type \"reliability\", open \"View reliability history\".",
    ];

    private static (string What, string Why, IReadOnlyList<string> Try, IReadOnlyList<string> Look) Advice(Moment m)
    {
        string detail = string.IsNullOrWhiteSpace(m.Detail) ? "" : " " + m.Detail.Trim();
        switch (m.Kind)
        {
            case "freeze":
                return ("SOLIDWORKS stopped answering Windows for a while, so the screen didn't update and clicks were ignored." + detail,
                    "Short freezes are often SOLIDWORKS working hard on one task, but repeated ones usually have a cause outside it: files over the network, the antivirus, the graphics driver, or memory running out.",
                    ["Note what you were doing (opening, switching sheets, saving, rebuilding). If it's always the same action on the same file, that file is the place to look.",
                     "Check the Fixes page: anything listed there can make freezes longer.",
                     "If the freezes happen with every file, try SOLIDWORKS Rx's \"Diagnostics\" tab to check the graphics driver is a certified one."],
                    ["The Companion's running log just above this panel shows each second of the freeze.", .. EventViewer]);

            case "slow-load":
                return ("After you clicked, SOLIDWORKS took longer than it normally does on this PC to get this ready." + detail,
                    "Windows and panels fill themselves in before they let you use them: lists of files, folders, configurations, references, materials or vault data. When that's slow, it's usually what the list has to reach, not the window itself.",
                    ["Notice whether it's slow every time or only sometimes. Only sometimes usually points to the network, the vault, or Defender scanning.",
                     "For Open and Save As: disconnected network drives and slow shortcuts in Quick Access make the file box wait for each one. Remove the ones you no longer use.",
                     "Try the same click with add-ins switched off (Tools > Add-Ins). Add-ins can join in when windows open.",
                     "If it's always slow in one file, the file itself (many configurations, references, or custom properties) is the likely reason."],
                    ["The running log shows what else was happening at that moment.", .. EventViewer]);

            case "box":
                return ("A SOLIDWORKS window popped up and closed again." + detail,
                    "Boxes that appear during a freeze are often the cause of the wait (a question waiting for an answer, a missing reference, a message from an add-in).",
                    ["If it happens again, read the box before it closes; its title is in the log.",
                     "If it's an add-in's box, try the same work with that add-in switched off (Tools > Add-Ins)."],
                    EventViewer);

            case "slow-open":
                return ("Opening this file took longer than it should." + detail,
                    "Slow opens usually come from the file itself (very large, made in an older SOLIDWORKS version and converted every time, missing references) or from where it lives (a network folder or vault).",
                    ["If the file comes from an older version, open it, save it, and see whether the next open is faster.",
                     "Check for missing references: SOLIDWORKS searches for them, which can take a long time over a network.",
                     "Copy the file locally and open it from there. If that's fast, the network or the server is the hold-up."],
                    ["The running log shows what was loading second by second.", .. EventViewer]);

            case "old-versions":
                return ("Some of these files were last saved in an older SOLIDWORKS than the one you're running." + detail,
                    "SOLIDWORKS converts each older file to the current format in memory every time it opens, and throws the conversion away when you close without saving. With many old files in an assembly or drawing, that adds up on every open.",
                    ["Save the files in your current version once, and the conversion stops. For many files, the SOLIDWORKS Task Scheduler (Start > SOLIDWORKS Tools > Task Scheduler > Convert Files) does it in one go.",
                     "If the files are in a PDM vault, check them out first and check them back in afterwards, or ask your PDM administrator to convert them.",
                     "Be careful: once saved in a newer version, they can't be opened in older SOLIDWORKS. Check that nobody who uses these files (colleagues, customers, suppliers) is on an older version first.",
                     "Library and Toolbox parts are often the oldest; converting those helps every assembly that uses them."],
                    ["SOLIDWORKS shows a file's saved-in version in File > Properties (Summary) for that file.", .. EventViewer]);

            case "slow-sheet":
                return ("Switching to this drawing sheet was slow." + detail,
                    "Sheets with many views, high-quality shaded views or views of large assemblies have to be redrawn on every switch.",
                    ["Set heavy views to draft quality, or use detached/lightweight views where you can.",
                     "Turn off automatic updating of views you aren't editing.",
                     "In SOLIDWORKS: Tools > Options > Drawings > Performance has settings that make sheet switches faster."],
                    EventViewer);

            case "slow-save":
                return ("Saving this file was slow." + detail,
                    "Slow saves usually come from saving to a network folder or vault, the antivirus scanning the file as it's written, or a very large file.",
                    ["Try saving the same file to a local folder. If that's fast, the network or the server is the hold-up.",
                     "Ask your IT team whether SOLIDWORKS files and folders can be excluded from antivirus scanning (Microsoft documents how)."],
                    EventViewer);

            case "slow-rebuild":
                return ("A rebuild took a long time." + detail,
                    "Long rebuilds usually come down to a few slow features, often patterns, fillets, or references to other parts.",
                    ["Use Performance Evaluation (Tools > Evaluate > Performance Evaluation) to find the slowest features.",
                     "Suppress or simplify the slowest features while you work on other areas."],
                    EventViewer);

            case "antivirus":
            case "defender-busy":
                return ("Windows Defender was scanning while you worked." + detail,
                    "Every file SOLIDWORKS opens or saves can be scanned first, which adds a wait to each one.",
                    ["Ask your IT team whether SOLIDWORKS's program, data and cache folders can be excluded from scanning (Microsoft documents how).",
                     "Schedule full scans for times you're not working."],
                    ["Windows Security > Virus & threat protection > Protection history lists what Defender did.", .. EventViewer]);

            case "slow-file":
            case "loading":
            case "files":
            case "saving":
                return ("SOLIDWORKS waited on files." + detail,
                    "Waiting on files is usually the network, the disk, or the antivirus, not SOLIDWORKS itself.",
                    ["If the files are on a network or vault, try the same work with a local copy.",
                     "Check the Details page's disk numbers during the wait."],
                    EventViewer);

            case "vault-trouble":
            case "vault-database":
            case "vault-archive":
                return ("SOLIDWORKS waited on the PDM vault's servers." + detail,
                    "Every check-in, check-out and file listing talks to the vault's servers; a slow or busy server makes SOLIDWORKS wait.",
                    ["Ask your PDM administrator whether the server was busy at that time.",
                     "If you're on a VPN, try the same work in the office to compare."],
                    EventViewer);

            case "graphics":
                return ("The graphics card was working flat out." + detail,
                    "Large assemblies, shaded views with high image quality, and RealView or shadows all add graphics work.",
                    ["Lower the image quality for large assemblies (Document Properties > Image Quality).",
                     "Check in SOLIDWORKS Rx that the graphics driver is the certified one for your card."],
                    EventViewer);

            case "commit":
                return ("Windows came close to the most memory it can hand out to programs." + detail,
                    "This limit is your memory chips plus the page file, a reserved area of the disk Windows uses as overflow. Programs reserve memory before they use it, so the reservations can reach the limit while the memory chips still look part-empty. At the limit, Windows refuses more memory, and SOLIDWORKS can stop responding, show an out-of-memory message, or crash.",
                    ["Save your work.",
                     "Close programs you don't need, starting with the biggest reservations listed above (web browsers with many tabs often reserve a lot).",
                     "Let Windows size the page file itself: press Start, type \"advanced system settings\" and open it. On the Advanced tab, under Performance, click Settings, then the Advanced tab, then Change under Virtual memory. Tick \"Automatically manage paging file size for all drives\", click OK, and restart the PC. (Changing this needs administrator rights; ask your IT team if it's locked.)",
                     "Make sure drive C: has plenty of free space (at least 20 GB): an automatic page file grows into it when needed.",
                     "If it keeps happening with your normal work, more memory (RAM) in the PC raises the limit."],
                    ["Task Manager shows the same numbers: Performance tab > Memory > \"Committed\" (used / limit). On the Details tab, right-click the column headers, choose Select columns, and add \"Commit size\" to see which programs reserve the most.",
                     .. EventViewer]);

            case "memory":
            case "objects":
                return ("The computer was close to a limit." + detail,
                    "When memory or Windows' object limit runs low, everything slows down and SOLIDWORKS may stop responding or crash.",
                    ["Close drawings and models you aren't using.", "Close other large programs (browsers with many tabs too).",
                     "See the Fixes page for the object-limit and memory checks."],
                    EventViewer);

            case "other-program":
            case "computing":
            case "computing-many":
            case "disk":
                return ("Something was keeping the computer busy." + detail,
                    "SOLIDWORKS shares the processor and disk with everything else running; a busy program elsewhere makes it wait.",
                    ["If it was another program, check whether it can run when you're not working.",
                     "If it was SOLIDWORKS computing, see whether the same action is always slow on the same file."],
                    EventViewer);

            case "crash":
            case "ended-frozen":
                return ("SOLIDWORKS ended." + detail,
                    "Crashes and forced closes are best looked into on the Crashes page, which reads SOLIDWORKS's own crash records.",
                    ["Open the Crashes page for the details and what to try."],
                    EventViewer);

            case "win-blocked":
                return ("Windows Defender's folder protection (Controlled folder access) stopped a program changing a file in a protected folder." + detail,
                    "Folder protection guards folders like Documents and Desktop against ransomware: only programs on its allowed list may change files there. When SOLIDWORKS isn't on the list, saving there fails or the file seems to vanish, and SOLIDWORKS can hang while it retries.",
                    ["Save the file somewhere else for now (a project folder outside Documents), so no work is lost.",
                     "Add SOLIDWORKS to the allowed list: Windows Security > Virus & threat protection > Manage ransomware protection > Allow an app through Controlled folder access > Add an allowed app, and pick SLDWORKS.exe (and the program named above, if different). This needs administrator rights; ask your IT team if it's locked.",
                     "Windows Security shows every block under Protection history."],
                    ["Windows Security > Virus & threat protection > Protection history lists each block with the program and file.", .. EventViewer]);

            case "win-quarantine":
                return ("Windows Defender decided a file was a threat and acted on it (usually by moving it into quarantine, where nothing can use it)." + detail,
                    "If it's a SOLIDWORKS file, macro or add-in, SOLIDWORKS may fail to open something, a macro may stop working, or an add-in may not load. Defender is sometimes wrong about macros and tools, but sometimes right.",
                    ["Don't restore the file unless you're sure it's safe. Check where it came from.",
                     "Ask your IT team to look at it in Windows Security > Virus & threat protection > Protection history; they can restore it and allow it if it's safe.",
                     "If it's a macro you wrote or trust, your IT team can report it to Microsoft as a false alarm."],
                    ["Windows Security > Virus & threat protection > Protection history.", .. EventViewer]);

            case "win-scan":
                return ("Windows Defender started a scan." + detail,
                    "A scan reads many files from the disk and uses the processor, so opens, saves and rebuilds can be slower until it finishes.",
                    ["If scans often run while you work, ask your IT team whether they can be scheduled outside working hours."],
                    ["Windows Security > Virus & threat protection shows the last scan and its results.", .. EventViewer]);

            case "win-hang":
                return ("Windows recorded that a program stopped responding and was closed (by you or by Windows)." + detail,
                    "If it's SOLIDWORKS, this is the record of a freeze that ended with the program being closed; any unsaved work in it was lost.",
                    ["Look at what the Companion's log said just before: what SOLIDWORKS was doing when it stopped.",
                     "If the same file causes it every time, try opening it with \"Do not load hidden components\" or in Large Design Review mode."],
                    EventViewer);

            case "win-crash":
                return ("Windows recorded that a program crashed." + detail,
                    "\"The part that failed\" names the piece of software where the crash happened. It is often an add-in, a graphics driver, or a Windows library, not the model.",
                    ["If the failing part belongs to an add-in, try working with that add-in switched off.",
                     "If it's a graphics driver file (names starting with nv, ig or ati/amd), install the certified driver for your card.",
                     "The Crashes page shows SOLIDWORKS's own crash records."],
                    EventViewer);

            case "win-gpu":
                return ("The graphics driver stopped responding, and Windows reset it. The screen may have gone black or flickered for a moment." + detail,
                    "This is a classic hidden cause of SOLIDWORKS freezes and crashes: everything waits while the graphics card restarts.",
                    ["Check in SOLIDWORKS Rx (Diagnostics tab) whether your graphics driver is the certified one, and install it if not.",
                     "Make sure SOLIDWORKS runs on the dedicated graphics card, not the built-in one (see the Fixes page).",
                     "If it keeps happening, check the card isn't overheating (dust in the fans, laptop on a soft surface)."],
                    EventViewer);

            case "win-disk":
                return ("A disk had trouble reading or writing." + detail,
                    "Every retry makes whatever was waiting on the disk freeze. Repeated disk errors can also mean the disk is starting to fail.",
                    ["Save your work and make sure it's backed up.",
                     "Ask your IT team to check the disk's health; repeated errors are a warning sign.",
                     "If it's a network or USB drive, check the cable and connection."],
                    EventViewer);

            case "win-memory":
                return ("Windows ran low on memory and warned about it." + detail,
                    "When memory runs out, Windows swaps to disk and everything slows down; SOLIDWORKS may stop responding or crash.",
                    ["Close models, drawings and other programs you aren't using.",
                     "If SOLIDWORKS is the biggest user every time, the assembly may need Large Assembly or Large Design Review mode.",
                     "Check the page file is managed by Windows (see the Fixes page)."],
                    EventViewer);

            case "win-network":
                return ("The network connection had trouble." + detail,
                    "Files in network folders and the PDM vault need the network; a drop or a slow name lookup makes SOLIDWORKS wait or fail to find files.",
                    ["If you're on Wi-Fi, try a cable.", "If you're on a VPN, check its connection.",
                     "Ask your IT team if the drop happens often."],
                    EventViewer);

            case "win-throttle":
                return ("The processor's speed was being limited." + detail,
                    "A slower processor makes every rebuild and open slower. It's usually heat, a power plan, or running on battery.",
                    ["Plug the laptop in and use the best-performance power mode (see the Fixes page).",
                     "Check the fans and vents aren't blocked."],
                    EventViewer);

            default:
                return ((string.IsNullOrWhiteSpace(m.Detail) ? "The Companion noticed this while you worked." : m.Detail.Trim()),
                    "It came and went, but was long enough to notice.",
                    ["If it keeps happening, note what you were doing each time."],
                    EventViewer);
        }
    }

    /// <summary>"Explain more": a fuller account of what goes on behind each kind, still in plain words.</summary>
    internal static IReadOnlyList<string> Deeper(string kind) => kind switch
    {
        "freeze" =>
        [
            "Windows sends every program a steady stream of small messages: move the mouse here, redraw this, a key was pressed. A program that's fine answers them within a fraction of a second. SOLIDWORKS does almost all its modelling work on the same thread that answers these messages, so while it's busy with one long job it can't answer, and Windows marks it \"Not Responding\".",
            "\"Not Responding\" doesn't mean broken. Often SOLIDWORKS is simply working (rebuilding, loading references, redrawing a big assembly) and comes back on its own. It becomes a problem when it waits on something outside itself, like a file on a slow server, a network that dropped, or a graphics driver that reset, or when it happens over and over.",
            "The Companion watches from outside, so it sees the effects (processor, disk, network, Defender, Windows events) but not SOLIDWORKS's internal steps. That's why it shows likely causes, not certainties.",
        ],
        "slow-load" =>
        [
            "When you click a button, SOLIDWORKS first builds the window or panel and fills it in, then hands control back to you. The Companion starts the clock at your click (SOLIDWORKS announces each command) and stops it the moment the window answers Windows, which is the moment you could use it. Anything after that, like you reading, choosing or selecting, isn't counted.",
            "\"Normal\" is learned on this PC: the Companion remembers the last 20 times for each window or command. It flags one only when it took about twice as long as usual and at least a second more, so ordinary ups and downs don't get flagged. Until it has seen a window three times, anything over 2 seconds is flagged.",
            "Windows that list things (files, folders, configurations, references, PDM data) are the most often slow, because filling the list may mean reaching across the network or waiting for the antivirus to check each item.",
        ],
        "commit" =>
        [
            "Think of memory like a restaurant. The memory chips are the tables; the page file is a waiting area outside. Windows takes bookings (programs reserving memory) up to the number of tables plus the waiting area. Many bookings never fully arrive, so the tables can look half-empty while the booking book is nearly full. When it's full, Windows turns new bookings away, and a program that can't get the memory it asks for, SOLIDWORKS included, can stop or crash.",
            "That's why the log can say \"close to the memory limit\" while memory shows, say, 60% in use: the percentage is the tables in use, the limit is the bookings.",
            "A small or fixed-size page file makes the limit low. Letting Windows manage the page file lets the waiting area grow when needed, which is what Microsoft recommends for most PCs.",
        ],
        "old-versions" =>
        [
            "Every SOLIDWORKS release stores files a little differently. A newer SOLIDWORKS can open older files, but it first converts them to its own format in memory. That conversion is repeated on every open until the file is saved in the new version.",
            "One old part is barely noticeable. An assembly with hundreds of old parts, or a drawing that loads many old models, can spend a real share of its open time converting. The Companion reads each file's saved-in version straight after the open, so it can say how many are old, but it can't time the conversion itself.",
            "Saving in the new version is a one-way step: older SOLIDWORKS can't open the converted files. That's why many teams convert on purpose, all at once, when everyone has upgraded.",
        ],
        "win-gpu" =>
        [
            "Windows gives the graphics driver about two seconds to answer. If it doesn't, Windows resets the driver to get the screen back. This is called a \"TDR\" (timeout, detection and recovery). Everything that draws on screen, SOLIDWORKS most of all, has to wait while that happens, and SOLIDWORKS sometimes can't recover its graphics and crashes.",
            "The usual reasons are a driver that isn't the one certified for SOLIDWORKS, a card that's overheating, or a model that asks the card for more than it can do at once (huge assemblies with RealView, shadows and high image quality).",
        ],
        "win-hang" or "win-crash" =>
        [
            "Windows keeps its own record of programs that stop responding or crash, separate from SOLIDWORKS's crash reports. The record names the program and, for a crash, the part of the software where it happened (a \"module\", usually a .dll file).",
            "The module is a clue, not a verdict: it's where the crash surfaced, which isn't always where the problem started. Graphics driver files, add-in files and network or antivirus files showing up repeatedly are worth acting on.",
        ],
        "win-disk" =>
        [
            "When a disk doesn't answer a request in time, Windows tries again. Each retry can take seconds, and anything waiting on that disk, including SOLIDWORKS opening or saving a file, waits too.",
            "A one-off retry can be harmless (a disk waking from sleep). Repeated retries, controller resets or bad-block warnings are an early sign of a failing disk, a loose cable or an overloaded storage controller.",
        ],
        "win-memory" or "memory" or "objects" =>
        [
            "When memory runs low, Windows moves parts of programs out to the page file on disk and back again as they're needed. Disk is far slower than memory, so everything slows down, and SOLIDWORKS can stop responding or crash if it can't get the memory it asks for.",
            "Windows records which programs were using the most memory at the time, which the Companion shows. SOLIDWORKS at the top with large assemblies open is common; something else at the top (a browser, another CAD tool) is worth closing.",
        ],
        "win-network" =>
        [
            "SOLIDWORKS doesn't know a file is on the network; it just asks Windows for it. If the network drops or a server name takes long to look up, Windows waits and retries, and SOLIDWORKS waits with it, often showing \"Not Responding\".",
            "Wi-Fi roaming, VPN reconnects and sleeping network adapters are common reasons. A wired connection and a stable VPN make the biggest difference for vault and network-folder work.",
        ],
        "antivirus" or "defender-busy" =>
        [
            "Defender checks files as programs open and save them. SOLIDWORKS opens many files at once (every part in an assembly, every referenced model in a drawing), so the checks add up.",
            "Excluding SOLIDWORKS's own folders and file types from real-time scanning is a common practice documented by Microsoft, but it's a security decision your IT team should make.",
        ],
        _ =>
        [
            "The Companion watches SOLIDWORKS from outside: how busy the processor, memory, disk, network and graphics card are, what Windows records, and whether SOLIDWORKS answers. When something stands out for a few seconds and then goes away, it keeps a record here so it isn't missed.",
            "Because it watches from outside, it can say what was happening around SOLIDWORKS with confidence, but what SOLIDWORKS was doing inside is a likely explanation, not a certainty.",
        ],
    };

    // A Windows-event moment shouldn't list itself as "nearby".
    private static bool SameEvent(Moment m, EventFinding e) => m.Kind == e.Kind && m.Start == e.Time;
}
