using System.Windows;

namespace KidSafe.Agent.Helper;

public partial class App : Application
{
    private Mutex? _single;
    private HelperHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // One helper per user session.
        _single = new Mutex(true, @"Local\KidSafeClone.Helper", out var created);
        if (!created) { Shutdown(); return; }
        _host = new HelperHost(Dispatcher);
        _host.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Stop();
        _single?.Dispose();
        base.OnExit(e);
    }
}
