using KidSafe.Agent.Core;

namespace KidSafe.Agent.Service;

public sealed class AgentWorker(IConfiguration config, ILoggerFactory loggers) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var log = loggers.CreateLogger("Agent");
        var dataDir = SecurePaths.EnsureDataDir();
        var helperPath = config["Agent:HelperPath"] is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, "Agent.Helper.exe");

        var options = new AgentOptions
        {
            BackendUrl = config["Agent:BackendUrl"] ?? "http://localhost:3000",
            StatePath = Path.Combine(dataDir, "state.bin"),
        };
        var launcher = new HelperLauncher(helperPath, loggers.CreateLogger("Launcher"));
        var controller = new AgentController(options, new DpapiProtector(), TimeProvider.System,
            SecurePaths.CreatePipe, loggers.CreateLogger("Controller"), launcher.AuthorizePipeClient);

        log.LogInformation("KidSafe agent starting; backend {Url}", options.BackendUrl);
        await Task.WhenAll(controller.RunAsync(ct), launcher.RunAsync(ct));
    }
}
