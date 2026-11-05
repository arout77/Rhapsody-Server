using Microsoft.Win32;

namespace RhapsodyServer.Tray;

/// <summary>
/// Registers rhapsody:// for the current user (HKCU, no admin needed). A bookmark or link to rhapsody://open
/// starts Rhapsody Server if it is not running, or just opens the dashboard if it is.
/// </summary>
static class ProtocolHandler
{
    public static void Register()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\rhapsody");
            key.SetValue("", "URL:Rhapsody Server");
            key.SetValue("URL Protocol", "");
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{exe}\",0");
            using var cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" --open");
        }
        catch { /* best effort: the app works without it */ }
    }
}
