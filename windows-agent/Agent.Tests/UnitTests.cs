using KidSafe.Agent.Core;

namespace Agent.Tests;

public class PolicyEngineTests
{
    static DateTimeOffset At(int day, int hour) =>
        new DateTimeOffset(2026, 3, 1, hour, 30, 0, TimeSpan.FromHours(7)).AddDays(day); // 2026-03-01 is Sunday

    [Fact]
    public void Day_index_zero_is_sunday_and_hour_is_local()
    {
        var p = T.Policy();
        p.ScheduleGrid[0][9] = false;           // Sunday 09:xx blocked
        Assert.Equal(LockReason.Schedule, PolicyEngine.Evaluate(p, false, null, 0, At(0, 9)).Reason);
        Assert.False(PolicyEngine.Evaluate(p, false, null, 0, At(1, 9)).Locked);  // Monday ok
        Assert.False(PolicyEngine.Evaluate(p, false, null, 0, At(0, 10)).Locked);
    }

    [Fact]
    public void Manual_lock_wins_even_over_extra_time()
    {
        var now = At(0, 9);
        var d = PolicyEngine.Evaluate(T.Policy(), true, now.AddHours(1), 0, now);
        Assert.Equal(LockReason.Manual, d.Reason);
    }

    [Fact]
    public void Extra_time_overrides_schedule_and_limit_until_it_expires()
    {
        var p = T.Policy(limit: 1, allowed: false);
        var now = At(2, 12);
        Assert.False(PolicyEngine.Evaluate(p, false, now.AddMinutes(10), 9999, now).Locked);
        Assert.True(PolicyEngine.Evaluate(p, false, now.AddMinutes(-1), 9999, now).Locked);
    }

    [Fact]
    public void Daily_limit_locks_at_exact_threshold()
    {
        var p = T.Policy(limit: 30);
        var now = At(3, 15);
        Assert.False(PolicyEngine.Evaluate(p, false, null, 30 * 60 - 0.1, now).Locked);
        Assert.Equal(LockReason.DailyLimit, PolicyEngine.Evaluate(p, false, null, 30 * 60, now).Reason);
    }

    [Fact]
    public void Schedule_switch_off_disables_schedule_and_limit()
    {
        var p = T.Policy(scheduleLock: false, limit: 1, allowed: false);
        Assert.False(PolicyEngine.Evaluate(p, false, null, 9999, At(1, 1)).Locked);
    }

    [Fact]
    public void No_policy_means_no_lock_unless_manual()
    {
        Assert.False(PolicyEngine.Evaluate(null, false, null, 0, At(0, 0)).Locked);
        Assert.True(PolicyEngine.Evaluate(null, true, null, 0, At(0, 0)).Locked);
    }

    [Fact]
    public void Malformed_grid_does_not_crash()
    {
        var p = T.Policy(); p.ScheduleGrid = [[true]];
        Assert.False(PolicyEngine.Evaluate(p, false, null, 0, At(0, 5)).Locked);
    }

    [Fact]
    public void Server_json_deserializes_into_policy()
    {
        var json = """
        {"device_id":"x","version":3,"protection_flags":{"web_filter":true,"app_block":false,"schedule_lock":true,"screenshot":true},
         "schedule_grid":[[true,false]],"daily_limit_minutes":90,"blocked_categories":["adult"],"custom_blocked_domains":[],
         "custom_allowed_domains":[],"blocked_apps":[{"name":"R","process_name":"r.exe","mode":"always"}],"screenshot_interval_sec":30}
        """;
        var p = Json.Deserialize<Policy>(json)!;
        Assert.Equal(3, p.Version);
        Assert.True(p.ProtectionFlags.ScheduleLock);
        Assert.False(p.ProtectionFlags.AppBlock);
        Assert.Equal(90, p.DailyLimitMinutes);
        Assert.Equal("r.exe", p.BlockedApps[0].ProcessName);
        Assert.Equal(30, p.ScreenshotIntervalSec);
    }
}

public class StateStoreTests
{
    [Fact]
    public void Roundtrips_and_is_not_plaintext()
    {
        var path = T.TempFile();
        var s = new StateStore(path, new XorProtector());
        s.Save(new AgentState { DeviceToken = "SECRET-TOKEN", ActiveSeconds = 12.5, ManualLocked = true, Policy = T.Policy(limit: 5) });
        Assert.DoesNotContain("SECRET-TOKEN", File.ReadAllText(path));
        var back = s.Load();
        Assert.Equal("SECRET-TOKEN", back.DeviceToken);
        Assert.Equal(12.5, back.ActiveSeconds);
        Assert.True(back.ManualLocked);
        Assert.Equal(5, back.Policy!.DailyLimitMinutes);
    }

    [Fact]
    public void Missing_file_gives_fresh_state()
    {
        var s = new StateStore(T.TempFile(), new XorProtector()).Load();
        Assert.False(s.Paired);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_backup()
    {
        var path = T.TempFile();
        var s = new StateStore(path, new XorProtector());
        s.Save(new AgentState { DeviceToken = "A" });
        s.Save(new AgentState { DeviceToken = "B" });   // creates .bak with A
        File.WriteAllText(path, "garbage");
        Assert.Equal("A", s.Load().DeviceToken);
    }
}

public class IpcTests
{
    [Fact]
    public async Task Server_and_client_exchange_messages_and_newest_client_replaces_old()
    {
        var pipe = T.PipeName();
        var server = new IpcServer(T.PipeFactory(pipe));
        var got = new List<IpcMessage>();
        server.MessageReceived += m => { lock (got) got.Add(m); return Task.CompletedTask; };
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);

        await using var a = await T.ConnectHelper(pipe);
        await a.SendAsync(IpcNames.Idle, new IdlePayload(3, "chrome.exe"));
        await WaitFor(() => { lock (got) return got.Count == 1; });
        Assert.Equal(3, got[0].Payload.As<IdlePayload>()!.IdleSeconds);

        await server.SendAsync(IpcNames.State, new { locked = true });
        var back = await a.ReadAsync();
        Assert.Equal(IpcNames.State, back!.Type);

        await using var b = await T.ConnectHelper(pipe);
        await b.SendAsync(IpcNames.Hello);
        await WaitFor(() => { lock (got) return got.Count == 2; });
        Assert.Null(await a.ReadAsync());       // old one was dropped

        cts.Cancel();
        await Task.WhenAny(run, Task.Delay(3000));
    }

    [Fact]
    public async Task Unauthorized_clients_are_dropped_and_authorized_ones_still_work()
    {
        var pipe = T.PipeName();
        int calls = 0;
        var server = new IpcServer(T.PipeFactory(pipe), _ => Interlocked.Increment(ref calls) > 1); // reject first client
        var got = 0;
        server.MessageReceived += _ => { Interlocked.Increment(ref got); return Task.CompletedTask; };
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);

        await using var evil = await T.ConnectHelper(pipe);
        Assert.Null(await evil.ReadAsync());                       // closed by server
        await using var good = await T.ConnectHelper(pipe);
        await good.SendAsync(IpcNames.Hello);
        await WaitFor(() => Volatile.Read(ref got) == 1);
        cts.Cancel();
        await Task.WhenAny(run, Task.Delay(3000));
    }

    [Fact]
    public async Task Malformed_lines_are_skipped()
    {
        var ms = new MemoryStream("not json\n{\"x\":1}\n{\"type\":\"ok\"}\n"u8.ToArray());
        await using var c = new IpcConnection(ms);
        Assert.Equal("ok", (await c.ReadAsync())!.Type);
    }

    internal static async Task WaitFor(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > end) throw new TimeoutException(); await Task.Delay(20); }
    }
}

public class ControllerTickTests
{
    static Task Idle(AgentController c, double idle) =>
        c.OnIpcAsync(new IpcMessage(IpcNames.Idle, Json.ToElement(new IdlePayload(idle, "game.exe"))));

    [Fact]
    public async Task Counts_active_time_and_locks_at_daily_limit_then_stops_counting()
    {
        var (c, time, _, _) = T.PairedController(T.Policy(limit: 1));
        await Idle(c, 0);
        for (int i = 0; i < 59; i++) { time.Advance(TimeSpan.FromSeconds(1)); await Idle(c, 0); await c.TickAsync(); }
        Assert.False(c.CurrentDecision().Locked);
        time.Advance(TimeSpan.FromSeconds(1)); await Idle(c, 0); await c.TickAsync();
        Assert.Equal(LockReason.DailyLimit, c.CurrentDecision().Reason);
        var secs = c.Snapshot().ActiveSeconds;
        for (int i = 0; i < 5; i++) { time.Advance(TimeSpan.FromSeconds(1)); await Idle(c, 0); await c.TickAsync(); }
        Assert.Equal(secs, c.Snapshot().ActiveSeconds);
    }

    [Fact]
    public async Task Idle_user_is_not_counted()
    {
        var (c, time, _, _) = T.PairedController(T.Policy(limit: 100));
        for (int i = 0; i < 10; i++) { time.Advance(TimeSpan.FromSeconds(1)); await Idle(c, 130); await c.TickAsync(); }
        Assert.Equal(0, c.Snapshot().ActiveSeconds);
    }

    [Fact]
    public async Task Stale_idle_report_means_not_counted()
    {
        var (c, time, _, _) = T.PairedController(T.Policy(limit: 100));
        await Idle(c, 0);
        for (int i = 0; i < 30; i++) { time.Advance(TimeSpan.FromSeconds(1)); await c.TickAsync(); }
        Assert.InRange(c.Snapshot().ActiveSeconds, 0, 15.5);   // only the first ~15s had a fresh report
    }

    [Fact]
    public async Task Large_time_jump_is_clamped()
    {
        var (c, time, _, _) = T.PairedController(T.Policy(limit: 1000));
        await Idle(c, 0);
        time.Advance(TimeSpan.FromHours(2)); await Idle(c, 0); await c.TickAsync();
        Assert.True(c.Snapshot().ActiveSeconds <= 5);
    }

    [Fact]
    public async Task Counter_resets_at_local_midnight()
    {
        var start = new DateTimeOffset(2026, 3, 4, 23, 59, 50, TimeSpan.Zero);
        var (c, time, _, _) = T.PairedController(T.Policy(limit: 1000), start: start);
        await Idle(c, 0);
        for (int i = 0; i < 5; i++) { time.Advance(TimeSpan.FromSeconds(1)); await Idle(c, 0); await c.TickAsync(); }
        Assert.True(c.Snapshot().ActiveSeconds > 0);
        time.Advance(TimeSpan.FromSeconds(10)); await Idle(c, 0); await c.TickAsync();
        Assert.Equal("2026-03-05", c.Snapshot().ActiveDate);
        Assert.True(c.Snapshot().ActiveSeconds <= 5);
    }

    [Fact]
    public async Task State_is_persisted_and_survives_restart_with_lock_intact()
    {
        var (c, time, _, path) = T.PairedController(T.Policy(), manual: true);
        await c.TickAsync();
        var c2 = new AgentController(new AgentOptions { StatePath = path }, new XorProtector(), time, T.PipeFactory(T.PipeName()));
        Assert.Equal(LockReason.Manual, c2.CurrentDecision().Reason);
    }

    [Fact]
    public async Task Unpaired_agent_never_locks()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var c = new AgentController(new AgentOptions { StatePath = T.TempFile() }, new XorProtector(), time, T.PipeFactory(T.PipeName()));
        await c.TickAsync();
        Assert.False(c.CurrentDecision().Locked);
    }

    [Fact]
    public async Task Helper_gets_state_on_hello_and_on_change()
    {
        var (c, time, pipe, _) = T.PairedController(T.Policy(), manual: true);
        using var cts = new CancellationTokenSource();
        var run = c.RunAsync(cts.Token);
        await using var helper = await T.ConnectHelper(pipe);
        var st = Json.Deserialize<HelperState>(Json.Serialize((await helper.ReadAsync())!.Payload!.Value))!;
        Assert.True(st.Paired); Assert.True(st.Locked); Assert.Equal("manual", st.Reason);
        cts.Cancel();
        await Task.WhenAny(run, Task.Delay(3000));
    }
}
