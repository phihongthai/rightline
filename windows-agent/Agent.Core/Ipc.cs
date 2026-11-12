using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace KidSafe.Agent.Core;

/// <summary>One JSON object per line: {"type":"...","payload":{...}}.</summary>
public sealed record IpcMessage(string Type, JsonElement? Payload = null);

public static class IpcNames
{
    public const string PipeName = "KidSafeClone.Agent";

    // Service -> Helper
    public const string State = "state";
    public const string PairResult = "pair:result";
    public const string RequestResult = "request:result";
    // Helper -> Service
    public const string Hello = "hello";
    public const string Idle = "idle";
    public const string PairSubmit = "pair:submit";
    public const string TimeRequest = "time_request";
    public const string Screenshot = "screenshot";
}

public sealed record HelperState(
    bool Paired, bool Locked, string Reason, string Message, bool Online,
    int ActiveMinutes, int? DailyLimitMinutes, DateTimeOffset? ExtraTimeUntil,
    bool ScreenshotEnabled = false, int ScreenshotIntervalSec = 60);
public sealed record IdlePayload(double IdleSeconds, string? ForegroundApp, string? ForegroundTitle = null);
public sealed record ScreenshotPayload(DateTimeOffset CapturedAt, string Data);
public sealed record PairSubmitPayload(string Code, string? MachineName);
public sealed record TimeRequestPayload(int Minutes, string Reason);
public sealed record ResultPayload(bool Ok, string? Error);

public sealed class IpcConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public IpcConnection(Stream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, new UTF8Encoding(false));
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    public async Task SendAsync(string type, object? payload = null, CancellationToken ct = default)
    {
        var line = Json.Serialize(new { type, payload });
        await _writeLock.WaitAsync(ct);
        try { await _writer.WriteLineAsync(line.AsMemory(), ct); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Returns null at end of stream.</summary>
    public async Task<IpcMessage?> ReadAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var line = await _reader.ReadLineAsync(ct);
            if (line is null) return null;
            if (line.Length is 0 or > 1_000_000) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type is null) continue;
                JsonElement? payload = root.TryGetProperty("payload", out var p) && p.ValueKind != JsonValueKind.Null ? p.Clone() : null;
                return new IpcMessage(type, payload);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // ignore malformed line
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _stream.Dispose(); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Accepts Helper connections. The newest connection replaces the previous one.</summary>
public sealed class IpcServer(Func<NamedPipeServerStream> createPipe, Func<NamedPipeServerStream, bool>? authorizeClient = null)
{
    private IpcConnection? _current;
    private readonly object _lock = new();

    public event Func<IpcMessage, Task>? MessageReceived;
    public event Action? ClientConnected;
    public event Action? ClientRejected;
    public bool HasClient { get { lock (_lock) return _current is not null; } }

    public async Task RunAsync(CancellationToken ct)
    {
        var tasks = new List<Task>();
        while (!ct.IsCancellationRequested)
        {
            var pipe = createPipe();
            try { await pipe.WaitForConnectionAsync(ct); }
            catch { pipe.Dispose(); break; }
            if (authorizeClient is not null && !SafeAuthorize(pipe)) { pipe.Dispose(); ClientRejected?.Invoke(); continue; }
            var conn = new IpcConnection(pipe);
            IpcConnection? old;
            lock (_lock) { old = _current; _current = conn; }
            if (old is not null) await old.DisposeAsync();
            tasks.RemoveAll(t => t.IsCompleted);
            tasks.Add(Task.Run(() => ServeAsync(conn, ct), ct));
        }
        IpcConnection? last;
        lock (_lock) { last = _current; _current = null; }
        if (last is not null) await last.DisposeAsync();
        try { await Task.WhenAll(tasks); } catch { /* ignore */ }
    }

    private bool SafeAuthorize(NamedPipeServerStream pipe)
    {
        try { return authorizeClient!(pipe); } catch { return false; }
    }

    private async Task ServeAsync(IpcConnection conn, CancellationToken ct)
    {
        try
        {
            ClientConnected?.Invoke();
            while (await conn.ReadAsync(ct) is { } msg)
            {
                if (MessageReceived is { } h)
                    foreach (var d in h.GetInvocationList().Cast<Func<IpcMessage, Task>>())
                        try { await d(msg); } catch { /* handler errors must not drop the pipe */ }
            }
        }
        catch (Exception) { /* broken pipe / cancel */ }
        finally
        {
            lock (_lock) { if (ReferenceEquals(_current, conn)) _current = null; }
            await conn.DisposeAsync();
        }
    }

    public async Task SendAsync(string type, object? payload)
    {
        IpcConnection? c;
        lock (_lock) c = _current;
        if (c is null) return;
        try { await c.SendAsync(type, payload); } catch { /* helper went away */ }
    }
}
