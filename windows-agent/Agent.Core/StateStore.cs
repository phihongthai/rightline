using System.Text;

namespace KidSafe.Agent.Core;

public interface IProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

/// <summary>Pass-through; for tests only. Production uses DPAPI LocalMachine.</summary>
public sealed class NullProtector : IProtector
{
    public byte[] Protect(byte[] data) => data;
    public byte[] Unprotect(byte[] data) => data;
}

/// <summary>Everything the service must keep to enforce rules with no network.</summary>
public sealed class AgentState
{
    public string? DeviceId { get; set; }
    public string? DeviceToken { get; set; }
    public Policy? Policy { get; set; }
    public bool ManualLocked { get; set; }
    public DateTimeOffset? ExtraTimeUntil { get; set; }
    public double ActiveSeconds { get; set; }
    /// <summary>Local date (yyyy-MM-dd) that ActiveSeconds belongs to.</summary>
    public string? ActiveDate { get; set; }
    public bool Paired => DeviceToken is not null;
}

public sealed class StateStore(string path, IProtector protector)
{
    public AgentState Load()
    {
        try
        {
            if (!File.Exists(path)) return new();
            var plain = protector.Unprotect(File.ReadAllBytes(path));
            return Json.Deserialize<AgentState>(Encoding.UTF8.GetString(plain)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {
            // A corrupt/foreign file must not brick the agent; fall back to the .bak copy.
            try
            {
                var bak = path + ".bak";
                if (File.Exists(bak))
                    return Json.Deserialize<AgentState>(Encoding.UTF8.GetString(protector.Unprotect(File.ReadAllBytes(bak)))) ?? new();
            }
            catch { /* ignore */ }
            return new();
        }
    }

    public void Save(AgentState s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bytes = protector.Protect(Encoding.UTF8.GetBytes(Json.Serialize(s)));
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        File.Move(tmp, path, overwrite: true);
    }
}
