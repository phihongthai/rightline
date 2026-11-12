using System.Net;
using System.Net.Sockets;
using KidSafe.Agent.Core;
using Microsoft.Extensions.Time.Testing;

namespace Agent.Tests;

public class DomainFilterTests
{
    static Policy P(Action<Policy>? f = null)
    {
        var p = T.Policy(); p.ProtectionFlags.WebFilter = true; p.BlockedCategories = ["gambling", "game"]; f?.Invoke(p); return p;
    }
    readonly DomainFilter _f = new();

    [Theory]
    [InlineData("bet365.com", true)]
    [InlineData("www.bet365.com", true)]
    [InlineData("A.B.Roblox.com.", true)]       // case + trailing dot + deep subdomain
    [InlineData("notbet365.com", false)]        // suffix must be on a label boundary
    [InlineData("box.com", false)]
    [InlineData("wikipedia.org", false)]
    [InlineData("pornhub.com", false)]          // adult category not selected
    public void Categories_match_on_label_boundaries(string d, bool blocked) =>
        Assert.Equal(blocked, _f.Decide(d, P()).Blocked);

    [Fact]
    public void Allow_list_beats_block_list_and_categories()
    {
        var p = P(x => { x.CustomAllowedDomains = ["roblox.com"]; x.CustomBlockedDomains = ["roblox.com", "evil.org"]; });
        Assert.False(_f.Decide("www.roblox.com", p).Blocked);
        Assert.Equal("custom", _f.Decide("sub.evil.org", p).Reason);
    }

    [Fact]
    public void Switch_off_or_no_policy_allows_everything()
    {
        Assert.False(_f.Decide("bet365.com", P(x => x.ProtectionFlags.WebFilter = false)).Blocked);
        Assert.False(_f.Decide("bet365.com", null).Blocked);
    }

    [Fact]
    public void Adult_category_is_available() =>
        Assert.True(_f.Decide("pornhub.com", P(x => x.BlockedCategories = ["adult"])).Blocked);
}

public class DnsTests
{
    static byte[] Query(string name, ushort qtype = 1, ushort id = 0x1234, bool edns = true)
    {
        var m = new MemoryStream();
        m.Write([(byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, (byte)(edns ? 1 : 0)]);
        foreach (var l in name.Split('.')) { m.WriteByte((byte)l.Length); m.Write(System.Text.Encoding.ASCII.GetBytes(l)); }
        m.WriteByte(0); m.Write([(byte)(qtype >> 8), (byte)qtype, 0, 1]);
        if (edns) m.Write([0, 0, 41, 0x10, 0, 0, 0, 0, 0, 0, 0]);       // OPT record
        return m.ToArray();
    }

    /// <summary>Fake upstream: answers every A query with 9.9.9.9 and counts hits.</summary>
    internal sealed class Upstream : IDisposable
    {
        readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        readonly TcpListener _tcp;
        public int Hits;
        public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;
        public Upstream()
        {
            _tcp = new TcpListener(IPAddress.Loopback, EndPoint.Port); _tcp.Start();
            _ = Task.Run(async () => { try { while (true) { var r = await _udp.ReceiveAsync(); Interlocked.Increment(ref Hits); await _udp.SendAsync(Answer(r.Buffer), r.RemoteEndPoint); } } catch { } });
            _ = Task.Run(async () => { try { while (true) { var c = await _tcp.AcceptTcpClientAsync(); _ = Task.Run(async () => {
                var s = c.GetStream(); var l = new byte[2]; await s.ReadExactlyAsync(l); var q = new byte[l[0] << 8 | l[1]]; await s.ReadExactlyAsync(q);
                Interlocked.Increment(ref Hits); var a = Answer(q); await s.WriteAsync(new[] { (byte)(a.Length >> 8), (byte)a.Length }); await s.WriteAsync(a); c.Dispose(); }); } } catch { } });
        }
        static byte[] Answer(byte[] q)
        {
            DnsPacket.TryParseQuery(q, out _, out _, out var end);
            var r = new byte[end + 16];
            Array.Copy(q, r, end);
            r[2] = 0x81; r[3] = 0x80; r[6] = 0; r[7] = 1; r[10] = 0; r[11] = 0;
            int o = end; r[o++] = 0xC0; r[o++] = 0x0C; r[o++] = 0; r[o++] = 1; r[o++] = 0; r[o++] = 1; o += 3; r[o++] = 30; r[o++] = 0; r[o++] = 4;
            r[o++] = 9; r[o++] = 9; r[o++] = 9; r[o++] = 9;
            return r;
        }
        public void Dispose() { _udp.Dispose(); _tcp.Stop(); }
    }

    static async Task<byte[]> Ask(IPEndPoint proxy, byte[] q)
    {
        using var c = new UdpClient();
        await c.SendAsync(q, proxy);
        var t = c.ReceiveAsync();
        if (await Task.WhenAny(t, Task.Delay(4000)) != t) throw new TimeoutException();
        return t.Result.Buffer;
    }

    [Fact]
    public void Parses_queries_and_rejects_garbage()
    {
        Assert.True(DnsPacket.TryParseQuery(Query("WWW.Example.com", 28), out var n, out var t, out var end));
        Assert.Equal("www.example.com", n); Assert.Equal(28, t); Assert.True(end > 12);
        Assert.False(DnsPacket.TryParseQuery(new byte[5], out _, out _, out _));
        var resp = Query("a.com"); resp[2] |= 0x80;                     // QR=1 is not a query
        Assert.False(DnsPacket.TryParseQuery(resp, out _, out _, out _));
    }

    [Fact]
    public void Sinkhole_answers_are_wellformed()
    {
        var q = Query("bad.com", 1); DnsPacket.TryParseQuery(q, out _, out _, out var end);
        var a = DnsPacket.BuildBlocked(q, end, 1);
        Assert.Equal(0x12, a[0]); Assert.Equal(0x34, a[1]);
        Assert.Equal(0x80, a[2] & 0x80); Assert.Equal(0, a[3] & 0x0F); Assert.Equal(1, a[7]);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, a[^4..]);
        Assert.Equal(0, a[11]);                                         // EDNS OPT stripped
        var nx = DnsPacket.BuildBlocked(q, end, 15 /*MX*/);
        Assert.Equal(3, nx[3] & 0x0F); Assert.Equal(0, nx[7]);
        var aaaa = DnsPacket.BuildBlocked(q, end, 28);
        Assert.Equal(end + 12 + 16, aaaa.Length);
        Assert.All(aaaa[^16..], b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Proxy_blocks_filtered_names_forwards_others_over_udp_and_tcp()
    {
        using var up = new Upstream();
        var seen = new List<(string, bool)>();
        var policy = T.Policy(); policy.ProtectionFlags.WebFilter = true; policy.BlockedCategories = ["gambling"];
        var f = new DomainFilter();
        await using var proxy = new DnsProxy(new IPEndPoint(IPAddress.Loopback, 0), n => f.Decide(n, policy), () => up.EndPoint,
            (d, b) => { lock (seen) seen.Add((d, b)); });
        proxy.Start();

        var blocked = await Ask(proxy.UdpEndPoint!, Query("www.bet365.com"));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, blocked[^4..]);
        Assert.Equal(0, up.Hits);                                        // never reached upstream

        var ok = await Ask(proxy.UdpEndPoint!, Query("wikipedia.org", id: 0x7777));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, ok[^4..]);
        Assert.Equal(0x77, ok[0]);
        Assert.Equal(1, up.Hits);

        // TCP path
        using var tcp = new TcpClient(); await tcp.ConnectAsync(proxy.TcpEndPoint!);
        var s = tcp.GetStream(); var q = Query("example.org");
        await s.WriteAsync(new[] { (byte)(q.Length >> 8), (byte)q.Length }); await s.WriteAsync(q);
        var l = new byte[2]; await s.ReadExactlyAsync(l); var r = new byte[l[0] << 8 | l[1]]; await s.ReadExactlyAsync(r);
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, r[^4..]);

        lock (seen) { Assert.Contains(("www.bet365.com", true), seen); Assert.Contains(("wikipedia.org", false), seen); }
    }

    [Fact]
    public async Task Dead_upstream_gives_servfail_not_a_hang()
    {
        var dead = new IPEndPoint(IPAddress.Loopback, 1);
        await using var proxy = new DnsProxy(new IPEndPoint(IPAddress.Loopback, 0), _ => FilterDecision.Allow, () => dead) { UpstreamTimeout = TimeSpan.FromMilliseconds(300) };
        proxy.Start();
        var r = await Ask(proxy.UdpEndPoint!, Query("example.com"));
        Assert.Equal(2, r[3] & 0x0F);
    }

    [Fact]
    public async Task Start_fails_loudly_when_port_is_taken()
    {
        using var taken = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)taken.Client.LocalEndPoint!).Port;
        await using var proxy = new DnsProxy(new IPEndPoint(IPAddress.Loopback, port), _ => FilterDecision.Allow, () => new IPEndPoint(IPAddress.Loopback, 9));
        Assert.ThrowsAny<SocketException>(() => proxy.Start());
    }
}

public class NetworkGuardTests
{
    internal sealed class FakeDns : IDnsConfigurator
    {
        public Dictionary<string, DnsAdapter> A = new();
        public int Flushes;
        public IReadOnlyList<DnsAdapter> GetAdapters() => A.Values.ToList();
        public void SetDns(string id, string[]? s) => A[id] = A[id] with { Servers = s ?? [] };
        public void FlushCache() => Flushes++;
    }

    [Fact]
    public void Redirects_remembers_detects_revert_and_restores()
    {
        var dns = new FakeDns();
        dns.A["a"] = new("a", "Wi-Fi", ["192.168.1.1"]);
        dns.A["b"] = new("b", "Ethernet", []);                           // DHCP
        var orig = new Dictionary<string, string[]>();
        var g = new DnsGuard(dns, orig);

        Assert.Empty(g.Enforce());
        Assert.All(dns.A.Values, a => Assert.Equal(["127.0.0.1"], a.Servers));
        Assert.Equal(["192.168.1.1"], orig["a"]);

        Assert.Empty(g.Enforce());                                       // idempotent
        dns.A["a"] = dns.A["a"] with { Servers = ["8.8.8.8"] };          // kid points it elsewhere
        Assert.Equal(["Wi-Fi"], g.Enforce());
        Assert.Equal(["127.0.0.1"], dns.A["a"].Servers);
        Assert.Equal(["192.168.1.1"], orig["a"]);                        // original not overwritten by the kid's value

        dns.A["c"] = new("c", "VPN", ["10.0.0.1"]);                      // new adapter: captured, not an alert
        Assert.Empty(g.Enforce());

        g.Restore();
        Assert.Equal(["192.168.1.1"], dns.A["a"].Servers);
        Assert.Empty(dns.A["b"].Servers);                                // back to automatic
        Assert.Equal(["10.0.0.1"], dns.A["c"].Servers);
        Assert.Empty(orig);
    }

    sealed class FakeReg : IBrowserPolicyWriter
    {
        public Dictionary<(string, string), string> V = new();
        public void Set(string k, string n, string v) => V[(k, n)] = v;
        public void Delete(string k, string n) => V.Remove((k, n));
    }

    [Fact]
    public void Doh_policy_covers_chrome_and_edge_with_spec_values_and_is_reversible()
    {
        var r = new FakeReg();
        DohPolicy.Apply(r, true);
        Assert.Equal("off", r.V[(@"SOFTWARE\Policies\Google\Chrome", "DnsOverHttpsMode")]);
        Assert.Equal("off", r.V[(@"SOFTWARE\Policies\Microsoft\Edge", "DnsOverHttpsMode")]);
        DohPolicy.Apply(r, false);
        Assert.Empty(r.V);
    }

    [Fact]
    public async Task Controller_enforces_dns_flags_tamper_and_cleans_up()
    {
        var dns = new FakeDns(); dns.A["a"] = new("a", "Wi-Fi", ["192.168.1.1"]);
        var reg = new FakeReg();
        var pol = T.Policy(); pol.ProtectionFlags.WebFilter = true;
        var (_, time, _, path) = T.PairedController(pol);
        var pipe = T.PipeName();
        var c = new AgentController(new AgentOptions { StatePath = path, DnsListen = new IPEndPoint(IPAddress.Loopback, 0), TickInterval = TimeSpan.FromMilliseconds(50) },
            new XorProtector(), TimeProvider.System, T.PipeFactory(pipe), platform: new AgentPlatform { Dns = dns, BrowserPolicy = reg });
        using var cts = new CancellationTokenSource();
        var run = c.RunAsync(cts.Token);
        await IpcTests.WaitFor(() => c.DnsEndPoint is not null && dns.A["a"].Servers is ["127.0.0.1"]);
        Assert.Equal(DohPolicy.Entries.Count, reg.V.Count);

        dns.A["a"] = dns.A["a"] with { Servers = ["1.1.1.1"] };
        c.EnforceNetwork();
        Assert.Contains(c.Snapshot().PendingAlerts, a => a.StartsWith("dns_changed:Wi-Fi"));

        cts.Cancel(); await Task.WhenAny(run, Task.Delay(3000));
        Assert.Equal(["127.0.0.1"], dns.A["a"].Servers);                 // stop = fail-closed, DNS stays on loopback

        c.Cleanup();                                                      // uninstall
        Assert.Equal(["192.168.1.1"], dns.A["a"].Servers);
        Assert.Empty(reg.V);
    }

    [Fact]
    public async Task Unclean_shutdown_is_reported_on_next_start()
    {
        var pol = T.Policy();
        var (_, _, _, path) = T.PairedController(pol, tweak: s => s.RunningSince = DateTimeOffset.UtcNow.AddMinutes(-5));
        var c = new AgentController(new AgentOptions { StatePath = path, TickInterval = TimeSpan.FromMilliseconds(50) }, new XorProtector(), TimeProvider.System, T.PipeFactory(T.PipeName()));
        using var cts = new CancellationTokenSource();
        var run = c.RunAsync(cts.Token);
        await IpcTests.WaitFor(() => c.Snapshot().PendingAlerts.Contains("unclean_shutdown"));
        cts.Cancel(); await Task.WhenAny(run, Task.Delay(3000));
        Assert.Null(new StateStore(path, new XorProtector()).Load().RunningSince);   // clean stop clears the marker
    }
}

public class AppBlockTests
{
    static Policy P(string mode, bool appBlock = true)
    {
        var p = T.Policy(); p.ProtectionFlags.AppBlock = appBlock;
        p.BlockedApps = [new() { Name = "Roblox", ProcessName = "RobloxPlayerBeta.exe", Mode = mode }];
        return p;
    }
    static readonly DateTimeOffset Noon = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("robloxplayerbeta.exe")]
    [InlineData("RobloxPlayerBeta")]
    [InlineData(@"C:\Games\RobloxPlayerBeta.EXE")]
    public void Matches_name_variants(string name) => Assert.True(AppBlockMatcher.ShouldBlock(P("always"), name, Noon));

    [Fact]
    public void Respects_switch_and_unrelated_processes()
    {
        Assert.False(AppBlockMatcher.ShouldBlock(P("always", appBlock: false), "RobloxPlayerBeta.exe", Noon));
        Assert.False(AppBlockMatcher.ShouldBlock(P("always"), "notepad.exe", Noon));
        Assert.False(AppBlockMatcher.ShouldBlock(null, "RobloxPlayerBeta.exe", Noon));
    }

    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("Agent.Helper.exe")]
    [InlineData("svchost")]
    public void Critical_processes_are_never_blocked(string name)
    {
        var p = T.Policy();
        p.BlockedApps = [new() { Name = "x", ProcessName = name, Mode = "always" }];
        Assert.False(AppBlockMatcher.ShouldBlock(p, name, Noon));
    }

    [Fact]
    public void Outside_schedule_mode_only_blocks_in_blocked_hours()
    {
        var p = P("outside_schedule");
        Assert.False(AppBlockMatcher.ShouldBlock(p, "RobloxPlayerBeta.exe", Noon));
        p.ScheduleGrid[(int)Noon.DayOfWeek][12] = false;
        Assert.True(AppBlockMatcher.ShouldBlock(p, "RobloxPlayerBeta.exe", Noon));
    }
}

public class UninstallAuthTests
{
    [Fact]
    public void Pin_matches_backend_vector()
    {
        // produced by backend/src/lib/tokens.ts: uninstallPin(hashToken("device-token-1"), "2026-01-01")
        Assert.Equal("29436126", UninstallAuth.Pin("device-token-1", new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void Verify_accepts_today_and_yesterday_only()
    {
        var now = new DateTimeOffset(2026, 1, 2, 0, 5, 0, TimeSpan.Zero);
        Assert.True(UninstallAuth.VerifyPin("tok", UninstallAuth.Pin("tok", new DateOnly(2026, 1, 2)), now));
        Assert.True(UninstallAuth.VerifyPin("tok", UninstallAuth.Pin("tok", new DateOnly(2026, 1, 1)), now));
        Assert.False(UninstallAuth.VerifyPin("tok", UninstallAuth.Pin("tok", new DateOnly(2025, 12, 30)), now));
        Assert.False(UninstallAuth.VerifyPin("tok", "00000000", now));
        Assert.False(UninstallAuth.VerifyPin("other", UninstallAuth.Pin("tok", new DateOnly(2026, 1, 2)), now));
    }

    [Fact]
    public async Task Unpaired_machine_may_uninstall_and_offline_wrong_secret_is_inconclusive()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var http = new HttpClient();
        Assert.Equal(0, await UninstallAuth.VerifyAsync(new AgentState(), "x", http, "http://127.0.0.1:1", time));
        Assert.Equal(2, await UninstallAuth.VerifyAsync(new AgentState { DeviceToken = "t" }, "wrong", http, "http://127.0.0.1:1", time));
        Assert.Equal(0, await UninstallAuth.VerifyAsync(new AgentState { DeviceToken = "t" }, UninstallAuth.Pin("t", DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)), http, "http://127.0.0.1:1", time));
    }
}

public class TelemetryTests
{
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public List<string> Calls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        { Calls.Add($"{r.Method} {r.RequestUri!.AbsolutePath}"); return Task.FromResult(f(r)); }
    }
    static HttpResponseMessage Json(string s, int code = 200) =>
        new((System.Net.HttpStatusCode)code) { Content = new StringContent(s, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public void ActivityLog_aggregates_apps_dedupes_domains_and_caps()
    {
        var t = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var log = new ActivityLog(t);
        for (int i = 0; i < 10; i++) log.RecordApp("chrome.exe", "YouTube", 1);
        log.RecordApp("chrome.exe", "Docs", 2);
        log.RecordApp(null, "x", 5);
        log.RecordDomain("a.com", false); log.RecordDomain("a.com", false); log.RecordDomain("a.com", true);
        var d = log.Drain();
        Assert.Equal(10, d.Single(e => e.Title == "YouTube").DurationSec);
        Assert.Equal(2, d.Count(e => e.Target == "a.com"));              // allowed + blocked kept apart
        Assert.Equal(4, d.Count);
        Assert.Empty(log.Drain());
        for (int i = 0; i < 500; i++) log.RecordDomain($"d{i}.com", false);
        Assert.Equal(300, log.Drain(1000).Count);
    }

    [Fact]
    public async Task ActivityLog_requeues_on_failure_and_drops_poison_batches()
    {
        var log = new ActivityLog(TimeProvider.System);
        log.RecordSystem("x", "y");
        using var down = new HttpClient(new Handler(_ => Json("{}", 503)));
        Assert.False(await log.FlushAsync(down, "http://x", "t"));
        Assert.Equal(1, log.Pending);
        using var bad = new HttpClient(new Handler(_ => Json("{}", 400)));
        Assert.True(await log.FlushAsync(bad, "http://x", "t"));
        Assert.Equal(0, log.Pending);
    }

    [Fact]
    public void Queue_keeps_at_most_50_newest()
    {
        var dir = Path.GetDirectoryName(T.TempFile())!;
        var up = new ScreenshotUploader(new HttpClient(), "http://x", dir);
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);
        for (int i = 0; i < 60; i++) Assert.True(up.Enqueue([0xFF, 0xD8, 1], t0.AddSeconds(i)));
        Assert.Equal(50, up.QueuedCount);
        Assert.False(up.Enqueue([], t0));
        Assert.False(up.Enqueue(new byte[ScreenshotUploader.MaxBytes + 1], t0));
        // oldest 10 were dropped
        Assert.DoesNotContain(Directory.GetFiles(dir), f => string.CompareOrdinal(Path.GetFileName(f)[..17], t0.AddSeconds(9).UtcDateTime.ToString("yyyyMMddHHmmssfff")) <= 0);
    }

    [Fact]
    public async Task Upload_presign_put_confirm_deletes_file_and_retries_when_offline()
    {
        var dir = Path.GetDirectoryName(T.TempFile())!;
        bool fail = true;
        var h = new Handler(r => r.RequestUri!.AbsolutePath switch
        {
            "/api/agent/screenshots/presign" => fail ? Json("{}", 503) : Json("""{"storage_key":"d/2026/x.jpg","upload_url":"http://storage.test/put/x"}"""),
            "/put/x" => new HttpResponseMessage(System.Net.HttpStatusCode.OK),
            "/api/agent/screenshots/confirm" => Json("""{"id":"1"}""", 201),
            _ => Json("{}", 404),
        });
        var up = new ScreenshotUploader(new HttpClient(h), "http://api.test", dir);
        up.Enqueue([0xFF, 0xD8, 9, 9], DateTimeOffset.UtcNow);
        Assert.Equal(0, await up.FlushAsync("tok"));
        Assert.Equal(1, up.QueuedCount);                                  // kept for retry
        fail = false;
        Assert.Equal(1, await up.FlushAsync("tok"));
        Assert.Equal(0, up.QueuedCount);                                  // deleted after success
        Assert.Equal(["POST /api/agent/screenshots/presign", "POST /api/agent/screenshots/presign", "PUT /put/x", "POST /api/agent/screenshots/confirm"], h.Calls);
    }
}

public class ControllerScreenshotTests
{
    static byte[] Jpeg => [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
    static IpcMessage Shot(byte[] data, DateTimeOffset? at = null) =>
        new(IpcNames.Screenshot, Json.ToElement(new ScreenshotPayload(at ?? DateTimeOffset.UtcNow, Convert.ToBase64String(data))));

    [Fact]
    public async Task Accepts_jpeg_only_when_policy_enables_screenshots()
    {
        var q = Path.GetDirectoryName(T.TempFile())!;
        Controller Make(bool flag)
        {
            var p = T.Policy(); p.ProtectionFlags.Screenshot = flag;
            var (_, _, _, path) = T.PairedController(p);
            return new(new AgentController(new AgentOptions { StatePath = path, QueueDir = q, BackendUrl = "http://127.0.0.1:1" }, new XorProtector(), TimeProvider.System, T.PipeFactory(T.PipeName())), q);
        }
        var off = Make(false);
        await off.C.OnIpcAsync(Shot(Jpeg));
        Assert.Empty(Directory.GetFiles(q, "*.jpg"));

        var on = Make(true);
        await on.C.OnIpcAsync(Shot([1, 2, 3, 4, 5]));                     // not a JPEG
        await on.C.OnIpcAsync(new IpcMessage(IpcNames.Screenshot, Json.ToElement(new ScreenshotPayload(DateTimeOffset.UtcNow, "%%%"))));
        Assert.Empty(Directory.GetFiles(q, "*.jpg"));
        await on.C.OnIpcAsync(Shot(Jpeg, DateTimeOffset.UtcNow.AddYears(5)));   // future timestamp is clamped
        var f = Directory.GetFiles(q, "*.jpg").Single();
        Assert.True(string.CompareOrdinal(Path.GetFileName(f)[..17], DateTime.UtcNow.AddMinutes(1).ToString("yyyyMMddHHmmssfff")) < 0);
    }

    record Controller(AgentController C, string Q);
}
