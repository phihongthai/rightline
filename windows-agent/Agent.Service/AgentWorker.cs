using KidSafe.Agent.Core;

namespace KidSafe.Agent.Service;

public sealed class AgentWorker(IConfiguration config, ILoggerFactory loggers) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var log = loggers.CreateLogger("Agent");
        var dataDir = SecurePaths.EnsureDataDir();
        TamperProtection.Apply(log);

        var helperPath = config["Agent:HelperPath"] is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, "Agent.Helper.exe");
        var options = AgentConfig.Options(config, dataDir);
        var launcher = new HelperLauncher(helperPath, loggers.CreateLogger("Launcher"));
        var controller = new AgentController(options, new DpapiProtector(), TimeProvider.System,
            SecurePaths.CreatePipe, loggers.CreateLogger("Controller"), launcher.AuthorizePipeClient, AgentConfig.Platform());

        using var blocker = new ProcessBlocker(controller, loggers.CreateLogger("Blocker"));
        blocker.Start();

        log.LogInformation("KidSafe agent starting; backend {Url}", options.BackendUrl);
        await Task.WhenAll(controller.RunAsync(ct), launcher.RunAsync(ct));
    }
}
