using System.IO.Pipes;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KidSafe.Agent.Core;

public sealed class AgentOptions
{
    public string BackendUrl { get; set; } = "http://localhost:3000";
    public string StatePath { get; set; } = "state.bin";
    /// <summary>Directory for the offline screenshot queue. null = screenshots disabled.</summary>
    public string? QueueDir { get; set; }
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

    /// <summary>Local DNS proxy bind address (127.0.0.1:53 in production). null = no DNS filtering.</summary>
    public IPEndPoint? DnsListen { get; set; }
    /// <summary>Used when the "adult" category is on (Cloudflare family: blocks malware + adult).</summary>
    public IPEndPoint DnsUpstreamFamily { get; set; } = new(IPAddress.Parse("1.1.1.3"), 53);
    public IPEndPoint DnsUpstreamDefault { get; set; } = new(IPAddress.Parse("8.8.8.8"), 53);
    public TimeSpan LogFlushInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan NetworkVerifyInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan AlertThrottle { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Brain of Agent.Service: owns persisted state, evaluates lock decisions every tick,
/// talks to the backend and to the user-session Helper, runs the DNS filter and uploads telemetry.
/// Platform-neutral; Windows specifics come in through <see cref="AgentPlatform"/>.
/// </summary>
public sealed class AgentController
{
    private readonly AgentOptions _o;
    private readonly StateStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly IpcServer _ipc;
    private readonly AgentPlatform _platform;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly DomainFilter _filter = new();
    private readonly ActivityLog _activity;
    private readonly ScreenshotUploader? _shots;
    private readonly Dictionary<string, DateTimeOffset> _alertLast = new();
    private readonly object _lock = new();

    private AgentState _state;
    private BackendClient? _client;
    private CancellationTokenSource? _syncCts;
    private Task? _syncTask;
    private CancellationToken _stopping;
    private DnsProxy? _dns;

    private double _idleSeconds = double.MaxValue;
    private DateTimeOffset _idleReportedAt = DateTimeOffset.MinValue;
    private string? _foregroundApp, _foregroundTitle;
    private bool _online;
    private long _lastTick;
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue, _lastFlush, _lastNetVerify;
    private HelperState? _lastPublished;
    private bool _dirty;
    private int _flushing;

    /// <summary>Raised after a new policy has been applied (the Windows layer re-sweeps running processes).</summary>
    public event Action? PolicyApplied;

    public AgentController(AgentOptions options, IProtector protector, TimeProvider time, Func<NamedPipeServerStream> createPipe,
        ILogger? logger = null, Func<NamedPipeServerStream, bool>? authorizeClient = null, AgentPlatform? platform = null)
    {
        _o = options;
        _time = time;
        _log = logger ?? NullLogger.Instance;
        _platform = platform ?? new AgentPlatform();
        _store = new StateStore(options.StatePath, protector);
        _state = _store.Load();
        _activity = new ActivityLog(time);
        if (options.QueueDir is not null) _shots = new ScreenshotUploader(_http, options.BackendUrl, options.QueueDir);
        _ipc = new IpcServer(createPipe, authorizeClient);
        _ipc.MessageReceived += OnIpcAsync;
        _ipc.ClientConnected += () => _ = PublishAsync(force: true);
        _ipc.ClientRejected += () => QueueTamper("impostor_pipe_client");
        _lastTick = time.GetTimestamp();
        _lastFlush = _lastNetVerify = time.GetLocalNow();
    }

    // ---- read-only views (tests/diagnostics) ----
    public AgentState Snapshot() { lock (_lock) return Json.Deserialize<AgentState>(Json.Serialize(_state))!; }
    public LockDecision CurrentDecision() { lock (_lock) return Decide(_time.GetLocalNow()); }
    public IPEndPoint? DnsEndPoint => _dns?.UdpEndPoint;

    public bool ShouldBlockProcess(string processName)
    {
        Policy? p;
        lock (_lock) p = _state.Paired ? _state.Policy : null;
        return AppBlockMatcher.ShouldBlock(p, processName, _time.GetLocalNow());
    }

    public void ReportBlockedApp(string processName) => _activity.RecordSystem("blocked_app", processName);

    public async Task RunAsync(CancellationToken ct)
    {
        _stopping = ct;
        lock (_lock)
        {
            if (_state.RunningSince is not null) QueueTamperLocked("unclean_shutdown");
            _state.RunningSince = _time.GetUtcNow();
            _store.Save(_state);
        }
        var ipcTask = _ipc.RunAsync(ct);
        StartDns();
        EnforceNetwork();
        StartSync();
        try
        {
            using var timer = new PeriodicTimer(_o.TickInterval, _time);
            while (await timer.WaitForNextTickAsync(ct)) await TickAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_lock) { _state.RunningSince = null; _store.Save(_state); }
            StopSync();
            if (_dns is not null) await _dns.DisposeAsync();
            try { await ipcTask; } catch { /* ignore */ }
            if (_syncTask is not null) try { await _syncTask; } catch { /* ignore */ }
        }
    }

    /// <summary>Uninstall path: undo every system change (DNS back to what it was, DoH policies removed).</summary>
    public void Cleanup()
    {
        lock (_lock)
        {
            if (_platform.Dns is { } dns) new DnsGuard(dns, _state.OriginalDns).Restore();
            if (_platform.BrowserPolicy is { } w) DohPolicy.Apply(w, enabled: false);
            _store.Save(_state);
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
            {
                _state.ActiveSeconds += elapsed;
                _activity.RecordApp(_foregroundApp, _foregroundTitle, elapsed);
            }

            if (_dirty || now - _lastSave >= _o.SaveInterval)
            {
                _store.Save(_state);
                _lastSave = now;
                _dirty = false;
            }
        }

        if (now - _lastNetVerify >= _o.NetworkVerifyInterval) { _lastNetVerify = now; EnforceNetwork(); }
        if (now - _lastFlush >= _o.LogFlushInterval) { _lastFlush = now; _ = FlushBackgroundAsync(); }
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
                (int)(_state.ActiveSeconds / 60), _state.Policy?.DailyLimitMinutes, _state.ExtraTimeUntil,
                ScreenshotEnabled: _state.Paired && _shots is not null && _state.Policy?.ProtectionFlags.Screenshot == true,
                ScreenshotIntervalSec: _state.Policy?.ScreenshotIntervalSec ?? 60);
        }
    }

    private async Task PublishAsync(bool force)
    {
        var s = BuildHelperState();
        if (!force && Equals(s, _lastPublished)) return;
        _lastPublished = s;
        await _ipc.SendAsync(IpcNames.State, s);
    }

    // ---- web filter: DNS proxy, adapter redirect, DoH policy ----
    private void StartDns()
    {
        if (_o.DnsListen is null) return;
        try
        {
            var dns = new DnsProxy(_o.DnsListen, name => _filter.Decide(name, PolicyForFilter()), ChooseUpstream,
                (domain, blocked) => { if (WebFilterOn()) _activity.RecordDomain(domain, blocked); });
            dns.Start();
            _dns = dns;
        }
        catch (Exception ex)
        {
            // Never redirect system DNS to a proxy that isn't listening: that would cut the internet.
            _log.LogError(ex, "DNS proxy could not start on {Endpoint}; web filtering disabled", _o.DnsListen);
        }
    }

    private Policy? PolicyForFilter() { lock (_lock) return _state.Paired ? _state.Policy : null; }
    private bool WebFilterOn() { lock (_lock) return _state.Paired && _state.Policy?.ProtectionFlags.WebFilter == true; }

    private IPEndPoint ChooseUpstream()
    {
        var p = PolicyForFilter();
        return p is { ProtectionFlags.WebFilter: true } && p.BlockedCategories.Contains("adult") ? _o.DnsUpstreamFamily : _o.DnsUpstreamDefault;
    }

    /// <summary>Idempotent: (re)applies adapter DNS + DoH policy to match the policy; reports tampering.</summary>
    internal void EnforceNetwork()
    {
        try
        {
            lock (_lock)
            {
                var web = WebFilterOn();
                if (_platform.Dns is { } dns)
                {
                    var guard = new DnsGuard(dns, _state.OriginalDns);
                    if (web && _dns is not null)
                    {
                        var reverted = guard.Enforce();
                        if (reverted.Count > 0) QueueTamperLocked("dns_changed:" + string.Join(",", reverted));
                    }
                    else if (!web) guard.Restore();
                    _dirty = true;
                }
                if (_platform.BrowserPolicy is { } w) DohPolicy.Apply(w, web);
            }
        }
        catch (Exception ex) { _log.LogError(ex, "network enforcement failed"); }
    }

    // ---- tamper alerts ----
    private void QueueTamper(string detail) { lock (_lock) QueueTamperLocked(detail); }

    private void QueueTamperLocked(string detail)
    {
        if (!_state.Paired) return;
        var now = _time.GetUtcNow();
        var key = detail.Split(':')[0];
        if (_alertLast.TryGetValue(key, out var last) && now - last < _o.AlertThrottle) return;
        _alertLast[key] = now;
        _state.PendingAlerts.Add(detail);
        if (_state.PendingAlerts.Count > 20) _state.PendingAlerts.RemoveAt(0);
        _activity.RecordSystem("tamper", detail);
        _dirty = true;
    }

    private async Task FlushAlertsAsync()
    {
        var c = _client;
        if (c is null || !c.IsConnected) return;
        string[] pending;
        lock (_lock) pending = _state.PendingAlerts.ToArray();
        foreach (var a in pending)
        {
            if (!await c.SendAsync("alert:tamper", new { detail = a })) return;
            lock (_lock) { _state.PendingAlerts.Remove(a); _dirty = true; }
        }
    }

    // ---- telemetry upload ----
    private async Task FlushBackgroundAsync()
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return;
        try
        {
            string? token;
            lock (_lock) token = _state.DeviceToken;
            if (token is null) return;
            await _activity.FlushAsync(_http, _o.BackendUrl, token, _stopping);
            if (_shots is not null) await _shots.FlushAsync(token, _stopping);
            await FlushAlertsAsync();
        }
        catch (Exception ex) { _log.LogDebug(ex, "background flush failed"); }
        finally { Volatile.Write(ref _flushing, 0); }
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
        client.PolicyReceived += p => { Mutate(s => s.Policy = p); EnforceNetwork(); PolicyApplied?.Invoke(); };
        client.LockChanged += l => Mutate(s => s.ManualLocked = l);
        client.TimeGranted += t => Mutate(s => s.ExtraTimeUntil = t);
        client.ConnectionChanged += c => { _online = c; _ = PublishAsync(force: false); if (c) _ = FlushBackgroundAsync(); };
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
                    _foregroundTitle = idle.ForegroundTitle;
                }
                break;

            case IpcNames.PairSubmit:
                if (m.Payload.As<PairSubmitPayload>() is { } ps) await PairAsync(ps);
                break;

            case IpcNames.TimeRequest:
                if (m.Payload.As<TimeRequestPayload>() is { } tr) await RequestTimeAsync(tr);
                break;

            case IpcNames.Screenshot:
                if (m.Payload.As<ScreenshotPayload>() is { } sp) OnScreenshot(sp);
                break;
        }
    }

    private void OnScreenshot(ScreenshotPayload sp)
    {
        if (_shots is null || !WebFilterOrScreenshotAllowed() || sp.Data.Length > ScreenshotUploader.MaxBytes * 2) return;
        byte[] jpeg;
        try { jpeg = Convert.FromBase64String(sp.Data); } catch (FormatException) { return; }
        // Sanity: JPEG magic bytes, and never trust a client-supplied future timestamp.
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return;
        var at = sp.CapturedAt > _time.GetUtcNow() ? _time.GetUtcNow() : sp.CapturedAt;
        if (_shots.Enqueue(jpeg, at)) _ = FlushBackgroundAsync();
    }

    private bool WebFilterOrScreenshotAllowed() { lock (_lock) return _state.Paired && _state.Policy?.ProtectionFlags.Screenshot == true; }

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
                    RunningSince = _time.GetUtcNow(),
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
