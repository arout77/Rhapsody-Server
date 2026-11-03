using System.Text;
using System.Text.RegularExpressions;
using RhapsodyServer.Core.Ai;

namespace RhapsodyServer.Core.Web;

public sealed class StackBackend : IDashboardBackend
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
        _ai = new AiHelper(ai, DescribeEnvironment, ReadLog, () => new[] { _s.Settings.UiToken, _s.Settings.PmaSecret });
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
        return new PhpModulesDto(installed, pick, Dto(_s.PhpExtensionList(pick)), running);
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
    public string ReadLog(string id, int lines)
    {
        if (!Regex.IsMatch(id, @"^[a-z0-9.\-]+$")) throw new ArgumentException("Bad log name");
        var path = Path.Combine(_s.Paths.Logs, id + ".log");
        if (!File.Exists(path)) return "(nothing logged yet)";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int Tail = 256 * 1024;
        if (fs.Length > Tail) fs.Seek(-Tail, SeekOrigin.End);
        using var sr = new StreamReader(fs);
        var all = sr.ReadToEnd().Replace("\r\n", "\n").Split('\n');
        return string.Join('\n', all.TakeLast(Math.Clamp(lines, 1, 2000)));
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
