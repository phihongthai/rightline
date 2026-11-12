using System.Net;
using System.Net.Sockets;

namespace KidSafe.Agent.Core;

/// <summary>
/// Local filtering DNS proxy (UDP + TCP). Blocked names get a sinkhole answer, everything else is
/// forwarded unchanged to an upstream resolver chosen per query (family-safe upstream when needed).
/// </summary>
public sealed class DnsProxy : IAsyncDisposable
{
    private readonly IPEndPoint _listen;
    private readonly Func<string, FilterDecision> _decide;
    private readonly Func<IPEndPoint> _upstream;
    private readonly Action<string, bool>? _onQuery;
    private readonly CancellationTokenSource _cts = new();
    private Socket? _udp;
    private TcpListener? _tcp;
    private readonly List<Task> _loops = [];

    public TimeSpan UpstreamTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public IPEndPoint? UdpEndPoint => _udp?.LocalEndPoint as IPEndPoint;
    public IPEndPoint? TcpEndPoint => _tcp?.LocalEndpoint as IPEndPoint;

    public DnsProxy(IPEndPoint listen, Func<string, FilterDecision> decide, Func<IPEndPoint> upstream, Action<string, bool>? onQuery = null)
    {
        _listen = listen; _decide = decide; _upstream = upstream; _onQuery = onQuery;
    }

    /// <summary>Binds both sockets; throws if either cannot bind (so the caller can avoid redirecting system DNS).</summary>
    public void Start()
    {
        _udp = new Socket(_listen.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _udp.Bind(_listen);
        try
        {
            _tcp = new TcpListener(new IPEndPoint(_listen.Address, UdpEndPoint!.Port));
            _tcp.Start();
        }
        catch { _udp.Dispose(); _udp = null; throw; }
        _loops.Add(Task.Run(UdpLoop));
        _loops.Add(Task.Run(TcpLoop));
    }

    private async Task UdpLoop()
    {
        var buf = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _udp!.ReceiveFromAsync(buf, SocketFlags.None, any, _cts.Token);
                var pkt = buf.AsSpan(0, r.ReceivedBytes).ToArray();
                var from = r.RemoteEndPoint;
                _ = Task.Run(async () =>
                {
                    var resp = await HandleAsync(pkt, tcp: false);
                    if (resp is not null) try { await _udp.SendToAsync(resp, SocketFlags.None, from, _cts.Token); } catch { /* client gone */ }
                });
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { /* ICMP unreachable on a previous reply etc. */ }
        }
    }

    private async Task TcpLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var c = await _tcp!.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => ServeTcp(c));
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { }
        }
    }

    private async Task ServeTcp(TcpClient c)
    {
        using (c)
        {
            try
            {
                var s = c.GetStream();
                var lenBuf = new byte[2];
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(10));
                while (true)
                {
                    await s.ReadExactlyAsync(lenBuf, idle.Token);
                    var msg = new byte[lenBuf[0] << 8 | lenBuf[1]];
                    await s.ReadExactlyAsync(msg, idle.Token);
                    var resp = await HandleAsync(msg, tcp: true);
                    if (resp is null) return;
                    await s.WriteAsync(new[] { (byte)(resp.Length >> 8), (byte)resp.Length }, idle.Token);
                    await s.WriteAsync(resp, idle.Token);
                }
            }
            catch { /* closed / timed out / malformed */ }
        }
    }

    internal async Task<byte[]?> HandleAsync(byte[] query, bool tcp)
    {
        if (DnsPacket.TryParseQuery(query, out var name, out var qtype, out var qend))
        {
            var d = _decide(name);
            try { _onQuery?.Invoke(name, d.Blocked); } catch { /* observers must not break DNS */ }
            if (d.Blocked) return DnsPacket.BuildBlocked(query, qend, qtype);
            return await ForwardAsync(query, tcp) ?? DnsPacket.BuildServFail(query, qend);
        }
        // Not something we understand (EDNS-only probes, multi-question...): pass through.
        return await ForwardAsync(query, tcp);
    }

    private async Task<byte[]?> ForwardAsync(byte[] query, bool tcp)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(UpstreamTimeout);
        var up = _upstream();
        try
        {
            return tcp ? await ForwardTcp(up, query, cts.Token) : await ForwardUdp(up, query, cts.Token);
        }
        catch { return null; }
    }

    private static async Task<byte[]> ForwardUdp(IPEndPoint up, byte[] q, CancellationToken ct)
    {
        using var s = new Socket(up.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await s.ConnectAsync(up, ct);
        await s.SendAsync(q, SocketFlags.None, ct);
        var buf = new byte[4096];
        while (true)
        {
            var n = await s.ReceiveAsync(buf, SocketFlags.None, ct);
            if (n >= 2 && buf[0] == q[0] && buf[1] == q[1]) return buf.AsSpan(0, n).ToArray(); // match transaction id
        }
    }

    private static async Task<byte[]> ForwardTcp(IPEndPoint up, byte[] q, CancellationToken ct)
    {
        using var c = new TcpClient();
        await c.ConnectAsync(up, ct);
        var s = c.GetStream();
        await s.WriteAsync(new[] { (byte)(q.Length >> 8), (byte)q.Length }, ct);
        await s.WriteAsync(q, ct);
        var lenBuf = new byte[2];
        await s.ReadExactlyAsync(lenBuf, ct);
        var resp = new byte[lenBuf[0] << 8 | lenBuf[1]];
        await s.ReadExactlyAsync(resp, ct);
        return resp;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _udp?.Dispose();
        _tcp?.Stop();
        try { await Task.WhenAll(_loops); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
