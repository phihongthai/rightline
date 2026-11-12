using System.IO.Pipes;
using KidSafe.Agent.Core;
using Microsoft.Extensions.Time.Testing;

namespace Agent.Tests;

public sealed class XorProtector : IProtector
{
    public byte[] Protect(byte[] d) => d.Select(b => (byte)(b ^ 0x5A)).ToArray();
    public byte[] Unprotect(byte[] d) => Protect(d);
}

public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KIDSAFE_BACKEND_URL")))
            Skip = "set KIDSAFE_BACKEND_URL (running backend) to run";
    }
}

public static class T
{
    public static string TempFile(string name = "state.bin") =>
        Path.Combine(Directory.CreateTempSubdirectory("kidsafe-test").FullName, name);

    public static string PipeName() => "kidsafe-test-" + Guid.NewGuid().ToString("N");

    public static Func<NamedPipeServerStream> PipeFactory(string name) =>
        () => new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    public static async Task<IpcConnection> ConnectHelper(string pipe)
    {
        var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await c.ConnectAsync(5000);
        return new IpcConnection(c);
    }

    public static Policy Policy(bool scheduleLock = true, int? limit = null, bool allowed = true) => new()
    {
        Version = 1,
        ProtectionFlags = new() { ScheduleLock = scheduleLock },
        ScheduleGrid = Enumerable.Range(0, 7).Select(_ => Enumerable.Repeat(allowed, 24).ToArray()).ToArray(),
        DailyLimitMinutes = limit,
    };

    /// <summary>Paired agent with a cached policy, never started (no network).</summary>
    public static (AgentController c, FakeTimeProvider time, string pipe, string path) PairedController(
        Policy policy, bool manual = false, DateTimeOffset? start = null, Action<AgentState>? tweak = null)
    {
        var time = new FakeTimeProvider(start ?? new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero)); // Wednesday
        var path = TempFile();
        var st = new AgentState { DeviceId = "d", DeviceToken = "tok", Policy = policy, ManualLocked = manual, ActiveDate = time.GetLocalNow().ToString("yyyy-MM-dd") };
        tweak?.Invoke(st);
        new StateStore(path, new XorProtector()).Save(st);
        var pipe = PipeName();
        var c = new AgentController(new AgentOptions { StatePath = path, SaveInterval = TimeSpan.Zero }, new XorProtector(), time, PipeFactory(pipe));
        return (c, time, pipe, path);
    }
}
