using System.IO.Pipes;
using System.Windows.Threading;
using KidSafe.Agent.Core;

namespace KidSafe.Agent.Helper;

/// <summary>Talks to Agent.Service over the named pipe and drives the UI.</summary>
public sealed class HelperHost(Dispatcher ui)
{
    private readonly CancellationTokenSource _cts = new();
    private volatile IpcConnection? _conn;
    private LockWindow? _lock;
    private PairWindow? _pair;
    private DateTime _pairClosedAt = DateTime.MinValue;

    public void Start()
    {
        _ = Task.Run(() => ConnectLoopAsync(_cts.Token));
        _ = Task.Run(() => IdleLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts.Cancel();
        NativeMethods.RemoveKeyboardBlock();
    }

    public Task SendAsync(string type, object? payload) =>
        _conn is { } c ? c.SendAsync(type, payload) : Task.CompletedTask;

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", IpcNames.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(3000, ct);
                var conn = new IpcConnection(pipe);
                _conn = conn;
                await conn.SendAsync(IpcNames.Hello, null, ct);
                while (await conn.ReadAsync(ct) is { } msg) await ui.InvokeAsync(() => Handle(msg));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* service not up yet / restarting */ }
            finally { _conn = null; }
            // Service gone: fail safe. Keep the lock screen as it is; just retry.
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task IdleLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do { await SendAsync(IpcNames.Idle, new IdlePayload(NativeMethods.IdleSeconds(), NativeMethods.ForegroundApp())); }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* pipe hiccup; next tick retries */ }
    }

    // ---- UI thread ----
    private void Handle(IpcMessage m)
    {
        switch (m.Type)
        {
            case IpcNames.State when m.Payload.As<HelperState>() is { } s:
                ApplyState(s);
                break;
            case IpcNames.PairResult when m.Payload.As<ResultPayload>() is { } r:
                _pair?.ShowResult(r);
                break;
            case IpcNames.RequestResult when m.Payload.As<ResultPayload>() is { } r:
                _lock?.ShowRequestResult(r);
                break;
        }
    }

    private void ApplyState(HelperState s)
    {
        if (!s.Paired)
        {
            if (_pair is null && DateTime.UtcNow - _pairClosedAt > TimeSpan.FromMinutes(1))
            {
                _pair = new PairWindow(this);
                _pair.Closed += (_, _) => { _pair = null; _pairClosedAt = DateTime.UtcNow; };
                _pair.Show();
            }
            return;
        }
        if (_pair is not null) { _pair.Close(); _pair = null; }

        if (s.Locked)
        {
            if (_lock is null)
            {
                _lock = new LockWindow(this);
                NativeMethods.InstallKeyboardBlock();
                _lock.Show();
            }
            _lock.Update(s);
        }
        else if (_lock is not null)
        {
            NativeMethods.RemoveKeyboardBlock();
            _lock.Dismiss();
            _lock = null;
        }
    }
}
