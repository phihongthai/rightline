using System.Diagnostics;
using System.Management;
using KidSafe.Agent.Core;
using Microsoft.Win32;

namespace KidSafe.Agent.Service;

/// <summary>Reads/sets DNS on IP-enabled adapters via WMI. "Static vs DHCP" comes from the registry NameServer value.</summary>
public sealed class WmiDnsConfigurator : IDnsConfigurator
{
    private const string Query = "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE";

    public IReadOnlyList<DnsAdapter> GetAdapters()
    {
        var list = new List<DnsAdapter>();
        using var searcher = new ManagementObjectSearcher(Query);
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                var id = (string?)mo["SettingID"];
                if (string.IsNullOrEmpty(id)) continue;
                list.Add(new DnsAdapter(id, (string?)mo["Description"] ?? id, StaticServers(id)));
            }
        }
        return list;
    }

    public void SetDns(string adapterId, string[]? servers)
    {
        using var searcher = new ManagementObjectSearcher(Query + $" AND SettingID = '{adapterId.Replace("'", "")}'");
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                // null args = reset to automatic (DHCP).
                mo.InvokeMethod("SetDNSServerSearchOrder", servers is { Length: > 0 } ? new object[] { servers } : null);
            }
        }
    }

    public void FlushCache()
    {
        try { using var p = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false }); p?.WaitForExit(5000); }
        catch { /* best effort */ }
    }

    private static string[] StaticServers(string settingId)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{settingId}");
        var ns = k?.GetValue("NameServer") as string;
        return string.IsNullOrWhiteSpace(ns) ? [] : ns.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
    }
}

public sealed class RegistryPolicyWriter : IBrowserPolicyWriter
{
    public void Set(string subKey, string name, string value)
    {
        using var k = Registry.LocalMachine.CreateSubKey(subKey, writable: true);
        if (!Equals(k.GetValue(name), value)) k.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string subKey, string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey(subKey, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>Kills blocked processes the moment they start (WMI Win32_ProcessStartTrace, no polling) plus a periodic sweep as backstop.</summary>
public sealed class ProcessBlocker : IDisposable
{
    private readonly AgentController _agent;
    private readonly ILogger _log;
    private ManagementEventWatcher? _watcher;
    private Timer? _sweepTimer;

    public ProcessBlocker(AgentController agent, ILogger log)
    {
        _agent = agent;
        _log = log;
    }

    public void Start()
    {
        try
        {
            _watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
            _watcher.EventArrived += (_, e) =>
            {
                try
                {
                    var name = (string)e.NewEvent["ProcessName"];
                    var pid = Convert.ToInt32(e.NewEvent["ProcessID"]);
                    if (_agent.ShouldBlockProcess(name)) Kill(pid, name);
                }
                catch (Exception ex) { _log.LogDebug(ex, "process event handling failed"); }
            };
            _watcher.Start();
        }
        catch (Exception ex) { _log.LogError(ex, "WMI process watcher failed to start; relying on periodic sweep"); }

        _agent.PolicyApplied += Sweep;
        _sweepTimer = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
    }

    public void Sweep()
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id <= 4 || p.Id == Environment.ProcessId) continue;
                var name = p.ProcessName + ".exe";
                if (_agent.ShouldBlockProcess(name)) Kill(p.Id, name);
            }
            catch { /* exited / access denied */ }
            finally { p.Dispose(); }
        }
    }

    private void Kill(int pid, string name)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            _agent.ReportBlockedApp(name);
            _log.LogInformation("Blocked and killed {Name} ({Pid})", name, pid);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
    }

    public void Dispose()
    {
        _agent.PolicyApplied -= Sweep;
        _sweepTimer?.Dispose();
        try { _watcher?.Stop(); } catch { /* ignore */ }
        _watcher?.Dispose();
    }
}
