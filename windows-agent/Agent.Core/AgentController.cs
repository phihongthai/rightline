using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KidSafe.Agent.Core;

public sealed class AgentOptions
{
    public string BackendUrl { get; set; } = "http://localhost:3000";
    public string StatePath { get; set; } = "state.bin";
    public string MachineName { get; set; } = Environment.MachineName;
    public string OsVersion { get; set; } = Environment.OSVersion.ToString();
    public string AgentVersion { get; set; } = "0.1.0";
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan SaveInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Idle at or above this many seconds = no usage counted.</summary>
    public double IdleThresholdSeconds { get; set; } = 120;
    /// <summary>Helper idle reports older than this are ignored (helper dead/not logged in).</summary>
    public double IdleReportMaxAgeSeconds { get; set; } = 15;
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Brain of Agent.Service: owns persisted state, evaluates lock decisions every tick,
/// talks to the backend and to the user-session Helper. Platform-neutral so it can be tested anywhere.
/// </summary>
public sealed class AgentController
{
    private readonly AgentOptions _o;
    private readonly StateStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly IpcServer _ipc;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly object _lock = new();

    private AgentState _state;
    private BackendClient? _client;
    private CancellationTokenSource? _syncCts;
    private Task? _syncTask;
    private CancellationToken _stopping;

    private double _idleSeconds = double.MaxValue;
    private DateTimeOffset _idleReportedAt = DateTimeOffset.MinValue;
    private string? _foregroundApp;
    private bool _online;
    private long _lastTick;
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    private HelperState? _lastPublished;
    private bool _dirty;

    public AgentController(AgentOptions options, IProtector protector, TimeProvider time, Func<NamedPipeServerStream> createPipe, ILogger? logger = null,
        Func<NamedPipeServerStream, bool>? authorizeClient = null)
    {
        _o = options;
        _time = time;
        _log = logger ?? NullLogger.Instance;
        _store = new StateStore(options.StatePath, protector);
        _state = _store.Load();
        _ipc = new IpcServer(createPipe, authorizeClient);
        _ipc.MessageReceived += OnIpcAsync;
        _ipc.ClientConnected += () => _ = PublishAsync(force: true);
        _lastTick = time.GetTimestamp();
    }

    // ---- read-only views (tests/diagnostics) ----
    public AgentState Snapshot() { lock (_lock) return Json.Deserialize<AgentState>(Json.Serialize(_state))!; }
    public LockDecision CurrentDecision() { lock (_lock) return Decide(_time.GetLocalNow()); }

    public async Task RunAsync(CancellationToken ct)
    {
        _stopping = ct;
        var ipcTask = _ipc.RunAsync(ct);
        StartSync();
        try
        {
            using var timer = new PeriodicTimer(_o.TickInterval, _time);
            while (await timer.WaitForNextTickAsync(ct)) await TickAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_lock) _store.Save(_state);
            StopSync();
            try { await ipcTask; } catch { /* ignore */ }
            if (_syncTask is not null) try { await _syncTask; } catch { /* ignore */ }
        }
    }

    // ---- ticking ----
    internal async Task TickAsync()
    {
        var now = _time.GetLocalNow();
        var nowTs = _time.GetTimestamp();
        var elapsed = Math.Clamp(_time.GetElapsedTime(_lastTick, nowTs).TotalSeconds, 0, 5); // ignore sleep/clock jumps
        _lastTick = nowTs;

        lock (_lock)
        {
            var today = now.ToString("yyyy-MM-dd");
            if (_state.ActiveDate != today) { _state.ActiveDate = today; _state.ActiveSeconds = 0; _dirty = true; }

            var idleFresh = (now - _idleReportedAt).TotalSeconds <= _o.IdleReportMaxAgeSeconds;
            if (_state.Paired && idleFresh && _idleSeconds < _o.IdleThresholdSeconds && !Decide(now).Locked)
                _state.ActiveSeconds += elapsed;

            if (_dirty || now - _lastSave >= _o.SaveInterval)
            {
                _store.Save(_state);
                _lastSave = now;
                _dirty = false;
            }
        }
        await PublishAsync(force: false);
    }

    private LockDecision Decide(DateTimeOffset now) =>
        _state.Paired
            ? PolicyEngine.Evaluate(_state.Policy, _state.ManualLocked, _state.ExtraTimeUntil, _state.ActiveSeconds, now)
            : new(false, LockReason.None);

    private HelperState BuildHelperState()
    {
        lock (_lock)
        {
            var d = Decide(_time.GetLocalNow());
            return new HelperState(_state.Paired, d.Locked, d.Code, d.Message, _online,
                (int)(_state.ActiveSeconds / 60), _state.Policy?.DailyLimitMinutes, _state.ExtraTimeUntil);
        }
    }

    private async Task PublishAsync(bool force)
    {
        var s = BuildHelperState();
        if (!force && Equals(s, _lastPublished)) return;
        _lastPublished = s;
        await _ipc.SendAsync(IpcNames.State, s);
    }

    // ---- backend sync ----
    private void StartSync()
    {
        string? token;
        lock (_lock) token = _state.DeviceToken;
        if (token is null) return;
        StopSync();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        var client = new BackendClient(_o.BackendUrl, token, BuildHeartbeat, _log) { HeartbeatInterval = _o.HeartbeatInterval };
        client.PolicyReceived += p => Mutate(s => s.Policy = p);
        client.LockChanged += l => Mutate(s => s.ManualLocked = l);
        client.TimeGranted += t => Mutate(s => s.ExtraTimeUntil = t);
        client.ConnectionChanged += c => { _online = c; _ = PublishAsync(force: false); };
        client.Unauthorized += () => _log.LogWarning("device token rejected by server");
        _client = client;
        _syncCts = cts;
        _syncTask = Task.Run(() => client.RunAsync(cts.Token));
    }

    private void StopSync()
    {
        _syncCts?.Cancel();
        _syncCts = null;
        _client = null;
        _online = false;
    }

    private void Mutate(Action<AgentState> f)
    {
        lock (_lock) { f(_state); _dirty = true; }
        _ = PublishAsync(force: false);
    }

    private HeartbeatPayload BuildHeartbeat()
    {
        lock (_lock)
            return new HeartbeatPayload((int)(_state.ActiveSeconds / 60), _foregroundApp, Decide(_time.GetLocalNow()).Locked);
    }

    // ---- IPC from Helper ----
    internal async Task OnIpcAsync(IpcMessage m)
    {
        switch (m.Type)
        {
            case IpcNames.Hello:
                await PublishAsync(force: true);
                break;

            case IpcNames.Idle:
                if (m.Payload.As<IdlePayload>() is { } idle)
                {
                    _idleSeconds = idle.IdleSeconds;
                    _idleReportedAt = _time.GetLocalNow();
                    _foregroundApp = idle.ForegroundApp;
                }
                break;

            case IpcNames.PairSubmit:
                if (m.Payload.As<PairSubmitPayload>() is { } ps) await PairAsync(ps);
                break;

            case IpcNames.TimeRequest:
                if (m.Payload.As<TimeRequestPayload>() is { } tr) await RequestTimeAsync(tr);
                break;
        }
    }

    private async Task PairAsync(PairSubmitPayload ps)
    {
        try
        {
            var res = await BackendApi.PairAsync(_http, _o.BackendUrl, new PairRequest(
                ps.Code.Trim().ToUpperInvariant(),
                string.IsNullOrWhiteSpace(ps.MachineName) ? _o.MachineName : ps.MachineName!,
                _o.OsVersion, _o.AgentVersion), _stopping);
            lock (_lock)
            {
                _state = new AgentState
                {
                    DeviceId = res.DeviceId,
                    DeviceToken = res.DeviceToken,
                    ActiveDate = _time.GetLocalNow().ToString("yyyy-MM-dd"),
                };
                _store.Save(_state);
            }
            StartSync();
            await _ipc.SendAsync(IpcNames.PairResult, new ResultPayload(true, null));
            await PublishAsync(force: true);
        }
        catch (PairingException ex)
        {
            await _ipc.SendAsync(IpcNames.PairResult, new ResultPayload(false, ex.Message));
        }
    }

    private async Task RequestTimeAsync(TimeRequestPayload tr)
    {
        var minutes = Math.Clamp(tr.Minutes, 1, 480);
        var reason = (tr.Reason ?? "").Trim();
        if (reason.Length > 500) reason = reason[..500];
        var c = _client;
        var ok = c is not null && await c.SendAsync("time_request:create", new { requested_minutes = minutes, reason });
        await _ipc.SendAsync(IpcNames.RequestResult, new ResultPayload(ok, ok ? null : "offline"));
    }
}
