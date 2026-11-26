using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace MathNotes.Core;

/// <summary>
/// Dauerhaft laufender Erkenner-Prozess (recognize.py --serve): das Modell wird einmal geladen,
/// danach folgen Anfragen schnell. Fehler/Absturz werden als Fehler-Ergebnis gemeldet und blockieren die App nie.
/// </summary>
public sealed class RecognizerService : IDisposable
{
    private readonly string _python;
    private readonly string _script;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<RecognitionOutcome>> _pending = new();
    private readonly StringBuilder _stderr = new();
    private Process? _proc;
    private TaskCompletionSource<bool>? _ready;
    private long _nextId;

    public string EngineName { get; private set; } = "unbekannt";
    public string EngineVersion { get; private set; } = "?";

    /// <summary>Statusmeldungen (kommen von einem Hintergrund-Thread).</summary>
    public event Action<string>? StatusChanged;

    public RecognizerService(string pythonExe, string scriptPath)
    {
        _python = pythonExe;
        _script = scriptPath;
    }

    public bool IsInstalled => File.Exists(_python) && File.Exists(_script);

    public async Task<RecognitionOutcome> RecognizeAsync(IReadOnlyList<StrokeData> strokes, CancellationToken ct = default)
    {
        long id = 0;
        try
        {
            await EnsureStartedAsync(ct);
            id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<RecognitionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            var arr = new JsonArray();
            foreach (var s in strokes) arr.Add(StrokeJson.ToNode(s));
            var req = new JsonObject
            {
                ["id"] = id,
                ["strokes"] = arr,
                ["debug_image"] = Pix2TexClient.DebugImagePath(),
            }.ToJsonString();

            await _sendLock.WaitAsync(ct);
            try
            {
                await _proc!.StandardInput.WriteLineAsync(req.AsMemory(), ct);
                await _proc.StandardInput.FlushAsync(ct);
            }
            finally
            {
                _sendLock.Release();
            }
            return await tcs.Task.WaitAsync(TimeSpan.FromMinutes(3), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return Fail("Zeitueberschreitung");
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (id != 0) _pending.TryRemove(id, out _);
        }
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            if (_proc is { HasExited: false } && _ready is not null)
            {
                await _ready.Task.WaitAsync(TimeSpan.FromMinutes(10), ct);
                return;
            }

            StatusChanged?.Invoke("Erkennung startet (Modell wird geladen) ...");
            lock (_stderr) _stderr.Clear();
            var psi = new ProcessStartInfo(_python)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(_script);
            psi.ArgumentList.Add("--serve");
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            var p = Process.Start(psi) ?? throw new InvalidOperationException("Prozess konnte nicht gestartet werden");
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _proc = p;
            _ready = ready;
            _ = Task.Run(() => ReadLoopAsync(p, ready));
            _ = Task.Run(() => DrainStderrAsync(p));
            await ready.Task.WaitAsync(TimeSpan.FromMinutes(10), ct);
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task ReadLoopAsync(Process p, TaskCompletionSource<bool> ready)
    {
        try
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) is not null)
                HandleLine(line, ready);
        }
        catch
        {
            // Prozess beendet oder Pipe geschlossen
        }
        var tail = StderrTail();
        ready.TrySetException(new InvalidOperationException("Erkenner beendet. " + tail));
        foreach (var kv in _pending)
            kv.Value.TrySetResult(Fail("Erkenner beendet. " + tail));
        StatusChanged?.Invoke("Erkennung beendet");
    }

    private async Task DrainStderrAsync(Process p)
    {
        try
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync()) is not null)
            {
                lock (_stderr)
                {
                    _stderr.AppendLine(line);
                    if (_stderr.Length > 4000) _stderr.Remove(0, _stderr.Length - 4000);
                }
            }
        }
        catch
        {
            // egal
        }
    }

    private string StderrTail()
    {
        lock (_stderr)
        {
            var s = _stderr.ToString().Trim();
            return s.Length <= 300 ? s : s[^300..];
        }
    }

    private void HandleLine(string line, TaskCompletionSource<bool> ready)
    {
        line = line.Trim();
        if (!line.StartsWith('{')) return;
        JsonObject o;
        try
        {
            o = JsonNode.Parse(line)!.AsObject();
        }
        catch
        {
            return;
        }

        if (o.ContainsKey("ready"))
        {
            if (o["ready"]!.GetValue<bool>())
            {
                EngineName = o["engine"]?.GetValue<string>() ?? "unbekannt";
                EngineVersion = o["engine_version"]?.GetValue<string>() ?? "?";
                ready.TrySetResult(true);
                StatusChanged?.Invoke("Erkennung bereit");
            }
            else
            {
                ready.TrySetException(new InvalidOperationException(
                    o["error"]?.GetValue<string>() ?? "Start fehlgeschlagen"));
            }
            return;
        }

        if (o["id"] is JsonNode idNode && _pending.TryGetValue(idNode.GetValue<long>(), out var tcs))
            tcs.TrySetResult(Pix2TexClient.ParseOutput(line));
    }

    private static RecognitionOutcome Fail(string msg) =>
        new("unbekannt", "?", Array.Empty<RecognitionCandidate>(), msg);

    public void Dispose()
    {
        try { _proc?.Kill(true); } catch { /* bereits beendet */ }
        _proc?.Dispose();
    }
}
