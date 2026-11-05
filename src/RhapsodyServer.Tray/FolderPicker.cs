using System.Runtime.InteropServices;
using RhapsodyServer.Core.Web;

namespace RhapsodyServer.Tray;

/// <summary>
/// The normal Windows "Select Folder" window (the Explorer-style one), opened from the dashboard's Browse button.
/// WinForms dialogs need their own single-threaded-apartment thread. They also need a window to sit on top of, and
/// that window has to win the foreground: the browser is the foreground app, and Windows won't let a background
/// process take focus, so without the extra steps below the dialog opens hidden behind the browser and the button
/// just says "Choosing…" forever.
/// </summary>
sealed class WinFormsFolderPicker : IFolderPicker
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    /// <summary>Takes the foreground from whatever has it (the browser) by briefly sharing its input queue.</summary>
    static void ForceForeground(IntPtr hwnd)
    {
        try
        {
            keybd_event(0x12, 0, 0, UIntPtr.Zero);   // a tap on Alt marks this process as having just had input, which unlocks SetForegroundWindow
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            var fg = GetForegroundWindow();
            var fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
            var me = GetCurrentThreadId();
            var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try { BringWindowToTop(hwnd); SetForegroundWindow(hwnd); }
            finally { if (attached) AttachThreadInput(me, fgThread, false); }
        }
        catch { /* best effort: the taskbar button below still lets the person find the window */ }
    }

    public Task<string?> PickAsync(string? initialDir, CancellationToken ct)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try
            {
                // An invisible, always-on-top owner centred on screen. It has a taskbar button so that, if Windows
                // still refuses to raise the dialog, it flashes there and can be clicked.
                using var owner = new Form
                {
                    Text = "Choose the project folder",
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = true,
                    StartPosition = FormStartPosition.CenterScreen,
                    Size = new Size(1, 1),
                    Opacity = 0,
                    TopMost = true
                };
                owner.Show();
                ForceForeground(owner.Handle);

                using var dlg = new FolderBrowserDialog
                {
                    Description = "Choose the project folder",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true
                };
                if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
                    dlg.InitialDirectory = initialDir;

                done.SetResult(dlg.ShowDialog(owner) == DialogResult.OK ? dlg.SelectedPath : null);
            }
            catch (Exception ex) { done.TrySetException(ex); }
        })
        { IsBackground = true, Name = "folder-picker" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return done.Task;
    }
}
