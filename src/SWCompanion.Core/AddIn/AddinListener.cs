using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SWCompanion.Contracts;

namespace SWCompanion.Core.AddIn;

/// <summary>
/// The Companion's end of the intercom. For each running SOLIDWORKS it keeps trying to dial the
/// add-in's line (every few seconds, quietly), reads one note per line, and queues them for the
/// screen to pick up. Read only: it never sends anything to SOLIDWORKS.
/// </summary>
public sealed class AddinListener : IDisposable
{
    private readonly ConcurrentQueue<(int Pid, AddinMessage Message)> _inbox = new();
    private readonly ConcurrentDictionary<int, Line> _lines = new();
    private readonly Func<int, string> _pipeNameFor;
    private readonly TimeSpan _retry;

    public AddinListener(Func<int, string>? pipeNameFor = null, TimeSpan? retry = null)
    {
        _pipeNameFor = pipeNameFor ?? AddinPipe.NameFor;
        _retry = retry ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>Process IDs of SOLIDWORKS copies whose add-in is connected right now.</summary>
    public IReadOnlyCollection<int> ConnectedTo => _lines.Where(l => l.Value.Connected).Select(l => l.Key).ToList();

    /// <summary>Tell the listener which SOLIDWORKS processes exist; it starts or stops dialling to match.</summary>
    public void Watch(IEnumerable<int> solidWorksPids)
    {
        var wanted = solidWorksPids.ToHashSet();
        foreach (var pid in wanted)
            _lines.GetOrAdd(pid, p => new Line(p, _pipeNameFor(p), _retry, m => _inbox.Enqueue((p, m))));
        foreach (var pid in _lines.Keys.Where(k => !wanted.Contains(k)).ToList())
            if (_lines.TryRemove(pid, out var gone)) gone.Dispose();
    }

    /// <summary>Everything received since the last call, oldest first.</summary>
    public List<(int Pid, AddinMessage Message)> Drain()
    {
        var list = new List<(int, AddinMessage)>();
        while (_inbox.TryDequeue(out var item)) list.Add(item);
        return list;
    }

    public void Dispose()
    {
        foreach (var l in _lines.Values) l.Dispose();
        _lines.Clear();
    }

    /// <summary>Reads one line of JSON from the add-in. Returns null for anything it can't understand.</summary>
    public static AddinMessage? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string? S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var kind = S("kind");
            if (string.IsNullOrEmpty(kind)) return null;
            var m = new AddinMessage
            {
                Kind = kind!,
                Document = S("document"),
                DocType = S("docType"),
                Sheet = S("sheet"),
                Note = S("note"),
            };
            if (DateTime.TryParse(S("time"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)) m.Time = t;
            if (r.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number) m.Seconds = s.GetDouble();
            if (r.TryGetProperty("views", out var views) && views.ValueKind == JsonValueKind.Array)
                m.Views = views.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object).Select(ParseView).ToList();
            return m;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ViewFacts ParseView(JsonElement v)
    {
        string? S(string n) => v.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
        double? D(string n) => v.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : null;
        bool? B(string n) => v.TryGetProperty(n, out var x) && x.ValueKind is JsonValueKind.True or JsonValueKind.False ? x.GetBoolean() : null;
        return new ViewFacts
        {
            Name = S("name") ?? "",
            Type = S("type"),
            Model = S("model"),
            Configuration = S("config"),
            Display = S("display"),
            DraftQuality = B("draft"),
            Components = D("components") is { } c ? (int)c : null,
            ModelLoaded = B("loaded"),
            Lightweight = B("lightweight"),
            OutOfDate = B("outOfDate"),
            Visible = B("visible"),
            LoadSeconds = D("loadSeconds"),
            DrawSeconds = D("drawSeconds"),
            Problem = S("problem"),
        };
    }

    private sealed class Line : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public volatile bool Connected;

        public Line(int pid, string pipeName, TimeSpan retry, Action<AddinMessage> received)
        {
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);
                        await pipe.ConnectAsync(1000, _stop.Token);
                        Connected = true;
                        using var reader = new StreamReader(pipe, Encoding.UTF8);
                        while (!_stop.IsCancellationRequested)
                        {
                            var text = await reader.ReadLineAsync(_stop.Token);
                            if (text is null) break; // the add-in closed the line (SOLIDWORKS closing)
                            if (Parse(text) is { } m) received(m);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (TimeoutException) { /* add-in not loaded (yet); try again later */ }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { /* e.g. SOLIDWORKS running as a different user */ }
                    finally { Connected = false; }
                    try { await Task.Delay(retry, _stop.Token); } catch (OperationCanceledException) { break; }
                }
            });
        }

        public void Dispose()
        {
            try { _stop.Cancel(); } catch { }
        }
    }
}
