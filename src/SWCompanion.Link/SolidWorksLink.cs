using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using SolidWorks.Interop.sldworks;
using SWCompanion.AddIn;
using SWCompanion.Contracts;
using Environment = System.Environment;

namespace SWCompanion.Link;

/// <summary>
/// The Companion's outside line into a running SOLIDWORKS, used when the add-in isn't installed.
/// It listens to the same SOLIDWORKS events the add-in does and produces the same notes, so the
/// rest of the Companion can't tell the difference.
///
/// Rules it keeps:
///  - It only ever connects to a SOLIDWORKS that is already running. It never starts one.
///  - Event handlers only drop a note in a queue and return at once: SOLIDWORKS waits for each
///    handler to return, so anything slower would slow SOLIDWORKS down.
///  - Every call into SOLIDWORKS runs on the link's own worker thread, never the Companion's
///    screen thread, so a frozen SOLIDWORKS can never freeze the Companion's window.
///  - It only reads, apart from the "time each view" test, which runs only when the owner clicks
///    the button and confirms.
/// </summary>
public sealed class SolidWorksLink : IDisposable
{
    private readonly ConcurrentQueue<AddinMessage> _inbox = new();
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _worker;
    private readonly Dictionary<string, Stopwatch> _opening = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DrawingHook> _drawings = new(StringComparer.OrdinalIgnoreCase);
    private SldWorks? _sw;
    private int _loadsThisSwitch;
    private static readonly object DiaryLock = new();

    /// <summary>
    /// The link's own diary, for finding out what happened when something doesn't come through:
    /// %LOCALAPPDATA%\SWCompanion\logs\link-YYYY-MM-DD.log. File names stay on this PC. Never throws.
    /// </summary>
    /// <summary>Tests switch the diary off so they never write into the owner's real log.</summary>
    public static bool KeepDiary { get; set; } = true;

    internal static void Diary(string text)
    {
        if (!KeepDiary) return;
        try
        {
            lock (DiaryLock)
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.FolderName, "logs");
                Directory.CreateDirectory(folder);
                var now = DateTime.Now;
                File.AppendAllText(Path.Combine(folder, $"link-{now:yyyy-MM-dd}.log"), $"{now:yyyy-MM-dd HH:mm:ss.fff}  {text}{Environment.NewLine}");
            }
        }
        catch { }
    }

    /// <summary>What to show the owner before the "time each view" test, so they can say no.</summary>
    public static string ViewTestWarning => ViewTimer.Warning;

    /// <summary>The SOLIDWORKS process this link is connected to, or null.</summary>
    public int? ConnectedTo { get; private set; }
    /// <summary>Why the last attempt to connect didn't work, in plain words.</summary>
    public string? Problem { get; private set; }
    /// <summary>True while the "time each view" test is running.</summary>
    public bool TimingViews { get; private set; }

    public SolidWorksLink()
    {
        _worker = new Thread(Work) { IsBackground = true, Name = "SOLIDWORKS outside link" };
        // MTA: SOLIDWORKS's event calls then arrive on Windows' own threads and need no message loop here.
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    /// <summary>Everything SOLIDWORKS reported since the last call, oldest first.</summary>
    public List<AddinMessage> Drain()
    {
        var list = new List<AddinMessage>();
        while (_inbox.TryDequeue(out var m)) list.Add(m);
        return list;
    }

    /// <summary>Connect to this running SOLIDWORKS (in the background). Does nothing if already connected to it.</summary>
    public void Attach(int pid) => Queue(() =>
    {
        if (ConnectedTo == pid) return;
        DetachNow();
        var sw = RunningSolidWorks.Find(pid, out var why);
        if (sw is null)
        {
            if (why != Problem) Diary($"Can't connect to process {pid}: {why}");
            Problem = why;
            return;
        }
        _sw = sw;
        try
        {
            _sw.FileOpenPreNotify += OnFileOpenPre;
            _sw.FileOpenPostNotify += OnFileOpenPost;
            _sw.ActiveDocChangeNotify += OnActiveDocChange;
            _sw.FileCloseNotify += OnFileClose;
            _sw.DocumentLoadNotify2 += OnDocumentLoad;
            _sw.CommandOpenPreNotify += OnCommandOpen;
            ConnectedTo = pid;
            Problem = null;
            Diary($"Connected to SOLIDWORKS process {pid} from outside.");
            Post(new AddinMessage { Kind = AddinMessage.Kinds.Connected, Note = "outside link" });
            ReadActiveDocument();
        }
        catch (Exception ex)
        {
            Problem = "SOLIDWORKS was found but wouldn't accept the connection: " + ex.Message;
            Diary("Connection refused: " + ex);
            DetachNow();
        }
    });

    /// <summary>Disconnect (for example because the add-in has connected, or SOLIDWORKS closed).</summary>
    public void Detach() => Queue(DetachNow);

    /// <summary>The "time each view" test on the active drawing's current sheet. Only call after the owner confirmed.</summary>
    public void TimeEachView() => Queue(() =>
    {
        if (_sw?.ActiveDoc is not DrawingDoc drawing || _sw.ActiveDoc is not ModelDoc2 doc) return;
        TimingViews = true;
        try
        {
            Post(new AddinMessage { Kind = AddinMessage.Kinds.ViewTestStart, Document = doc.GetPathName(), DocType = "drawing", Note = ViewTimer.CurrentSheetName(drawing) });
            Post(ViewTimer.Run(drawing, doc));
        }
        catch (Exception ex) { Lost(ex); }
        finally { TimingViews = false; }
    });

    // ---- SOLIDWORKS events: note it and return straight away ----

    private int OnFileOpenPre(string fileName)
    {
        lock (_opening) _opening[fileName] = Stopwatch.StartNew();
        Post(new AddinMessage { Kind = AddinMessage.Kinds.OpenStart, Document = fileName });
        return 0;
    }

    private int OnFileOpenPost(string fileName)
    {
        double? seconds = null;
        lock (_opening)
            if (_opening.Remove(fileName, out var timer)) seconds = timer.Elapsed.TotalSeconds;
        Post(new AddinMessage { Kind = AddinMessage.Kinds.OpenEnd, Document = fileName, Seconds = seconds });
        Queue(() => ReadFileVersions(fileName)); // calls into SOLIDWORKS: never from inside the event
        return 0;
    }

    /// <summary>The most files whose saved-in version is read after one open, and the longest it may take.</summary>
    private const int MaxVersionFiles = 600;
    private static readonly TimeSpan MaxVersionTime = TimeSpan.FromSeconds(20);

    /// <summary>
    /// After a file opens: asks SOLIDWORKS which version the file and every file it references was last saved in
    /// (read only; SOLIDWORKS reads this from the files' headers). Files saved in an older version are converted in
    /// memory on every open, which is a known cause of slow opens.
    /// </summary>
    private void ReadFileVersions(string fileName)
    {
        if (_sw is null) return;
        var watch = Stopwatch.StartNew();
        try
        {
            var paths = new List<string> { fileName };
            if (_sw.GetDocumentDependencies2(fileName, true, true, false) is object[] deps)
                for (int i = 1; i < deps.Length; i += 2) // name, path, name, path...
                    if (deps[i] is string p && p.Length > 0) paths.Add(p);
            var lines = new List<string>();
            bool first = true;
            foreach (var p in paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxVersionFiles))
            {
                if (watch.Elapsed > MaxVersionTime) break;
                if (_sw.VersionHistory(p) is object[] { Length: > 0 } history)
                {
                    lines.Add(p + "\t" + history[^1]);
                    // The exact wording SOLIDWORKS uses, once per open, so it can be checked in the diary.
                    if (first) { Diary($"Version history sample for {Path.GetFileName(p)}: {string.Join(" | ", history.Select(h => h?.ToString()))}"); first = false; }
                }
            }
            Diary($"Read saved-in versions of {lines.Count} of {paths.Count} file(s) for {Path.GetFileName(fileName)} in {watch.Elapsed.TotalSeconds:0.0} s.");
            Post(new AddinMessage { Kind = AddinMessage.Kinds.FileVersions, Document = fileName, Seconds = watch.Elapsed.TotalSeconds,
                Note = string.Join("\n", lines) });
        }
        catch (Exception ex) { Lost(ex); }
    }

    private int OnActiveDocChange()
    {
        Queue(ReadActiveDocument); // reading the document is a call into SOLIDWORKS, so not in here
        return 0;
    }

    /// <summary>
    /// A command started (you clicked a toolbar button or menu item). Passed straight on with the time, so the
    /// Companion can time how long the window or panel it opens takes to be ready.
    /// </summary>
    public event Action<DateTime, string>? CommandStarted;

    private int OnCommandOpen(int command, int userCommand)
    {
        var now = DateTime.Now;
        try { CommandStarted?.Invoke(now, CommandName(command)); } catch { }
        return 0;
    }

    /// <summary>"swCommands_Extrude" â†’ "Extrude"; unknown numbers stay as "command 1234".</summary>
    internal static string CommandName(int command)
    {
        var name = Enum.GetName(typeof(SolidWorks.Interop.swcommands.swCommands_e), command);
        if (name is null) return $"command {command}";
        name = name.StartsWith("swCommands_", StringComparison.Ordinal) ? name["swCommands_".Length..] : name;
        return name.Replace('_', ' ').Replace("  ", " ").Trim();
    }

    private int OnFileClose(string fileName, int reason)
    {
        Post(new AddinMessage { Kind = AddinMessage.Kinds.Close, Document = fileName });
        return 0;
    }

    private int OnDocumentLoad(string docTitle, string docPath)
    {
        bool switching;
        lock (_drawings) switching = _drawings.Values.Any(d => d.Switching);
        if (!switching) { _loadsThisSwitch = 0; return 0; }
        if (Interlocked.Increment(ref _loadsThisSwitch) <= 300)
            Post(new AddinMessage { Kind = AddinMessage.Kinds.ModelLoad, Document = string.IsNullOrEmpty(docPath) ? docTitle : docPath });
        return 0;
    }

    // ---- work done on the link's own thread ----

    private void ReadActiveDocument()
    {
        if (_sw is null) return;
        try
        {
            if (_sw.ActiveDoc is ModelDoc2 doc)
            {
                string path = doc.GetPathName();
                if (string.IsNullOrEmpty(path)) path = doc.GetTitle();
                Post(new AddinMessage { Kind = AddinMessage.Kinds.ActiveDocument, Document = path, DocType = TypeName(doc.GetType()) });
                if (doc is DrawingDoc drawing)
                {
                    lock (_drawings)
                    {
                        if (!_drawings.ContainsKey(path))
                        {
                            _drawings[path] = new DrawingHook(this, drawing, path);
                            Diary($"Listening to sheet switches in {Path.GetFileName(path)}.");
                        }
                    }
                }
                else Diary($"Active document: {Path.GetFileName(path)} (type {doc.GetType()}, not a drawing).");
            }
            else Post(new AddinMessage { Kind = AddinMessage.Kinds.ActiveDocument });
        }
        catch (Exception ex) { Lost(ex); }
    }

    private void DetachNow()
    {
        var sw = _sw;
        _sw = null;
        if (ConnectedTo is not null) Post(new AddinMessage { Kind = AddinMessage.Kinds.Disconnected, Note = "outside link" });
        ConnectedTo = null;
        lock (_drawings)
        {
            foreach (var d in _drawings.Values) d.Unhook();
            _drawings.Clear();
        }
        if (sw is null) return;
        try
        {
            sw.FileOpenPreNotify -= OnFileOpenPre;
            sw.FileOpenPostNotify -= OnFileOpenPost;
            sw.ActiveDocChangeNotify -= OnActiveDocChange;
            sw.FileCloseNotify -= OnFileClose;
            sw.DocumentLoadNotify2 -= OnDocumentLoad;
            sw.CommandOpenPreNotify -= OnCommandOpen;
        }
        catch { /* SOLIDWORKS may already be gone */ }
        try { Marshal.ReleaseComObject(sw); } catch { }
    }

    /// <summary>A call into SOLIDWORKS failed. If SOLIDWORKS has gone, drop the line quietly.</summary>
    private void Lost(Exception ex)
    {
        Diary("Problem: " + ex.GetType().Name + ": " + ex.Message);
        if (ex is COMException or InvalidComObjectException)
        {
            Problem = "Lost the line to SOLIDWORKS (it may have closed).";
            DetachNow();
        }
    }

    private void Work()
    {
        foreach (var job in _work.GetConsumingEnumerable())
        {
            try { job(); }
            catch (Exception ex) { Lost(ex); }
        }
    }

    private void Queue(Action job)
    {
        if (!_work.IsAddingCompleted) _work.Add(job);
    }

    private void Post(AddinMessage m)
    {
        if (m.Time == default) m.Time = DateTime.UtcNow;
        m.DocType ??= TypeFromPath(m.Document);
        _inbox.Enqueue(m);
    }

    private static string? TypeName(int t) => t switch { 1 => "part", 2 => "assembly", 3 => "drawing", _ => null };

    private static string? TypeFromPath(string? path) => Path.GetExtension(path ?? "").ToUpperInvariant() switch
    {
        ".SLDPRT" => "part",
        ".SLDASM" => "assembly",
        ".SLDDRW" => "drawing",
        _ => null,
    };

    public void Dispose()
    {
        Queue(DetachNow);
        _work.CompleteAdding();
        _worker.Join(2000);
    }

    /// <summary>One open drawing: sheet switch timing, and view facts after a slow switch.</summary>
    private sealed class DrawingHook
    {
        private readonly SolidWorksLink _link;
        private readonly DrawingDoc _drawing;
        private readonly string _path;
        private Stopwatch? _switch;

        public bool Switching => _switch is not null;

        public DrawingHook(SolidWorksLink link, DrawingDoc drawing, string path)
        {
            _link = link;
            _drawing = drawing;
            _path = path;
            drawing.ActivateSheetPreNotify += OnPre;
            drawing.ActivateSheetPostNotify += OnPost;
            drawing.DestroyNotify2 += OnDestroy;
        }

        public void Unhook()
        {
            try
            {
                _drawing.ActivateSheetPreNotify -= OnPre;
                _drawing.ActivateSheetPostNotify -= OnPost;
                _drawing.DestroyNotify2 -= OnDestroy;
            }
            catch { }
        }

        private int OnPre(string sheet)
        {
            _switch = Stopwatch.StartNew();
            Diary($"Sheet switch started: {sheet}");
            _link.Post(new AddinMessage { Kind = AddinMessage.Kinds.SheetStart, Document = _path, Sheet = sheet });
            return 0;
        }

        private int OnPost(string sheet)
        {
            double seconds = _switch?.Elapsed.TotalSeconds ?? 0;
            _switch = null;
            Diary($"Sheet switch finished: {sheet} in {seconds:0.00} s");
            _link.Post(new AddinMessage { Kind = AddinMessage.Kinds.SheetEnd, Document = _path, Sheet = sheet, Seconds = seconds });
            if (seconds >= DrawingWatch.SlowSwitchSeconds)
            {
                // Read the views after SOLIDWORKS has finished handling the switch, not inside its event.
                _link.Queue(() =>
                {
                    try
                    {
                        _link.Post(new AddinMessage
                        {
                            Kind = AddinMessage.Kinds.SheetViews, Document = _path, Sheet = sheet, Seconds = seconds,
                            Views = ViewReader.Read(_drawing),
                        });
                    }
                    catch (Exception ex) { _link.Lost(ex); }
                });
            }
            return 0;
        }

        private int OnDestroy(int destroyType)
        {
            Unhook();
            lock (_link._drawings) _link._drawings.Remove(_path);
            return 0;
        }
    }
}

/// <summary>Finds a running SOLIDWORKS through Windows' list of running programs that others may connect to.</summary>
internal static class RunningSolidWorks
{
    /// <summary>
    /// The SOLIDWORKS running as this process, or null with the reason. Never starts SOLIDWORKS:
    /// it only looks in Windows' table of running objects.
    /// </summary>
    public static SldWorks? Find(int pid, out string? why)
    {
        why = null;
        try
        {
            // 1. SOLIDWORKS registers itself by process ID, which tells copies apart.
            if (FromRunningTable("SolidWorks_PID_" + pid) is SldWorks byPid) return byPid;

            // 2. Otherwise the "current" SOLIDWORKS, but only if it is the same process.
            if (GetActiveByProgId("SldWorks.Application") is SldWorks any)
            {
                if (any.GetProcessID() == pid) return any;
                Marshal.ReleaseComObject(any);
            }
        }
        catch (Exception ex)
        {
            why = "Couldn't reach SOLIDWORKS from outside: " + ex.Message;
            return null;
        }
        why = IsAdministrator()
            ? "SOLIDWORKS can't be reached from outside while the Companion runs as administrator and SOLIDWORKS doesn't. " +
              "Restart the Companion normally, or install the add-in (docs/addin-setup.md) to have both."
            : "SOLIDWORKS didn't answer yet. It may still be starting; the Companion keeps trying.";
        return null;
    }

    private static object? FromRunningTable(string name)
    {
        if (GetRunningObjectTable(0, out var rot) != 0 || rot is null) return null;
        rot.EnumRunning(out var monikers);
        if (monikers is null) return null;
        CreateBindCtx(0, out var ctx);
        var one = new IMoniker[1];
        while (monikers.Next(1, one, IntPtr.Zero) == 0)
        {
            try
            {
                one[0].GetDisplayName(ctx, null, out var display);
                if (display is not null && display.Contains(name, StringComparison.OrdinalIgnoreCase) &&
                    rot.GetObject(one[0], out var obj) == 0)
                    return obj;
            }
            catch { /* entries from other programs that won't describe themselves */ }
        }
        return null;
    }

    private static object? GetActiveByProgId(string progId)
    {
        if (CLSIDFromProgID(progId, out var clsid) != 0) return null;
        return GetActiveObject(ref clsid, IntPtr.Zero, out var obj) == 0 ? obj : null;
    }

    private static bool IsAdministrator()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable? rot);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx ctx);
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int CLSIDFromProgID(string progId, out Guid clsid);
    [DllImport("oleaut32.dll")] private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);
}
