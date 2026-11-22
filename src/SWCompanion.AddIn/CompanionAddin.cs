using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Environment = System.Environment;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;
using SWCompanion.Contracts;

namespace SWCompanion.AddIn;

/// <summary>
/// The Companion for SOLIDWORKS add-in. It only LISTENS: when SOLIDWORKS starts or finishes opening a
/// file, or you switch documents, it notes the time and passes it to the Companion app.
/// It never changes models, settings or files, and it never waits for the Companion.
///
/// Safety: every SOLIDWORKS event handler is wrapped so that a mistake in here can never
/// surface as an error inside SOLIDWORKS; it is written to the add-in's own log instead.
/// </summary>
[ComVisible(true)]
[Guid(AddinId)]
[ProgId("SWCompanion.AddIn")]
public sealed class CompanionAddin : ISwAddin
{
    public const string AddinId = "1363e5e6-22da-4602-8249-468d1df9ea02";
    private const string Title = ProductInfo.Name;
    private const string Description = "Passes timings from inside SOLIDWORKS (file opens, document switches) to the " +
                                       ProductInfo.Name + " app. Read only.";

    private SldWorks? _sw;
    private AddinOutbox? _outbox;
    private AddinLog? _log;
    private readonly Dictionary<string, Stopwatch> _opening = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DrawingWatch> _drawings = new(StringComparer.OrdinalIgnoreCase);
    private int _cookie;
    private int _loadsThisSwitch;
    private const int MaxLoadsPerSwitch = 300;
    private const int DocDrawing = 3; // swDocDRAWING
    // "@&Tools" puts the command in SOLIDWORKS's Tools menu (drawings only).
    private const string TimeViewsMenu = "Companion: Time Each View on This Sheet@&Tools";

    // ---- loading and unloading (called by SOLIDWORKS) ----

    public bool ConnectToSW(object ThisSW, int Cookie)
    {
        try
        {
            _log = new AddinLog();
            _sw = (SldWorks)ThisSW;
            int pid = Process.GetCurrentProcess().Id;
            _outbox = new AddinOutbox(AddinPipe.NameFor(pid), CreatePipe) { Diagnostic = _log.Write };

            _sw.FileOpenPreNotify += OnFileOpenPre;
            _sw.FileOpenPostNotify += OnFileOpenPost;
            _sw.ActiveDocChangeNotify += OnActiveDocChange;
            _sw.FileCloseNotify += OnFileClose;
            _sw.DocumentLoadNotify2 += OnDocumentLoad;

            // The "time each view" command: something you start yourself from the Tools menu.
            _cookie = Cookie;
            _sw.SetAddinCallbackInfo2(0, this, Cookie);
            int menu = _sw.AddMenuItem5(DocDrawing, Cookie, TimeViewsMenu, -1, nameof(TimeEachView), nameof(TimeEachViewEnable),
                "Times how long each view on this sheet takes to load and redraw (Companion for SOLIDWORKS)", null);
            _log.Write($"Added Tools menu command (result {menu}).");
            var version = typeof(CompanionAddin).Assembly.GetName().Version?.ToString() ?? "?";
            _log.Write($"Loaded (add-in {version}, SOLIDWORKS {_sw.RevisionNumber()}, process {pid}).");
            Post(AddinMessage.Kinds.Connected, note: version);
            OnActiveDocChange(); // in case a document was already open when the add-in was switched on
        }
        catch (Exception ex)
        {
            _log?.Write("Could not start: " + ex);
        }
        return true; // always let SOLIDWORKS carry on
    }

    public bool DisconnectFromSW()
    {
        try
        {
            if (_sw is not null)
            {
                _sw.FileOpenPreNotify -= OnFileOpenPre;
                _sw.FileOpenPostNotify -= OnFileOpenPost;
                _sw.ActiveDocChangeNotify -= OnActiveDocChange;
                _sw.FileCloseNotify -= OnFileClose;
                _sw.DocumentLoadNotify2 -= OnDocumentLoad;
                try { _sw.RemoveMenu(DocDrawing, TimeViewsMenu, ""); } catch { }
            }
            foreach (var d in _drawings.Values) d.Unhook();
            _drawings.Clear();
            Post(AddinMessage.Kinds.Disconnected);
            _outbox?.Dispose();
            _log?.Write("Unloaded.");
        }
        catch (Exception ex)
        {
            _log?.Write("Problem while unloading: " + ex);
        }
        finally
        {
            if (_sw is not null) Marshal.ReleaseComObject(_sw);
            _sw = null;
            _outbox = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return true;
    }

    // ---- SOLIDWORKS events (each returns 0 = "carry on") ----

    private int OnFileOpenPre(string fileName)
    {
        Safe(() =>
        {
            _opening[fileName] = Stopwatch.StartNew();
            Post(AddinMessage.Kinds.OpenStart, fileName);
        });
        return 0;
    }

    private int OnFileOpenPost(string fileName)
    {
        Safe(() =>
        {
            double? seconds = null;
            if (_opening.TryGetValue(fileName, out var timer))
            {
                seconds = timer.Elapsed.TotalSeconds;
                _opening.Remove(fileName);
            }
            Post(AddinMessage.Kinds.OpenEnd, fileName, seconds: seconds);
        });
        return 0;
    }

    private int OnActiveDocChange()
    {
        Safe(() =>
        {
            if (_sw?.ActiveDoc is ModelDoc2 doc)
            {
                string path = doc.GetPathName();
                if (string.IsNullOrEmpty(path)) path = doc.GetTitle(); // new, never-saved document
                Post(AddinMessage.Kinds.ActiveDocument, path, TypeName(doc.GetType()));
                if (doc is DrawingDoc drawing && !_drawings.ContainsKey(path))
                    _drawings[path] = new DrawingWatch(drawing, path, m => _outbox?.Post(m), Log, w => _drawings.Remove(w.Path));
            }
            else
            {
                Post(AddinMessage.Kinds.ActiveDocument);
            }
        });
        return 0;
    }

    private int OnFileClose(string fileName, int reason)
    {
        Safe(() => Post(AddinMessage.Kinds.Close, fileName));
        return 0;
    }

    /// <summary>A model finished loading. Only passed on during a sheet switch, to see which views'' models load.</summary>
    private int OnDocumentLoad(string docTitle, string docPath)
    {
        Safe(() =>
        {
            bool switching = false;
            foreach (var d in _drawings.Values) if (d.Switching) { switching = true; break; }
            if (!switching) { _loadsThisSwitch = 0; return; }
            if (++_loadsThisSwitch > MaxLoadsPerSwitch) return;
            Post(AddinMessage.Kinds.ModelLoad, string.IsNullOrEmpty(docPath) ? docTitle : docPath);
        });
        return 0;
    }

    // ---- "time each view" (Tools menu, drawings only; runs only when you click it) ----

    /// <summary>Called by SOLIDWORKS when you click the menu command. Must be public.</summary>
    public void TimeEachView()
    {
        try
        {
            if (_sw?.ActiveDoc is not DrawingDoc drawing || _sw.ActiveDoc is not ModelDoc2 doc) return;
            string sheetName = ViewTimer.CurrentSheetName(drawing) ?? "";
            if (_sw.SendMsgToUser2(ViewTimer.Warning, 1 /* swMbWarning */, 3 /* swMbOkCancel */) != 4 /* swMbHitOk */)
            {
                Log("View timing cancelled by the user.");
                return;
            }
            Post(AddinMessage.Kinds.ViewTestStart, doc.GetPathName(), "drawing", note: sheetName);
            Log($"View timing started on sheet \"{sheetName}\".");

            var result = ViewTimer.Run(drawing, doc);
            _outbox?.Post(result);
            int count = result.Views?.Count ?? 0;
            Log($"View timing finished: {count} views in {result.Seconds:0.0} s.");
            _sw.SendMsgToUser2(
                $"Timed {count} views in {result.Seconds:0.0} s. The results are in Companion for SOLIDWORKS, section 08." +
                (result.Note == "marked-changed" ? "\n\nSOLIDWORKS now shows the drawing as changed. Nothing in it was changed by the test." : ""),
                2 /* swMbInformation */, 2 /* swMbOk */);
        }
        catch (Exception ex)
        {
            Log("View timing problem: " + ex);
        }
    }
    /// <summary>SOLIDWORKS asks this whether to show the menu command as available: 1 = yes.</summary>
    public int TimeEachViewEnable() => _sw?.ActiveDoc is DrawingDoc ? 1 : 0;

    private void Log(string text) => _log?.Write(text);

    // ---- helpers ----

    private void Post(string kind, string? document = null, string? docType = null, double? seconds = null, string? note = null)
    {
        _outbox?.Post(new AddinMessage
        {
            Kind = kind,
            Time = DateTime.UtcNow,
            Document = document,
            DocType = docType ?? TypeFromPath(document),
            Seconds = seconds,
            Note = note,
        });
    }

    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { _log?.Write("Event problem (ignored): " + ex.GetType().Name + ": " + ex.Message); }
    }

    private static string? TypeName(int swDocumentType) => swDocumentType switch
    {
        1 => "part",      // swDocPART
        2 => "assembly",  // swDocASSEMBLY
        3 => "drawing",   // swDocDRAWING
        _ => null,
    };

    private static string? TypeFromPath(string? path) => Path.GetExtension(path ?? "").ToUpperInvariant() switch
    {
        ".SLDPRT" => "part",
        ".SLDASM" => "assembly",
        ".SLDDRW" => "drawing",
        _ => null,
    };

    /// <summary>The private line: only the signed-in Windows user can connect to it.</summary>
    private static NamedPipeServerStream CreatePipe(string name)
    {
        var security = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }

    // ---- registration (run by RegAsm from the setup script, as administrator) ----

    private static string AddinKey(Type t) => @"SOFTWARE\SolidWorks\Addins\{" + t.GUID.ToString() + "}";
    private static string StartupKey(Type t) => @"Software\SolidWorks\AddInsStartup\{" + t.GUID.ToString() + "}";

    [ComRegisterFunction]
    public static void Register(Type t)
    {
        using (var key = Registry.LocalMachine.CreateSubKey(AddinKey(t)))
        {
            key.SetValue(null, 0);               // 0 = not forced on for every user
            key.SetValue("Title", Title);
            key.SetValue("Description", Description);
        }
        using (var key = Registry.CurrentUser.CreateSubKey(StartupKey(t)))
            key.SetValue(null, 1);               // 1 = load when SOLIDWORKS starts (you can untick it in Tools > Add-Ins)
    }

    [ComUnregisterFunction]
    public static void Unregister(Type t)
    {
        Registry.LocalMachine.DeleteSubKey(AddinKey(t), throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKey(StartupKey(t), throwOnMissingSubKey: false);
    }
}

/// <summary>The add-in's own diary: %LOCALAPPDATA%\SWCompanion\logs\addin-YYYY-MM-DD.log. Never throws.</summary>
internal sealed class AddinLog
{
    private readonly object _lock = new();
    private readonly string _folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.FolderName, "logs");

    public void Write(string text)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_folder);
                var now = DateTime.Now;
                File.AppendAllText(Path.Combine(_folder, $"addin-{now:yyyy-MM-dd}.log"), $"{now:yyyy-MM-dd HH:mm:ss.fff}  {text}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
