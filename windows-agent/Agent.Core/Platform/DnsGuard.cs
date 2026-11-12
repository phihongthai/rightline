namespace KidSafe.Agent.Core;

/// <summary>Points active adapters at the local proxy, remembers what they had, restores it later, and notices tampering.</summary>
public sealed class DnsGuard(IDnsConfigurator dns, Dictionary<string, string[]> originals)
{
    public const string Loopback = "127.0.0.1";
    private static bool IsLoopback(string[] s) => s.Length == 1 && s[0] == Loopback;

    /// <summary>Returns the adapters we had already redirected that somebody switched back (tamper signal).</summary>
    public List<string> Enforce()
    {
        var reverted = new List<string>();
        var changed = false;
        foreach (var a in dns.GetAdapters())
        {
            if (IsLoopback(a.Servers)) continue;
            if (originals.ContainsKey(a.Id)) reverted.Add(a.Name);   // we managed it before; it was changed back
            else originals[a.Id] = a.Servers;                          // first time we see it: remember
            dns.SetDns(a.Id, [Loopback]);
            changed = true;
        }
        if (changed) dns.FlushCache();
        return reverted;
    }

    public void Restore()
    {
        if (originals.Count == 0) return;
        var present = dns.GetAdapters().ToDictionary(a => a.Id);
        foreach (var (id, servers) in originals.ToList())
        {
            if (present.ContainsKey(id)) dns.SetDns(id, servers.Length == 0 ? null : servers);
            originals.Remove(id);
        }
        dns.FlushCache();
    }
}

/// <summary>Registry policies that stop browsers from bypassing local DNS via DNS-over-HTTPS.</summary>
public static class DohPolicy
{
    public sealed record Entry(string SubKey, string Name, string Value);

    public static readonly IReadOnlyList<Entry> Entries =
    [
        new(@"SOFTWARE\Policies\Google\Chrome", "DnsOverHttpsMode", "off"),
        new(@"SOFTWARE\Policies\Microsoft\Edge", "DnsOverHttpsMode", "off"),
        new(@"SOFTWARE\Policies\BraveSoftware\Brave", "DnsOverHttpsMode", "off"),
    ];

    public static void Apply(IBrowserPolicyWriter w, bool enabled)
    {
        foreach (var e in Entries)
        {
            if (enabled) w.Set(e.SubKey, e.Name, e.Value);
            else w.Delete(e.SubKey, e.Name);
        }
    }
}
