using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KidSafe.Agent.Core;

public sealed class PairingException(string message) : Exception(message);

public static class BackendApi
{
    public static async Task<PairResponse> PairAsync(HttpClient http, string baseUrl, PairRequest req, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(req, options: Json.Options);
        HttpResponseMessage res;
        try { res = await http.PostAsync($"{baseUrl.TrimEnd('/')}/api/agent/pair", content, ct); }
        catch (HttpRequestException) { throw new PairingException("network"); }
        using (res)
        {
            if (res.StatusCode == HttpStatusCode.BadRequest) throw new PairingException("invalid_or_expired_code");
            if (res.StatusCode == (HttpStatusCode)429) throw new PairingException("rate_limited");
            if (!res.IsSuccessStatusCode) throw new PairingException($"http_{(int)res.StatusCode}");
            return (await res.Content.ReadFromJsonAsync<PairResponse>(Json.Options, ct))!;
        }
    }
}

/// <summary>Persistent WebSocket to /ws/agent with automatic reconnect.</summary>
public sealed class BackendClient(string baseUrl, string deviceToken, Func<HeartbeatPayload> heartbeat, ILogger? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger.Instance;
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    public event Action<Policy>? PolicyReceived;
    public event Action<bool>? LockChanged;                 // true = lock, false = unlock
    public event Action<DateTimeOffset>? TimeGranted;
    public event Action<bool>? ConnectionChanged;
    public event Action? Unauthorized;                      // token revoked/re-paired

    public bool IsConnected => _ws is { State: WebSocketState.Open };

    public async Task RunAsync(CancellationToken ct)
    {
        var uri = new Uri(baseUrl.TrimEnd('/').Replace("https://", "wss://").Replace("http://", "ws://") + "/ws/agent");
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", $"Bearer {deviceToken}");
            try
            {
                await ws.ConnectAsync(uri, ct);
                _ws = ws;
                backoff = TimeSpan.FromSeconds(1);
                ConnectionChanged?.Invoke(true);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var hb = HeartbeatLoopAsync(linked.Token);
                try { await ReceiveLoopAsync(ws, linked.Token); }
                finally { linked.Cancel(); try { await hb; } catch { /* ignore */ } }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (WebSocketException ex)
            {
                if (ws.HttpStatusCode == HttpStatusCode.Unauthorized) { Unauthorized?.Invoke(); }
                _log.LogDebug(ex, "ws error");
            }
            catch (Exception ex) { _log.LogDebug(ex, "ws error"); }
            finally
            {
                var was = IsConnected || _ws == ws;
                _ws = null;
                ws.Dispose();
                if (was) ConnectionChanged?.Invoke(false);
            }
            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds));
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        await SendAsync("heartbeat", heartbeat(), ct);
        using var timer = new PeriodicTimer(HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(ct)) await SendAsync("heartbeat", heartbeat(), ct);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, r.Count);
                if (ms.Length > 4_000_000) return;
            } while (!r.EndOfMessage);
            Dispatch(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
        }
    }

    internal void Dispatch(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var type = doc.RootElement.GetProperty("type").GetString();
            JsonElement? payload = doc.RootElement.TryGetProperty("payload", out var p) && p.ValueKind != JsonValueKind.Null ? p.Clone() : null;
            switch (type)
            {
                case "policy:sync": if (payload.As<Policy>() is { } pol) PolicyReceived?.Invoke(pol); break;
                case "command:lock": LockChanged?.Invoke(true); break;
                case "command:unlock": LockChanged?.Invoke(false); break;
                case "command:grant_time": if (payload.As<GrantTimePayload>() is { } g) TimeGranted?.Invoke(g.ExtraTimeUntil); break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            _log.LogWarning("bad server message");
        }
    }

    public async Task<bool> SendAsync(string type, object? payload, CancellationToken ct = default)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) return false;
        var bytes = Encoding.UTF8.GetBytes(Json.Serialize(new { type, payload }));
        await _sendLock.WaitAsync(ct);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); return true; }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { return false; }
        finally { _sendLock.Release(); }
    }
}
