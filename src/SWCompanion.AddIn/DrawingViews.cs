using System;
using System.Collections.Generic;
using System.Diagnostics;
using SolidWorks.Interop.sldworks;
using SWCompanion.Contracts;

namespace SWCompanion.AddIn;

/// <summary>
/// Listens to one open drawing: times each sheet switch, and after a slow one reads the facts
/// about every view on the new sheet. Only reads; never changes the drawing.
/// </summary>
internal sealed class DrawingWatch
{
    /// <summary>Only look closer at sheet switches at least this slow, so quick switches cost nothing extra.</summary>
    public const double SlowSwitchSeconds = 2.0;

    private readonly DrawingDoc _drawing;
    private readonly string _path;
    private readonly Action<AddinMessage> _post;
    private readonly Action<string> _log;
    private readonly Action<DrawingWatch> _gone;
    private Stopwatch? _switch;
    private string? _toSheet;

    /// <summary>True while a sheet switch is under way (the add-in then reports model loads).</summary>
    public bool Switching => _switch is not null;

    public DrawingWatch(DrawingDoc drawing, string path, Action<AddinMessage> post, Action<string> log, Action<DrawingWatch> gone)
    {
        _drawing = drawing;
        _path = path;
        _post = post;
        _log = log;
        _gone = gone;
        drawing.ActivateSheetPreNotify += OnSheetPre;
        drawing.ActivateSheetPostNotify += OnSheetPost;
        drawing.DestroyNotify2 += OnDestroy;
    }

    public string Path => _path;

    public void Unhook()
    {
        try
        {
            _drawing.ActivateSheetPreNotify -= OnSheetPre;
            _drawing.ActivateSheetPostNotify -= OnSheetPost;
            _drawing.DestroyNotify2 -= OnDestroy;
        }
        catch (Exception ex) { _log("Unhook problem (ignored): " + ex.Message); }
    }

    private int OnSheetPre(string sheetName)
    {
        try
        {
            _switch = Stopwatch.StartNew();
            _toSheet = sheetName;
            _post(Msg(AddinMessage.Kinds.SheetStart, sheetName));
        }
        catch (Exception ex) { _log("Sheet start problem (ignored): " + ex.Message); }
        return 0;
    }

    private int OnSheetPost(string sheetName)
    {
        try
        {
            double seconds = _switch?.Elapsed.TotalSeconds ?? 0;
            _switch = null;
            var end = Msg(AddinMessage.Kinds.SheetEnd, sheetName);
            end.Seconds = seconds;
            _post(end);

            if (seconds >= SlowSwitchSeconds)
            {
                var read = Stopwatch.StartNew();
                var views = ViewReader.Read(_drawing);
                var facts = Msg(AddinMessage.Kinds.SheetViews, sheetName);
                facts.Views = views;
                facts.Seconds = seconds;
                facts.Note = $"read {views.Count} views in {read.Elapsed.TotalSeconds:0.00} s";
                _post(facts);
            }
        }
        catch (Exception ex) { _log("Sheet end problem (ignored): " + ex.Message); }
        finally { _toSheet = null; }
        return 0;
    }

    private int OnDestroy(int destroyType)
    {
        Unhook();
        _gone(this);
        return 0;
    }

    private AddinMessage Msg(string kind, string sheet) => new()
    {
        Kind = kind,
        Time = DateTime.UtcNow,
        Document = _path,
        DocType = "drawing",
        Sheet = sheet ?? _toSheet,
    };
}

/// <summary>
/// The "time each view" test, shared by the add-in (Tools menu) and the Companion's outside link
/// (button). Loads (if needed) and redraws each view on the current sheet one at a time and times it.
/// Changes and saves nothing; reports whether SOLIDWORKS marked the drawing as changed anyway.
/// </summary>
internal static class ViewTimer
{
    public const string Warning =
        "Companion for SOLIDWORKS will load and redraw each view on the current sheet one at a time and time it.\n\n" +
        "It doesn't change or save anything, but SOLIDWORKS may afterwards mark the drawing as changed (an asterisk in the title). " +
        "If so, you can close without saving, or save: either is the same as before.\n\n" +
        "On a large sheet this can take as long as switching to the sheet does. Carry on?";

    public static string? CurrentSheetName(DrawingDoc drawing) => (drawing.GetCurrentSheet() as Sheet)?.GetName();

    public static AddinMessage Run(DrawingDoc drawing, ModelDoc2 doc)
    {
        bool wasChanged = doc.GetSaveFlag();
        var sheet = drawing.GetCurrentSheet() as Sheet;
        var results = new List<ViewFacts>();
        var whole = Stopwatch.StartNew();
        if (sheet?.GetViews() is object[] views)
        {
            foreach (var o in views)
            {
                if (o is not View v) continue;
                var f = ViewReader.Facts(v);
                try
                {
                    if (f.ModelLoaded == false)
                    {
                        var load = Stopwatch.StartNew();
                        v.LoadModel();
                        f.LoadSeconds = load.Elapsed.TotalSeconds;
                    }
                    var draw = Stopwatch.StartNew();
                    v.UpdateViewDisplayGeometry();
                    f.DrawSeconds = draw.Elapsed.TotalSeconds;
                }
                catch (Exception ex)
                {
                    f.Problem = "couldn't time this view: " + ex.Message;
                }
                results.Add(f);
            }
        }
        bool nowChanged = doc.GetSaveFlag();
        return new AddinMessage
        {
            Kind = AddinMessage.Kinds.ViewTimes,
            Time = DateTime.UtcNow,
            Document = doc.GetPathName(),
            DocType = "drawing",
            Sheet = sheet?.GetName() ?? "",
            Seconds = whole.Elapsed.TotalSeconds,
            Views = results,
            Note = !wasChanged && nowChanged ? "marked-changed" : null,
        };
    }
}

/// <summary>Reads what SOLIDWORKS will tell us about the views on the current sheet.</summary>
internal static class ViewReader
{
    public static List<ViewFacts> Read(DrawingDoc drawing)
    {
        var list = new List<ViewFacts>();
        if (drawing.GetCurrentSheet() is not Sheet sheet) return list;
        if (sheet.GetViews() is not object[] views) return list;
        foreach (var o in views)
            if (o is View v) list.Add(Facts(v));
        return list;
    }

    public static ViewFacts Facts(View v)
    {
        var f = new ViewFacts();
        // Each fact on its own, so one awkward view setting doesn't lose the rest.
        Try(() => f.Name = v.GetName2() ?? "");
        Try(() => f.Type = TypeName(v.Type));
        Try(() => f.Model = NullIfEmpty(v.GetReferencedModelName()));
        Try(() => f.Configuration = NullIfEmpty(v.ReferencedConfiguration));
        Try(() =>
        {
            int mode = v.GetDisplayMode2();
            f.Display = DisplayName(mode);
            f.DraftQuality = mode is 4 or 5 or 6 || v.GetFacettedHlrDisplay();
        });
        Try(() => f.ModelLoaded = v.IsModelLoaded());
        Try(() => f.Lightweight = v.IsLightweight());
        Try(() => f.OutOfDate = v.IsModelOutOfDate());
        Try(() => f.Visible = v.GetVisible());
        Try(() =>
        {
            int n = v.GetVisibleComponentCount();
            if (n > 0) f.Components = n;
        });
        return f;
    }

    private static void Try(Action a)
    {
        try { a(); } catch { }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // swDrawingViewTypes_e
    private static string? TypeName(int t) => t switch
    {
        2 => "section",
        3 => "detail",
        4 => "projected",
        5 => "auxiliary",
        6 => "standard",
        7 => "named",
        8 => "relative",
        9 => "detached",
        10 => "alternate position",
        _ => null,
    };

    // swDisplayMode_e (4-6 are the faceted, i.e. draft quality, versions of 0-2)
    private static string? DisplayName(int m) => m switch
    {
        0 or 4 => "wireframe",
        1 or 5 => "hidden lines visible",
        2 or 6 => "hidden lines removed",
        3 => "shaded",
        7 => "shaded with edges",
        8 => "same as parent view",
        _ => null,
    };
}
