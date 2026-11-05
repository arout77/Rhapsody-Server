using System.Diagnostics;

namespace RhapsodyServer.Core.Web;

/// <summary>Shows a folder in the normal file manager. The dashboard never passes a path in: it names a place, and the backend picks the path.</summary>
public interface IFolderOpener
{
    bool CanOpen { get; }
    void Open(string folder);
    /// <summary>Opens one file in the program the person edits that kind of file with.</summary>
    void OpenFile(string file) => throw new NotSupportedException("Opening files isn't supported here.");
    /// <summary>Opens the file's folder with the file selected.</summary>
    void Reveal(string file) => throw new NotSupportedException("Opening files isn't supported here.");
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

    /// <summary>The default program for the file type; Notepad if Windows has none for it (a bare .ini often has none).</summary>
    public void OpenFile(string file)
    {
        if (!CanOpen) throw new InvalidOperationException("Opening files needs Windows.");
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception)
        {
            var psi = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(file);
            Process.Start(psi)?.Dispose();
        }
    }

    public void Reveal(string file)
    {
        if (!CanOpen) throw new InvalidOperationException("Opening folders needs Windows.");
        // Explorer wants /select,"path" as one argument; ArgumentList would quote it differently.
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, Arguments = $"/select,\"{file}\"" })?.Dispose();
    }
}
