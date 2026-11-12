using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace KidSafe.Agent.Service;

/// <summary>
/// Session 0 isolation: the service can't show UI, so it starts Agent.Helper inside the
/// active console session using that user's token (WTSQueryUserToken + CreateProcessAsUser)
/// and keeps it alive / follows session switches.
/// </summary>
public sealed class HelperLauncher(string helperPath, ILogger log)
{
    private readonly string _helperPath = Path.GetFullPath(helperPath);
    private readonly string _processName = Path.GetFileNameWithoutExtension(helperPath);

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            try { EnsureRunning(); }
            catch (Exception ex) { log.LogError(ex, "helper launch check failed"); }
        } while (await WaitAsync(timer, ct));
        KillAll(except: Native.NoSession);
    }

    private static async Task<bool> WaitAsync(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); } catch (OperationCanceledException) { return false; }
    }

    private void EnsureRunning()
    {
        var session = Native.WTSGetActiveConsoleSessionId();
        if (session == Native.NoSession) { KillAll(except: Native.NoSession); return; }

        KillAll(except: session);                       // follow fast-user-switching
        var running = Process.GetProcessesByName(_processName).Any(p => SafeSession(p) == session && PathMatches(p));
        if (running) return;

        if (!Native.WTSQueryUserToken(session, out var userToken))
        {
            log.LogDebug("no user token for session {Session} (nobody logged on yet): {Err}", session, Marshal.GetLastWin32Error());
            return;
        }
        try { Launch(userToken, session); }
        finally { Native.CloseHandle(userToken); }
    }

    private void Launch(IntPtr userToken, uint session)
    {
        IntPtr primary = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!Native.DuplicateTokenEx(userToken, Native.TokenAllAccess, IntPtr.Zero, 2 /*SecurityImpersonation*/, 1 /*TokenPrimary*/, out primary))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx");
            if (!Native.CreateEnvironmentBlock(out env, primary, false))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock");

            var si = new Native.STARTUPINFO { cb = Marshal.SizeOf<Native.STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var cmd = new StringBuilder($"\"{_helperPath}\"");
            if (!Native.CreateProcessAsUser(primary, null, cmd, IntPtr.Zero, IntPtr.Zero, false, Native.CreateUnicodeEnvironment,
                    env, Path.GetDirectoryName(_helperPath), ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser");

            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess);
            log.LogInformation("Started helper pid {Pid} in session {Session}", pi.dwProcessId, session);
        }
        finally
        {
            if (env != IntPtr.Zero) Native.DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) Native.CloseHandle(primary);
        }
    }

    private void KillAll(uint except)
    {
        foreach (var p in Process.GetProcessesByName(_processName))
        {
            try { if (SafeSession(p) != except && PathMatches(p)) p.Kill(); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { /* already gone */ }
            finally { p.Dispose(); }
        }
    }

    private static uint SafeSession(Process p)
    {
        try { return Native.ProcessIdToSessionId((uint)p.Id, out var s) ? s : uint.MaxValue - 1; } catch { return uint.MaxValue - 1; }
    }

    private bool PathMatches(Process p) =>
        string.Equals(ImagePath((uint)p.Id), _helperPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Full image path of a process, or null.</summary>
    public static string? ImagePath(uint pid)
    {
        var h = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>
    /// Only our own Helper binary (in the ACL-protected install dir) may talk on the pipe;
    /// otherwise a child could connect a fake helper reporting "idle" forever to dodge the daily limit.
    /// </summary>
    public bool AuthorizePipeClient(NamedPipeServerStream pipe)
    {
        if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid)) return false;
        return string.Equals(ImagePath(pid), _helperPath, StringComparison.OrdinalIgnoreCase);
    }
}
