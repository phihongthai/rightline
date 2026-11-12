namespace KidSafe.Agent.Core;

public enum LockReason { None, Manual, Schedule, DailyLimit }

public readonly record struct LockDecision(bool Locked, LockReason Reason)
{
    public string Code => Reason switch
    {
        LockReason.Manual => "manual",
        LockReason.Schedule => "schedule",
        LockReason.DailyLimit => "daily_limit",
        _ => "none",
    };

    public string Message => Reason switch
    {
        LockReason.Manual => "Phụ huynh đã khóa máy.",
        LockReason.Schedule => "Hiện không nằm trong giờ được phép sử dụng máy.",
        LockReason.DailyLimit => "Đã hết thời gian sử dụng hôm nay.",
        _ => "",
    };
}

/// <summary>Pure decision logic; runs the same online or offline.</summary>
public static class PolicyEngine
{
    /// <param name="now">Local wall-clock time (day/hour are taken from its own offset).</param>
    public static LockDecision Evaluate(Policy? policy, bool manualLocked, DateTimeOffset? extraTimeUntil, double activeSecondsToday, DateTimeOffset now)
    {
        if (manualLocked) return new(true, LockReason.Manual);
        if (policy is null) return new(false, LockReason.None);
        if (extraTimeUntil is { } extra && extra > now) return new(false, LockReason.None);

        // Schedule and daily limit are both governed by the "Lịch dùng máy" switch.
        if (!policy.ProtectionFlags.ScheduleLock) return new(false, LockReason.None);

        var grid = policy.ScheduleGrid;
        int d = (int)now.DayOfWeek, h = now.Hour;
        if (grid.Length == 7 && grid[d].Length == 24 && !grid[d][h]) return new(true, LockReason.Schedule);

        if (policy.DailyLimitMinutes is { } limit && activeSecondsToday >= limit * 60.0)
            return new(true, LockReason.DailyLimit);

        return new(false, LockReason.None);
    }
}
