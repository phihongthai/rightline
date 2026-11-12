using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidSafe.Agent.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
    public static string Serialize<T>(T v) => JsonSerializer.Serialize(v, Options);
    public static T? Deserialize<T>(string s) => JsonSerializer.Deserialize<T>(s, Options);
    public static JsonElement ToElement<T>(T v) => JsonSerializer.SerializeToElement(v, Options);
    public static T? As<T>(this JsonElement? e) => e is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } v ? v.Deserialize<T>(Options) : default;
}

public sealed class ProtectionFlags
{
    public bool WebFilter { get; set; } = true;
    public bool AppBlock { get; set; } = true;
    public bool ScheduleLock { get; set; }
    public bool Screenshot { get; set; } = true;
}

public sealed class BlockedApp
{
    public string Name { get; set; } = "";
    public string ProcessName { get; set; } = "";
    /// <summary>"always" | "outside_schedule"</summary>
    public string Mode { get; set; } = "always";
}

/// <summary>Policy as pushed by the backend (snake_case on the wire).</summary>
public sealed class Policy
{
    public string? DeviceId { get; set; }
    public int Version { get; set; }
    public ProtectionFlags ProtectionFlags { get; set; } = new();
    /// <summary>[day][hour], day 0 = Sunday (same as DayOfWeek). true = allowed.</summary>
    public bool[][] ScheduleGrid { get; set; } = [];
    public int? DailyLimitMinutes { get; set; }
    public List<string> BlockedCategories { get; set; } = [];
    public List<string> CustomBlockedDomains { get; set; } = [];
    public List<string> CustomAllowedDomains { get; set; } = [];
    public List<BlockedApp> BlockedApps { get; set; } = [];
    public int ScreenshotIntervalSec { get; set; } = 60;
}

public sealed record PairRequest(string PairingCode, string MachineName, string? OsVersion, string? AgentVersion);
public sealed record PairResponse(string DeviceId, string DeviceToken);
public sealed record HeartbeatPayload(int ActiveMinutesToday, string? CurrentApp, bool IsLocked);
public sealed record GrantTimePayload(DateTimeOffset ExtraTimeUntil);
