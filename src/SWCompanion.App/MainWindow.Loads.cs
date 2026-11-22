using SWCompanion.Core.Now;

namespace SWCompanion.App;

/// <summary>
/// Times how long each SOLIDWORKS window (or command panel) takes to be ready after you click, and flags it when it
/// takes clearly longer than normal on this PC. Once it's ready, the time it sits waiting for you isn't counted.
/// </summary>
public partial class MainWindow
{
    private readonly LoadTracker _loadTracker = new();
    private readonly LoadHistory _loadHistory = new(LoadHistory.DefaultPath);
    private WindowLoadWatcher? _loadWatch;

    private void SetupLoads()
    {
        _loadWatch = new WindowLoadWatcher(_loadTracker);
        _loadWatch.Ready += t => Dispatcher.BeginInvoke(() => OnLoaded(t));
        _link.CommandStarted += (time, name) => _loadTracker.CommandStarted(time, name);
        Closed += (_, _) => _loadWatch.Dispose();
    }

    /// <summary>Every second, from the refresh: which SOLIDWORKS to watch and its main window.</summary>
    private void WatchLoads(int? pid, IReadOnlyList<SwWindow> windows) =>
        _loadWatch?.Watch(pid, windows.FirstOrDefault(w => w.IsMain)?.Handle ?? IntPtr.Zero);

    private void OnLoaded(LoadTiming t)
    {
        var v = _loadHistory.Judge(t);
        string name = t.IsWindow ? $"the \"{LoadHistory.Normalize(t.What)}\" window" : $"\"{t.What}\"";
        string from = t.FromClick ? "from your click" : "from the window appearing";
        _log.Write($"load: {(t.IsWindow ? "window" : "command")} \"{t.What}\" ready in {t.Seconds:0.00} s {from}" +
                   (t.Command is not null && t.IsWindow ? $" (command {t.Command})" : "") +
                   $"; {v.Reason}; {v.Samples} earlier timing(s).", t.Ready);

        if (!v.Slow)
        {
            // Normal: a quiet line for windows that took a noticeable moment; nothing for quick ones or panels.
            if (t.IsWindow && t.Seconds >= 0.5)
                _story.Note(t.Ready, $"{Capital(name)} was ready in {t.Seconds:0.0} s ({(v.Usual is null ? "first time timed" : "normal here")}).", StoryTone.Normal);
            return;
        }

        string detail = $"It took {t.Seconds:0.0} s {from} until it was ready, {v.Reason}. " +
                        (t.FromClick ? "" : "SOLIDWORKS didn't announce a command for it, so any time before the window appeared isn't counted. ") +
                        "The time it then waited for you isn't counted.";
        _catcher.Report(new Moment(t.Start, t.Ready - t.Start, "slow-load", $"{Capital(name)} took {t.Seconds:0.0} s to be ready",
            detail, LookInto: true));
        ShowCaught();
    }

    /// <summary>
    /// After a file opens: were it or the files it uses saved in an older SOLIDWORKS? Each such file is converted on
    /// every open. Flagged as a hang-up when the open was slow or many files are old; otherwise a note in the log.
    /// </summary>
    private void ShowOldVersions(string opened, string? note, DateTime now)
    {
        if (_versions?.SolidWorks?.Year is not { } running) return;
        string name = System.IO.Path.GetFileName(opened);
        double? openSeconds = _addinTracker.Finished.FirstOrDefault(a => a.What == "open" &&
            string.Equals(a.FileName, name, StringComparison.OrdinalIgnoreCase))?.Seconds;
        var r = Core.Files.OldVersions.Read(opened, note, running, openSeconds);
        _log.Write($"file versions: {name}: {r.Total} file(s) read, {r.Old.Count} saved in an older SOLIDWORKS" +
                   (r.Unknown > 0 ? $", {r.Unknown} unreadable" : "") + (r.Any ? ". " + Core.Files.OldVersions.Detail(r) : "."), now);
        if (!r.Any) return;
        if (Core.Files.OldVersions.WorthFlagging(r))
        {
            _catcher.Report(new Moment(now, TimeSpan.Zero, "old-versions", Core.Files.OldVersions.Line(r), Core.Files.OldVersions.Detail(r), LookInto: true));
            ShowCaught();
        }
        else _story.Note(now, "Worth knowing: " + Core.Files.OldVersions.Line(r) + ".", StoryTone.Warn);
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
