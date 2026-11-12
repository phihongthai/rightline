namespace KidSafe.Agent.Core;

public static class AppBlockMatcher
{
    /// <summary>Never killed, even if a policy lists them (would brick the session or the agent itself).</summary>
    public static readonly IReadOnlySet<string> Protected = new HashSet<string>
    {
        "system", "idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer",
        "fontdrvhost", "sihost", "taskhostw", "ctfmon", "agent.service", "agent.helper",
    };

    /// <param name="processName">"RobloxPlayerBeta.exe" or "RobloxPlayerBeta" (case-insensitive).</param>
    public static bool ShouldBlock(Policy? policy, string processName, DateTimeOffset localNow)
    {
        if (policy is null || !policy.ProtectionFlags.AppBlock || policy.BlockedApps.Count == 0) return false;
        var n = Norm(processName);
        if (n.Length == 0 || Protected.Contains(n)) return false;
        foreach (var a in policy.BlockedApps)
        {
            if (Norm(a.ProcessName) != n) continue;
            if (a.Mode == "outside_schedule")
            {
                var g = policy.ScheduleGrid;
                int d = (int)localNow.DayOfWeek, h = localNow.Hour;
                if (g.Length == 7 && g[d].Length == 24 && !g[d][h]) return true;
                continue;
            }
            return true;
        }
        return false;
    }

    private static string Norm(string s)
    {
        s = s.Trim();
        s = s[(s.LastIndexOfAny(['\\', '/']) + 1)..].ToLowerInvariant();   // path separators of either OS
        return s.EndsWith(".exe", StringComparison.Ordinal) ? s[..^4] : s;
    }
}
