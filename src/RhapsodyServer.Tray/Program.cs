namespace RhapsodyServer.Tray;

static class Program
{
    [STAThread]
    static void Main()
    {
        // One instance only; a second launch just exits instantly.
        using var mutex = new Mutex(true, @"Local\RhapsodyServer.Tray", out var first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
