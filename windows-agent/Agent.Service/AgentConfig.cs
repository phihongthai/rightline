using System.Net;
using KidSafe.Agent.Core;

namespace KidSafe.Agent.Service;

public static class AgentConfig
{
    public static IConfiguration Load() => new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
        .AddEnvironmentVariables("KIDSAFE_")
        .Build();

    public static AgentOptions Options(IConfiguration c, string dataDir) => new()
    {
        BackendUrl = c["Agent:BackendUrl"] ?? "http://localhost:3000",
        StatePath = Path.Combine(dataDir, "state.bin"),
        QueueDir = Path.Combine(dataDir, "shots"),
        DnsListen = new IPEndPoint(IPAddress.Loopback, 53),
    };

    public static AgentPlatform Platform() => new() { Dns = new WmiDnsConfigurator(), BrowserPolicy = new RegistryPolicyWriter() };
}
