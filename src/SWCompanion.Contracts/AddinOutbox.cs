using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace SWCompanion.Contracts;

/// <summary>
/// The add-in's "out tray". SOLIDWORKS event handlers drop notes in here and return at once;
/// a separate background thread hands them to the Companion over a private named pipe.
///
/// Why this shape:
///  - The add-in is the side that opens the line (the "server") and the Companion dials in.
///    That way the Companion can be started, closed or restarted as administrator at any time,
///    and SOLIDWORKS never waits for it.
///  - Nothing here ever blocks or throws into SOLIDWORKS. If no one is listening, notes are kept
///    up to a small limit and the oldest are dropped.
///  - The last few notes are replayed when the Companion connects, so it learns which document
///    is open even if it was started after SOLIDWORKS.
/// </summary>
public sealed class AddinOutbox : IDisposable
{
    public const int MaxWaiting = 500;
    public const int ReplayCount = 30;

    private readonly string _pipeName;
    private readonly Func<string, NamedPipeServerStream> _createPipe;
    private readonly Queue<string> _waiting = new();
    private readonly LinkedList<string> _recent = new();
    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;
    private volatile NamedPipeServerStream? _pipe;

    /// <summary>Optional: where to write the add-in's own diagnostic lines (never throws).</summary>
    public Action<string>? Diagnostic { get; set; }

    public int Dropped { get; private set; }
    public bool Connected { get; private set; }

    public AddinOutbox(string pipeName, Func<string, NamedPipeServerStream>? createPipe = null)
    {
        _pipeName = pipeName;
        _createPipe = createPipe ?? (n => new NamedPipeServerStream(n, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous));
        _thread = new Thread(Run) { IsBackground = true, Name = "SWCompanion add-in sender" };
        _thread.Start();
    }

    /// <summary>Called from SOLIDWORKS event handlers. Quick, never throws.</summary>
    public void Post(AddinMessage message)
    {
        try
        {
            string line = message.ToJson();
            lock (_lock)
            {
                _recent.AddLast(line);
                while (_recent.Count > ReplayCount) _recent.RemoveFirst();
                if (!Connected) return; // kept in "recent"; replayed on connect
                _waiting.Enqueue(line);
                while (_waiting.Count > MaxWaiting) { _waiting.Dequeue(); Dropped++; }
            }
            _wake.Set();
        }
        catch { /* never let a problem here reach SOLIDWORKS */ }
    }

    private void Run()
    {
        while (!_stopping)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = _createPipe(_pipeName);
                _pipe = pipe;
                pipe.WaitForConnection();
                if (_stopping) break;
                lock (_lock)
                {
                    _waiting.Clear();
                    foreach (var line in _recent) _waiting.Enqueue(line);
                    Connected = true;
                }
                Say("Companion connected.");
                Pump(pipe);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                if (!_stopping) Say("Companion line closed: " + ex.Message);
            }
            catch (Exception ex)
            {
                Say("Sender problem: " + ex.GetType().Name + ": " + ex.Message);
                if (!_stopping) Thread.Sleep(1000);
            }
            finally
            {
                lock (_lock) { Connected = false; _waiting.Clear(); }
                _pipe = null;
                try { pipe?.Dispose(); } catch { }
            }
        }
    }

    private void Pump(NamedPipeServerStream pipe)
    {
        var newline = new byte[] { (byte)'\n' };
        while (!_stopping && pipe.IsConnected)
        {
            string? line;
            lock (_lock) line = _waiting.Count > 0 ? _waiting.Dequeue() : null;
            if (line is null)
            {
                // Nothing to send: wait for a note, but check every second that the line is still open.
                _wake.WaitOne(1000);
                continue;
            }
            var bytes = Encoding.UTF8.GetBytes(line);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Write(newline, 0, 1);
            pipe.Flush();
        }
    }

    private void Say(string text)
    {
        try { Diagnostic?.Invoke(text); } catch { }
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        // If the sender is waiting for the Companion, dial in once so it wakes up and sees we're stopping.
        if (!Connected)
        {
            try
            {
                using var poke = new NamedPipeClientStream(".", _pipeName, PipeDirection.In);
                poke.Connect(200);
            }
            catch { }
        }
        if (!_thread.Join(1000))
            try { _pipe?.Dispose(); } catch { }
    }
}
