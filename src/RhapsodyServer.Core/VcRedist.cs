using System.Diagnostics;
using Microsoft.Win32;

namespace RhapsodyServer.Core;

/// <summary>
/// A clean Windows machine usually lacks the VC++ runtime, which is the classic "php-cgi won't start" failure.
/// Best-effort: if this can't be checked or installed, Rhapsody Server carries on and says so.
/// </summary>
public static class VcRedist
{
    /// <summary>VS2022-era runtimes are build 14.3x (Bld >= 30000); older ones lack symbols newer PHP builds use.</summary>
    public static bool IsInstalled()
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
            return k?.GetValue("Installed") is int i && i == 1 && k.GetValue("Bld") is int b && b >= 30000;
        }
        catch { return false; }
    }

    /// <summary>Runs the installer silently. One UAC prompt. Exit 3010 = reboot suggested, 1638 = newer already present.</summary>
    public static async Task InstallAsync(string exePath, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exePath, "/install /quiet /norestart") { UseShellExecute = true, Verb = "runas" };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the VC++ installer");
        await p.WaitForExitAsync(ct);
        if (p.ExitCode is not (0 or 3010 or 1638))
            throw new InvalidOperationException($"VC++ runtime installer failed (code {p.ExitCode})");
    }
}
