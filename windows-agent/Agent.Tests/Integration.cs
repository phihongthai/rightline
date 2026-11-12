using System.Net.Http.Json;
using System.Text.Json;
using KidSafe.Agent.Core;

namespace Agent.Tests;

/// <summary>Runs the real AgentController against a live backend (KIDSAFE_BACKEND_URL).</summary>
public class BackendIntegrationTests
{
    static readonly string Base = Environment.GetEnvironmentVariable("KIDSAFE_BACKEND_URL") ?? "";

    sealed class Parent
    {
        readonly HttpClient _h = new();
        public string DeviceId = "", Code = "";
        public async Task InitAsync()
        {
            var r = await _h.PostAsJsonAsync($"{Base}/api/auth/register", new { email = $"cs{Guid.NewGuid():N}@example.com", password = "password123", name = "P" });
            var token = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
            _h.DefaultRequestHeaders.Authorization = new("Bearer", token);
            var d = await (await _h.PostAsJsonAsync($"{Base}/api/devices", new { name = "Test PC" })).Content.ReadFromJsonAsync<JsonElement>();
            DeviceId = d.GetProperty("id").GetString()!;
            Code = d.GetProperty("pairing_code").GetString()!;
        }
        public Task<HttpResponseMessage> Post(string path, object? body = null) => _h.PostAsJsonAsync($"{Base}/api/devices/{DeviceId}/{path}", body ?? new { });
        public Task<HttpResponseMessage> Patch(object body) => _h.PatchAsJsonAsync($"{Base}/api/devices/{DeviceId}/policy", body);
        public async Task<JsonElement> Get(string path = "") => await _h.GetFromJsonAsync<JsonElement>($"{Base}/api/devices/{DeviceId}{path}");
    }

    static async Task<HelperState> NextState(IpcConnection helper, Func<HelperState, bool> pred, int ms = 8000)
    {
        using var cts = new CancellationTokenSource(ms);
        while (true)
        {
            var m = await helper.ReadAsync(cts.Token) ?? throw new Exception("pipe closed");
            if (m.Type == IpcNames.State && Json.Deserialize<HelperState>(m.Payload!.Value.GetRawText()) is { } s && pred(s)) return s;
        }
    }

    static async Task<ResultPayload> NextResult(IpcConnection helper, string type)
    {
        using var cts = new CancellationTokenSource(8000);
        while (true)
        {
            var m = await helper.ReadAsync(cts.Token) ?? throw new Exception("pipe closed");
            if (m.Type == type) return m.Payload.As<ResultPayload>()!;
        }
    }

    [IntegrationFact]
    public async Task Pair_sync_lock_grant_request_and_offline_enforcement()
    {
        var parent = new Parent();
        await parent.InitAsync();

        var time = TimeProvider.System;
        var statePath = T.TempFile();
        var pipe = T.PipeName();
        AgentController Make(string url, string p) => new(
            new AgentOptions { BackendUrl = url, StatePath = statePath, TickInterval = TimeSpan.FromMilliseconds(100), HeartbeatInterval = TimeSpan.FromSeconds(1), SaveInterval = TimeSpan.Zero },
            new XorProtector(), time, T.PipeFactory(p));

        // --- first boot: unpaired
        var agent = Make(Base, pipe);
        using var cts = new CancellationTokenSource();
        var run = agent.RunAsync(cts.Token);
        await using var helper = await T.ConnectHelper(pipe);
        var s0 = await NextState(helper, _ => true);
        Assert.False(s0.Paired);

        // --- wrong code, then right code
        await helper.SendAsync(IpcNames.PairSubmit, new PairSubmitPayload("ZZZZZZ", null));
        var bad = await NextResult(helper, IpcNames.PairResult);
        Assert.False(bad.Ok); Assert.Equal("invalid_or_expired_code", bad.Error);

        await helper.SendAsync(IpcNames.PairSubmit, new PairSubmitPayload(parent.Code.ToLowerInvariant(), "BO-PC"));
        Assert.True((await NextResult(helper, IpcNames.PairResult)).Ok);
        var paired = await NextState(helper, s => s.Paired && s.Online);
        Assert.False(paired.Locked);
        var dev = await parent.Get();
        Assert.True(dev.GetProperty("paired").GetBoolean());
        var saved = new StateStore(statePath, new XorProtector()).Load();
        Assert.True(saved.Paired);
        Assert.Equal(parent.DeviceId, saved.DeviceId);

        // --- heartbeat reaches parent (idle report -> foreground app)
        await helper.SendAsync(IpcNames.Idle, new IdlePayload(1, "roblox.exe"));
        await Eventually(async () => (await parent.Get()).GetProperty("current_app").GetString() == "roblox.exe");

        // --- policy push: whole week blocked => schedule lock
        var grid = Enumerable.Range(0, 7).Select(_ => Enumerable.Repeat(false, 24)).ToArray();
        await parent.Patch(new { protection_flags = new { schedule_lock = true }, schedule_grid = grid });
        var locked = await NextState(helper, s => s.Locked);
        Assert.Equal("schedule", locked.Reason);
        Assert.Equal("Hiện không nằm trong giờ được phép sử dụng máy.", locked.Message);

        // --- time request from the lock screen, parent approves => unlocked via grant_time
        await helper.SendAsync(IpcNames.TimeRequest, new TimeRequestPayload(15, "bài tập"));
        Assert.True((await NextResult(helper, IpcNames.RequestResult)).Ok);
        var reqs = (await parent.Get("/time-requests?status=pending")).GetProperty("requests");
        Assert.Equal(1, reqs.GetArrayLength());
        Assert.Equal(15, reqs[0].GetProperty("requested_minutes").GetInt32());
        await parent.Post($"time-requests/{reqs[0].GetProperty("id").GetString()}/approve");
        var unlocked = await NextState(helper, s => !s.Locked);
        Assert.NotNull(unlocked.ExtraTimeUntil);

        // --- manual lock overrides extra time; unlock reverts
        await parent.Post("lock");
        Assert.Equal("manual", (await NextState(helper, s => s.Locked)).Reason);
        await parent.Post("unlock");
        await NextState(helper, s => !s.Locked);

        // --- agent stops, backend unreachable: cached policy + lock must still be enforced
        await parent.Post("lock");
        await NextState(helper, s => s.Locked);
        cts.Cancel();
        await Task.WhenAny(run, Task.Delay(5000));

        var offlinePipe = T.PipeName();
        var offline = Make("http://127.0.0.1:1", offlinePipe);
        Assert.Equal(LockReason.Manual, offline.CurrentDecision().Reason);
        using var cts2 = new CancellationTokenSource();
        var run2 = offline.RunAsync(cts2.Token);
        await using var helper2 = await T.ConnectHelper(offlinePipe);
        var off = await NextState(helper2, _ => true);
        Assert.True(off.Locked); Assert.False(off.Online);
        // time request cannot go out while offline
        await helper2.SendAsync(IpcNames.TimeRequest, new TimeRequestPayload(10, "x"));
        var r = await NextResult(helper2, IpcNames.RequestResult);
        Assert.False(r.Ok); Assert.Equal("offline", r.Error);
        cts2.Cancel();
        await Task.WhenAny(run2, Task.Delay(5000));

        // --- parent unlocks while agent is offline; on reconnect the agent resyncs
        await parent.Post("unlock");
        var back = Make(Base, T.PipeName());
        using var cts3 = new CancellationTokenSource();
        var run3 = back.RunAsync(cts3.Token);
        await Eventually(() => Task.FromResult(!back.CurrentDecision().Locked && !back.Snapshot().ManualLocked));
        cts3.Cancel();
        await Task.WhenAny(run3, Task.Delay(5000));
    }

    static async Task Eventually(Func<Task<bool>> cond, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!await cond()) { if (DateTime.UtcNow > end) throw new TimeoutException("condition not met"); await Task.Delay(100); }
    }
}
