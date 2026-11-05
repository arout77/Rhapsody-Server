using System.Text;
using System.Text.RegularExpressions;
using RhapsodyServer.Core.Ai;

namespace RhapsodyServer.Core.Web;

public sealed class StackBackend : IDashboardBackend, IDevContext
{
    readonly Stack _s;
    readonly IFolderPicker? _picker;
    readonly AiHelper _ai;
    readonly IFolderOpener _opener;
    public event Action? Changed;
    public event Action? ProfileChanged;

    public StackBackend(Stack stack, IFolderPicker? folderPicker = null, AiService? ai = null, IFolderOpener? opener = null)
    {
        _s = stack;
        _picker = folderPicker;
        _opener = opener ?? new ShellFolderOpener();
        _s.Settings.Ai ??= new AiSettings();   // a settings.json from before the AI helper existed
        ai ??= new AiService(_s.Settings.Ai, _s.Save, new AiKeyStore(Path.Combine(_s.Paths.Root, "ai-keys.json")),
            Path.Combine(_s.Paths.Root, "ai-providers.json"));
        _ai = new AiHelper(ai, DescribeEnvironment, ReadLog, () => new[] { _s.Settings.UiToken, _s.Settings.PmaSecret }, dev: this);
        _s.Supervisor.StateChanged += _ => Changed?.Invoke();
        _s.OperationChanged += () => Changed?.Invoke();
        _s.Profile.Changed += () => ProfileChanged?.Invoke();
    }

    public Profiling.ProfileSnapshot GetProfile() => _s.Profile.Snapshot(_s.Settings.ProfilingEnabled);
    public void ClearProfile() => _s.Profile.Clear();
    public Task SetProfilingAsync(bool on, IProgress<string> p, CancellationToken ct) => _s.SetProfilingAsync(on, p, ct);

    static string StateName(ServiceState s) => s.ToString().ToLowerInvariant();

    // ------------------------------------------------------------------ state
    public DashboardState GetState()
    {
        var st = _s.Settings;
        var services = new List<ServiceDto>();

        var web = _s.WebServerId;
        var webName = web == "nginx" ? "nginx" : "Apache";
        var webVer = _s.EffectiveActive(web);
        services.Add(new ServiceDto(web, webVer != null ? $"{webName} {webVer}" : webName, Of(web), st.HttpPort, true,
            $"http :{st.HttpPort}, https :{st.HttpsPort}"));

        var dbVer = _s.EffectiveActive("mariadb");
        services.Add(new ServiceDto("mariadb", dbVer != null ? $"MariaDB {dbVer}" : "MariaDB", Of("mariadb"), st.MariaDbPort, true,
            $"127.0.0.1:{st.MariaDbPort}, user root, no password"));
        services.Add(new ServiceDto("redis", "Redis", Of("redis"), st.RedisPort, true, $"127.0.0.1:{st.RedisPort}"));
        services.Add(new ServiceDto("mailpit", "Mailpit", Of("mailpit"), st.MailUiPort, true,
            $"SMTP 127.0.0.1:{st.SmtpPort}, inbox at https://{_s.Domain("mail")}"));

        foreach (var branch in _s.BranchesInUse.OrderByDescending(b => b, VersionUtil.Comparer))
        {
            var workers = _s.Supervisor.All.Where(w => w.Spec.Id.StartsWith($"php-{branch}-")).ToList();
            var state = workers.Count == 0 ? "stopped"
                : workers.Any(w => w.State == ServiceState.Crashed) ? "crashed"
                : workers.Any(w => w.State == ServiceState.Starting) ? "starting"
                : workers.All(w => w.State == ServiceState.Running) ? "running" : "stopped";
            var users = _s.Settings.Sites.Count(x => x.Php == branch);
            services.Add(new ServiceDto($"php-{branch}", $"PHP {branch}", state, null, false,
                $"{st.PhpWorkersPerVersion} workers, starts with the web server" + (users > 0 ? $", {users} site{(users == 1 ? "" : "s")}" : "")));
        }

        var sites = st.Sites.Select(x => new SiteDto(x.Name, _s.Domain(x.Name),
            $"{(x.Https ? "https" : "http")}://{_s.Domain(x.Name)}", x.Root, x.PublicDir, x.Php, x.Https)).ToArray();

        var settings = new SettingsDto(st.Tld, st.WebServer.ToString(), st.DefaultPhp, PhpResolver.SupportedBranches,
            st.HttpPort, st.HttpsPort, st.MariaDbPort, st.RedisPort, st.SmtpPort, st.MailUiPort, st.ResolveSitesRoot(),
            _s.Paths.Root, _s.Paths.Logs);

        return new DashboardState(services.ToArray(), sites, settings, HostsFile.CanWrite(), OperatingSystem.IsWindows(), CanPickFolder, _opener.CanOpen);

        string Of(string id) => StateName(_s.Supervisor.Get(id)?.State ?? ServiceState.Stopped);
    }

    // ---------------------------------------------------------- open a folder
    /// <summary>
    /// The page names a place ("sites", "logs", a site by name); the path always comes from here, never from the request,
    /// so this can't be used to open or launch anything else.
    /// </summary>
    public async Task<string> CheckSiteAsync(string name, CancellationToken ct)
    {
        var site = _s.Settings.Sites.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"There is no site called \"{name}\".");
        var domain = _s.Domain(site.Name);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Site: {domain}   (PHP {site.Php}, folder {site.Root})");
        sb.AppendLine($"Web server: {_s.WebServerId} is {StateName(_s.Supervisor.Get(_s.WebServerId)?.State ?? ServiceState.Stopped)}");
        foreach (var w in _s.Supervisor.All.Where(x => x.Spec.Id.StartsWith("php-" + site.Php + "-")))
            sb.AppendLine($"{w.Spec.Name}: {StateName(w.State)}, port {w.Spec.ReadyPort} {(PortProbe.IsOpen(w.Spec.ReadyPort ?? 0) ? "open" : "NOT open")}");
        try
        {
            var cgi = Path.Combine(_s.PhpDir(site.Php), "php-cgi.exe");
            if (File.Exists(cgi))
            {
                var v = await ProcessRunner.RunAsync(cgi, new[] { "-v" }, _s.PhpDir(site.Php), _s.PhpEnv(site.Php).ToDictionary(k => k.Key, k => k.Value), TimeSpan.FromSeconds(15), ct);
                sb.AppendLine($"php-cgi -v: exit code {v.ExitCode}{(v.Output.Trim().Length == 0 ? ", printed nothing (a missing DLL or a crash looks like this)" : "")}");
                if (v.Output.Trim().Length > 0) sb.AppendLine(string.Join("\n", v.Output.Trim().Split('\n').Take(8).Select(l => "  " + l.TrimEnd())));
            }
            else sb.AppendLine($"php-cgi.exe is NOT in {_s.PhpDir(site.Php)}");
        }
        catch (Exception ex) { sb.AppendLine("php-cgi -v could not run: " + ex.Message); }
        sb.AppendLine($"Ports: {_s.Settings.HttpPort} {(PortProbe.IsOpen(_s.Settings.HttpPort) ? "open" : "NOT open")}, {_s.Settings.HttpsPort} {(PortProbe.IsOpen(_s.Settings.HttpsPort) ? "open" : "NOT open")}");
        sb.AppendLine($"Entry for {domain} in the hosts file: {(HostsFile.Contains(domain) ? "yes" : "NO")}");
        sb.AppendLine(File.Exists(Path.Combine(site.Root, ".env")) ? ".env: found" : ".env: MISSING in the site folder");

        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
            using var rq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_s.Settings.HttpPort}/");
            rq.Headers.Host = domain;
            using var rs = await http.SendAsync(rq, ct);
            var body = await rs.Content.ReadAsStringAsync(ct);
            body = System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(body, @"(?is)<(script|style).*?</\1>", " "), "<[^>]+>", " ");
            body = System.Text.RegularExpressions.Regex.Replace(body, @"\s+", " ").Trim();
            sb.AppendLine($"\nRequest for http://{domain}/ answered: {(int)rs.StatusCode} {rs.ReasonPhrase}");
            if (rs.Headers.Location != null) sb.AppendLine("Redirects to: " + rs.Headers.Location);
            if (body.Length > 0) sb.AppendLine("Page says: " + (body.Length > 600 ? body[..600] + "…" : body));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            sb.AppendLine($"\nRequest for http://{domain}/ got no answer: {ex.Message}");
        }

        var errs = new[] { Path.Combine(_s.Paths.Etc, _s.WebServerId, "logs", "error.log"), Path.Combine(_s.Paths.Data, "php-tmp", "php_error.log") };
        sb.AppendLine($"\n----- last lines of php-{site.Php}.log (what the PHP workers printed) -----\n{TailOf(Path.Combine(_s.Paths.Logs, $"php-{site.Php}.log"), 15) ?? "(empty)"}");
        foreach (var f in errs)
            sb.AppendLine($"\n----- last lines of {Path.GetFileName(f)} -----\n{TailOf(f, 15) ?? "(empty or not created yet)"}");
        return sb.ToString();
    }

    public void OpenFolder(OpenFolderRequest req)
    {
        var target = (req.Target ?? "").Trim().ToLowerInvariant();
        string path;
        switch (target)
        {
            case "sites":
                path = _s.Settings.ResolveSitesRoot();
                Directory.CreateDirectory(path);   // before the first site exists the folder isn't there yet; opening it should still work
                break;
            case "data": path = _s.Paths.Root; break;
            case "logs": path = _s.Paths.Logs; break;
            case "site":
                var site = _s.Settings.Sites.FirstOrDefault(x => x.Name.Equals(req.Name ?? "", StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"There is no site called \"{req.Name}\".");
                path = site.Root;
                break;
            default: throw new ArgumentException("Unknown folder.");
        }
        if (!Directory.Exists(path)) throw new ArgumentException($"That folder doesn't exist any more: {path}");
        _opener.Open(path);
    }

    /// <summary>The page names a PHP version and an action; the path is always the one PHP is told to load (PHPRC), never anything the page sends.</summary>
    public void OpenPhpIni(PhpIniRequest req)
    {
        var installed = _s.InstalledPhpBranches();
        var branch = string.IsNullOrWhiteSpace(req.Branch) ? _s.EffectiveActive("php") : req.Branch.Trim();
        if (branch == null || !installed.Contains(branch)) throw new ArgumentException($"PHP {req.Branch ?? "(default)"} isn't installed.");
        var action = (req.Action ?? "").Trim().ToLowerInvariant();
        if (action is not ("edit" or "reveal")) throw new ArgumentException("Unknown action.");
        var file = _s.EnsurePhpIni(branch);
        if (action == "edit") _opener.OpenFile(file); else _opener.Reveal(file);
    }

    public Task RestartPhpAsync(string branch, IProgress<string> p, CancellationToken ct)
    {
        if (!_s.InstalledPhpBranches().Contains(branch)) throw new ArgumentException($"PHP {branch} isn't installed.");
        return Run($"Restarting PHP {branch}", () => _s.RestartPhpAsync(branch, p, ct));
    }

    // ---------------------------------------------------------- folder picker
    public bool CanPickFolder => _picker != null;

    public async Task<string?> PickFolderAsync(string? initial, CancellationToken ct)
    {
        if (_picker == null) throw new InvalidOperationException("This host can't show a folder window.");
        var start = !string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial.Trim().Trim('"')) ? initial.Trim().Trim('"')
            : Directory.Exists(_s.Settings.ResolveSitesRoot()) ? _s.Settings.ResolveSitesRoot() : null;
        return await _picker.PickAsync(start, ct);
    }

    // ------------------------------------------------------- modules / extensions
    static ModuleDto[] Dto(IEnumerable<ModuleInfo> l) => l.Select(m => new ModuleDto(m.Name, m.Description, m.Enabled, m.Locked, m.Note)).ToArray();

    public PhpModulesDto GetPhpModules(string? branch)
    {
        var installed = _s.InstalledPhpBranches().ToArray();
        var pick = branch != null && installed.Contains(branch) ? branch
            : installed.Contains(_s.Settings.DefaultPhp) ? _s.Settings.DefaultPhp
            : installed.FirstOrDefault();
        if (pick == null) return new PhpModulesDto(installed, null, Array.Empty<ModuleDto>(), false);
        var running = _s.Supervisor.All.Any(w => w.Spec.Id.StartsWith($"php-{pick}-") && w.State == ServiceState.Running);
        return new PhpModulesDto(installed, pick, Dto(_s.PhpExtensionList(pick)), running, _s.PhpIniPath(pick));
    }

    public ApacheModulesDto GetApacheModules()
    {
        var list = _s.ApacheModuleList();
        var serving = _s.Settings.WebServer == WebServerKind.Apache;
        var running = _s.Supervisor.Get("apache")?.State == ServiceState.Running;
        return list == null
            ? new ApacheModulesDto(false, null, serving, running, Array.Empty<ModuleDto>())
            : new ApacheModulesDto(true, _s.EffectiveActive("apache"), serving, running, Dto(list));
    }

    public Task SetPhpExtensionsAsync(string branch, string[] enabled, IProgress<string> p, CancellationToken ct) =>
        Run($"Updating PHP {branch} extensions", () => _s.SetPhpExtensionsAsync(branch, enabled, p, ct));

    public Task SetApacheModulesAsync(string[] enabled, IProgress<string> p, CancellationToken ct) =>
        Run("Updating Apache modules", () => _s.SetApacheModulesAsync(enabled, p, ct));

    // ------------------------------------------------------------------- logs
    /// <summary>The last lines of a text file that another program may still be writing to, or null if it isn't there or is empty.</summary>
    const int TailBytes = 256 * 1024;
    static string? TailOf(string path, int lines)
    {
        if (!File.Exists(path)) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int Tail = TailBytes;
        if (fs.Length > Tail) fs.Seek(-Tail, SeekOrigin.End);
        using var sr = new StreamReader(fs);
        var all = sr.ReadToEnd().Replace("\r\n", "\n").Split('\n');
        var text = string.Join('\n', all.TakeLast(Math.Clamp(lines, 1, 2000))).Trim('\n');
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// What this service said (the supervisor's log), plus the file where the program itself writes its real errors:
    /// the web server's error.log, or PHP's php_error.log. Without those the dashboard only shows start/stop lines.
    /// </summary>
    public string ReadLog(string id, int lines)
    {
        if (!Regex.IsMatch(id, @"^[a-z0-9.\-]+$")) throw new ArgumentException("Bad log name");
        var parts = new List<string>();
        if (TailOf(Path.Combine(_s.Paths.Logs, id + ".log"), lines) is { } own) parts.Add(own);

        string? errorFile = id is "apache" or "nginx" ? Path.Combine(_s.Paths.Etc, id, "logs", "error.log")
            : id.StartsWith("php-") ? Path.Combine(_s.Paths.Data, "php-tmp", "php_error.log")
            : null;
        if (errorFile != null && TailOf(errorFile, lines) is { } err)
            parts.Add($"----- error log ({Path.GetFileName(errorFile)}): what the program itself reported -----\n{err}");

        return parts.Count == 0 ? "(nothing logged yet)" : string.Join("\n\n", parts);
    }

    // ------------------------------------------------------- the Logs page
    /// <summary>The fixed list of log files the Logs page may show. An id from the page is looked up here, never turned into a path.</summary>
    IEnumerable<(string Id, string Name, string Group, string Detail, string Path, string? Explain)> LogFiles()
    {
        var logs = _s.Paths.Logs;
        foreach (var web in new[] { "nginx", "apache" })
        {
            var g = web == "nginx" ? "Nginx" : "Apache";
            yield return (web, web + ".log", g, "start, stop and program output", Path.Combine(logs, web + ".log"), web);
            yield return (web + "-error", "error.log", g, "what the web server reported", Path.Combine(_s.Paths.Etc, web, "logs", "error.log"), web);
        }
        var branches = _s.BranchesInUse.OrderByDescending(b => b, VersionUtil.Comparer).ToList();
        foreach (var b in branches)
            yield return ($"php-{b}", $"php-{b}.log", "PHP", $"PHP {b} workers' output", Path.Combine(logs, $"php-{b}.log"), $"php-{b}");
        yield return ("php-error", "php_error.log", "PHP", "errors and warnings from your code",
            Path.Combine(_s.Paths.Data, "php-tmp", "php_error.log"), branches.Count > 0 ? $"php-{branches[0]}" : null);
        yield return ("mariadb", "mariadb.log", "MariaDB", "database server output", Path.Combine(logs, "mariadb.log"), "mariadb");
        yield return ("redis", "redis.log", "Redis", "cache server output", Path.Combine(logs, "redis.log"), "redis");
        yield return ("mailpit", "mailpit.log", "Mailpit", "mail catcher output", Path.Combine(logs, "mailpit.log"), "mailpit");
    }

    public LogSourceDto[] GetLogSources()
    {
        var list = new List<LogSourceDto>();
        foreach (var f in LogFiles())
        {
            try
            {
                var fi = new FileInfo(f.Path);
                if (fi.Exists) list.Add(new LogSourceDto(f.Id, f.Name, f.Group, f.Detail, fi.Length, f.Explain));
            }
            catch (IOException) { }
        }
        return list.ToArray();
    }

    public LogContentDto ReadLogSource(string id, int lines)
    {
        var f = LogFiles().FirstOrDefault(x => x.Id == id);
        if (f.Id == null) throw new ArgumentException("There is no such log.");
        long size = 0;
        try { var fi = new FileInfo(f.Path); if (fi.Exists) size = fi.Length; } catch (IOException) { }
        return new LogContentDto(TailOf(f.Path, lines) ?? "", size, size > TailBytes);
    }

    // --------------------------------------------------------------- versions
    static readonly Dictionary<string, string> Notes = new()
    {
        ["php"] = "The default is used for new sites, phpMyAdmin and the mail inbox. Each site can pick its own.",
        ["apache"] = "Used when Apache is your web server.",
        ["nginx"] = "Used when nginx is your web server.",
        ["mariadb"] = "Each version keeps its own databases, so switching never touches your data."
    };

    public async Task<ComponentDto[]> GetVersionsAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(12));   // a slow vendor site must not freeze the page

        var online = await Task.WhenAll(Catalog.VersionedIds.Select(async id =>
        {
            try { return (id, list: await _s.AvailableAsync(id, cts.Token), ok: true); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
            { return (id, list: (IReadOnlyList<Package>)Array.Empty<Package>(), ok: false); }
        }));

        var result = new List<ComponentDto>();
        foreach (var (id, list, ok) in online)
        {
            var installed = _s.InstalledSeries(id).ToHashSet();
            var bundled = _s.Installer.Bundle?.Entries.Where(e => e.Id == id).ToDictionary(e => e.Series) ?? new();
            var series = installed.Concat(bundled.Keys).Concat(list.Select(p => p.Series)).Distinct()
                .OrderByDescending(x => x, VersionUtil.Comparer).ToList();
            var active = _s.EffectiveActive(id);

            var rows = series.Select(sr =>
            {
                var onl = list.FirstOrDefault(p => p.Series == sr);
                // The version shown is the one Install will give you: what's installed, else the bundled copy, else the newest online.
                var ver = (installed.Contains(sr) ? _s.InstalledPkg(id, sr).Version : null)
                    ?? bundled.GetValueOrDefault(sr)?.Version
                    ?? onl?.Version ?? sr;
                var inUse = id == "php" && _s.Settings.Sites.Any(x => x.Php == sr);
                var have = installed.Contains(sr) ? _s.InstalledPkg(id, sr).Version : null;
                var update = have != null && onl != null && Stack.UpdatableIds.Contains(id) && VersionUtil.Compare(onl.Version, have) > 0 ? onl.Version : null;
                return new VersionDto(sr, ver, installed.Contains(sr), bundled.ContainsKey(sr), onl != null,
                    sr == active, installed.Contains(sr) && sr != active && !inUse, update);
            }).ToArray();

            result.Add(new ComponentDto(id, Catalog.Shapes[id].Name, Notes[id], !ok, rows));
        }
        return result.ToArray();
    }

    // ------------------------------------------------------------ operations
    Task Run(string title, Func<Task> work) => _s.RunExclusiveAsync(title, work);

    public Task StartAllAsync(IProgress<string> p, CancellationToken ct) => Run("Starting everything", () => _s.StartAllAsync(p, ct));
    public Task StopAllAsync(IProgress<string> p, CancellationToken ct) => Run("Stopping everything", () => _s.StopAllAsync());
    public Task StartServiceAsync(string id, IProgress<string> p, CancellationToken ct) => Run($"Starting {id}", () => _s.StartServiceAsync(id, p, ct));
    public Task StopServiceAsync(string id, IProgress<string> p, CancellationToken ct) => Run($"Stopping {id}", () => _s.StopServiceAsync(id));

    public Task AddSiteAsync(SiteRequest r, IProgress<string> p, CancellationToken ct) => Run("Adding site", async () =>
    {
        var root = (r.Root ?? "").Trim().Trim('"');
        // Blank or "/" means "serve the project folder itself" (Rhapsody). A folder name means exactly that folder. No guessing.
        await _s.AddSiteAsync(new Site { Name = (r.Name ?? "").Trim().ToLowerInvariant(), Root = root, PublicDir = r.PublicDir ?? "", Php = r.Php, Https = r.Https ?? true }, p, ct);
    });

    public Task SetSitePhpAsync(string name, string php, IProgress<string> p, CancellationToken ct) => Run($"Switching {name} to PHP {php}", async () =>
    {
        var site = _s.Settings.Sites.FirstOrDefault(x => x.Name == name) ?? throw new ArgumentException($"No site named '{name}'");
        await _s.AddSiteAsync(new Site { Name = site.Name, Root = site.Root, PublicDir = site.PublicDir, Https = site.Https, Php = php }, p, ct);
    });

    public Task RemoveSiteAsync(string name, IProgress<string> p, CancellationToken ct) => Run("Removing site", () => _s.RemoveSiteAsync(name, p, ct));

    public Task ScaffoldAsync(ScaffoldRequestDto r, IProgress<string> p, CancellationToken ct) => Run("Creating Rhapsody site", async () =>
        { await RhapsodyScaffold.RunAsync(_s, new ScaffoldRequest((r.Name ?? "").Trim().ToLowerInvariant(), r.Php,
            DbName: r.DbName, DbUser: r.DbUser, DbPass: r.DbPass), p, ct); });

    public Task SwitchWebServerAsync(string kind, IProgress<string> p, CancellationToken ct) => Run("Switching web server", () =>
        _s.SwitchWebServerAsync(Enum.Parse<WebServerKind>(kind, ignoreCase: true), p, ct));

    public Task InstallVersionAsync(string id, string series, IProgress<string> p, CancellationToken ct) => Run($"Installing {id} {series}", () => _s.InstallVersionAsync(id, series, p, ct));
    public Task SetActiveVersionAsync(string id, string series, IProgress<string> p, CancellationToken ct) => Run($"Switching {id} to {series}", () => _s.SetActiveAsync(id, series, p, ct));
    public Task RemoveVersionAsync(string id, string series, IProgress<string> p, CancellationToken ct) => Run($"Removing {id} {series}", () => _s.UninstallVersionAsync(id, series));
    public Task UpdateVersionAsync(string id, string series, IProgress<string> p, CancellationToken ct) => Run($"Updating {id} {series}", () => _s.UpdateVersionAsync(id, series, p, ct));
    public Task ImportApacheAsync(string fileName, Stream data, IProgress<string> p, CancellationToken ct) => Run("Installing Apache from a file", () => _s.ImportApacheAsync(fileName, data, p, ct));

    public Task GrantHostsAccessAsync(IProgress<string> p, CancellationToken ct) => Run("Granting hosts-file access", async () =>
    {
        p.Report("Waiting for Windows approval...");
        await HostsFile.GrantCurrentUserAccessAsync();
        await _s.ApplyAsync(p, ct);
    });

    // ------------------------------------------------------------------- AI helper
    public AiConfigDto GetAi() => _ai.Config();
    public void SaveAi(AiSaveInput input) => _ai.Save(input);
    public Task<AiTestResult> TestAiAsync(string which, CancellationToken ct) => _ai.TestAsync(which, ct);
    public Task<AiAnswerDto> ExplainAsync(ExplainRequest req, CancellationToken ct) => _ai.ExplainAsync(req, ct);
    public Task<AiAskDto> AskAiAsync(AiAskRequest req, IProgress<string>? progress, CancellationToken ct) => _ai.AskAsync(req, progress, ct);
    public void CancelAi() => _ai.Cancel();

    // ------------------------------------------------------- what the Ask assistant can see (IDevContext)
    // ReadLog and CheckSiteAsync above already have the right shape and serve both the dashboard and the assistant.
    IReadOnlyList<DevSite> IDevContext.Sites =>
        _s.Settings.Sites.Select(x => new DevSite(x.Name, _s.Domain(x.Name), x.Root, x.PublicDir, x.Php, x.Https)).ToList();

    IReadOnlyList<string> IDevContext.LogIds => GetState().Services.Select(x => x.Id).ToList();

    bool IDevContext.ProfilingEnabled => _s.Settings.ProfilingEnabled;

    Profiling.ProfileSnapshot IDevContext.Profile() => _s.Profile.Snapshot(_s.Settings.ProfilingEnabled, maxRequests: 500, maxQueries: 5000);

    /// <summary>
    /// A GET to this computer's own web server, with the site's name in the Host header (and as the TLS name for https sites).
    /// Redirects are not followed, so a 301 to the login page is reported as what it is. The body is cut to what a person would read.
    /// </summary>
    async Task<DevProbe> IDevContext.ProbeAsync(DevSite site, string pathAndQuery, CancellationToken ct)
    {
        var st = _s.Settings;
        var scheme = site.Https ? "https" : "http";
        var port = site.Https ? st.HttpsPort : st.HttpPort;
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5) };
        handler.SslOptions.TargetHost = site.Domain;   // the certificate is for the site's name, not 127.0.0.1
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var rq = new HttpRequestMessage(HttpMethod.Get, $"{scheme}://127.0.0.1:{port}{pathAndQuery}");
        rq.Headers.Host = site.Domain;
        rq.Headers.UserAgent.ParseAdd("RhapsodyServer-Assistant/1.0");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var rs = await http.SendAsync(rq, HttpCompletionOption.ResponseHeadersRead, ct);
            var type = rs.Content.Headers.ContentType?.MediaType ?? "";
            string body;
            if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || type.Contains("json") || type.Contains("xml") || type.Length == 0)
            {
                await using var stream = await rs.Content.ReadAsStreamAsync(ct);
                var buf = new byte[200_000];
                var n = 0; int r;
                while (n < buf.Length && (r = await stream.ReadAsync(buf.AsMemory(n, buf.Length - n), ct)) > 0) n += r;
                body = Encoding.UTF8.GetString(buf, 0, n);
                if (type.Contains("html", StringComparison.OrdinalIgnoreCase) || body.TrimStart().StartsWith('<'))
                    body = Regex.Replace(Regex.Replace(body, @"(?is)<(script|style).*?</\1>", " "), "<[^>]+>", " ");
                body = Regex.Replace(System.Net.WebUtility.HtmlDecode(body), @"\s+", " ").Trim();
                if (body.Length > 2500) body = body[..2500] + "…";
            }
            else body = $"({type}, {rs.Content.Headers.ContentLength?.ToString("N0") ?? "unknown"} bytes, not shown)";
            sw.Stop();

            var headers = new List<string>();
            foreach (var name in new[] { "Content-Type", "Location", "Server", "X-Powered-By", "Cache-Control" })
                if (rs.Headers.TryGetValues(name, out var hv) || rs.Content.Headers.TryGetValues(name, out hv)) headers.Add($"{name}: {string.Join(",", hv!)}");
            return new DevProbe((int)rs.StatusCode, rs.ReasonPhrase, sw.Elapsed.TotalMilliseconds, headers, body, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Security.Authentication.AuthenticationException)
        {
            if (ct.IsCancellationRequested) throw;
            var why = ex.InnerException is System.Security.Authentication.AuthenticationException || ex is System.Security.Authentication.AuthenticationException
                ? "the HTTPS certificate wasn't accepted (" + (ex.InnerException?.Message ?? ex.Message) + ")"
                : ex.Message;
            return new DevProbe(null, null, sw.Elapsed.TotalMilliseconds, Array.Empty<string>(), "", why);
        }
    }

    static readonly Regex PhpBranchRx = new(@"(?i)php[\s\-_\\/]*(\d\.\d)\b", RegexOptions.Compiled);

    /// <summary>
    /// A few lines about the setup, so an answer can say "your site uses PHP 8.1, which has no X" instead of guessing.
    /// Versions, ports, site names and which extensions are on; no folder paths, no credentials.
    /// </summary>
    string DescribeEnvironment(string aboutText)
    {
        var st = _s.Settings;
        var state = GetState();
        var sb = new StringBuilder();
        sb.AppendLine($"Web server selected: {st.WebServer}");
        sb.AppendLine("Services: " + string.Join("; ", state.Services.Select(x => $"{x.Name} {x.State}")));

        var installed = _s.InstalledPhpBranches();
        sb.AppendLine($"Default PHP: {st.DefaultPhp}. Installed PHP versions: {(installed.Count > 0 ? string.Join(", ", installed) : "none yet")}");
        if (st.Sites.Count > 0)
            sb.AppendLine("Sites: " + string.Join(", ", st.Sites.Select(x =>
                $"{_s.Domain(x.Name)} (PHP {x.Php}, {(string.IsNullOrEmpty(x.PublicDir) ? "serves the project folder" : "serves /" + x.PublicDir)})")));
        else sb.AppendLine("Sites: none yet");
        sb.AppendLine($"Ports: http {st.HttpPort}, https {st.HttpsPort}, MariaDB {st.MariaDbPort}, Redis {st.RedisPort}, SMTP {st.SmtpPort}. Profiler {(st.ProfilingEnabled ? "on" : "off")}. Hosts file {(state.HostsWritable ? "writable" : "not writable by this app")}.");

        var mentioned = PhpBranchRx.Matches(aboutText ?? "").Select(m => m.Groups[1].Value).Distinct().Where(installed.Contains).Take(2).ToList();
        if (mentioned.Count == 0 && installed.Contains(st.DefaultPhp)) mentioned.Add(st.DefaultPhp);
        foreach (var b in mentioned)
        {
            try { sb.AppendLine($"PHP {b} extensions on: {string.Join(", ", _s.PhpExtensionList(b).Where(x => x.Enabled).Select(x => x.Name))}"); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { /* not readable right now: leave it out */ }
        }
        if (Regex.IsMatch(aboutText ?? "", @"(?i)apache|httpd|\bAH\d{4,5}\b") && _s.ApacheModuleList() is { } mods)
            sb.AppendLine("Apache modules on: " + string.Join(", ", mods.Where(x => x.Enabled).Select(x => x.Name)));
        return sb.ToString().TrimEnd();
    }
}
