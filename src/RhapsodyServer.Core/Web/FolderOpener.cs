using System.Diagnostics;

namespace RhapsodyServer.Core.Web;

/// <summary>Shows a folder in the normal file manager. The dashboard never passes a path in: it names a place, and the backend picks the path.</summary>
public interface IFolderOpener
{
    bool CanOpen { get; }
    void Open(string folder);
}

/// <summary>Windows Explorer. Doesn't wait for it: Explorer reports exit code 1 even when it worked.</summary>
public sealed class ShellFolderOpener : IFolderOpener
{
    public bool CanOpen => OperatingSystem.IsWindows();

    public void Open(string folder)
    {
        if (!CanOpen) throw new InvalidOperationException("Opening folders needs Windows.");
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(folder);
        Process.Start(psi)?.Dispose();
    }
}
