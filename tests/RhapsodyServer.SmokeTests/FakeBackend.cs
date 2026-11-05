using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using RhapsodyServer.Core;
using RhapsodyServer.Core.Ai;
using RhapsodyServer.Core.Web;

/// <summary>Not encryption: just enough for tests to prove the key store never writes a key in the clear.</summary>
sealed class XorProtector : ISecretProtector
{
    public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).Reverse().ToArray();
    public byte[] Unprotect(byte[] protectedBytes) => protectedBytes.Reverse().Select(b => (byte)(b ^ 0x5A)).ToArray();
}

/// <summary>Stands in for the internet: records what an AI provider would have received and answers from a script.</summary>
sealed class StubAiHandler : HttpMessageHandler
{
    public readonly List<(string Url, string? Auth, string Body)> Requests = new();
    public Func<int, HttpResponseMessage>? Script;   // call number (1-based) -> response; null = a normal answer
    public int DelayMs;
    public string Answer = "The `opcache` extension isn't loaded for PHP 8.5.\n\n- Open **Modules** and check it is on.";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        int n;
        lock (Requests) { Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body)); n = Requests.Count; }
        if (DelayMs > 0) await Task.Delay(DelayMs, ct);
        if (Script != null) return Script(n);
        var json = System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = Answer }, finish_reason = "stop" } } });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

/// <summary>In-memory stand-in for the real stack: realistic data, small delays, progress messages.</summary>
sealed class FakeBackend : IDashboardBackend
{
    public event Action? Changed;
    public event Action? ProfileChanged;
    public bool Profiling;
    public readonly RhapsodyServer.Core.Profiling.ProfileStore Store = new();
    public readonly StubAiHandler AiNet = new();
    public readonly AiService AiSvc;
    readonly AiHelper _ai;
    public readonly string AiDir = Path.Combine(Path.GetTempPath(), "rhapsody-server-ai-" + Guid.NewGuid().ToString("N")[..8]);
    public readonly AiSettings AiSettingsState = new();
    public FakeBackend()
    {
        Store.Changed += () => ProfileChanged?.Invoke();
        Directory.CreateDirectory(AiDir);
        AiSvc = new AiService(AiSettingsState, () => { }, new AiKeyStore(Path.Combine(AiDir, "ai-keys.json"), new XorProtector()),
            null, new AiClient(new HttpClient(AiNet)));
        _ai = new AiHelper(AiSvc, text => "Web server selected: " + _web + "\nDefault PHP: " + _active["php"], ReadLog, () => new[] { "fake-secret-token-123" });
    }

    public AiConfigDto GetAi() => _ai.Config();
    public void SaveAi(AiSaveInput input) => _ai.Save(input);
    public Task<AiTestResult> TestAiAsync(string which, CancellationToken ct) => _ai.TestAsync(which, ct);
    public Task<AiAnswerDto> ExplainAsync(ExplainRequest req, CancellationToken ct) => _ai.ExplainAsync(req, ct);
    public int StepMs = 120;
    public bool HostsWritable;

    sealed class Row { public string Series = "", Version = ""; public bool Installed, Bundled, Available; }
    readonly object _g = new();
    readonly Dictionary<string, string> _st = new() { ["nginx"] = "stopped", ["mariadb"] = "stopped", ["redis"] = "stopped", ["mailpit"] = "stopped" };
    readonly List<SiteDto> _sites = new()
    {
        new("rhapsody", "rhapsody.test", "https://rhapsody.test", @"C:\Users\andrew\Sites\rhapsody", "", "8.3", true),
        new("iwf", "iwf.test", "https://iwf.test", @"C:\Users\andrew\Sites\iwf", "public", "8.1", true),
    };
    string _web = "Nginx";
    readonly Dictionary<string, string> _active = new() { ["php"] = "8.5", ["apache"] = "2.4", ["nginx"] = "1.28", ["mariadb"] = "11.8" };
    readonly Dictionary<string, List<Row>> _v = new()
    {
        ["php"] = new() {
            new() { Series = "8.5", Version = "8.5.1", Installed = true, Bundled = true, Available = true },
            new() { Series = "8.4", Version = "8.4.14", Available = true },
            new() { Series = "8.3", Version = "8.3.27", Installed = true, Available = true },
            new() { Series = "8.1", Version = "8.1.34", Installed = true, Available = true },
            new() { Series = "8.0", Version = "8.0.30", Available = true },
            new() { Series = "7.4", Version = "7.4.33", Available = true },
            new() { Series = "7.1", Version = "7.1.33", Available = true } },
        ["apache"] = new() { new() { Series = "2.4", Version = "2.4.63", Installed = true, Bundled = true, Available = true } },
        ["nginx"] = new() {
            new() { Series = "1.29", Version = "1.29.1", Available = true },
            new() { Series = "1.28", Version = "1.28.0", Installed = true, Bundled = true, Available = true } },
        ["mariadb"] = new() {
            new() { Series = "11.8", Version = "11.8.2", Installed = true, Bundled = true, Available = true },
            new() { Series = "11.4", Version = "11.4.5", Available = true },
            new() { Series = "10.11", Version = "10.11.10", Available = true } },
    };

    string Web => _web == "Nginx" ? "nginx" : "apache";
    void Fire() => Changed?.Invoke();
    async Task Step(IProgress<string> p, string msg) { p.Report(msg); await Task.Delay(StepMs); }
    void Set(string id, string state) { lock (_g) _st[id == "apache" ? "nginx" : id] = state; Fire(); }

    /// <summary>Realistic-looking recent traffic, for the --demo dashboard.</summary>
    public void SeedDemo()
    {
        Profiling = true;
        var now = DateTimeOffset.UtcNow;
        (string m, string host, string uri, int status, double ms, double up, (string sql, double t, string? err)[] qs)[] reqs =
        {
            ("GET", "iwf.test", "/rankings", 200, 412, 396, new[] {
                ("SELECT w.*, COUNT(m.id) AS matches FROM wrestlers w LEFT JOIN matches m ON m.winner_id = w.id GROUP BY w.id ORDER BY matches DESC", 287.4, (string?)null),
                ("SELECT * FROM titles WHERE active = 1", 3.1, null) }),
            ("GET", "iwf.test", "/wrestlers/12", 200, 64, 58, new[] {
                ("SELECT * FROM wrestlers WHERE id = ?", 1.2, (string?)null),
                ("SELECT * FROM matches WHERE winner_id = ? OR loser_id = ? ORDER BY date DESC LIMIT 10", 9.8, null) }),
            ("POST", "bible.test", "/api/bookmarks", 500, 120, 118, new[] {
                ("INSERT INTO bookmarks (user_id, verse_id) VALUES (?, ?)", 4.0, (string?)"#1054 Unknown column 'verse_id' in 'field list'") }),
            ("GET", "rhapsody.test", "/", 200, 18, 15, Array.Empty<(string, double, string?)>()),
            ("GET", "bible.test", "/read/john/3", 200, 640, 622, new[] {
                ("SELECT * FROM verses WHERE book = ? AND chapter = ? ORDER BY verse", 612.9, (string?)null) }),
        };
        var t = now.AddSeconds(-30);
        foreach (var r in reqs)
        {
            Store.Add(new RhapsodyServer.Core.Profiling.RequestRecord { StartedAt = t, DurationMs = r.ms, Method = r.m, Host = r.host, Uri = r.uri, Status = r.status, UpstreamMs = r.up });
            var qt = t.AddMilliseconds(8);
            foreach (var q in r.qs)
            {
                Store.Add(new RhapsodyServer.Core.Profiling.QueryRecord { StartedAt = qt, Sql = q.sql, FirstByteMs = q.t, TotalMs = q.t, Error = q.err, Process = "php-cgi", Pid = 6120, Prepared = q.sql.Contains('?') });
                qt = qt.AddMilliseconds(q.t + 1);
            }
            t = t.AddSeconds(5);
        }
    }

    public void RaiseProfileChanged() => ProfileChanged?.Invoke();
    public RhapsodyServer.Core.Profiling.ProfileSnapshot GetProfile() => Store.Snapshot(Profiling);
    public void ClearProfile() { Store.Clear(); ProfileChanged?.Invoke(); }
    public async Task SetProfilingAsync(bool on, IProgress<string> p, CancellationToken ct)
    {
        await Step(p, on ? "Restarting services with profiling on" : "Restarting services with profiling off");
        Profiling = on;
        ProfileChanged?.Invoke();
        Fire();
    }

    // ---------------------------------------------------------- folder picker
    public bool CanPickFolder { get; set; } = true;
    public string? NextPick = @"C:\Users\andrew\Sites\My Blog";
    public string? PickedFrom;
    public int PickDelayMs;
    public async Task<string?> PickFolderAsync(string? initial, CancellationToken ct)
    {
        PickedFrom = initial;
        if (PickDelayMs > 0) await Task.Delay(PickDelayMs, ct);
        return NextPick;
    }

    // ------------------------------------------------------- modules / extensions
    static readonly string[] FakeExt = { "bz2", "curl", "exif", "fileinfo", "gd", "gettext", "gmp", "intl", "ldap", "mbstring", "mysqli", "opcache",
        "openssl", "pdo_mysql", "pdo_pgsql", "pdo_sqlite", "pgsql", "soap", "sockets", "sodium", "sqlite3", "zip" };
    public readonly Dictionary<string, HashSet<string>> ExtOn = new();
    HashSet<string> Ext(string b) { lock (_g) { if (!ExtOn.TryGetValue(b, out var h)) ExtOn[b] = h = new(PhpExtensions.Defaults(b).Where(FakeExt.Contains)); return h; } }

    public PhpModulesDto GetPhpModules(string? branch)
    {
        string[] installed; lock (_g) installed = _v["php"].Where(r => r.Installed).Select(r => r.Series).ToArray();
        var pick = branch != null && installed.Contains(branch) ? branch : installed.Contains(_active["php"]) ? _active["php"] : installed.FirstOrDefault();
        if (pick == null) return new PhpModulesDto(installed, null, Array.Empty<ModuleDto>(), false);
        var on = Ext(pick);
        return new PhpModulesDto(installed, pick, FakeExt.Select(n => new ModuleDto(n, PhpExtensions.Describe(n), on.Contains(n),
            PhpExtensions.Essential.Contains(n), PhpExtensions.Essential.Contains(n) ? PhpExtensions.EssentialReason : null)).ToArray(), _st["nginx"] == "running",
            $@"C:\Users\andrew\AppData\Local\RhapsodyServer\etc\php\{pick}\php.ini");
    }

    public async Task SetPhpExtensionsAsync(string branch, string[] enabled, IProgress<string> p, CancellationToken ct)
    {
        var bad = enabled.Where(x => !FakeExt.Contains(x)).ToArray();
        if (bad.Length > 0) throw new ArgumentException($"PHP {branch} has no extension named {string.Join(", ", bad)}.");
        await Step(p, $"Restarting PHP {branch}...");
        lock (_g) ExtOn[branch] = new HashSet<string>(enabled.Concat(PhpExtensions.Essential));
        Fire();
    }

    static readonly string[] FakeApache = { "alias", "authz_core", "authz_host", "cache", "cache_disk", "deflate", "dir", "expires", "headers", "log_config",
        "mime", "proxy", "proxy_fcgi", "proxy_http", "proxy_wstunnel", "rewrite", "setenvif", "socache_shmcb", "ssl" };
    public readonly HashSet<string> ApacheOn = new(ApacheModuleCatalog.Defaults);

    public ApacheModulesDto GetApacheModules()
    {
        lock (_g)
            return new ApacheModulesDto(true, "2.4.63", _web == "Apache", _web == "Apache" && _st["nginx"] == "running",
                FakeApache.Select(n => { var req = ApacheModuleCatalog.Required.Contains(n);
                    return new ModuleDto(n, ApacheModuleCatalog.Describe(n), req || ApacheOn.Contains(n), req, req ? ApacheModuleCatalog.RequiredReason : null); }).ToArray());
    }

    public async Task SetApacheModulesAsync(string[] enabled, IProgress<string> p, CancellationToken ct)
    {
        var bad = enabled.Where(x => !FakeApache.Contains(x)).ToArray();
        if (bad.Length > 0) throw new ArgumentException($"This Apache has no module named {string.Join(", ", bad)}.");
        await Step(p, "Restarting Apache...");
        lock (_g) { ApacheOn.Clear(); foreach (var m in ApacheModuleCatalog.Optional(enabled, FakeApache)) ApacheOn.Add(m); }
        Fire();
    }

    public DashboardState GetState()
    {
        lock (_g)
        {
            var web = _st["nginx"];
            var svcs = new List<ServiceDto>
            {
                new(Web, _web == "Nginx" ? "nginx 1.28" : "Apache 2.4", web, 80, true, "http :80, https :443"),
                new("mariadb", "MariaDB 11.8", _st["mariadb"], 3306, true, "127.0.0.1:3306, user root, no password"),
                new("redis", "Redis", _st["redis"], 6379, true, "127.0.0.1:6379"),
                new("mailpit", "Mailpit", _st["mailpit"], 8025, true, "SMTP 127.0.0.1:1025, inbox at https://mail.test"),
            };
            foreach (var b in _sites.Select(s => s.Php).Append(_active["php"]).Distinct().OrderByDescending(x => x))
                { var n = _sites.Count(s => s.Php == b); svcs.Add(new($"php-{b}", $"PHP {b}", web, null, false, $"2 workers, starts with the web server" + (n > 0 ? $", {n} site{(n == 1 ? "" : "s")}" : ""))); }
            return new DashboardState(svcs.ToArray(), _sites.ToArray(),
                new SettingsDto("test", _web, _active["php"], new[] { "7.1", "7.4", "8.0", "8.1", "8.3", "8.4", "8.5" },
                    80, 443, 3306, 6379, 1025, 8025, @"C:\Users\andrew\Sites", @"C:\Users\andrew\AppData\Local\RhapsodyServer", @"C:\Users\andrew\AppData\Local\RhapsodyServer\logs"),
                HostsWritable, true, CanPickFolder, true);
        }
    }

    /// <summary>Folders the page asked to open, in order. Paths come from here, so a test can prove the request never chooses one.</summary>
    public readonly List<string> Opened = new();
    public Task<string> CheckSiteAsync(string name, CancellationToken ct) =>
        Task.FromResult(name == "gone" ? throw new ArgumentException($"There is no site called \"{name}\".") : $"Site: {name}.test\nRequest answered: 200 OK");

    /// <summary>php.ini requests as "branch:action", in order.</summary>
    public readonly List<string> IniRequests = new();
    public readonly List<string> Restarted = new();
    public void OpenPhpIni(PhpIniRequest req)
    {
        if ((req.Action ?? "") is not ("edit" or "reveal")) throw new ArgumentException("Unknown action.");
        string[] installed; lock (_g) installed = _v["php"].Where(r => r.Installed).Select(r => r.Series).ToArray();
        var b = req.Branch ?? _active["php"];
        if (!installed.Contains(b)) throw new ArgumentException($"PHP {b} isn't installed.");
        lock (_g) IniRequests.Add($"{b}:{req.Action}");
    }

    public async Task RestartPhpAsync(string branch, IProgress<string> p, CancellationToken ct)
    {
        await Step(p, $"Restarting PHP {branch}...");
        lock (_g) Restarted.Add(branch);
    }

    public void OpenFolder(OpenFolderRequest req)
    {
        string path;
        lock (_g)
            path = (req.Target ?? "").ToLowerInvariant() switch
            {
                "sites" => @"C:\Users\andrew\Sites",
                "data" => @"C:\Users\andrew\AppData\Local\RhapsodyServer",
                "logs" => @"C:\Users\andrew\AppData\Local\RhapsodyServer\logs",
                "site" => _sites.FirstOrDefault(x => x.Name.Equals(req.Name ?? "", StringComparison.OrdinalIgnoreCase))?.Root
                          ?? throw new ArgumentException($"There is no site called \"{req.Name}\"."),
                _ => throw new ArgumentException("Unknown folder.")
            };
        lock (Opened) Opened.Add(path);
    }

    public string ReadLog(string id, int lines) =>
        $"[{id}] 2026-11-01 09:14:02 ready to accept connections\n[{id}] 2026-11-01 09:14:09 request GET /\n[{id}] 2026-11-01 09:14:09 request GET /assets/app.css";

    public async Task<ComponentDto[]> GetVersionsAsync(CancellationToken ct)
    {
        await Task.Delay(150, ct);
        lock (_g)
            return _v.Select(kv => new ComponentDto(kv.Key,
                kv.Key switch { "php" => "PHP", "apache" => "Apache", "nginx" => "nginx", _ => "MariaDB" },
                kv.Key == "php" ? "The default is used for new sites, phpMyAdmin and the mail inbox. Each site can pick its own."
                    : kv.Key == "mariadb" ? "Each version keeps its own databases, so switching never touches your data." : "Used when it is your web server.",
                false,
                kv.Value.Select(r => new VersionDto(r.Series, r.Version, r.Installed, r.Bundled, r.Available, _active[kv.Key] == r.Series,
                    r.Installed && _active[kv.Key] != r.Series && !(kv.Key == "php" && _sites.Any(s => s.Php == r.Series)),
                    r.Installed && Stack.UpdatableIds.Contains(kv.Key) && r.Version == "2.4.63" ? "2.4.66" : null)).ToArray())).ToArray();
    }

    public async Task StartAllAsync(IProgress<string> p, CancellationToken ct)
    { foreach (var id in new[] { "mariadb", "redis", "mailpit", Web }) await StartServiceAsync(id, p, ct); }

    public async Task StopAllAsync(IProgress<string> p, CancellationToken ct)
    { foreach (var id in new[] { Web, "mailpit", "redis", "mariadb" }) await StopServiceAsync(id, p, ct); }

    public async Task StartServiceAsync(string id, IProgress<string> p, CancellationToken ct)
    { Set(id, "starting"); await Step(p, $"Starting {id}..."); Set(id, "running"); }

    public async Task StopServiceAsync(string id, IProgress<string> p, CancellationToken ct)
    { await Step(p, $"Stopping {id}..."); Set(id, "stopped"); }

    public async Task AddSiteAsync(SiteRequest r, IProgress<string> p, CancellationToken ct)
    {
        if (!Regex.IsMatch(r.Name ?? "", "^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")) throw new ArgumentException("Site name: lowercase letters, digits and hyphens only (e.g. 'my-app').");
        if (string.IsNullOrWhiteSpace(r.Root)) throw new ArgumentException("Choose a project folder.");
        await Step(p, "Writing config...");
        lock (_g) _sites.Add(new(r.Name!, $"{r.Name}.test", $"https://{r.Name}.test", r.Root, r.PublicDir ?? "", r.Php, true));
        Fire();
    }

    public async Task SetSitePhpAsync(string name, string php, IProgress<string> p, CancellationToken ct)
    {
        await Step(p, $"Reloading {Web}...");
        lock (_g) { var i = _sites.FindIndex(s => s.Name == name); _sites[i] = _sites[i] with { Php = php }; }
        Fire();
    }

    public async Task RemoveSiteAsync(string name, IProgress<string> p, CancellationToken ct)
    { await Step(p, "Updating config..."); lock (_g) _sites.RemoveAll(s => s.Name == name); Fire(); }

    public ScaffoldRequestDto? LastScaffold;
    public async Task ScaffoldAsync(ScaffoldRequestDto r, IProgress<string> p, CancellationToken ct)
    {
        LastScaffold = r; var name = r.Name; var php = r.Php;
        await Step(p, "Creating project from arout/rhapsody-app...");
        if (name == "fail") throw new InvalidOperationException("composer create-project failed: package arout/rhapsody-app was not found");
        await Step(p, $"Creating database {name.Replace('-', '_')}...");
        await Step(p, "Registering site...");
        lock (_g) _sites.Add(new(name, $"{name}.test", $"https://{name}.test", $@"C:\Users\andrew\Sites\{name}", "", php, true));
        Fire();
    }

    public async Task SwitchWebServerAsync(string kind, IProgress<string> p, CancellationToken ct)
    { await Step(p, "Switching..."); lock (_g) _web = kind; Fire(); }

    Row Find(string id, string series) => _v[id].First(r => r.Series == series);

    public async Task InstallVersionAsync(string id, string series, IProgress<string> p, CancellationToken ct)
    {
        foreach (var pct in new[] { 20, 60, 100 }) await Step(p, $"Downloading {id} {series}: {pct}%");
        lock (_g) Find(id, series).Installed = true;
        Fire();
    }

    public async Task SetActiveVersionAsync(string id, string series, IProgress<string> p, CancellationToken ct)
    { await Step(p, "Switching..."); lock (_g) _active[id] = series; Fire(); }

    public async Task RemoveVersionAsync(string id, string series, IProgress<string> p, CancellationToken ct)
    { await Step(p, "Removing..."); lock (_g) Find(id, series).Installed = false; Fire(); }

    public string? LastImportName; public long LastImportBytes;
    public async Task UpdateVersionAsync(string id, string series, IProgress<string> p, CancellationToken ct)
    {
        if (!Stack.UpdatableIds.Contains(id)) throw new ArgumentException($"{id} can't be updated in place.");
        foreach (var pct in new[] { 30, 100 }) await Step(p, $"Downloading {id} {series}: {pct}%");
        lock (_g) { var r = Find(id, series); r.Version = r.Version == "2.4.63" ? "2.4.66" : r.Version; }
        Fire();
    }

    public async Task ImportApacheAsync(string fileName, Stream data, IProgress<string> p, CancellationToken ct)
    {
        var ver = ApacheResolver.VersionFromArchiveName(fileName) ?? throw new ArgumentException("That doesn't look like an Apache Lounge download.");
        using var ms = new MemoryStream(); await data.CopyToAsync(ms, ct);
        LastImportName = fileName; LastImportBytes = ms.Length;
        await Step(p, $"Installing Apache {ver}...");
        lock (_g) { var r = Find("apache", VersionUtil.Series(ver)); r.Version = ver; r.Installed = true; }
        Fire();
    }

    public async Task GrantHostsAccessAsync(IProgress<string> p, CancellationToken ct)
    { await Step(p, "Waiting for Windows approval..."); HostsWritable = true; Fire(); }
}
