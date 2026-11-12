using KidSafe.Agent.Core;
using KidSafe.Agent.Service;

// Command-line verbs used by the installer / uninstaller (run elevated):
//   Agent.Service.exe harden                      apply service recovery + ACLs
//   Agent.Service.exe verify-uninstall <secret>   exit 0 allowed, 1 denied, 2 cannot verify (offline & not a valid PIN)
//   Agent.Service.exe cleanup                     restore DNS and remove browser DoH policies
if (args.Length > 0)
{
    var cfg = AgentConfig.Load();
    switch (args[0])
    {
        case "harden":
            TamperProtection.Apply();
            return 0;

        case "verify-uninstall":
        {
            var secret = args.Length > 1 ? args[1] : Console.In.ReadLine() ?? "";
            var state = new StateStore(Path.Combine(SecurePaths.EnsureDataDir(), "state.bin"), new DpapiProtector()).Load();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            return await UninstallAuth.VerifyAsync(state, secret, http, cfg["Agent:BackendUrl"] ?? "http://localhost:3000", TimeProvider.System);
        }

        case "cleanup":
        {
            var opts = AgentConfig.Options(cfg, SecurePaths.EnsureDataDir());
            var agent = new AgentController(opts, new DpapiProtector(), TimeProvider.System, () => throw new InvalidOperationException(), platform: AgentConfig.Platform());
            agent.Cleanup();
            return 0;
        }

        default:
            Console.Error.WriteLine("unknown command: " + args[0]);
            return 64;
    }
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = TamperProtection.ServiceName);
builder.Services.AddHostedService<AgentWorker>();
await builder.Build().RunAsync();
return 0;
