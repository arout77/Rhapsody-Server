namespace RhapsodyServer.Tray;

static class Program
{
    /// <summary>A second launch (Start menu, rhapsody:// link, autostart) rings this bell in the first one.</summary>
    internal const string OpenSignalName = @"Local\RhapsodyServer.OpenDashboard";

    [STAThread]
    static void Main()
    {
        // One instance only. A second launch asks the first to open the dashboard, then exits.
        using var mutex = new Mutex(true, @"Local\RhapsodyServer.Tray", out var first);
        if (!first)
        {
            var args = Environment.GetCommandLineArgs();
            if (!args.Contains("--no-splash"))   // autostart passes --no-splash and should stay quiet
                try { using var bell = EventWaitHandle.OpenExisting(OpenSignalName); bell.Set(); } catch { }
            return;
        }

        ProtocolHandler.Register();
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
