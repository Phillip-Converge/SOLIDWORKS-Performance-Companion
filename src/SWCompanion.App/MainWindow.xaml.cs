using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SWCompanion.Core;
using SWCompanion.Contracts;
using SWCompanion.Core.AddIn;
using SWCompanion.Core.Antivirus;
using SWCompanion.Core.Network;
using SWCompanion.Link;
using SWCompanion.Core.Checkup;
using SWCompanion.Core.Crash;
using SWCompanion.Core.Cpu;
using SWCompanion.Core.Files;
using SWCompanion.Core.Gpu;
using SWCompanion.Core.Logging;
using SWCompanion.Core.Io;
using SWCompanion.Core.Memory;
using SWCompanion.Core.Now;
using SWCompanion.Core.Pdm;
using SWCompanion.Core.Processes;

namespace SWCompanion.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private const int ProcessScanEveryTicks = 2;

    private readonly DispatcherTimer _timer = new() { Interval = Tick };
    private readonly CpuSampler _cpu = new();
    private readonly CpuSmoother _cpuSmoother = new(window: 3);
    private readonly CpuTopology _topology = CpuTopology.Read();
    private readonly MemorySampler _memory = new();
    private readonly WhyPanel _cpuWhy;
    private readonly WhyPanel _memoryWhy;
    private readonly GpuSampler _gpu = new();
    private readonly WhyPanel _gpuWhy;
    private GpuSnapshot? _lastGpu;
    private readonly IoSampler _io = new();
    private readonly WhyPanel _ioWhy;
    private IoSnapshot? _lastIo;
    private readonly OtherProgramsSampler _others = new();
    private readonly FreezeTracker _freeze = new();
    private OtherPrograms? _lastOthers;
    private readonly FileActivityTracker _files = new();
    private readonly FileTraceSession _fileTrace;
    private readonly WhyPanel _filesWhy;
    private readonly ActivityLog _log = new();
    private IReadOnlyList<FileActivity> _activeFiles = [];
    private FreezeEpisode? _loggedEpisode;
    private readonly TrafficMapView _map = new();
    private bool _showMapExample;
    private NowReport? _lastNow;
    private VerdictReport? _lastVerdictReport;
    private readonly VaultTrafficTracker _vaultTraffic = new();
    private IReadOnlyList<ServerTraffic> _vaultTrafficNow = [];
    private readonly VaultNotesStore _vaultNotes = new();
    private IReadOnlyList<VaultView> _vaults = [];
    private IReadOnlyDictionary<string, NameLookup> _lookups = new Dictionary<string, NameLookup>();
    private IReadOnlyList<HostsEntry> _hosts = [];
    private readonly Dictionary<string, List<PortTest>> _portTests = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CheckResult> _baseChecks = [];
    private string? _selectedVault;
    private readonly LifeSigns _life = new();
    private int? _lifePid;
    private string? _lastAlertKey;
    private Verdict _lastVerdict = Verdict.None;
    private const int CheckupEveryTicks = 10;
    private readonly HashSet<string> _openFixes = new();
    private IReadOnlyList<CheckResult> _checks = [];
    private CpuSnapshot? _lastSmoothed;
    private MemorySnapshot? _lastMemory;
    private IReadOnlyList<SeenProcess> _processes = [];
    private int _tickCount;
    private bool _busy;
    private readonly AddinListener _addin = new();
    private AddinTracker _addinTracker = new();
    private int? _addinPid;
    private InstalledVersions? _versions;
    private SheetReport? _shownSheetReport;
    private readonly Layout _layout;
    private readonly DispatcherTimer _notesTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _loadingNotes;
    private readonly MomentCatcher _catcher = new();
    private readonly MomentStore _moments = new();
    private int _shownCaught = -1, _shownUnseen = -1;
    private readonly LiveStory _story = new();
    private readonly DefenderSampler _defender = new();
    private readonly CrashWatch _crashWatch = new();
    private IReadOnlyList<CrashRecord> _crashes = [];
    private readonly List<Issue> _crashIssues = new();
    private (int Pid, DateTime Started)? _swStart;
    private readonly VpnRate _vpnRate = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<StoryRow> _storyRows = new();
    private string? _issueSignature;
    private readonly SolidWorksLink _link = new();
    private DateTime _swSeenAt, _lastLinkTry = DateTime.MinValue;
    private AddinTracker.Line? _loggedLine;
    private static readonly TimeSpan LinkStartDelay = TimeSpan.FromSeconds(8); // give an installed add-in the first chance
    private static readonly TimeSpan LinkRetry = TimeSpan.FromSeconds(10);
    private string? _versionsFor = ""; // which program file the version was read from (""= not read yet)

    public MainWindow()
    {
        InitializeComponent();
        _layout = new Layout(this, Sections, PageDetails, NowScroll, NowHeader, NowChevron); // foldable sections, steady live boxes
        SetupAssist();
        // Each page scrolls gently; panels that scroll inside pass the wheel on to their page at their ends.
        foreach (var page in new[] { PagePerformance, PageFixes, PageCrashes, PageDetails, PageSettings }) Layout.GentleWheel(page);
        Layout.ForwardWheel(CaughtScroll, PagePerformance);
        Layout.ForwardWheel(StoryScroll, PagePerformance);
        Layout.ForwardWheel(IssueScroll, PageFixes);
        Layout.ForwardWheel(VitalsScroll, PageDetails);
        // The Details page lays its sections out as tiles, two or three across when there's room.
        Loaded += (_, _) => _layout.SetupDetailTiles(this, DetailTilesHost, sidebarWidth: 280);
        RestoreWindowPlace();
        ThemeKey.Content = App.Dark ? "light mode" : "dark mode";
        // Who made the Companion: shown to everyone, at the bottom of the sidebar (the owner's choice, 2026-11-22).
        CreditText.Text = AppInfo.Credit;
        CreditText.Visibility = Visibility.Visible;

        // Things you entered last time: the vault you were looking at and its last port tests.
        _selectedVault = App.Settings.SelectedVault;
        foreach (var (vault, tests) in App.Settings.PortTests) _portTests[vault] = tests;

        // Vault notes save themselves a moment after you stop typing or choosing (the button still works too).
        _notesTimer.Tick += (_, _) => { _notesTimer.Stop(); SaveNotes(clicked: false); };
        foreach (var box in new[] { NoteDatabaseWhere, NoteArchiveWhere, NoteLink, NoteReplicated })
            box.SelectionChanged += (_, _) => NotesChanged();
        NoteText.TextChanged += (_, _) => NotesChanged();
        Closing += (_, _) =>
        {
            if (_notesTimer.IsEnabled) { _notesTimer.Stop(); SaveNotes(clicked: false); } // keep the last few keystrokes
            SaveWindowPlace();
        };

        // The running story: each new line is added at the bottom; nothing above it moves.
        StoryRows.ItemsSource = _storyRows;
        _story.NewLine += line =>
        {
            // Follow the newest line only if you were already at the bottom (so reading back isn't interrupted).
            bool atBottom = StoryScroll.VerticalOffset >= StoryScroll.ScrollableHeight - 4;
            var row = StoryRow.From(line, _nextLineIsHang);
            if (_nextLineIsHang) _lastHangRow = row;
            _nextLineIsHang = false;
            _storyRows.Add(row);
            StoryEmpty.Visibility = Visibility.Collapsed;
            while (_storyRows.Count > LiveStory.Keep) _storyRows.RemoveAt(0);
            if (atBottom) StoryScroll.ScrollToEnd();
        };

        // Things that come and go: keep them on screen, save them, and note them in the activity log.
        _catcher.Restore(_moments.LoadDay(DateTime.Now));
        // For a slow open, save or sheet switch: what Defender did meanwhile (processor always; files scanned with file tracing).
        _catcher.ActionNote = (from, to) =>
        {
            var scanned = _fileTrace?.IsRunning == true // set up a few lines further down; this only runs later
                ? _files.Recent(DateTime.Now, FileActivityTracker.KeepFor).Where(f => f.AntivirusFirst is { } a && a >= from.AddSeconds(-1) && a <= to.AddSeconds(1)).ToList()
                : [];
            return DefenderExplainer.DuringAction(_defender.CpuSecondsBetween(from, to),
                scanned.Count > 0 ? scanned.Sum(f => f.AntivirusTime.TotalSeconds) : null, scanned.Count);
        };
        // Past crashes, read once in the background at start (and again after a crash).
        Loaded += async (_, _) => { _crashes = await Task.Run(() => CrashReader.ReadAll()); ShowCrashes(); };
        _catcher.NewMoment += m =>
        {
            PullOut(m);
            if (m.Title.Contains("(demo)")) return; // the --demo-hang pretend event is never saved
            _moments.Save(m);
            _log.Write("caught: " + m.Line + (m.LookInto ? "   [look into this]" : "") + "   " + m.Detail, m.Start);
        };
        CaughtBadge.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowPage("performance");
            CaughtSection.BringIntoView();
            _catcher.MarkSeen();
            ShowCaught();
        };
        DataFoldersText.Text = "Logs and reports will be saved to: " + string.Join("  and  ", AppPaths.DataFolders);
        var keyStyle = (Style)FindResource("KeyButton");
        _cpuWhy = new WhyPanel(QuestionKeys, AnswerBox, AnswerTitle, AnswerText, CpuExplainer.Questions,
            id => _lastSmoothed is null ? null : CpuExplainer.Answer(id, _lastSmoothed, _topology), keyStyle);
        _memoryWhy = new WhyPanel(MemoryKeys, MemoryAnswerBox, MemoryAnswerTitle, MemoryAnswerText, MemoryExplainer.Questions,
            id => _lastMemory is null ? null : MemoryExplainer.Answer(id, _lastMemory), keyStyle);
        _gpuWhy = new WhyPanel(GpuKeys, GpuAnswerBox, GpuAnswerTitle, GpuAnswerText, GpuExplainer.Questions,
            id => _lastGpu is null ? null : GpuExplainer.Answer(id, _lastGpu), keyStyle);
        _ioWhy = new WhyPanel(IoKeys, IoAnswerBox, IoAnswerTitle, IoAnswerText, IoExplainer.Questions,
            id => _lastIo is null ? null : IoExplainer.Answer(id, _lastIo), keyStyle);

        MapHost.Content = _map;
        NoteDatabaseWhere.ItemsSource = VaultNotesStore.Places;
        NoteArchiveWhere.ItemsSource = VaultNotesStore.Places;
        NoteLink.ItemsSource = VaultNotesStore.Links;
        NoteReplicated.ItemsSource = VaultNotesStore.YesNo;
        foreach (var box in new[] { NoteDatabaseWhere, NoteArchiveWhere, NoteLink, NoteReplicated }) box.SelectedIndex = 0;

        _filesWhy = new WhyPanel(FilesKeys, FilesAnswerBox, FilesAnswerTitle, FilesAnswerText, FileExplainer.Questions,
            FileExplainer.Answer, keyStyle);

        // File tracing: starts only if the Companion is running as administrator.
        _fileTrace = new FileTraceSession(_files, _vaultTraffic); // also watches traffic to the vault's servers only
        foreach (var pid in FileTraceSession.FindAntivirusPids()) _files.AntivirusPids.Add(pid);
        _fileTrace.Start();
        _files.FileFinished += f =>
        {
            if (f.IsSolidWorksDocument || f.WaitTime >= FileExplainer.Slow)
                _log.Write("file: " + FileExplainer.Line(f), f.LastSeen);
            _catcher.AddFile(f);
        };
        _log.Write(_fileTrace.IsRunning
            ? "Companion started. File tracing is on."
            : $"Companion started. File tracing is off ({_fileTrace.NotRunningReason})");

        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { await RefreshAsync(); _timer.Start(); };
        Closed += (_, _) =>
        {
            _timer.Stop(); _cpu.Dispose(); _memory.Dispose();
            _fileTrace.Dispose(); // stops the Windows trace session
            _addin.Dispose();
            _link.Dispose();
            _log.Write("Companion closed.");
        };
    }

    private async Task RefreshAsync()
    {
        if (_busy) return; // never pile up readings if one is slow
        _busy = true;
        try
        {
            // 3 s after start (graphics needs two readings first), then every 10 s
            bool runCheckup = _tickCount % CheckupEveryTicks == 3 || _checkNow; // or straight away after "I did it, check it"
            _checkNow = false;
            bool scanProcesses = _tickCount++ % ProcessScanEveryTicks == 0;

            // All reading happens on a background thread so the window never freezes.
            var (processes, cpu, memory, gpu, io, others, windows, responding, life) = await Task.Run(() =>
            {
                var procs = scanProcesses ? SolidWorksProcessFinder.Scan() : _processes;
                var main = procs.FirstOrDefault(p => p.Role == ProcessRole.Main);
                int? swPid = main?.Id;

                // Is SOLIDWORKS answering? Asked every second, from this background thread.
                IReadOnlyList<SwWindow> wins = swPid is null ? [] : HangProbe.ListWindows(swPid.Value);
                // Ask each SOLIDWORKS box (at most two) whether it answers on its own, to tell
                // SOLIDWORKS's "busy" notice apart from a question it's waiting on.
                wins = wins.Select((w, i) => w.IsDialog && !w.IsMain && i <= 2 ? w with { Responding = HangProbe.IsResponding(w.Handle, 200) } : w).ToList();
                var mainWindow = wins.FirstOrDefault(w => w.IsMain);
                bool? answering = mainWindow is null ? null : HangProbe.IsResponding(mainWindow.Handle, 300);
                // Signs of life: is SOLIDWORKS still doing anything at all?
                var lifeSample = swPid is null ? null : LifeSignsSampler.Sample(swPid.Value, mainWindow?.Handle ?? IntPtr.Zero, DateTime.Now);

                // Memory, graphics, disk, network and other programs are read with the program list (every 2 s) to save CPU.
                var exclude = new HashSet<int> { Environment.ProcessId };
                if (swPid is { } id) exclude.Add(id);
                _defender.Sample(DateTime.Now); // Defender's processor use, every second, no administrator needed
                return (procs, _cpu.Sample(swPid),
                        scanProcesses ? _memory.Sample(swPid) : null,
                        scanProcesses ? _gpu.Sample(swPid, main?.Path) : null,
                        scanProcesses ? _io.Sample(swPid) : null,
                        scanProcesses ? _others.Sample(exclude) : null,
                        wins, answering, lifeSample);
            });

            if (scanProcesses)
            {
                _processes = processes;
                ProcessGrid.ItemsSource = processes.Select(Row.From).ToList();
                // Which SOLIDWORKS version: re-read only when the running program changes.
                string? mainPath = processes.FirstOrDefault(p => p.Role == ProcessRole.Main)?.Path;
                if (mainPath != _versionsFor)
                {
                    _versionsFor = mainPath;
                    _versions = await Task.Run(() => InstalledVersions.Read(mainPath));
                    if (_versions.SolidWorks is not null) _log.Write("version: " + _versions.Describe(mainPath is not null));
                }
                StatusText.Text = DescribeStatus(processes) + (_versions is null ? "" : "  " + _versions.Describe(mainPath is not null));
                StatusDot.Fill = processes.Any(p => p.Role == ProcessRole.Main) ? Theme.Ok : Theme.Dim;
            }

            if (cpu is not null) { ShowCpu(cpu); ShowVitals(cpu); }
            if (memory is not null) ShowMemory(memory);
            if (gpu is not null) ShowGpu(gpu);
            if (io is not null) ShowIo(io);
            if (others is not null) _lastOthers = others;
            ShowFiles(processes);
            _vaultTrafficNow = _fileTrace.IsRunning ? _vaultTraffic.Snapshot(DateTime.Now, TimeSpan.FromSeconds(5)) : [];
            if (scanProcesses) ShowVaultTraffic();
            int? currentPid = _processes.FirstOrDefault(p => p.Role == ProcessRole.Main)?.Id;
            if (currentPid != _lifePid) { _life.Reset(); _lifePid = currentPid; }
            if (life is not null) _life.Add(life);
            ShowAddin(currentPid, responding);
            WatchLoads(currentPid, windows);
            ShowNow(windows, responding);
            if (runCheckup)
            {
                var gpuNow = _lastGpu;
                string? swFolder = _versions?.SolidWorksPath is { } swExe ? System.IO.Path.GetDirectoryName(swExe) : null;
                string? activeDoc = _addinTracker.ActiveDocument;
                (_baseChecks, _vaults, _lookups, _hosts) = await Task.Run(() =>
                {
                    // Machine checks plus the owner's optimization checklist.
                    var checklist = ChecklistReader.Read();
                    var basic = CheckupEvaluator.Evaluate(CheckupReader.Read(), gpuNow)
                        .Concat(ChecklistEvaluator.Evaluate(checklist))
                        // Antivirus, Defender's folder protection, and folders SOLIDWORKS can't write to (read only).
                        .Concat(Core.Antivirus.SecurityEvaluator.Evaluate(Core.Antivirus.SecurityReader.Read(checklist.SolidWorksYear))).ToList();
                    // Vault views and their servers' names (read only; lookups only, nothing is sent to the servers).
                    var vaults = VaultDiscovery.Read();
                    var names = vaults.SelectMany(v => new[] { v.ArchiveServer, v.DatabaseHost })
                        .Where(n => n is not null).Distinct(StringComparer.OrdinalIgnoreCase);
                    var lookups = names.ToDictionary(n => n!, n => NetworkChecks.Lookup(n!), StringComparer.OrdinalIgnoreCase);
                    var hosts = vaults.Count > 0 ? NetworkChecks.ReadHostsFile() : [];

                    // VPN: does the vault's or the network folders' traffic go through one? (Windows is asked; nothing is sent.)
                    var servers = vaults.SelectMany(v => new[] { ("archive", v.ArchiveServer), ("database", v.DatabaseHost) })
                        .Concat(checklist.FileLocations.Select(l => ("network folder", VpnCheck.ServerOf(l.Path))))
                        .Append(("open document's folder", VpnCheck.ServerOf(activeDoc)))
                        .Where(s => s.Item2 is not null).Select(s => (s.Item1, s.Item2!)).ToList();
                    foreach (var name in servers.Select(s => s.Item2).Where(n => !lookups.ContainsKey(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                        lookups[name] = NetworkChecks.Lookup(name);
                    var addresses = lookups.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.Addresses.Select(a => a.ToString()).ToList(), StringComparer.OrdinalIgnoreCase);
                    var vpn = VpnCheck.Read(servers, addresses);
                    vpn = vpn with { VpnBytesPerSecond = _vpnRate.Update(DateTime.Now, vpn.Vpns) };
                    basic.Add(VpnExplainer.Evaluate(vpn));

                    // Defender: are the folders SOLIDWORKS reads most excluded? (Only readable as administrator.)
                    var folders = new List<(string, string)>();
                    if (swFolder is not null) folders.Add(("the SOLIDWORKS program folder", swFolder));
                    folders.AddRange(vaults.Where(v => v.LocalFolder is not null).Select(v => ($"the {v.Name} vault folder", v.LocalFolder!)));
                    basic.Add(DefenderExplainer.Exclusions(DefenderExclusions.Read(), folders));
                    return ((IReadOnlyList<CheckResult>)basic, vaults, (IReadOnlyDictionary<string, NameLookup>)lookups, hosts);
                });
                // Tell the traffic tracker which addresses and ports are the vault's servers.
                _vaultTraffic.SetTargets(_vaults.SelectMany(v => new[]
                {
                    v.ArchiveServer is { } a ? new VaultServerTarget("archive", a, _lookups.TryGetValue(a, out var la) ? la.Addresses : [], v.ArchivePortOrDefault) : null,
                    v.DatabaseHost is { } d && v.SqlPort is { } p ? new VaultServerTarget("database", d, _lookups.TryGetValue(d, out var ld) ? ld.Addresses : [], p) : null,
                }).OfType<VaultServerTarget>());
                RebuildChecks();
                ShowVault();
            }
            ReadOnlyText.ToolTip = "This app watches and never changes or stops anything.\n" + DataFoldersText.Text + "\n" + $"Last checked {DateTime.Now:HH:mm:ss}. CPU every {Tick.TotalSeconds:0} s; memory, graphics and program list every {Tick.TotalSeconds * ProcessScanEveryTicks:0} s.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not take a reading: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowCpu(CpuSnapshot cpu)
    {
        // The sentence and explanations use the last 3 seconds so they don't
        // flicker; the numbers and bars below show this second, live.
        var smoothed = _cpuSmoother.Add(cpu);
        _lastSmoothed = smoothed;
        CpuSummaryText.Text = CpuInterpreter.Describe(smoothed);
        ExplainText.Text = CpuExplainer.WhatIsHappening(smoothed);
        _cpuWhy.Refresh();

        var sw = cpu.SolidWorks;
        CpuDetailText.Text = sw is null
            ? $"This second: whole PC {cpu.SystemPercent:0}%."
            : $"This second: whole PC {cpu.SystemPercent:0}%.   SOLIDWORKS: {CpuInterpreter.Cores(sw.CoresWorth)} ({sw.PercentOfPc:0}% of the PC).   " +
              (sw.ThreadsVisible
                  ? $"Busiest SOLIDWORKS thread: {sw.TopThreadPercent:0}% of a core.   Threads using over half a core: {sw.BusyThreads}."
                  : "Threads: hidden by Windows.");

        CoreBars.ItemsSource = cpu.CorePercent
            .Select((pct, i) => CoreBar.From(i, pct, _topology))
            .ToList();
    }

    /// <summary>The program table sits inside the scrolling page; pass the mouse wheel
    /// to the page so scrolling over the table moves the whole window as expected.</summary>
    private void ProcessGrid_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        PageDetails.ScrollToVerticalOffset(PageDetails.VerticalOffset - Layout.WheelAmount(e));
        e.Handled = true;
    }

    // ---- memory ----

    private void ShowMemory(MemorySnapshot m)
    {
        _lastMemory = m;
        MemorySummaryText.Text = MemoryExplainer.Describe(m);
        MemoryExplainText.Text = MemoryExplainer.WhatIsHappening(m);
        _memoryWhy.Refresh();

        var sw = m.SolidWorks;
        MemoryDetailText.Text = sw is null
            ? "SOLIDWORKS: not running."
            : $"SOLIDWORKS: {MemoryExplainer.Gb((ulong)sw.InRam)} in RAM, {MemoryExplainer.Gb((ulong)sw.Committed)} set aside, " +
              $"most in RAM so far {MemoryExplainer.Gb((ulong)sw.PeakInRam)}.   Other Windows handles: {sw.Handles:N0}.";

        double total = Math.Max(1, m.TotalRam);
        double limit = Math.Max(1, m.CommitLimit);
        var meters = new List<Meter>
        {
            Meter.Stacked("ram (memory chips)",
                sw is null ? 0 : sw.InRam / total, m.UsedRam / total,
                $"{MemoryExplainer.Gb(m.UsedRam)} / {MemoryExplainer.Gb(m.TotalRam)}",
                m.RamUsedPercent >= MemoryExplainer.RamShort,
                $"RAM: {m.RamUsedPercent:0}% in use. Orange part = SOLIDWORKS."),
            Meter.Stacked("total limit (ram + page file)",
                sw is null ? 0 : sw.Committed / limit, m.CommitUsed / limit,
                $"{MemoryExplainer.Gb(m.CommitUsed)} / {MemoryExplainer.Gb(m.CommitLimit)}",
                m.CommitUsedPercent >= MemoryExplainer.CommitNearLimit,
                $"Windows' total memory limit: {m.CommitUsedPercent:0}% set aside. Orange part = SOLIDWORKS."),
        };
        if (sw is not null)
        {
            meters.Add(Meter.Level("sw drawing objects (gdi)", sw.GdiPercent / 100,
                $"{sw.GdiObjects:N0} / {sw.GdiLimit:N0}", $"Most so far: {sw.GdiPeak:N0}"));
            meters.Add(Meter.Level("sw window objects (user)", sw.UserPercent / 100,
                $"{sw.UserObjects:N0} / {sw.UserLimit:N0}", $"Most so far: {sw.UserPeak:N0}"));
        }
        MemoryMeters.ItemsSource = meters;
    }

    // ---- inside SOLIDWORKS (the add-in) ----

    private void ShowAddin(int? swPid, bool? responding)
    {
        var now = DateTime.Now;
        _addin.Watch(swPid is { } id ? [id] : []);
        if (swPid != _addinPid)
        {
            // A new (or no) SOLIDWORKS starts with a clean slate.
            _addinTracker = new();
            _addinPid = swPid;
            _swSeenAt = now;
            _lastLinkTry = DateTime.MinValue;
        }

        // Prefer the add-in when it's installed; otherwise connect from outside once SOLIDWORKS has had time to start.
        bool viaAddin = swPid is { } p && _addin.ConnectedTo.Contains(p);
        if (swPid is null || viaAddin)
        {
            if (_link.ConnectedTo is not null) _link.Detach();
        }
        else if (_link.ConnectedTo != swPid && now - _swSeenAt >= LinkStartDelay && now - _lastLinkTry >= LinkRetry)
        {
            _lastLinkTry = now;
            _link.Attach(swPid.Value);
        }
        bool viaLink = !viaAddin && swPid is not null && _link.ConnectedTo == swPid;
        var line = viaAddin ? AddinTracker.Line.AddIn : viaLink ? AddinTracker.Line.Outside : AddinTracker.Line.None;
        if (line != _loggedLine && swPid is not null)
        {
            _loggedLine = line;
            _log.Write(line switch
            {
                AddinTracker.Line.AddIn => "inside solidworks: connected through the add-in",
                AddinTracker.Line.Outside => "inside solidworks: connected from outside (no add-in)",
                _ => "inside solidworks: not connected" + (_link.Problem is { } why ? " (" + why + ")" : ""),
            }, now);
        }
        _addinTracker.HowToMeasure = viaAddin ? null : SheetAnalysis.ButtonHint;

        var incoming = _addin.Drain().Select(x => x.Message).Concat(_link.Drain()).ToList();
        foreach (var m in incoming)
        {
            _addinTracker.Apply(m, now);
            // Finished actions go in the activity log (local only; file names stay on this PC).
            if ((m.Kind.EndsWith("-end", StringComparison.Ordinal) || m.Kind == AddinMessage.Kinds.ViewTimes)
                && _addinTracker.Finished.FirstOrDefault() is { } done)
            {
                _log.Write("solidworks: " + AddinTracker.Describe(done), now);
                _catcher.AddAction(done);
            }
            if (m.Kind == AddinMessage.Kinds.FileVersions && m.Document is { } opened) ShowOldVersions(opened, m.Note, now);
        }

        bool connected = line != AddinTracker.Line.None;
        AddinStatusLabel.Text = line switch
        {
            AddinTracker.Line.AddIn => "connected: add-in",
            AddinTracker.Line.Outside => "connected: from outside",
            _ => "not connected",
        };
        string active = _addinTracker.ActiveDocument is { } doc
            ? $"Active document: {System.IO.Path.GetFileName(doc)}" + (_addinTracker.ActiveDocType is { } t ? $" ({t})" : "")
            : "No document active.";
        AddinNowText.Text = !connected ? "Not hearing from inside SOLIDWORKS." : _addinTracker.NowLine(now) ?? active;
        AddinRecentRows.ItemsSource = connected ? _addinTracker.Finished.Take(8).Select(a => $"{a.Started:HH:mm:ss}   {AddinTracker.Describe(a)}").ToList() : null;
        AddinExplainText.Text = AddinTracker.Explain(swPid is not null, line, _link.Problem);

        // "Time each view": a button here when connected from outside (with the add-in it's in SOLIDWORKS's Tools menu).
        TimeViewsRow.Visibility = viaLink ? Visibility.Visible : Visibility.Collapsed;
        bool drawing = _addinTracker.ActiveDocType == "drawing";
        TimeViewsKey.IsEnabled = viaLink && drawing && responding == true && !_link.TimingViews && _addinTracker.InProgress is null;
        TimeViewsNote.Text = _link.TimingViews ? "Timing each view now..."
            : !drawing ? "Open a drawing in SOLIDWORKS to time its views."
            : responding != true ? "Waiting for SOLIDWORKS to be free."
            : "Loads (if needed) and redraws each view on the current sheet one at a time, and times it. Only when you click; asks first.";

        // The last slow sheet switch, or the last "time each view" test.
        var report = connected ? _addinTracker.SheetReport : null;
        AddinSheetBox.Visibility = report is null ? Visibility.Collapsed : Visibility.Visible;
        if (report is not null && !ReferenceEquals(report, _shownSheetReport))
        {
            _shownSheetReport = report;
            AddinSheetHeadline.Text = report.Headline;
            AddinSuspects.ItemsSource = report.Suspects.Select((s, i) =>
                $"{i + 1}. {s.View}" + (s.MeasuredSeconds is { } t ? $"  ({AddinTracker.Clock(t)})" : "") + ": " + string.Join("; ", s.Reasons)).ToList();
            AddinSheetCaveat.Text = report.Caveat;
            _log.Write("sheet: " + report.Headline);
        }
    }

    /// <summary>
    /// "Working: SOLIDWORKS on 1.2 cores (busiest task 98% of a core), memory 3.1 GB, reading 12 MB/s, ..."
    /// Only while SOLIDWORKS is busy or not responding; null when it's idle, so the log stays quiet then.
    /// </summary>
    private string? ActivityLine(bool running, bool? responding)
    {
        var sw = _lastSmoothed?.SolidWorks;
        if (!running || sw is null) return null;
        bool busy = sw.CoresWorth >= 0.3 || responding == false;
        if (!busy) return null;
        var parts = new List<string> { $"SOLIDWORKS on {CpuInterpreter.Cores(sw.CoresWorth)}" + (sw.ThreadsVisible ? $" (busiest task {sw.TopThreadPercent:0}% of a core)" : "") };
        if (_lastMemory?.SolidWorks is { } m) parts.Add($"memory {MemoryExplainer.Gb((ulong)m.InRam)}");
        if (_lastIo?.SolidWorks is { } io)
        {
            if (io.ReadBytesPerSec >= 100_000) parts.Add($"reading {IoExplainer.Size((ulong)io.ReadBytesPerSec)}/s");
            if (io.WriteBytesPerSec >= 100_000) parts.Add($"writing {IoExplainer.Size((ulong)io.WriteBytesPerSec)}/s");
        }
        if (_lastGpu?.Adapters.Max(a => (double?)a.SwBusyPercent) is { } g && g >= 10) parts.Add($"graphics card {g:0}%");
        if (_defender.CoresNow is { } d && d >= 0.3) parts.Add($"Defender {d:0.0} cores");
        return (responding == false ? "Not responding. Underneath: " : "Working: ") + string.Join(", ", parts) + ".";
    }

    // ---- vitals: the live numbers on the main view ----

    private void ShowVitals(CpuSnapshot cpu)
    {
        var sw = cpu.SolidWorks;
        var mem = _lastMemory;
        var swMem = mem?.SolidWorks;

        VitalsHeadline.Text = sw is null
            ? $"SOLIDWORKS isn't running. The PC is {cpu.SystemPercent:0}% busy."
            : sw.CoresWorth < 0.05
            ? "SOLIDWORKS is idle, waiting for you" + (swMem is not null ? $" ({MemoryExplainer.Gb((ulong)swMem.InRam)} of memory)." : ".")
            : $"SOLIDWORKS is using {CpuInterpreter.Cores(sw.CoresWorth)}" +
              (sw.ThreadsVisible ? $", its busiest task {sw.TopThreadPercent:0}% of a core" : "") +
              (swMem is not null ? $", and {MemoryExplainer.Gb((ulong)swMem.InRam)} of memory." : ".");

        VitalCores.ItemsSource = cpu.CorePercent.Select((pct, i) => CoreBar.From(i, pct, _topology)).ToList();

        var meters = new List<Meter>
        {
            Meter.Stacked("processor", (sw?.PercentOfPc ?? 0) / 100, cpu.SystemPercent / 100,
                sw is null ? $"{cpu.SystemPercent:0}%" : $"{cpu.SystemPercent:0}%  sw {sw.PercentOfPc:0}%", cpu.SystemPercent >= 90,
                "The whole processor. Orange is SOLIDWORKS's share."),
        };
        if (sw is { ThreadsVisible: true })
            meters.Add(Meter.Level("busiest sw task", sw.TopThreadPercent / 100, $"{sw.TopThreadPercent:0}% core",
                "SOLIDWORKS's busiest task. Near 100% means one core is doing step-by-step work (rebuilds, drawing views, mates) and the others can't help."));
        if (mem is not null)
        {
            double total = Math.Max(1, mem.TotalRam);
            meters.Add(Meter.Stacked("memory", (swMem?.InRam ?? 0) / total, mem.UsedRam / total,
                $"{MemoryExplainer.Gb(mem.UsedRam)} / {MemoryExplainer.Gb(mem.TotalRam)}", mem.RamUsedPercent >= MemoryExplainer.RamShort,
                "Memory chips (RAM). Orange is SOLIDWORKS's share. Close to full, Windows starts using the much slower page file."));
            if (swMem is not null)
                meters.Add(Meter.Level("drawing objects", swMem.GdiPercent / 100, $"{swMem.GdiObjects:N0} / {swMem.GdiLimit:N0}",
                    "Windows drawing objects (GDI) SOLIDWORKS is using, against Windows' limit. At the limit, menus and windows stop drawing and SOLIDWORKS can crash."));
        }
        if (_lastGpu?.Adapters.OrderByDescending(a => a.BusyPercent).FirstOrDefault() is { } gpu)
            meters.Add(Meter.Stacked("graphics card", gpu.SwBusyPercent / 100, gpu.BusyPercent / 100,
                $"{gpu.BusyPercent:0}%", gpu.BusyPercent >= 95, "How busy the graphics card is. Orange is SOLIDWORKS's share."));
        if (_lastIo is { } io)
        {
            double diskBytes = io.Disks.Sum(d => d.ReadBytesPerSec + d.WriteBytesPerSec);
            double diskBusy = io.Disks.Count == 0 ? 0 : io.Disks.Max(d => d.BusyPercent);
            meters.Add(Meter.Level("disk", diskBusy / 100, $"{IoExplainer.Size((ulong)diskBytes)}/s",
                "Reading and writing on the disk; the bar is how busy the busiest disk is."));
            double netBytes = io.Networks.Sum(n => n.ReceiveBytesPerSec + n.SendBytesPerSec);
            double netCapacity = io.Networks.Sum(n => n.SpeedBitsPerSec / 8);
            meters.Add(Meter.Level("network", netCapacity > 0 ? netBytes / netCapacity : 0, $"{IoExplainer.Size((ulong)netBytes)}/s",
                "Everything going in and out over the network; the bar is against the connection's speed."));
        }
        if (_defender.CoresNow is { } defender)
            meters.Add(Meter.Level("defender", defender / Math.Max(1, cpu.CorePercent.Count) * 4, $"{defender:0.0} cores",
                "Windows Defender's virus scanning. When it's busy while SOLIDWORKS opens or saves files, it's usually checking those files."));
        VitalMeters.ItemsSource = meters;
    }

    // ---- crashes ----

    private void ShowCrashes()
    {
        _crashIssues.Clear();
        if (_crashes.Count == 0)
        {
            CrashSummary.Text = "no SOLIDWORKS crash records on this PC";
            CrashRows.ItemsSource = null;
            CrashPatternBox.Visibility = Visibility.Collapsed;
            return;
        }
        var first = _crashes[^1].When;
        int lastMonth = _crashes.Count(c => c.When >= DateTime.Now.AddDays(-30));
        CrashSummary.Text = $"{_crashes.Count} crash{(_crashes.Count == 1 ? "" : "es")} since {first:MMM yyyy}, {lastMonth} in the last 30 days.";
        CrashRows.ItemsSource = _crashes.Take(15).Select(c => new CrashRow(
            $"{c.When:d MMM yyyy HH:mm}   SOLIDWORKS {CrashExplainer.VersionName(c.Version)}" + (c.Lead is { } l ? $"   in {l}" : ""),
            CrashExplainer.Summary(c))).ToList();
        if (CrashExplainer.Pattern(_crashes) is { } p)
        {
            CrashPatternTitle.Text = p.Title;
            CrashPatternWhy.Text = p.Why;
            CrashPatternTry.Text = p.Try;
            CrashPatternBox.Visibility = Visibility.Visible;
            _crashIssues.Add(new Issue(p.Title, p.Why, p.Try, IssueLevel.Medium, "crashes"));
        }
        else CrashPatternBox.Visibility = Visibility.Collapsed;
        if (_crashes[0] is { } latest && latest.When >= DateTime.Now.AddDays(-7))
            _crashIssues.Add(new Issue($"SOLIDWORKS crashed on {latest.When:d MMM}", CrashExplainer.Summary(latest), "", IssueLevel.Medium, "crashes"));
        _issueSignature = null; // show the change in the issues list straight away
    }

    /// <summary>SOLIDWORKS has gone: tell the story, keep it in "caught", and save a report.</summary>
    private void SessionEnded(Ending how, CrashRecord? record, DateTime endedAt)
    {
        if (how == Ending.Closed) return;
        string howText = how == Ending.Crashed
            ? "SOLIDWORKS crashed. " + CrashExplainer.Summary(record!)
            : "SOLIDWORKS ended while it wasn't responding. There's no crash record, so it was most likely ended by hand " +
              "(Task Manager, or Windows' \"close the program\").";
        var saw = _story.Lines.TakeLast(20).Select(l => $"{l.Clock}  {l.Text}")
            .Concat(_catcher.Caught.Where(m => m.Start >= endedAt.AddMinutes(-30)).Select(m => "caught: " + m.Line)).ToList();
        string text = CrashExplainer.Report(record, howText, saw, DateTime.Now);
        string? saved = null;
        foreach (var folder in AppPaths.DataFolders)
        {
            try
            {
                var dir = System.IO.Path.Combine(folder, "reports");
                System.IO.Directory.CreateDirectory(dir);
                var file = System.IO.Path.Combine(dir, $"crash-{endedAt:yyyy-MM-dd-HHmmss}.txt");
                System.IO.File.WriteAllText(file, text);
                saved ??= file;
            }
            catch (Exception) { }
        }
        _story.Note(DateTime.Now, howText + (saved is null ? "" : " A report is saved in the reports folder (crashes section)."),
            how == Ending.Crashed ? StoryTone.Bad : StoryTone.Warn);
        _catcher.Report(new Moment(endedAt, TimeSpan.Zero, how == Ending.Crashed ? "crash" : "ended-frozen",
            how == Ending.Crashed ? "SOLIDWORKS crashed" : "SOLIDWORKS was ended while frozen", howText, LookInto: true));
        _log.Write($"{(how == Ending.Crashed ? "crash" : "ended while frozen")}: {howText}" + (saved is null ? "" : $" Report: {saved}"), endedAt);
        ShowCrashes();
    }

    private void OpenReports_Click(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(AppPaths.LocalAppDataFolder, "reports");
        System.IO.Directory.CreateDirectory(dir);
        _log.Write("action: opened the reports folder (requested by the user).");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private sealed record CrashRow(string Line, string Detail);

    // ---- obvious issues ----

    private void ShowIssues(DateTime now, NowReport report)
    {
        // Crashes live on their own page (looked into later); fixes here are about live performance.
        var all = IssueBoard.Build(now, report, _addinTracker.SheetReport, _catcher.Caught, _checks);
        // Fixes you dismissed stay out of the list, the count and the guide until you bring them back.
        var issues = all.Where(i => !App.Settings.DismissedFixes.Contains(i.Title)).ToList();
        int dismissed = all.Count - issues.Count;
        DismissedLink.Text = $"{dismissed} dismissed · show them again";
        DismissedLink.Visibility = dismissed > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowAssist(now, report, _processes.Any(p => p.Role == ProcessRole.Main), issues); // the guide and the pictures
        // Rebuild the list only when it actually changes, so it doesn't flicker.
        string signature = _showAllIssues + "|" + string.Join("|", issues.Select(i => i.Title + i.Why));
        if (signature == _issueSignature) return;
        _issueSignature = signature;
        // Keep it short: the top few, one line each; the rest behind "show more".
        var shown = _showAllIssues ? issues : issues.Take(IssuesShown).ToList();
        IssueRows.ItemsSource = shown.Select(i => new IssueVm(i.Title, i.Why, i.Try,
            i.Level == IssueLevel.High ? Theme.Alarm : Theme.Accent, _expandedIssues.Contains(i.Title))).ToList();
        NoIssues.Visibility = issues.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        IssueCount.Text = issues.Count == 0 ? "" : $"{issues.Count}";
        int hidden = issues.Count - shown.Count;
        IssueMore.Visibility = issues.Count > IssuesShown ? Visibility.Visible : Visibility.Collapsed;
        IssueMore.Text = hidden > 0 ? $"show {hidden} more" : "show fewer";
        foreach (var i in issues.Where(i => i.Level == IssueLevel.High)) _log.Write("issue: " + i.Title + ". " + i.Why, now);
    }

    private const int IssuesShown = 3;
    private bool _showAllIssues;
    private readonly HashSet<string> _expandedIssues = new();

    private void IssueRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not IssueVm vm) return;
        vm.Expanded = !vm.Expanded;
        if (vm.Expanded) _expandedIssues.Add(vm.Title); else _expandedIssues.Remove(vm.Title);
    }

    private void IssueMore_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _showAllIssues = !_showAllIssues;
        _issueSignature = null;
        if (_lastNow is { } r) ShowIssues(DateTime.Now, r);
    }

    /// <summary>One line in "worth fixing"; opens to show why and what to try.</summary>
    private sealed class IssueVm(string title, string why, string @try, Brush titleBrush, bool expanded) : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _expanded = expanded;
        public string Title { get; } = title;
        public string Why { get; } = why;
        public string Try { get; } = @try;
        public Brush TitleBrush { get; } = titleBrush;
        public Visibility TryVisibility => string.IsNullOrWhiteSpace(Try) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility DetailVisibility => _expanded ? Visibility.Visible : Visibility.Collapsed;
        public string Chevron => _expanded ? "\u25BE" : "\u203A";
        public bool Expanded
        {
            get => _expanded;
            set
            {
                _expanded = value;
                PropertyChanged?.Invoke(this, new(nameof(DetailVisibility)));
                PropertyChanged?.Invoke(this, new(nameof(Chevron)));
            }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record StoryRow(string Clock, string Text, Brush Dot, Brush Tint, FontWeight Weight)
    {
        /// <param name="hang">A line that was pulled out into the "hung up" panel: it stays highlighted in the log.</param>
        public static StoryRow From(StoryLine l, bool hang = false) => new(l.Clock, l.Text, l.Tone switch
        {
            StoryTone.Good => Theme.Ok,
            StoryTone.Warn => Theme.Warn,
            StoryTone.Bad => Theme.Alarm,
            _ => Theme.Dim,
        }, hang ? HangTint : Brushes.Transparent, hang ? FontWeights.SemiBold : FontWeights.Normal);
    }

    // ---- caught: things that came and went ----

    private Moment? _shownFirst;

    private void ShowCaught()
    {
        var caught = _catcher.Caught;
        if (ReferenceEquals(caught.FirstOrDefault(), _shownFirst) && caught.Count == _shownCaught && _catcher.Unseen == _shownUnseen) return; // nothing new
        _shownFirst = caught.FirstOrDefault();
        _shownCaught = caught.Count;
        _shownUnseen = _catcher.Unseen;
        int lookInto = caught.Count(m => m.LookInto);
        CaughtSummary.Text = caught.Count == 0 ? "nothing yet"
            : $"{caught.Count} today" + (lookInto > 0 ? $", {lookInto} to look into" : "");
        CaughtRows.ItemsSource = caught.Select(CaughtRow.From).ToList();
        CaughtBadge.Visibility = _catcher.Unseen > 0 ? Visibility.Visible : Visibility.Collapsed;
        CaughtBadgeText.Text = $"{_catcher.Unseen} caught";
    }

    private void CaughtSeenKey_Click(object sender, RoutedEventArgs e)
    {
        _catcher.MarkSeen();
        ShowCaught();
    }

    private sealed record CaughtRow(string Title, string When, Brush Edge, Visibility TagVisibility, Moment Moment)
    {
        public static CaughtRow From(Moment m) => new(m.Title,
            m.Duration >= TimeSpan.FromSeconds(1) ? $"{m.Start:HH:mm:ss}  ·  {NowEngine.Seconds(m.Duration)}" : $"{m.Start:HH:mm:ss}",
            m.Kind.StartsWith("win-") ? Theme.Info : m.LookInto ? Theme.Alarm : Theme.Dim,
            m.LookInto ? Visibility.Visible : Visibility.Collapsed, m);
    }

    private void TimeViewsKey_Click(object sender, RoutedEventArgs e)
    {
        string doc = System.IO.Path.GetFileName(_addinTracker.ActiveDocument ?? "the active drawing");
        var answer = MessageBox.Show(this, SolidWorksLink.ViewTestWarning, "Time each view", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
        {
            _log.Write($"action: time each view on {doc}: cancelled by the user. Nothing changed.");
            return;
        }
        // Logged as an action: it is the one thing the Companion asks SOLIDWORKS to do.
        _log.Write($"action: time each view on the current sheet of {doc}, requested by the user. Original state: drawing as it was; nothing is saved.");
        _link.TimeEachView();
    }
    // ---- files (file tracing) ----

    private void ShowFiles(IReadOnlyList<SeenProcess> processes)
    {
        var now = DateTime.Now;
        _files.SolidWorksPid = processes.FirstOrDefault(p => p.Role == ProcessRole.Main)?.Id;
        _files.Prune(now);

        bool tracing = _fileTrace.IsRunning;
        bool admin = FileTraceSession.IsAdministrator();
        FilesStatusLabel.Text = tracing ? "file tracing: on (running as administrator)" : "file tracing: off";
        AdminRow.Visibility = tracing ? Visibility.Collapsed : Visibility.Visible;
        RestartAdminKey.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        if (!tracing && admin) AdminText.Text = _fileTrace.NotRunningReason ?? "";

        _activeFiles = tracing ? _files.Active(now) : [];
        var recent = tracing ? _files.Recent(now, FileActivityTracker.KeepFor) : [];
        FilesSummaryText.Text = FileExplainer.Describe(_activeFiles, recent, tracing);
        FilesExplainText.Text = FileExplainer.WhatIsHappening(recent, tracing);
        _filesWhy.Refresh();

        // Show SOLIDWORKS documents and anything slow; skip the hundreds of quick program files.
        FileRows.ItemsSource = recent
            .Where(f => f.IsSolidWorksDocument || f.WaitTime >= FileExplainer.Slow || f.ScannedByAntivirus)
            .Take(12)
            .Select(f => new FileRow(f.Name, f.Path, Details(f, now),
                f.WaitTime >= FileExplainer.Slow ? Theme.Accent : Theme.ScreenText,
                f.ScannedByAntivirus ? Visibility.Visible : Visibility.Collapsed))
            .ToList();
    }

    private static string Details(FileActivity f, DateTime now)
    {
        string ago = now - f.LastSeen < FileActivityTracker.ActiveWindow ? "now" : $"{(now - f.LastSeen).TotalSeconds:0} s ago";
        var parts = new List<string> { f.Where, ago };
        if (f.WaitTime >= TimeSpan.FromMilliseconds(50)) parts.Add($"waited {FileExplainer.Secs(f.WaitTime)}");
        if (f.BytesRead > 0) parts.Add($"read {IoExplainer.Size((ulong)f.BytesRead)}");
        if (f.BytesWritten > 0) parts.Add($"wrote {IoExplainer.Size((ulong)f.BytesWritten)}");
        if (f.ScannedByAntivirus) parts.Add($"scan {FileExplainer.Secs(f.AntivirusTime)}");
        return string.Join("   ", parts);
    }

    private sealed record FileRow(string Name, string Path, string Details, Brush NameBrush, Visibility ScanVisibility);

    /// <summary>
    /// Starts a new copy of the Companion with administrator rights (Windows shows its own
    /// permission prompt), then closes this one. If the prompt is cancelled, nothing changes.
    /// </summary>
    private void RestartAdminKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            _log.Write("Restarting as administrator (requested by the user).");
            Close();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            AdminText.Text = "The Windows permission prompt was cancelled, so file tracing stays off. Nothing was changed.";
        }
    }

    // ---- right now ----

    private void ShowNow(IReadOnlyList<SwWindow> windows, bool? responding)
    {
        var now = DateTime.Now;
        bool running = _processes.Any(p => p.Role == ProcessRole.Main);
        _freeze.Observe(running ? responding : null, now);

        var unresponsiveFor = _freeze.UnresponsiveFor(now);
        bool frozenNow = running && responding == false && unresponsiveFor >= NowEngine.FrozenAfter;
        var verdict = _life.Evaluate(now, frozenNow);
        var report = NowEngine.Evaluate(new NowInputs(
            running, responding, unresponsiveFor,
            _lastSmoothed, _lastMemory, _lastGpu, _lastIo, _lastOthers, windows, _checks, _activeFiles, verdict, _vaultTrafficNow,
            _addinTracker.NowLine(now), _defender.CoresNow));
        _freeze.Record(report, now);
        _catcher.Observe(now, report, windows, _freeze.LastEpisode);
        // Crashes: notice SOLIDWORKS ending, then (after it has had time to write its record) say how.
        int? swPid = _processes.FirstOrDefault(p => p.Role == ProcessRole.Main)?.Id;
        if (swPid is { } pidNow && _swStart?.Pid != pidNow)
        {
            try { using var proc = System.Diagnostics.Process.GetProcessById(pidNow); _swStart = (pidNow, proc.StartTime); }
            catch (Exception) { _swStart = (pidNow, now); }
        }
        _crashWatch.Observe(now, swPid, swPid is null ? null : _swStart?.Started, report.Level == NowLevel.Frozen);
        if (_crashWatch.Decide(now, () => _crashes = CrashReader.ReadAll()) is { } ending) SessionEnded(ending.How, ending.Record, ending.EndedAt);
        ShowCaught();
        _story.Update(now, new StoryInputs(running, report, _addinTracker.InProgress, _addinTracker.Finished.FirstOrDefault(),
            _addinTracker.LastSheet, _addinTracker.ActiveDocument, _activeFiles, ActivityLine(running, responding)));
        ShowIssues(now, report);
        _lastNow = report;
        _lastVerdictReport = verdict;
        ShowVerdict(verdict, now);
        ShowAlert(report, verdict);
        // The freeze box only appears when there's something to say: a freeze or a big slowdown.
        FreezeBox.Visibility = AlertBanner.Visibility == Visibility.Visible || VerdictBox.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;

        // Write each finished freeze to the activity log once, with its timeline.
        if (_freeze.LastEpisode is { } ep && ep != _loggedEpisode)
        {
            _loggedEpisode = ep;
            _log.Write("freeze: " + ep.Summary.Replace("Last freeze: ", ""), ep.Start);
            foreach (var line in ep.Timeline) _log.Write($"  freeze timeline: {line.Text}", line.Time);
        }

        NowClock.Text = now.ToString("HH:mm:ss");
        NowHeadline.Text = report.Headline;
        // Underneath: which SOLIDWORKS, and the document you're in.
        string version = _versions?.SolidWorks?.Name ?? "SOLIDWORKS";
        NowSubline.Text = !running ? (_versions?.SolidWorks is { } v ? $"{v.Name} is installed." : "")
            : _addinTracker.ActiveDocument is { } doc ? $"{version} \u00B7 {System.IO.Path.GetFileName(doc)}" : version;
        var (strip, headline) = report.Level switch
        {
            NowLevel.Frozen => (Theme.Alarm, Theme.Alarm),
            NowLevel.Busy => (Theme.Warn, Theme.Text),
            NowLevel.Calm => (Theme.Ok, Theme.Text),
            _ => (Theme.Dim, Theme.Text),
        };
        NowStrip.Background = strip;
        NowHeadline.Foreground = headline;
        // In the header the status has no box of its own; during a freeze the freeze box below carries the red.
        FreezeBox.BorderBrush = report.Level == NowLevel.Frozen ? Theme.Alarm : (Brush)FindResource("Line");
        FreezeBox.BorderThickness = new Thickness(report.Level == NowLevel.Frozen ? 2 : 1);

        NowCauses.ItemsSource = report.Causes.Take(4).Select(c => new NowCauseRow(
            c.Title, c.Evidence,
            c.Kind == "setup" ? Theme.Dim : report.Level == NowLevel.Frozen ? Theme.Alarm : Theme.Warn)).ToList();

        var timeline = _freeze.CurrentTimeline;
        NowTimeline.Visibility = timeline.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NowTimeline.Text = string.Join("\n", timeline.TakeLast(5).Select(l => $"{l.Time:HH:mm:ss}  {l.Text}"));

        NowLastFreeze.Visibility = _freeze.LastEpisode is null || report.Level == NowLevel.Frozen ? Visibility.Collapsed : Visibility.Visible;
        NowLastFreeze.Text = _freeze.LastEpisode?.Summary ?? "";
    }

    private sealed record NowCauseRow(string Title, string Evidence, Brush Dot);

    /// <summary>During a freeze: still working, or stopped? With the "last sign of life" clock.</summary>
    private void ShowVerdict(VerdictReport v, DateTime now)
    {
        if (v.Verdict == Verdict.None)
        {
            VerdictBox.Visibility = Visibility.Collapsed;
            _lastVerdict = Verdict.None;
            return;
        }

        var (badge, colour) = v.Verdict switch
        {
            Verdict.Working => ("WORKING", Theme.Ok),
            Verdict.Background => ("WORKING", Theme.Ok),
            Verdict.Quiet => ("QUIET", Theme.Warn),
            Verdict.ProbablyStuck => ("PROBABLY STUCK", Theme.Accent),
            _ => ("STUCK", Theme.Alarm),
        };
        VerdictBox.Visibility = Visibility.Visible;
        VerdictBadge.Background = colour;
        VerdictBadgeText.Text = badge;
        VerdictTitle.Text = v.Title;
        VerdictEvidence.Text = v.Evidence;
        VerdictClock.Text = v.LastSignOfLife is { } last
            ? $"last sign of life: {last:HH:mm:ss} ({NowEngine.Seconds(now - last)} ago)"
            : "last sign of life: not measured yet";
        // Always say which button to pick in SOLIDWORKS's own "busy" box during a freeze.
        VerdictAdviceBox.Visibility = Visibility.Visible;
        VerdictAdvice.Text = string.IsNullOrEmpty(v.Advice) ? LifeSigns.WhileWorking : v.Advice;

        // Log each change of verdict, so a freeze report shows when it stopped making progress.
        if (v.Verdict != _lastVerdict)
        {
            _log.Write($"freeze verdict: {v.Title}. {v.Evidence}");
            _lastVerdict = v.Verdict;
        }
    }

    /// <summary>
    /// The loud alert for obvious, large slowdowns (and for a stuck SOLIDWORKS). When a new one
    /// appears, the Companion's taskbar button flashes so it's noticed on a second monitor.
    /// It never steals focus or pops in front of SOLIDWORKS.
    /// </summary>
    private void ShowAlert(NowReport report, VerdictReport verdict)
    {
        var glaring = report.Glaring;
        string? key = null;

        if (verdict.Verdict is Verdict.Stuck or Verdict.ProbablyStuck)
        {
            key = "verdict:" + verdict.Verdict;
            AlertTitle.Text = verdict.Title;
            AlertFix.Text = verdict.Verdict == Verdict.Stuck
                ? "SOLIDWORKS has stopped making progress. Read the steps below before ending it."
                : "No progress for a while. Check for a hidden message box; the app will say if it becomes stuck.";
            AlertMore.Text = glaring.Count > 0 ? "Also: " + string.Join("; ", glaring.Select(g => g.Title)) : "";
        }
        else if (glaring.Count > 0)
        {
            var top = glaring[0];
            key = top.Kind + ":" + top.Title;
            AlertTitle.Text = top.Title.Replace("Also: ", "");
            AlertFix.Text = string.IsNullOrEmpty(top.Fix) ? top.Evidence : "What to do: " + top.Fix;
            AlertMore.Text = glaring.Count > 1 ? "Also: " + string.Join("; ", glaring.Skip(1).Select(g => g.Title.Replace("Also: ", ""))) : "";
        }

        AlertBanner.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
        AlertMore.Visibility = string.IsNullOrEmpty(AlertMore.Text) ? Visibility.Collapsed : Visibility.Visible;

        if (key is not null && key != _lastAlertKey)
        {
            FlashTaskbar();
            _log.Write($"alert: {AlertTitle.Text}. {AlertFix.Text}");
        }
        _lastAlertKey = key;
    }

    /// <summary>Flashes this window's taskbar button a few times. Doesn't move or focus any window.</summary>
    private void FlashTaskbar()
    {
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = new WindowInteropHelper(this).Handle,
            Flags = FlashTray | FlashTimerNoForeground,
            Count = 5,
        };
        FlashWindowEx(ref info);
    }

    private const uint FlashTray = 0x2, FlashTimerNoForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags; public uint Count; public uint Timeout; }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    // ---- disk and network ----

    private void ShowIo(IoSnapshot s)
    {
        _lastIo = s;
        IoSummaryText.Text = IoExplainer.Describe(s);
        IoExplainText.Text = IoExplainer.WhatIsHappening(s);
        _ioWhy.Refresh();

        IoDetailText.Text = s.SolidWorks is { } sw
            ? $"SOLIDWORKS: {IoExplainer.Rate(sw.ReadBytesPerSec)} read ({sw.ReadsPerSec:0} reads/s), " +
              $"{IoExplainer.Rate(sw.WriteBytesPerSec)} written ({sw.WritesPerSec:0} writes/s). Since it started: " +
              $"{IoExplainer.Size(sw.TotalRead)} read, {IoExplainer.Size(sw.TotalWritten)} written. Disk and network together."
            : "SOLIDWORKS is not running.";

        var meters = new List<Meter>();
        foreach (var d in s.Disks)
            meters.Add(Meter.Level($"disk {DiskLetters(d.Name)} busy", d.BusyPercent / 100,
                $"{d.BusyPercent:0}%  {d.ResponseMs:0.0} ms",
                $"Disk {d.Name}: reading {IoExplainer.Rate(d.ReadBytesPerSec)}, writing {IoExplainer.Rate(d.WriteBytesPerSec)}."));
        foreach (var n in s.Networks.Where(n => n.SpeedBitsPerSec > 0))
            meters.Add(Meter.Level($"{ShortNetName(n.Name)}: in use", n.UsedPercent / 100,
                $"{IoExplainer.Rate(n.ReceiveBytesPerSec)} in",
                $"{n.Name}: {IoExplainer.Rate(n.ReceiveBytesPerSec)} in, {IoExplainer.Rate(n.SendBytesPerSec)} out, " +
                $"link speed {n.SpeedBitsPerSec / 1_000_000:0} Mbit/s."));
        IoMeters.ItemsSource = meters;
    }

    /// <summary>"0 C:" → "c:"; "1 D: E:" → "d: e:"; "2" (no letter) → "2".</summary>
    private static string DiskLetters(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var letters = parts.Skip(1).ToList();
        return (letters.Count > 0 ? string.Join(" ", letters) : parts.FirstOrDefault() ?? name).ToLowerInvariant();
    }

    /// <summary>"Intel[R] Wi-Fi 6E AX211 160MHz" → "wi-fi"; "Realtek USB GbE Family Controller" → "realtek usb gbe".</summary>
    private static string ShortNetName(string name)
    {
        var s = name.ToLowerInvariant();
        if (s.Contains("wi-fi") || s.Contains("wireless") || s.Contains("wlan")) return "wi-fi";
        foreach (var noise in new[] { "[r]", "(r)", " family controller", " network adapter", " adapter", " controller" })
            s = s.Replace(noise, "");
        s = s.Replace("  ", " ").Trim();
        return s.Length > 22 ? s[..22] : s;
    }

    // ---- pdm vault ----

    /// <summary>All checkup rows: machine, checklist, and each vault's server checks, in group order.</summary>
    private void RebuildChecks()
    {
        var vaultRows = _vaults.SelectMany(v => VaultExplainer.Checks(v, _lookups, _hosts,
            _portTests.TryGetValue(v.Name, out var t) ? t : []));
        string[] order = ["Windows", "SOLIDWORKS", "PDM client"];
        _checks = _baseChecks.Concat(vaultRows).OrderBy(c => Array.IndexOf(order, c.Group)).ToList();
        _lastCheckupAt = DateTime.Now;
        ShowCheckup();
    }

    private void ShowVault()
    {
        bool any = _vaults.Count > 0;
        VaultNotesForm.IsEnabled = any;
        PortTestKey.IsEnabled = any;
        if (!any)
        {
            VaultSummaryText.Text = "There's no PDM vault view on this PC yet. Once one is set up, the app finds its database and " +
                                    "archive servers automatically and checks how this PC reaches them.";
            VaultPicker.Visibility = Visibility.Collapsed;
            VaultRows.ItemsSource = null;
            _selectedVault = null;
            return;
        }

        var names = _vaults.Select(v => v.Name).ToList();
        if (!names.SequenceEqual(VaultPicker.Items.Cast<string>()))
        {
            VaultPicker.ItemsSource = names;
            VaultPicker.SelectedItem = _selectedVault is not null && names.Contains(_selectedVault) ? _selectedVault : names[0];
        }
        VaultPicker.Visibility = names.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _selectedVault ??= names[0];

        VaultSummaryText.Text = names.Count == 1
            ? $"Vault view on this PC: {names[0]}."
            : $"{names.Count} vault views on this PC: {string.Join(", ", names)}. Showing {_selectedVault}.";
        VaultRows.ItemsSource = _checks.Where(c => c.Id.EndsWith(":" + _selectedVault, StringComparison.OrdinalIgnoreCase))
            .Select(c => new NowCauseRow(c.Title.Replace($"vault {_selectedVault}: ", ""), c.Finding, c.Status switch
            {
                CheckStatus.Good => Theme.Ok,
                CheckStatus.Look => Theme.Warn,
                CheckStatus.Problem => Theme.Alarm,
                _ => (Brush)Theme.Dim,
            }))
            .ToList();
    }

    /// <summary>Traffic to the vault's servers over the last minute.</summary>
    private void ShowVaultTraffic()
    {
        bool tracing = _fileTrace.IsRunning;
        var minute = tracing ? _vaultTraffic.Snapshot(DateTime.Now, TimeSpan.FromMinutes(1)) : [];
        VaultTrafficSummary.Text = VaultTrafficExplainer.Summary(minute, tracing);
        VaultTrafficRows.ItemsSource = minute.Select(VaultTrafficExplainer.Line).ToList();
        VaultTrafficExplain.Text = VaultTrafficExplainer.WhatIsHappening(minute, tracing);
        VaultTrafficPlain.Text = VaultTrafficExplainer.Plain;
        ShowMap();
    }

    /// <summary>The live traffic map: the last few seconds of traffic, plus SOLIDWORKS's state.</summary>
    private void ShowMap()
    {
        var model = _showMapExample
            ? TrafficMap.Example()
            : TrafficMap.Build(_vaults, _vaultTrafficNow, _fileTrace.IsRunning,
                _lastNow?.Level ?? NowLevel.NotRunning, _lastVerdictReport?.Verdict ?? Verdict.None);
        _map.Show(model);
        MapNote.Text = model.Note;
    }

    private void MapExampleKey_Click(object sender, RoutedEventArgs e)
    {
        _showMapExample = !_showMapExample;
        MapExampleKey.Tag = _showMapExample ? "on" : null;
        MapExampleKey.Content = _showMapExample ? "back to live" : "show an example";
        ShowMap();
    }

    private void VaultPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VaultPicker.SelectedItem is not string name) return;
        _selectedVault = name;
        App.Settings.SelectedVault = name;
        App.Settings.Save();
        LoadNotes(name);
        ShowVault();
    }

    /// <summary>
    /// Opt-in port test, only when the user clicks: knocks on each server's port, times the
    /// answer, closes straight away. Nothing is sent to PDM or SQL Server. Every test is logged.
    /// </summary>
    private async void PortTestKey_Click(object sender, RoutedEventArgs e)
    {
        var vault = _vaults.FirstOrDefault(v => v.Name == _selectedVault);
        if (vault is null) return;
        PortTestKey.IsEnabled = false;
        PortTestNote.Text = "testing...";
        try
        {
            var results = await Task.Run(() =>
            {
                var list = new List<PortTest>();
                if (vault.ArchiveServer is { } a) list.Add(NetworkChecks.TestPort("archive", a, vault.ArchivePortOrDefault));
                if (vault.DatabaseHost is { } d && vault.SqlPort is { } p) list.Add(NetworkChecks.TestPort("database", d, p));
                return list;
            });
            _portTests[vault.Name] = results;
            App.Settings.PortTests[vault.Name] = results; // still there next time
            App.Settings.Save();
            foreach (var r in results)
                _log.Write($"port test (requested by the user): vault {vault.Name}, {r.Role} server {r.Server} port {r.Port}: " +
                           $"{r.Result}{(r.Milliseconds is { } ms ? $" in {ms:0} ms" : "")}.");
            PortTestNote.Text = $"Tested at {DateTime.Now:HH:mm:ss}. Results are in the rows above and in the checkup.";
            RebuildChecks();
            ShowVault();
        }
        finally
        {
            PortTestKey.IsEnabled = true;
        }
    }

    private void LoadNotes(string vault)
    {
        var note = _vaultNotes.Load().TryGetValue(vault, out var n) ? n : new VaultNote();
        _loadingNotes = true; // filling the form is not you changing it, so it is not saved back
        try
        {
            NoteDatabaseWhere.SelectedItem = note.DatabaseWhere;
            NoteArchiveWhere.SelectedItem = note.ArchiveWhere;
            NoteLink.SelectedItem = note.Link;
            NoteReplicated.SelectedItem = note.Replicated;
            NoteText.Text = note.Notes;
        }
        finally { _loadingNotes = false; }
        NotesStatus.Text = "";
    }

    private void NotesChanged()
    {
        if (_loadingNotes || _selectedVault is null) return;
        NotesStatus.Text = "Changed; saving...";
        _notesTimer.Stop();
        _notesTimer.Start();
    }

    private void SaveNotesKey_Click(object sender, RoutedEventArgs e)
    {
        _notesTimer.Stop();
        SaveNotes(clicked: true);
    }

    /// <summary>Saves the notes for the selected vault to the user's own app-data folder only.</summary>
    private void SaveNotes(bool clicked)
    {
        if (_selectedVault is null) return;
        try
        {
            _vaultNotes.Save(_selectedVault, new VaultNote(
                NoteDatabaseWhere.SelectedItem as string ?? "not known",
                NoteArchiveWhere.SelectedItem as string ?? "not known",
                NoteLink.SelectedItem as string ?? "not known",
                NoteReplicated.SelectedItem as string ?? "not known",
                NoteText.Text.Trim()));
            _log.Write($"vault notes saved for {_selectedVault} ({(clicked ? "save button" : "saved automatically")}, in {_vaultNotes.FilePath}).");
            NotesStatus.Text = $"Saved at {DateTime.Now:HH:mm} to {_vaultNotes.FilePath} (this PC only).";
        }
        catch (Exception ex)
        {
            NotesStatus.Text = "Couldn't save: " + ex.Message;
        }
    }

    // ---- your look and window place, kept for next time ----

    private void ThemeKey_Click(object sender, RoutedEventArgs e)
    {
        // Colours are set when the window is built, so the Companion reopens itself in the new look.
        App.Settings.Theme = App.Dark ? "light" : "dark";
        SaveWindowPlace();
        _log.Write($"look changed to {App.Settings.Theme} by the user; reopening the Companion.");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Saved. The new look will show next time you open the Companion.\n\n(" + ex.Message + ")", "Look");
        }
    }

    private void RestoreWindowPlace()
    {
        var s = App.Settings;
        var area = SystemParameters.WorkArea;
        if (s.Width is { } w && s.Height is { } h && s.Left is { } l && s.Top is { } t &&
            l + w > SystemParameters.VirtualScreenLeft + 50 && t + 30 > SystemParameters.VirtualScreenTop &&
            l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50 &&
            t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50)
        {
            // Where you left it, but only if that place is still on a screen (a second monitor may have been unplugged).
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l; Top = t; Width = w; Height = h;
            if (s.Maximized) WindowState = WindowState.Maximized;
        }
        else if (area.Width >= Layout.WideLive + 60)
        {
            // First time on a wide screen: wide enough for the columns.
            Width = Math.Min(1560, area.Width - 40);
            Height = Math.Min(900, area.Height - 40);
        }
    }

    private void SaveWindowPlace()
    {
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (b.IsEmpty) return;
        App.Settings.Left = b.Left; App.Settings.Top = b.Top; App.Settings.Width = b.Width; App.Settings.Height = b.Height;
        App.Settings.Maximized = WindowState == WindowState.Maximized;
        App.Settings.Save();
    }

    // ---- checkup ----

    private void ShowCheckup()
    {
        CheckupSummaryText.Text = CheckupEvaluator.Summary(_checks);
        // A heading above the first row of each group.
        CheckRows.ItemsSource = _checks.Select((c, i) => CheckRow.From(c, _openFixes.Contains(c.Id),
            i == 0 || _checks[i - 1].Group != c.Group)).ToList();
    }

    /// <summary>"how to fix" key: opens or closes that check's steps.</summary>
    private void CheckFixKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string id }) return;
        if (!_openFixes.Remove(id)) _openFixes.Add(id);
        ShowCheckup();
    }

    private sealed record CheckRow(string Id, string Title, string Finding, Brush Dot, string StatusWords,
                                   string Plain, string HowToFix, string Source, string? KeyTag, string KeyLabel,
                                   Visibility FixKeyVisibility, Visibility FixVisibility, Visibility HowToFixVisibility,
                                   string GroupHeader, Visibility GroupHeaderVisibility)
    {
        public static CheckRow From(CheckResult c, bool open, bool firstOfGroup)
        {
            var (dot, words) = c.Status switch
            {
                CheckStatus.Good => (Theme.Ok, "looks good"),
                CheckStatus.Look => (Theme.Warn, "worth a look"),
                CheckStatus.Problem => (Theme.Alarm, "likely slowing SOLIDWORKS down"),
                _ => ((Brush)Theme.Dim, "couldn't check yet"),
            };
            bool hasFix = !string.IsNullOrEmpty(c.HowToFix);
            bool hasPlain = !string.IsNullOrEmpty(c.Plain);
            bool hasKey = hasFix || hasPlain;
            return new CheckRow(c.Id, c.Title, c.Finding, dot, words, c.Plain, c.HowToFix, "source: " + c.Source,
                open ? "on" : null,
                hasFix ? "how to fix" : "what's this?",
                hasKey ? Visibility.Visible : Visibility.Collapsed,
                hasKey && open ? Visibility.Visible : Visibility.Collapsed,
                hasFix ? Visibility.Visible : Visibility.Collapsed,
                c.Group.ToLowerInvariant(),
                firstOfGroup ? Visibility.Visible : Visibility.Collapsed);
        }
    }

    // ---- graphics ----

    private void ShowGpu(GpuSnapshot g)
    {
        _lastGpu = g;
        GpuSummaryText.Text = GpuExplainer.Describe(g);
        GpuExplainText.Text = GpuExplainer.WhatIsHappening(g);
        _gpuWhy.Refresh();

        var sw = g.SolidWorksAdapter;
        GpuDetailText.Text = sw is null
            ? $"Windows' graphics setting for SOLIDWORKS: {g.WindowsPreference}."
            : $"SOLIDWORKS on the {sw.Adapter.Name}: {sw.SwBusyPercent:0}% of its work, {MemoryExplainer.Gb(sw.SwDedicated)} video memory, " +
              $"{MemoryExplainer.Gb(sw.SwShared)} borrowed main memory.   Windows' graphics setting for SOLIDWORKS: {g.WindowsPreference}.";

        var meters = new List<Meter>();
        // Hide Windows' software fallback unless SOLIDWORKS is actually using it.
        foreach (var a in g.Adapters.Where(a => a.Adapter.Kind != GpuKind.Software || a == sw))
        {
            string name = ShortGpuName(a.Adapter.Name);
            meters.Add(Meter.Stacked($"{name}: busy", a.SwBusyPercent / 100, a.BusyPercent / 100,
                $"{a.BusyPercent:0}%", a.BusyPercent >= GpuExplainer.Busy,
                $"{a.Adapter.Name}: {a.BusyPercent:0}% busy, {a.SwBusyPercent:0}% from SOLIDWORKS."));
            if (a.Adapter.DedicatedTotal >= 1024UL * 1024 * 1024)
            {
                double total = a.Adapter.DedicatedTotal;
                // Windows counts a program's video memory a little differently from the card's total
                // (the program figure includes parts temporarily moved off the card), so the card total
                // can read lower than SOLIDWORKS alone. Show the larger so the bar never contradicts itself.
                ulong used = Math.Max(a.DedicatedUsed, a.SwDedicated);
                meters.Add(Meter.Stacked($"{name}: video memory", a.SwDedicated / total, used / total,
                    $"{MemoryExplainer.Gb(used)} / {MemoryExplainer.Gb(a.Adapter.DedicatedTotal)}",
                    used / total * 100 >= GpuExplainer.VideoMemoryFull,
                    $"{a.Adapter.Name}: video memory in use. Orange part = SOLIDWORKS."));
            }
        }
        GpuMeters.ItemsSource = meters;
    }

    /// <summary>"NVIDIA GeForce RTX 4070 Laptop GPU" → "nvidia rtx 4070"; "Intel Iris Xe Graphics" → "intel iris xe".</summary>
    private static string ShortGpuName(string name)
    {
        var s = name.ToLowerInvariant();
        foreach (var noise in new[] { " laptop gpu", " gpu", " graphics", "geforce ", "radeon(tm) ", "(r)", "(tm)" })
            s = s.Replace(noise, "");
        s = s.Replace("  ", " ").Trim();
        return s.Length > 24 ? s[..24] : s;
    }

    /// <summary>One horizontal meter on the memory display.</summary>
    /// <param name="First">Width share of the first (orange) part, as a star value.</param>
    /// <param name="Second">Width share of the second (plain) part.</param>
    /// <param name="Rest">Width share of the empty remainder.</param>
    private sealed record Meter(string Label, string Value, GridLength First, Brush FirstFill,
                                GridLength Second, Brush SecondFill, GridLength Rest, Brush ValueBrush, string Tip)
    {
        private static GridLength Star(double share) => new(Math.Clamp(share, 0, 1), GridUnitType.Star);

        /// <summary>RAM-style meter: SOLIDWORKS's part in orange, everything else in plain.</summary>
        public static Meter Stacked(string label, double swShare, double usedShare, string value, bool danger, string tip)
        {
            swShare = Math.Min(swShare, usedShare);
            return new Meter(label, value, Star(swShare), Theme.Accent, Star(usedShare - swShare), Theme.ScreenText,
                Star(1 - usedShare), danger ? Theme.Alarm : Theme.ScreenText, tip);
        }

        /// <summary>Limit meter: plain under 70%, orange from 70%, red from 90%.</summary>
        public static Meter Level(string label, double share, string value, string tip)
        {
            var fill = share >= MemoryExplainer.ObjectsDanger / 100 ? Theme.Alarm
                     : share >= MemoryExplainer.ObjectsWatch / 100 ? Theme.Accent
                     : Theme.ScreenText;
            return new Meter(label, value, Star(share), fill, Star(0), fill, Star(1 - share),
                share >= MemoryExplainer.ObjectsDanger / 100 ? Theme.Alarm : Theme.ScreenText, tip);
        }
    }

    private static string DescribeStatus(IReadOnlyList<SeenProcess> seen)
    {
        var main = seen.Where(s => s.Role == ProcessRole.Main).ToList();
        int others = seen.Count - main.Count;
        return main.Count switch
        {
            0 => $"SOLIDWORKS is not running. {others} related program(s) running.",
            1 => $"SOLIDWORKS is running (process ID {main[0].Id}), with {others} related program(s).",
            _ => $"{main.Count} copies of SOLIDWORKS are running (process IDs {string.Join(", ", main.Select(m => m.Id))}). CPU figures are for the first one.",
        };
    }

    /// <summary>One core's bar: white normally, orange when busy, red when maxed.</summary>
    /// <param name="TypeLetter">"P" performance or "E" efficiency; empty on non-hybrid processors.</param>
    /// <param name="Spacing">Gap after the bar: pairs sit close together, and there is a wider gap
    /// between the performance and efficiency groups.</param>
    private sealed record CoreBar(string Label, string TypeLetter, string Tip, double BarHeight, Brush Fill, Thickness Spacing)
    {
        /// <summary>Height for the smaller bars in the vitals panel.</summary>
        public double SmallHeight => BarHeight * 40 / MaxHeight;
        private const double MaxHeight = 52;

        public static CoreBar From(int index, double pct, CpuTopology topo)
        {
            var brush = pct >= 90 ? Theme.Alarm : pct >= CpuSnapshot.BusyCoreThreshold ? Theme.Accent : Theme.ScreenText;
            var core = topo.Cores.FirstOrDefault(c => c.Index == index);
            var pair = topo.PairOf(index);

            string kind = core is null || !topo.IsHybrid ? "core" : core.IsPerformance ? "performance core" : "efficiency core";
            string tip = $"slot {index:00}: {kind}, {pct:0}% busy" +
                         (pair is not null ? $" (shares one physical core with slot {pair.Index:00})" : "");
            string letter = core is null || !topo.IsHybrid ? "" : core.IsPerformance ? "P" : "E";

            // close together within a pair, normal gap otherwise, wide gap before the efficiency group
            bool firstOfPair = pair is not null && pair.Index > index;
            var next = topo.Cores.FirstOrDefault(c => c.Index == index + 1);
            bool groupChangesNext = topo.IsHybrid && core is not null && next is not null && next.IsPerformance != core.IsPerformance;
            double right = firstOfPair ? 2 : groupChangesNext ? 26 : 10;

            return new CoreBar($"{index:00}", letter, tip, Math.Max(2, pct / 100 * MaxHeight), brush, new Thickness(0, 0, right, 0));
        }
    }

    /// <summary>Theme brushes, looked up once from whichever theme App.xaml loads.</summary>
    private static class Theme
    {
        public static readonly SolidColorBrush Text = Get("Text");
        public static readonly SolidColorBrush ScreenText = Get("ScreenText");
        public static readonly SolidColorBrush Dim = Get("Dim");
        public static readonly SolidColorBrush Accent = Get("Accent");
        public static readonly SolidColorBrush Info = Get("Info");
        public static readonly SolidColorBrush Warn = Get("Warn");
        public static readonly SolidColorBrush Alarm = Get("Alarm");
        public static readonly SolidColorBrush Ok = Get("Ok");
        public static readonly Color Bg = (Color)Application.Current.Resources["BgColor"];

        private static SolidColorBrush Get(string key) => (SolidColorBrush)Application.Current.Resources[key];
    }

    // ---- Windows title bar colour, matched to the theme ----

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // Dark title bar for dark themes, light for light ones, decided from the body colour.
        double brightness = (0.299 * Theme.Bg.R + 0.587 * Theme.Bg.G + 0.114 * Theme.Bg.B) / 255;
        int dark = brightness < 0.5 ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        // Windows 11: paint the title bar the same colour as the body. Ignored on older Windows.
        int caption = Theme.Bg.R | (Theme.Bg.G << 8) | (Theme.Bg.B << 16);
        DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
    }

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>One line in the program table.</summary>
    private sealed record Row(Visibility LockVisibility, string RoleText, Brush TypeBrush, string ExeName, int Id, string Description)
    {
        public static Row From(SeenProcess s)
        {
            var (text, brush) = s.Role switch
            {
                ProcessRole.Main => ("solidworks", Theme.Accent),
                ProcessRole.Helper => ("helper", (Brush)Theme.Text),
                ProcessRole.Pdm => ("pdm client", Theme.Info),
                ProcessRole.Licensing => ("licensing", Theme.Warn),
                _ => ("not reviewed", Theme.Alarm),
            };
            return new Row(s.Protected ? Visibility.Visible : Visibility.Collapsed, text, brush, s.ExeName, s.Id, s.Description);
        }
    }
}
