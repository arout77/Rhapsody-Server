using System.Diagnostics;
using RhapsodyServer.Core;
using RhapsodyServer.Core.Web;

namespace RhapsodyServer.Tray;

/// <summary>
/// The tray icon is the always-there part: status at a glance, quick toggles, and "Open dashboard".
/// Anything bigger (sites, versions, logs, scaffolding) lives in the web dashboard, which is only started on demand.
/// No main window, no timers, no polling: the menu is rebuilt from live state when you open it.
/// </summary>
sealed class TrayContext : ApplicationContext
{
    readonly Stack _stack = new();
    readonly StackBackend _backend;
    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu = new();
    readonly Control _ui = new();          // hidden control, only used to marshal calls onto the UI thread
    readonly IProgress<string> _progress;
    DashboardServer? _dash;

    bool Busy => _stack.CurrentOperation != null;

    public TrayContext()
    {
        _backend = new StackBackend(_stack, new WinFormsFolderPicker());
        _ = _ui.Handle;                    // force handle creation on the UI thread
        _progress = new Progress<string>(msg => _ui.BeginInvoke(() => _icon.Text = Trunc(msg)));
        _icon = new NotifyIcon { Icon = AppIcon(), Text = "Rhapsody Server", Visible = true, ContextMenuStrip = _menu };
        _menu.Opening += (_, _) => Rebuild();
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenDashboard(); };

        _stack.Supervisor.StateChanged += s =>
        {
            if (s.State == ServiceState.Crashed)
                _ui.BeginInvoke(() => { if (!Busy) Balloon($"{s.Spec.Name} stopped unexpectedly. Open the dashboard to see its log.", ToolTipIcon.Warning); });
        };

        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("--no-splash")) new SplashForm(TimeSpan.FromMilliseconds(1600)).Show();

        // Start listening right away so the address is always live while the tray is running,
        // and let a second launch ask this instance to open the dashboard.
        try { StartDashboard(); } catch (Exception ex) { Balloon("Couldn't start the dashboard: " + ex.Message, ToolTipIcon.Error); }
        ListenForSecondLaunch();
        if (args.Contains("--open")) OpenDashboard();
    }

    void ListenForSecondLaunch()
    {
        var bell = new EventWaitHandle(false, EventResetMode.AutoReset, Program.OpenSignalName);
        new Thread(() =>
        {
            while (bell.WaitOne()) _ui.BeginInvoke(() => OpenDashboard());
        }) { IsBackground = true, Name = "open-dashboard-signal" }.Start();
    }

    void StartDashboard()
    {
        if (_dash != null) return;
        _dash = new DashboardServer(_backend, _stack.Settings.UiToken);
        _dash.Start(_stack.Settings.UiPort);
    }

    /// <summary>The icon built into RhapsodyServer.exe (see ApplicationIcon in the project file).</summary>
    static Icon AppIcon()
    {
        try { if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } ico) return ico; }
        catch { /* fall through */ }
        return SystemIcons.Application;
    }

    // ------------------------------------------------------------------ menu
    void Rebuild()
    {
        _menu.Items.Clear();
        var running = _stack.Supervisor.All.Any(s => s.State == ServiceState.Running);

        var open = Plain("Open dashboard", () => OpenDashboard());   // never blocked by a running operation: you may want to watch it
        open.Font = new Font(open.Font, FontStyle.Bold);
        _menu.Items.Add(new ToolStripSeparator());

        Item("Start all", "Starting everything", () => _stack.StartAllAsync(_progress), !Busy);
        Item("Stop all", "Stopping everything", () => _stack.StopAllAsync(), !Busy && running);
        _menu.Items.Add(new ToolStripSeparator());

        foreach (var id in new[] { _stack.WebServerId }.Concat(Stack.CoreServiceIds))
        {
            var state = _stack.Supervisor.Get(id)?.State ?? ServiceState.Stopped;
            var on = state is ServiceState.Running or ServiceState.Starting;
            Item($"{Dot(state)}  {ServiceLabel(id)}", on ? $"Stopping {id}" : $"Starting {id}",
                () => on ? _stack.StopServiceAsync(id) : _stack.StartServiceAsync(id, _progress), !Busy);
        }

        if (_stack.Settings.Sites.Count > 0)
        {
            _menu.Items.Add(new ToolStripSeparator());
            var sites = new ToolStripMenuItem("Sites");
            foreach (var site in _stack.Settings.Sites.ToArray())
            {
                var url = $"{(site.Https ? "https" : "http")}://{_stack.Domain(site.Name)}";
                var it = new ToolStripMenuItem($"{_stack.Domain(site.Name)}   (PHP {site.Php})");
                it.Click += (_, _) => Shell(url);
                sites.DropDownItems.Add(it);
            }
            _menu.Items.Add(sites);
        }

        _menu.Items.Add(new ToolStripSeparator());
        Plain("Open sites folder", OpenSitesFolder);
        Plain("Edit php.ini", EditPhpIni);
        Plain("Help", () => OpenDashboard("help"));
        Plain("Quit", () => _ = Quit());
    }

    /// <summary>The php.ini of the default PHP, in the person's editor (Notepad if nothing is set up for .ini files).</summary>
    void EditPhpIni()
    {
        try { _backend.OpenPhpIni(new PhpIniRequest(null, "edit")); }
        catch (Exception ex) { Balloon("Couldn't open php.ini: " + ex.Message, ToolTipIcon.Error); }
    }

    /// <summary>Where new sites are created. The folder is made first, because before the first site it doesn't exist yet.</summary>
    void OpenSitesFolder()
    {
        try
        {
            var dir = _stack.Settings.ResolveSitesRoot();
            Directory.CreateDirectory(dir);
            Shell(dir);
        }
        catch (Exception ex) { Balloon("Couldn't open the sites folder: " + ex.Message, ToolTipIcon.Error); }
    }

    void OpenDashboard(string? page = null)
    {
        try
        {
            StartDashboard();
            Shell(page == null ? _dash.OpenUrl : _dash.OpenUrl + "&go=" + page);   // the page reads &go= once, with the token
        }
        catch (Exception ex) { Balloon("Couldn't start the dashboard: " + ex.Message, ToolTipIcon.Error); }
    }

    async Task Quit()
    {
        _icon.Text = "Rhapsody Server: stopping services...";
        _dash?.Dispose();
        await _stack.StopAllAsync();
        _icon.Visible = false;
        _stack.Dispose();
        ExitThread();
    }

    // --------------------------------------------------------------- plumbing
    ToolStripMenuItem Item(string text, string title, Func<Task> action, bool enabled)
    {
        var it = new ToolStripMenuItem(text) { Enabled = enabled };
        it.Click += (_, _) => Run(title, action);
        _menu.Items.Add(it);
        return it;
    }

    ToolStripMenuItem Plain(string text, Action action)
    {
        var it = new ToolStripMenuItem(text);
        it.Click += (_, _) => action();
        _menu.Items.Add(it);
        return it;
    }

    // async void is correct here: it is a UI event handler, and every exception is handled inside.
    async void Run(string title, Func<Task> action)
    {
        try
        {
            try { await _stack.RunExclusiveAsync(title, action); }
            catch (UnauthorizedAccessException ex) when (ex.Message.Contains("hosts file"))
            {
                var ok = MessageBox.Show(
                    "Rhapsody Server needs permission to edit your hosts file so .test domains work.\n\n" +
                    "Windows will ask for administrator approval once. After that there are no more prompts.",
                    "Rhapsody Server", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (ok != DialogResult.OK) return;
                await HostsFile.GrantCurrentUserAccessAsync();
                await _stack.RunExclusiveAsync(title, action);
            }
        }
        catch (Exception ex) { Balloon(ex.Message, ToolTipIcon.Error); }
        finally { _icon.Text = "Rhapsody Server"; }
    }

    void Balloon(string text, ToolTipIcon icon) => _icon.ShowBalloonTip(6000, "Rhapsody Server", Trunc(text, 250), icon);
    static string Trunc(string s, int max = 63) => s.Length <= max ? s : s[..(max - 1)] + "…";
    static string Dot(ServiceState s) => s switch { ServiceState.Running => "●", ServiceState.Starting => "◐", ServiceState.Crashed => "✖", _ => "○" };

    string ServiceLabel(string id) => id switch
    {
        "nginx" => "nginx",
        "apache" => "Apache",
        "mariadb" => $"MariaDB  :{_stack.Settings.MariaDbPort}",
        "redis" => $"Redis  :{_stack.Settings.RedisPort}",
        "mailpit" => $"Mailpit  smtp :{_stack.Settings.SmtpPort}",
        _ => id
    };

    static void Shell(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
