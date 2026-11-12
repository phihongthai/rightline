using KidSafe.Agent.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "KidSafeAgent");
builder.Services.AddHostedService<AgentWorker>();
await builder.Build().RunAsync();
