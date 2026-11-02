using RhapsodyServer.Core.Web;

namespace RhapsodyServer.Tray;

/// <summary>
/// The normal Windows "Select Folder" window (the Explorer-style one), opened from the dashboard's Browse button.
/// WinForms dialogs need their own single-threaded-apartment thread, and a window to sit on top of, because
/// the browser is the foreground app and would otherwise cover the dialog.
/// </summary>
sealed class WinFormsFolderPicker : IFolderPicker
{
    public Task<string?> PickAsync(string? initialDir, CancellationToken ct)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try
            {
                // An invisible, always-on-top owner: brings the dialog to the front and centres it on screen.
                using var owner = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.CenterScreen,
                    Size = new Size(1, 1),
                    Opacity = 0,
                    TopMost = true
                };
                owner.Show();
                owner.Activate();

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
