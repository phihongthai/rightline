using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace KidSafe.Agent.Service;

/// <summary>Anti-tamper hardening. Idempotent; runs on every service start and from the installer ("harden").</summary>
public static class TamperProtection
{
    public const string ServiceName = "KidSafeAgent";

    // SYSTEM: full; Administrators: full; Interactive/Service users: query only (no start/stop/pause, no config change).
    private const string ServiceSddl =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    public static void Apply(ILogger? log = null)
    {
        Try(log, "service recovery", () =>
        {
            Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/1000/restart/1000/restart/1000");
            Sc("failureflag", ServiceName, "1");          // also restart when the process exits with an error code
        });
        Try(log, "service ACL (deny stop for non-admins)", () => Sc("sdset", ServiceName, ServiceSddl));
        Try(log, "install dir ACL", LockInstallDirectory);
    }

    /// <summary>Install dir: SYSTEM + Administrators full control, Users read/execute only. Skipped outside Program Files (dev runs).</summary>
    private static void LockInstallDirectory()
    {
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!dir.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) return;

        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(sec);
    }

    private static void Sc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"sc {args[0]} exited with {p.ExitCode}");
    }

    private static void Try(ILogger? log, string what, Action a)
    {
        try { a(); }
        catch (Exception ex) { log?.LogWarning(ex, "tamper protection step failed: {What}", what); }
    }
}
