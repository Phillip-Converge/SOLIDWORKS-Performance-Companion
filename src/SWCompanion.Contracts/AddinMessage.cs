using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SWCompanion.Contracts;

/// <summary>
/// One note the SOLIDWORKS add-in passes to the Companion, e.g. "opened Bracket.SLDDRW in 4.2 s".
/// Sent as one line of JSON over a private named pipe on this PC. Shared by both sides, so it
/// targets netstandard2.0 (usable inside SOLIDWORKS's .NET Framework) with no extra libraries:
/// the add-in must not load anything that could clash with what SOLIDWORKS itself loads.
/// </summary>
public sealed class AddinMessage
{
    public string Kind { get; set; } = "";
    public DateTime Time { get; set; }
    /// <summary>Full path of the document, if any.</summary>
    public string? Document { get; set; }
    /// <summary>"part", "assembly", "drawing" or null.</summary>
    public string? DocType { get; set; }
    /// <summary>Drawing sheet name, for sheet events.</summary>
    public string? Sheet { get; set; }
    /// <summary>How long the action took, for "...-end" messages.</summary>
    public double? Seconds { get; set; }
    /// <summary>Anything else worth passing on, in plain words.</summary>
    public string? Note { get; set; }
    /// <summary>Facts about each drawing view on a sheet (sheet-views and view-times messages).</summary>
    public List<ViewFacts>? Views { get; set; }

    /// <summary>The kinds of message the add-in sends.</summary>
    public static class Kinds
    {
        public const string Connected = "connected";
        public const string Disconnected = "disconnected";
        public const string OpenStart = "open-start";
        public const string OpenEnd = "open-end";
        public const string ActiveDocument = "active-doc";
        public const string Close = "close";
        public const string SheetStart = "sheet-start";
        public const string SheetEnd = "sheet-end";
        public const string RebuildStart = "rebuild-start";
        public const string RebuildEnd = "rebuild-end";
        public const string SaveStart = "save-start";
        public const string SaveEnd = "save-end";
        public const string Problem = "problem";
        /// <summary>A referenced model finished loading (used to see which views' models load during a sheet switch).</summary>
        public const string ModelLoad = "model-load";
        /// <summary>Facts about each view on a sheet, sent after a slow sheet switch.</summary>
        public const string SheetViews = "sheet-views";
        /// <summary>The owner started the "time each view" test from the SOLIDWORKS menu.</summary>
        public const string ViewTestStart = "view-test-start";
        /// <summary>Results of the "time each view" test: measured seconds per view.</summary>
        public const string ViewTimes = "view-times";
        /// <summary>
        /// After a file opens: which SOLIDWORKS version each file in it (the file and everything it references) was last
        /// saved in. Note holds one line per file: full path, a tab, then SOLIDWORKS's own text for the last saved version.
        /// </summary>
        public const string FileVersions = "file-versions";
    }

    /// <summary>One line of JSON, written by hand so the add-in needs no JSON library.</summary>
    public string ToJson()
    {
        var sb = new StringBuilder("{");
        Field(sb, "kind", Kind, first: true);
        Field(sb, "time", Time.ToString("o", CultureInfo.InvariantCulture));
        Field(sb, "document", Document);
        Field(sb, "docType", DocType);
        Field(sb, "sheet", Sheet);
        if (Seconds is { } s) sb.Append(",\"seconds\":").Append(s.ToString("0.###", CultureInfo.InvariantCulture));
        Field(sb, "note", Note);
        if (Views is not null)
        {
            sb.Append(",\"views\":[");
            for (int i = 0; i < Views.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Views[i].WriteJson(sb);
            }
            sb.Append(']');
        }
        return sb.Append('}').ToString();
    }

    internal static void Number(StringBuilder sb, string name, double? value)
    {
        if (value is { } v) sb.Append(",\"").Append(name).Append("\":").Append(v.ToString("0.###", CultureInfo.InvariantCulture));
    }

    internal static void Flag(StringBuilder sb, string name, bool? value)
    {
        if (value is { } v) sb.Append(",\"").Append(name).Append("\":").Append(v ? "true" : "false");
    }

    internal static void Field(StringBuilder sb, string name, string? value, bool first = false)
    {
        if (value is null) return;
        if (!first) sb.Append(',');
        sb.Append('"').Append(name).Append("\":\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}

/// <summary>
/// What the add-in can read about one drawing view. SOLIDWORKS does not time views on its own;
/// LoadSeconds and DrawSeconds are filled in only by the "time each view" test.
/// </summary>
public sealed class ViewFacts
{
    public string Name { get; set; } = "";
    /// <summary>"section", "detail", "projected", "auxiliary", "standard", "named", "relative", "detached", "alternate position" or null.</summary>
    public string? Type { get; set; }
    /// <summary>Full path of the model the view shows.</summary>
    public string? Model { get; set; }
    public string? Configuration { get; set; }
    /// <summary>"wireframe", "hidden lines visible", "hidden lines removed", "shaded", "shaded with edges", or null.</summary>
    public string? Display { get; set; }
    /// <summary>True when SOLIDWORKS reports the view as using faceted (draft quality) hidden-line display.</summary>
    public bool? DraftQuality { get; set; }
    /// <summary>How many components are visible in the view (assemblies).</summary>
    public int? Components { get; set; }
    public bool? ModelLoaded { get; set; }
    public bool? Lightweight { get; set; }
    public bool? OutOfDate { get; set; }
    public bool? Visible { get; set; }
    /// <summary>Measured: seconds to load the view's model (only if it wasn't loaded).</summary>
    public double? LoadSeconds { get; set; }
    /// <summary>Measured: seconds to redraw the view.</summary>
    public double? DrawSeconds { get; set; }
    /// <summary>Why a measurement couldn't be taken, if it couldn't.</summary>
    public string? Problem { get; set; }

    internal void WriteJson(StringBuilder sb)
    {
        sb.Append('{');
        AddinMessage.Field(sb, "name", Name, first: true);
        AddinMessage.Field(sb, "type", Type);
        AddinMessage.Field(sb, "model", Model);
        AddinMessage.Field(sb, "config", Configuration);
        AddinMessage.Field(sb, "display", Display);
        AddinMessage.Flag(sb, "draft", DraftQuality);
        AddinMessage.Number(sb, "components", Components);
        AddinMessage.Flag(sb, "loaded", ModelLoaded);
        AddinMessage.Flag(sb, "lightweight", Lightweight);
        AddinMessage.Flag(sb, "outOfDate", OutOfDate);
        AddinMessage.Flag(sb, "visible", Visible);
        AddinMessage.Number(sb, "loadSeconds", LoadSeconds);
        AddinMessage.Number(sb, "drawSeconds", DrawSeconds);
        AddinMessage.Field(sb, "problem", Problem);
        sb.Append('}');
    }
}

/// <summary>Where the intercom line lives. One line per SOLIDWORKS process, named after its process ID.</summary>
public static class AddinPipe
{
    public const string Prefix = "SWCompanion.AddIn.v1.";

    public static string NameFor(int solidWorksProcessId) => Prefix + solidWorksProcessId.ToString(CultureInfo.InvariantCulture);
}
