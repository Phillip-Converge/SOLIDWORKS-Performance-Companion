using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace SWCompanion.Core.Files;

/// <summary>
/// Watches which files SOLIDWORKS opens, reads and writes, and when Windows Defender
/// scans them, using Windows' built-in event tracing (the same mechanism Microsoft's
/// own performance tools use). READ ONLY: it only listens. It needs administrator
/// rights because Windows only lets administrators listen to file activity.
/// The trace session is stopped when this object is disposed or the app closes.
/// </summary>
public sealed class FileTraceSession : IDisposable
{
    public const string SessionName = "SOLIDWORKS-Companion-FileTrace";

    private readonly FileActivityTracker _tracker;
    private readonly Pdm.VaultTrafficTracker? _traffic;
    private readonly ConcurrentDictionary<int, string> _programNames = new();
    private readonly KernelPaths _paths = new();
    private readonly ConcurrentDictionary<ulong, (FileOp Op, int Pid, string Path, long Bytes, DateTime Start)> _pending = new();
    private TraceEventSession? _session;
    private Thread? _thread;

    /// <param name="traffic">If given, also watches network traffic to the PDM vault's servers (and only those).</param>
    public FileTraceSession(FileActivityTracker tracker, Pdm.VaultTrafficTracker? traffic = null)
    {
        _tracker = tracker;
        _traffic = traffic;
    }

    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public bool IsRunning => _session is not null;

    /// <summary>Why tracing isn't running, in plain English (null if it is).</summary>
    public string? NotRunningReason { get; private set; } = "Not started.";

    public void Start()
    {
        if (_session is not null) return;
        if (!IsAdministrator())
        {
            NotRunningReason = "File tracing needs the Companion to run as administrator.";
            return;
        }

        try
        {
            // If an earlier run crashed and left our session behind, close it first. Only ever our own session name.
            if (TraceEventSession.GetActiveSessionNames().Contains(SessionName))
                using (var old = new TraceEventSession(SessionName)) old.Stop();

            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            var keywords = KernelTraceEventParser.Keywords.FileIOInit |
                           KernelTraceEventParser.Keywords.FileIO |
                           KernelTraceEventParser.Keywords.Process |
                           KernelTraceEventParser.Keywords.Thread;
            if (_traffic is not null) keywords |= KernelTraceEventParser.Keywords.NetworkTCPIP;
            _session.EnableKernelProvider(keywords);

            var k = _session.Source.Kernel;
            k.FileIOCreate += e => Begin(e.IrpPtr, FileOp.Open, e.ProcessID, e.FileName, 0, e.TimeStamp);
            k.FileIORead += e => Begin(e.IrpPtr, FileOp.Read, e.ProcessID, e.FileName, e.IoSize, e.TimeStamp);
            k.FileIOWrite += e => Begin(e.IrpPtr, FileOp.Write, e.ProcessID, e.FileName, e.IoSize, e.TimeStamp);
            k.FileIOOperationEnd += e => End(e.IrpPtr, e.TimeStamp);

            if (_traffic is not null)
            {
                // Network traffic: only the vault's servers are kept; everything else is dropped on sight.
                k.TcpIpSend += e => Net(e.TimeStamp, e.ProcessID, Pdm.TcpEventKind.Send, e.daddr, e.dport, e.sport, e.size);
                k.TcpIpRecv += e => Net(e.TimeStamp, e.ProcessID, Pdm.TcpEventKind.Receive, e.daddr, e.dport, e.sport, e.size);
                k.TcpIpSendIPV6 += e => Net(e.TimeStamp, e.ProcessID, Pdm.TcpEventKind.Send, e.daddr, e.dport, e.sport, e.size);
                k.TcpIpRecvIPV6 += e => Net(e.TimeStamp, e.ProcessID, Pdm.TcpEventKind.Receive, e.daddr, e.dport, e.sport, e.size);
                k.TcpIpRetransmit += e => Net(e.TimeStamp, e.ProcessID, Pdm.TcpEventKind.Retransmit, e.daddr, e.dport, e.sport, 0);
                k.TcpIpReconnect += e => Net(e.TimeStamp, e.ProcessID, Pdm.TcpEventKind.Reconnect, e.daddr, e.dport, e.sport, 0);
                k.TcpIpConnect += e => Net(e.TimeStamp, e.ProcessID, Pdm.TcpEventKind.Connect, e.daddr, e.dport, e.sport, 0);
            }

            _thread = new Thread(() =>
            {
                try { _session.Source.Process(); } // runs until the session stops
                catch { /* session stopped */ }
            }) { IsBackground = true, Name = "File tracing" };
            _thread.Start();
            NotRunningReason = null;
        }
        catch (Exception ex)
        {
            Dispose();
            NotRunningReason = "Windows wouldn't start file tracing: " + ex.Message;
        }
    }

    /// <summary>Only SOLIDWORKS and the antivirus are of interest; everything else is ignored straight away.</summary>
    private bool Wanted(int pid) => pid == _tracker.SolidWorksPid || _tracker.AntivirusPids.Contains(pid);

    private void Begin(ulong irp, FileOp op, int pid, string kernelPath, long bytes, DateTime time)
    {
        if (!Wanted(pid) || string.IsNullOrEmpty(kernelPath)) return;
        var path = _paths.ToNormal(kernelPath);
        if (irp == 0)
        {
            _tracker.Add(new FileEvent(time, pid, op, path, bytes, null));
            return;
        }
        _pending[irp] = (op, pid, path, bytes, time);
        if (_pending.Count > 20_000) _pending.Clear(); // safety valve: never let this grow without limit
    }

    private void End(ulong irp, DateTime time)
    {
        if (!_pending.TryRemove(irp, out var p)) return;
        _tracker.Add(new FileEvent(p.Start, p.Pid, p.Op, p.Path, p.Bytes, time - p.Start));
    }

    private void Net(DateTime time, int pid, Pdm.TcpEventKind kind, System.Net.IPAddress remote, int remotePort, int localPort, int bytes)
    {
        if (_traffic is null || remote is null || !_traffic.IsVaultTraffic(remote, remotePort)) return;
        _traffic.Add(new Pdm.TcpEvent(time, pid, ProgramName(pid), kind, remote, remotePort, localPort, bytes));
    }

    /// <summary>"SLDWORKS", "explorer"... looked up once per process.</summary>
    private string ProgramName(int pid) => _programNames.GetOrAdd(pid, id =>
    {
        try { using var p = Process.GetProcessById(id); return p.ProcessName; }
        catch { return $"process {id}"; }
    });

    /// <summary>Process IDs of Windows Defender's scanning engine.</summary>
    public static IEnumerable<int> FindAntivirusPids()
    {
        foreach (var p in Process.GetProcessesByName("MsMpEng"))
            using (p) yield return p.Id;
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
        NotRunningReason ??= "Stopped.";
    }
}
