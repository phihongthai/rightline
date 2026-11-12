namespace KidSafe.Agent.Core;

/// <summary>Servers empty = automatic (DHCP-provided).</summary>
public sealed record DnsAdapter(string Id, string Name, string[] Servers);

public interface IDnsConfigurator
{
    IReadOnlyList<DnsAdapter> GetAdapters();
    /// <summary>null/empty = back to automatic.</summary>
    void SetDns(string adapterId, string[]? servers);
    void FlushCache();
}

public interface IBrowserPolicyWriter
{
    void Set(string subKey, string name, string value);
    void Delete(string subKey, string name);
}

/// <summary>Platform hooks the Windows service plugs in. All optional so Core is testable anywhere.</summary>
public sealed class AgentPlatform
{
    public IDnsConfigurator? Dns { get; init; }
    public IBrowserPolicyWriter? BrowserPolicy { get; init; }
}
