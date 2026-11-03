using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RhapsodyServer.Core;

/// <summary>
/// The whole local stack. Design rules that keep it fast:
///  - nothing starts until asked (PHP workers only for versions that sites actually use)
///  - independent services install/start in parallel; the web server goes last
///  - status is event-driven; there are no background timers
///  - config files are regenerated from settings.json, never parsed back
///  - components come from the bundle shipped with the app first, the network second
/// </summary>
public sealed class Stack : IDisposable
{
    public static readonly Regex NameRx = new("^[a-z0-9]([a-z0-9-]{0,40}[a-z0-9])?$");
    static readonly string[] Reserved = { "phpmyadmin", "mail", "localhost" };
    public static readonly string[] CoreServiceIds = { "mariadb", "redis", "mailpit" };

    readonly object _saveLock = new();
    readonly SemaphoreSlim _opGate = new(1, 1);

    public ServerPaths Paths { get; }
    public Settings Settings { get; }
    public ProcessSupervisor Supervisor { get; } = new();
    public Installer Installer { get; }

    /// <summary>Recent requests and SQL queries seen while profiling is on.</summary>
    public Profiling.ProfileStore Profile { get; } = new();
    Profiling.MySqlProxy? _dbProxy;
    Profiling.AccessLogTailer? _tailer;

    /// <summary>
    /// The port MariaDB itself listens on. With profiling on it sits behind the recording proxy,
    /// which owns the public <see cref="Settings.MariaDbPort"/>.
    /// </summary>
    public int DbBackendPort => Settings.ProfilingEnabled ? Settings.MariaDbInternalPort : Settings.MariaDbPort;
    public string ProfileLogPath => Path.Combine(Paths.Etc, WebServerId, "logs", "profile.tsv");

    /// <summary>Title of the long-running operation in progress (start, install, scaffold...), if any.</summary>
    public string? CurrentOperation { get; private set; }
    public event Action? OperationChanged;

    public Stack(ServerPaths? paths = null, Installer? installer = null)
    {
        Paths = paths ?? new ServerPaths();
        Paths.EnsureCreated();
        Settings = SettingsStore.Load(Paths.SettingsFile);
        Installer = installer ?? new Installer(Paths);
        Installer.Bundle ??= BundleSource.Locate();

        if (string.IsNullOrEmpty(Settings.UiToken))
        {
            Settings.UiToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            Save();
        }
    }

    public void Save() { lock (_saveLock) SettingsStore.Save(Paths.SettingsFile, Settings); }

    /// <summary>One long operation at a time, shared by the tray and the dashboard so they can't trip over each other.</summary>
    public async Task RunExclusiveAsync(string title, Func<Task> work)
    {
        if (!await _opGate.WaitAsync(0))
            throw new InvalidOperationException($"Rhapsody Server is busy ({CurrentOperation}). Try again in a moment.");
        CurrentOperation = title;
        OperationChanged?.Invoke();
        try { await work(); }
        finally { CurrentOperation = null; _opGate.Release(); OperationChanged?.Invoke(); }
    }

    // ------------------------------------------------------------ naming / paths
    public string WebServerId => Settings.WebServer == WebServerKind.Nginx ? "nginx" : "apache";
    public string Domain(string name) => $"{name}.{Settings.Tld}";
    string LogOf(string id) => Path.Combine(Paths.Logs, id + ".log");
    string CertOf(string domain) => Path.Combine(Paths.Certs, domain + ".pem");
    string KeyOf(string domain) => Path.Combine(Paths.Certs, domain + "-key.pem");

    public IEnumerable<string> BranchesInUse =>
        Settings.Sites.Select(s => s.Php).Append(Settings.DefaultPhp).Distinct();

    public string PhpDir(string branch) => Paths.ComponentDir("php", branch);
    public string PhpExe(string branch) => Path.Combine(PhpDir(branch), "php.exe");
    public string MariaDbCli() => Path.Combine(Installer.DirOf(ActivePkg("mariadb")), "bin", "mariadb.exe");

    public IReadOnlyDictionary<string, string> PhpEnv(string branch, bool forComposer = false)
    {
        var env = new Dictionary<string, string>
        {
            ["PHPRC"] = Path.Combine(Paths.Etc, "php", branch),
            ["PHP_FCGI_MAX_REQUESTS"] = "0"   // php-cgi's default of 500 makes the worker quit and look like a crash
        };
        if (forComposer)
        {
            env["COMPOSER_HOME"] = Path.Combine(Paths.Data, "composer");
            env["COMPOSER_NO_INTERACTION"] = "1";
        }
        return env;
    }

    public IReadOnlyList<SiteSpec> BuildSiteSpecs()
    {
        var list = new List<SiteSpec>();
        foreach (var s in Settings.Sites)
        {
            var d = Domain(s.Name);
            var doc = string.IsNullOrEmpty(s.PublicDir) ? s.Root : Path.Combine(s.Root, s.PublicDir);
            list.Add(new SiteSpec(d, doc, null, s.Php, s.Https, CertOf(d), KeyOf(d)));
        }
        var pma = Domain("phpmyadmin");
        list.Add(new SiteSpec(pma, Installer.DirOf(Catalog.PhpMyAdmin), null, Settings.DefaultPhp, true, CertOf(pma), KeyOf(pma)));
        var mail = Domain("mail");
        list.Add(new SiteSpec(mail, null, $"127.0.0.1:{Settings.MailUiPort}", Settings.DefaultPhp, true, CertOf(mail), KeyOf(mail)));
        return list;
    }

    public IEnumerable<string> AllDomains() => BuildSiteSpecs().Select(s => s.Domain);

    // --------------------------------------------------------- versioned packages
    public IReadOnlyList<string> InstalledSeries(string id)
    {
        var root = Path.Combine(Paths.Bin, id);
        if (!Directory.Exists(root)) return Array.Empty<string>();
        var exe = Catalog.Shapes[id].Exe;
        return Directory.GetDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, exe)))
            .Select(d => Path.GetFileName(d)!)
            .OrderByDescending(x => x, VersionUtil.Comparer)
            .ToList();
    }

    public bool IsSeriesInstalled(string id, string series) =>
        File.Exists(Path.Combine(Paths.ComponentDir(id, series), Catalog.Shapes[id].Exe));

    /// <summary>The series that will actually run: the chosen one if installed, otherwise the newest installed. Null if none.</summary>
    public string? EffectiveActive(string id)
    {
        if (id == "php") return IsSeriesInstalled("php", Settings.DefaultPhp) ? Settings.DefaultPhp : InstalledSeries("php").FirstOrDefault();
        if (Settings.Active.TryGetValue(id, out var s) && IsSeriesInstalled(id, s)) return s;
        return InstalledSeries(id).FirstOrDefault();
    }

    public Package InstalledPkg(string id, string series)
    {
        var sh = Catalog.Shapes[id];
        var ver = Settings.Versions.GetValueOrDefault($"{id}-{series}") ?? series;
        return new Package(id, sh.Name, series, ver, "", PackageKind.Zip, sh.Exe, null, sh.InnerRoot);
    }

    public Package ActivePkg(string id)
    {
        var series = EffectiveActive(id)
            ?? throw new InvalidOperationException($"{Catalog.Shapes[id].Name} is not installed yet.");
        return InstalledPkg(id, series);
    }

    public async Task<IReadOnlyList<Package>> AvailableAsync(string id, CancellationToken ct = default) =>
        await PackageSources.ListAsync(Installer.Http, id, ct);

    /// <summary>Bundle first (works offline), then the newest the network offers.</summary>
    async Task<Package> ResolveLatestAsync(string id, CancellationToken ct)
    {
        var bundled = Installer.Bundle?.Entries.Where(e => e.Id == id)
            .OrderByDescending(e => e.Series, VersionUtil.Comparer).FirstOrDefault();
        if (id == "nginx" && Installer.Bundle != null)
            bundled = Installer.Bundle.Entries.Where(e => e.Id == id && NginxResolver.IsStable(e.Series))
                .OrderByDescending(e => e.Series, VersionUtil.Comparer).FirstOrDefault();
        if (bundled != null) return Installer.Bundle!.ToPackage(bundled);

        var list = await AvailableAsync(id, ct);
        return PackageSources.Latest(id, list)
            ?? throw new InvalidOperationException($"No {Catalog.Shapes[id].Name} download was found.");
    }

    /// <param name="preferBundle">true: use the copy that ships with the app when there is one (no download). false: ask the vendor for the newest first.</param>
    async Task<Package> ResolveSeriesAsync(string id, string series, bool preferBundle, CancellationToken ct)
    {
        var bundled = Installer.Bundle?.Find(id, series);
        if (preferBundle && bundled != null) return Installer.Bundle!.ToPackage(bundled);
        try
        {
            var online = (await AvailableAsync(id, ct)).FirstOrDefault(p => p.Series == series);
            if (online != null) return online;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && bundled != null) { /* offline: fall back */ }
        if (bundled != null) return Installer.Bundle!.ToPackage(bundled);
        throw new InvalidOperationException($"{Catalog.Shapes[id].Name} {series} isn't bundled and wasn't found online.");
    }

    async Task InstallPackageAsync(Package pkg, IProgress<string>? p, CancellationToken ct)
    {
        await Installer.InstallAsync(pkg, p, ct);
        Settings.Versions[$"{pkg.Id}-{pkg.Series}"] = pkg.Version;
        Save();
    }

    /// <summary>Makes sure some version of apache/nginx/mariadb is installed and active, installing the bundled/latest one if not.</summary>
    public async Task<Package> EnsureActiveAsync(string id, IProgress<string>? p = null, CancellationToken ct = default)
    {
        if (EffectiveActive(id) == null)
        {
            var pkg = await ResolveLatestAsync(id, ct);
            await InstallPackageAsync(pkg, p, ct);
        }
        var series = EffectiveActive(id)!;
        if (id != "php" && Settings.Active.GetValueOrDefault(id) != series) { Settings.Active[id] = series; Save(); }
        return ActivePkg(id);
    }

    public async Task EnsurePhpAsync(string branch, IProgress<string>? p = null, CancellationToken ct = default)
    {
        if (!PhpResolver.SupportedBranches.Contains(branch))
            throw new ArgumentException($"Unsupported PHP version '{branch}'");
        if (!IsSeriesInstalled("php", branch))
        {
            p?.Report($"Preparing PHP {branch}...");
            await InstallPackageAsync(await ResolveSeriesAsync("php", branch, preferBundle: true, ct), p, ct);
        }
        WritePhpIni(branch);
    }

    void WritePhpIni(string branch)
    {
        var etc = Path.Combine(Paths.Etc, "php", branch);
        var tmp = Path.Combine(Paths.Data, "php-tmp");
        Directory.CreateDirectory(etc);
        Directory.CreateDirectory(Path.Combine(tmp, "sessions"));
        File.WriteAllText(Path.Combine(etc, "php.ini"),
            ConfigWriter.PhpIni(branch, PhpDir(branch), tmp, Settings, PhpIniOptionsFor(branch)));
    }

    /// <summary>
    /// What php.ini should say, decided from the files that are really on disk: only extensions this PHP has,
    /// and OPcache as a module only if its DLL exists (PHP 8.5 has it built in, so there is nothing to load).
    /// </summary>
    PhpIniOptions PhpIniOptionsFor(string branch)
    {
        var available = PhpExtensions.Discover(PhpDir(branch), branch);
        var on = PhpExtensions.Effective(Settings.PhpExtensions.GetValueOrDefault(branch), branch, available);
        var opcacheOn = on.Contains("opcache");
        var dll = File.Exists(Path.Combine(PhpDir(branch), "ext", "php_opcache.dll"));
        return new PhpIniOptions(on, LoadOpcacheModule: dll && opcacheOn, OpcacheEnabled: opcacheOn);
    }

    // ------------------------------------------------------------ modules / extensions
    public IReadOnlyList<string> InstalledPhpBranches() => InstalledSeries("php");

    /// <summary>The extensions of one installed PHP, with their on/off state.</summary>
    public IReadOnlyList<ModuleInfo> PhpExtensionList(string branch)
    {
        if (!IsSeriesInstalled("php", branch)) throw new InvalidOperationException($"PHP {branch} isn't installed.");
        var available = PhpExtensions.Discover(PhpDir(branch), branch);
        var on = PhpExtensions.Effective(Settings.PhpExtensions.GetValueOrDefault(branch), branch, available).ToHashSet();
        return available.Select(n => new ModuleInfo(n, PhpExtensions.Describe(n), on.Contains(n),
            PhpExtensions.Essential.Contains(n), PhpExtensions.Essential.Contains(n) ? PhpExtensions.EssentialReason : PhpExtensions.NoteFor(n))).ToList();
    }

    public async Task SetPhpExtensionsAsync(string branch, IEnumerable<string> enabled, IProgress<string>? p = null, CancellationToken ct = default)
    {
        if (!IsSeriesInstalled("php", branch)) throw new InvalidOperationException($"PHP {branch} isn't installed.");
        var available = PhpExtensions.Discover(PhpDir(branch), branch);
        var wanted = enabled.Select(x => x.Trim().ToLowerInvariant()).Distinct().ToList();
        var unknown = wanted.Where(x => !available.Contains(x)).ToList();
        if (unknown.Count > 0) throw new ArgumentException($"PHP {branch} has no extension named {string.Join(", ", unknown)}.");

        Settings.PhpExtensions[branch] = PhpExtensions.Effective(wanted, branch, available).ToList();
        Save();
        WritePhpIni(branch);

        // Running PHP workers read php.ini once, when they start.
        var workers = Supervisor.All.Where(w => w.Spec.Id.StartsWith($"php-{branch}-") && w.State is ServiceState.Running or ServiceState.Starting).ToList();
        if (workers.Count > 0)
        {
            p?.Report($"Restarting PHP {branch}...");
            await Task.WhenAll(workers.Select(w => w.StopAsync()));
            await StartPhpWorkersAsync(ct);
        }
    }

    /// <summary>Every module the installed Apache ships, with on/off state. Null when Apache isn't installed yet.</summary>
    public IReadOnlyList<ModuleInfo>? ApacheModuleList()
    {
        var series = EffectiveActive("apache");
        if (series == null) return null;
        var available = ApacheModuleCatalog.Discover(Installer.DirOf(ActivePkg("apache"))) ?? new List<string>();
        var on = ApacheModuleCatalog.Optional(Settings.ApacheModules, available).ToHashSet();
        var all = available.Concat(ApacheModuleCatalog.Required).Distinct().OrderBy(x => x, StringComparer.Ordinal);
        return all.Select(n =>
        {
            var required = ApacheModuleCatalog.Required.Contains(n);
            return new ModuleInfo(n, ApacheModuleCatalog.Describe(n), required || on.Contains(n), required,
                required ? ApacheModuleCatalog.RequiredReason : null);
        }).ToList();
    }

    public async Task SetApacheModulesAsync(IEnumerable<string> enabled, IProgress<string>? p = null, CancellationToken ct = default)
    {
        if (EffectiveActive("apache") == null) throw new InvalidOperationException("Apache isn't installed yet. Install it from the Versions page first.");
        var available = ApacheModuleCatalog.Discover(Installer.DirOf(ActivePkg("apache"))) ?? new List<string>();
        var wanted = enabled.Select(x => x.Trim().ToLowerInvariant()).Distinct()
            .Where(x => !ApacheModuleCatalog.Required.Contains(x)).ToList();
        var unknown = wanted.Where(x => !available.Contains(x)).ToList();
        if (unknown.Count > 0) throw new ArgumentException($"This Apache has no module named {string.Join(", ", unknown)}.");

        Settings.ApacheModules = ApacheModuleCatalog.Optional(wanted, available).ToList();   // adds the base module of any that need one
        Save();

        // The change is picked up when Apache next starts. If it is serving right now, restart it so it applies.
        if (Settings.WebServer == WebServerKind.Apache && Supervisor.Get("apache")?.State == ServiceState.Running)
        {
            p?.Report("Restarting Apache...");
            await WriteWebConfigAsync(p, ct);
            await ReloadWebServerAsync(ct);
        }
    }

    IReadOnlyList<string> ApacheOptionalModules()
    {
        var available = ApacheModuleCatalog.Discover(Installer.DirOf(ActivePkg("apache")));
        return ApacheModuleCatalog.Optional(Settings.ApacheModules, available);
    }

    // -- the "Versions" page actions
    public async Task InstallVersionAsync(string id, string series, IProgress<string>? p = null, CancellationToken ct = default)
    {
        if (!Catalog.Shapes.ContainsKey(id)) throw new ArgumentException($"Unknown component '{id}'");
        if (IsSeriesInstalled(id, series)) return;
        await InstallPackageAsync(await ResolveSeriesAsync(id, series, preferBundle: true, ct), p, ct);   // bundled = no download
        if (id == "php") WritePhpIni(series);
    }

    public async Task SetActiveAsync(string id, string series, IProgress<string>? p = null, CancellationToken ct = default)
    {
        if (!IsSeriesInstalled(id, series))
            throw new InvalidOperationException($"{Catalog.Shapes[id].Name} {series} isn't installed.");
        if (id == "php")
        {
            Settings.DefaultPhp = series;
            Save();
            await ApplyAsync(p, ct);          // phpMyAdmin and mail.test follow the default PHP
            return;
        }
        var wasRunning = Supervisor.Get(id)?.State == ServiceState.Running;
        if (wasRunning) await StopServiceAsync(id);
        Settings.Active[id] = series;
        Save();
        if (wasRunning) await StartServiceAsync(id, p, ct);
    }

    // -- updating and importing web servers
    /// <summary>Tools whose newest build can be swapped in place. (PHP keeps several branches side by side; MariaDB upgrades need care.)</summary>
    public static readonly string[] UpdatableIds = { "apache", "nginx" };

    /// <summary>
    /// Swaps in a different build of a series that may already be installed: stops the server if it is serving from that folder,
    /// replaces the files, and starts it again.
    /// </summary>
    async Task ReplaceBuildAsync(Package pkg, IProgress<string>? p, CancellationToken ct)
    {
        var running = EffectiveActive(pkg.Id) == pkg.Series && Supervisor.Get(pkg.Id)?.State is ServiceState.Running or ServiceState.Starting;
        if (running) { p?.Report($"Stopping {pkg.Name}..."); await StopServiceAsync(pkg.Id); }
        await InstallPackageAsync(pkg, p, ct);
        if (running) await StartServiceAsync(pkg.Id, p, ct);
    }

    /// <summary>Installs the newest build the vendor offers for a series that is already installed.</summary>
    public async Task UpdateVersionAsync(string id, string series, IProgress<string>? p = null, CancellationToken ct = default)
    {
        if (!UpdatableIds.Contains(id)) throw new ArgumentException($"{Catalog.Shapes.GetValueOrDefault(id)?.Name ?? id} can't be updated in place.");
        if (!IsSeriesInstalled(id, series)) throw new InvalidOperationException($"{Catalog.Shapes[id].Name} {series} isn't installed.");
        var have = InstalledPkg(id, series).Version;
        p?.Report($"Looking for a newer {Catalog.Shapes[id].Name}...");
        var online = (await AvailableAsync(id, ct)).FirstOrDefault(x => x.Series == series)
            ?? throw new InvalidOperationException($"No {Catalog.Shapes[id].Name} {series} download was found.");
        if (VersionUtil.Compare(online.Version, have) <= 0)
        {
            p?.Report($"{Catalog.Shapes[id].Name} {have} is already the newest.");
            return;
        }
        await ReplaceBuildAsync(online, p, ct);
    }

    /// <summary>Largest archive the dashboard will accept. Apache's zip is about 12 MB.</summary>
    public const long MaxImportBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Installs an Apache Lounge zip the person downloaded themselves, which is the way to get a specific or older build
    /// (Apache Lounge keeps no archive and sometimes blocks scripted downloads). It replaces the build in that 2.x series.
    /// </summary>
    public async Task ImportApacheAsync(string fileName, Stream data, IProgress<string>? p = null, CancellationToken ct = default)
    {
        var version = ApacheResolver.VersionFromArchiveName(fileName)
            ?? throw new ArgumentException("That doesn't look like an Apache Lounge download. The file name should be like httpd-2.4.66-251206-win64-VS17.zip.");
        if (fileName.Contains("win32", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("That is the 32-bit build. Rhapsody Server needs the Win64 one.");

        var target = Path.Combine(Paths.Downloads, $"apache-{version}.zip");
        Directory.CreateDirectory(Paths.Downloads);
        var part = target + ".part";
        p?.Report("Receiving the file...");
        try
        {
            await using (var dst = File.Create(part))
            {
                var buf = new byte[81920]; long total = 0; int n;
                while ((n = await data.ReadAsync(buf, ct)) > 0)
                {
                    total += n;
                    if (total > MaxImportBytes) throw new InvalidDataException("That file is far bigger than an Apache download. Nothing was installed.");
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            p?.Report("Checking the archive...");
            if (!ApacheResolver.LooksLikeApacheZip(part))
                throw new InvalidDataException("That zip doesn't contain Apache24/bin/httpd.exe, so it isn't an Apache for Windows download. Nothing was installed.");
            File.Move(part, target, overwrite: true);
        }
        finally { try { if (File.Exists(part)) File.Delete(part); } catch { /* best effort */ } }

        var pkg = Catalog.Versioned("apache", VersionUtil.Series(version), version, "");
        p?.Report($"Installing Apache {version}...");
        await ReplaceBuildAsync(pkg, p, ct);
    }

    public Task UninstallVersionAsync(string id, string series)
    {
        if (id == "php" && (Settings.DefaultPhp == series || Settings.Sites.Any(s => s.Php == series)))
            throw new InvalidOperationException($"PHP {series} is the default or used by a site. Switch those first.");
        if (id != "php" && EffectiveActive(id) == series)
            throw new InvalidOperationException($"{Catalog.Shapes[id].Name} {series} is the active version. Make another one active first.");
        var dir = Paths.ComponentDir(id, series);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Settings.Versions.Remove($"{id}-{series}");
        Save();
        return Task.CompletedTask;   // note: database files under data/ are never deleted
    }

    // -------------------------------------------------------------- service specs
    public ServiceSpec SpecFor(string id)
    {
        switch (id)
        {
            case "nginx":
            {
                var pkg = ActivePkg("nginx");
                var dir = Installer.DirOf(pkg);
                var exe = Installer.PathOf(pkg);
                var args = new[]
                {
                    "-p", Util.Fwd(Path.Combine(Paths.Etc, "nginx")) + "/",
                    "-c", Util.Fwd(Path.Combine(Paths.Etc, "nginx", "nginx.conf"))
                };
                return new ServiceSpec(id, "nginx", exe, args, dir, LogOf(id), null, Settings.HttpPort,
                    new StopCommand(exe, args.Concat(new[] { "-s", "quit" }).ToArray()));
            }
            case "apache":
            {
                var pkg = ActivePkg("apache");
                var dir = Installer.DirOf(pkg);
                var args = new[] { "-d", Util.Fwd(dir), "-f", Util.Fwd(Path.Combine(Paths.Etc, "apache", "httpd.conf")) };
                return new ServiceSpec(id, "Apache", Installer.PathOf(pkg), args, dir, LogOf(id), null, Settings.HttpPort);
            }
            case "mariadb":
            {
                var pkg = ActivePkg("mariadb");
                var dir = Installer.DirOf(pkg);
                var admin = Path.Combine(dir, "bin", "mariadb-admin.exe");
                return new ServiceSpec(id, "MariaDB", Installer.PathOf(pkg),
                    new[] { "--defaults-file=" + Path.Combine(Paths.Etc, "mariadb", pkg.Series, "my.ini"), "--console" },
                    dir, LogOf(id), null, DbBackendPort,
                    new StopCommand(admin, new[] { "--host=127.0.0.1", $"--port={DbBackendPort}", "-uroot", "shutdown" }));
            }
            case "redis":
            {
                var dir = Installer.DirOf(Catalog.Redis);
                var cli = Path.Combine(dir, "redis-cli.exe");
                return new ServiceSpec(id, "Redis", Installer.PathOf(Catalog.Redis),
                    new[] { Path.Combine(Paths.Etc, "redis", "redis.conf") },
                    dir, LogOf(id), null, Settings.RedisPort,
                    new StopCommand(cli, new[] { "-p", Settings.RedisPort.ToString(), "shutdown", "nosave" }));
            }
            case "mailpit":
            {
                var dir = Installer.DirOf(Catalog.Mailpit);
                return new ServiceSpec(id, "Mailpit", Installer.PathOf(Catalog.Mailpit),
                    new[]
                    {
                        "--smtp", $"127.0.0.1:{Settings.SmtpPort}",
                        "--listen", $"127.0.0.1:{Settings.MailUiPort}",
                        "--database", Path.Combine(Paths.Data, "mailpit.db")
                    },
                    dir, LogOf(id), null, Settings.MailUiPort);
            }
            default:
                throw new ArgumentException($"Unknown service '{id}'");
        }
    }

    ServiceSpec PhpWorkerSpec(string branch, int port, int index) =>
        new($"php-{branch}-{index}", $"PHP {branch} #{index}",
            Path.Combine(PhpDir(branch), "php-cgi.exe"),
            new[] { "-b", $"127.0.0.1:{port}" },
            PhpDir(branch), LogOf($"php-{branch}"), PhpEnv(branch), port);

    async Task PrepareAsync(string id, CancellationToken ct)
    {
        if (id == "mariadb")
        {
            var pkg = ActivePkg("mariadb");
            var dir = Installer.DirOf(pkg);
            var data = Path.Combine(Paths.Data, "mariadb", pkg.Series);   // one data dir per series: a downgrade can't corrupt it
            var etc = Path.Combine(Paths.Etc, "mariadb", pkg.Series);
            Directory.CreateDirectory(etc);
            File.WriteAllText(Path.Combine(etc, "my.ini"), ConfigWriter.MyIni(dir, data, DbBackendPort));
            if (!Directory.Exists(Path.Combine(data, "mysql")))
            {
                Directory.CreateDirectory(data);
                var installDb = new[] { "mariadb-install-db.exe", "mysql_install_db.exe" }
                    .Select(n => Path.Combine(dir, "bin", n)).FirstOrDefault(File.Exists)
                    ?? throw new FileNotFoundException("MariaDB's database initialiser was not found in " + Path.Combine(dir, "bin"));
                var r = await ProcessRunner.RunAsync(installDb, new[] { "--datadir=" + data }, dir, ct: ct);
                if (!r.Ok) throw new InvalidOperationException("MariaDB initialisation failed:\n" + r.Output);
            }
        }
        else if (id == "redis")
        {
            var etc = Path.Combine(Paths.Etc, "redis");
            var data = Path.Combine(Paths.Data, "redis");
            Directory.CreateDirectory(etc);
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(etc, "redis.conf"), ConfigWriter.RedisConf(data, Settings.RedisPort));
        }
    }

    void ThrowIfPortBusy(string name, params int[] ports)
    {
        var busy = PortProbe.ListeningAmong(ports);
        if (busy.Length > 0)
            throw new InvalidOperationException(
                $"{name} can't start: port {string.Join(", ", busy)} is already in use by another program " +
                "(IIS, Skype, another MySQL/Apache?). Change the port in settings.json or stop that program.");
    }

    async Task EnsureRuntimeAsync(IProgress<string>? p, CancellationToken ct)
    {
        if (VcRedist.IsInstalled()) return;
        try
        {
            p?.Report("Installing the Microsoft VC++ runtime (Windows will ask for approval)...");
            await Installer.EnsureAsync(Catalog.VcRedist, p, ct);
            await VcRedist.InstallAsync(Installer.PathOf(Catalog.VcRedist), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            p?.Report("Couldn't install the VC++ runtime automatically; continuing. If PHP won't start, install it from aka.ms/vs/17/release/vc_redist.x64.exe");
        }
    }

    // --------------------------------------------------------------- start / stop
    public async Task StartServiceAsync(string id, IProgress<string>? p = null, CancellationToken ct = default)
    {
        if (id == WebServerId) { await StartWebAsync(p, ct); return; }

        await EnsureRuntimeAsync(p, ct);
        string name;
        if (id == "mariadb") name = (await EnsureActiveAsync("mariadb", p, ct)).Name;
        else
        {
            var pkg = id switch { "redis" => Catalog.Redis, "mailpit" => Catalog.Mailpit, _ => throw new ArgumentException($"Unknown service '{id}'") };
            await Installer.EnsureAsync(pkg, p, ct);
            name = pkg.Name;
        }
        await PrepareAsync(id, ct);

        if (Supervisor.Get(id)?.State is ServiceState.Running or ServiceState.Starting) return;
        var spec = SpecFor(id);
        ThrowIfPortBusy(name, id == "mailpit" ? new[] { Settings.SmtpPort, Settings.MailUiPort }
            : id == "mariadb" && Settings.ProfilingEnabled ? new[] { spec.ReadyPort!.Value, Settings.MariaDbPort }
            : new[] { spec.ReadyPort!.Value });

        p?.Report($"Starting {name}...");
        await Supervisor.Register(spec).StartAsync(ct: ct);
        if (id == "mariadb" && Settings.ProfilingEnabled) StartDbProxy();
    }

    async Task StartWebAsync(IProgress<string>? p, CancellationToken ct)
    {
        if (Supervisor.Get(WebServerId)?.State is ServiceState.Running or ServiceState.Starting) return;

        await EnsureRuntimeAsync(p, ct);
        await Task.WhenAll(
            EnsureActiveAsync(WebServerId, p, ct),
            Installer.EnsureAsync(Catalog.PhpMyAdmin, p, ct),
            Installer.EnsureAsync(Catalog.Mkcert, p, ct),
            Task.WhenAll(BranchesInUse.Select(b => EnsurePhpAsync(b, p, ct))));

        ThrowIfPortBusy(ActivePkg(WebServerId).Name, Settings.HttpPort, Settings.HttpsPort);
        await WriteWebConfigAsync(p, ct);
        if (Settings.ProfilingEnabled) StartTailer();
        HostsFile.Apply(AllDomains());   // phpmyadmin.test / mail.test need entries from the very first start
        await StartPhpWorkersAsync(ct);
        p?.Report($"Starting {ActivePkg(WebServerId).Name}...");
        await Supervisor.Register(SpecFor(WebServerId)).StartAsync(ct: ct);
    }

    public Task StartAllAsync(IProgress<string>? p = null, CancellationToken ct = default) =>
        Task.WhenAll(CoreServiceIds.Append(WebServerId).Select(id => StartServiceAsync(id, p, ct)));

    public async Task StopServiceAsync(string id)
    {
        if (id == "mariadb") StopDbProxy();                 // drop app connections first, then MariaDB
        if (id == WebServerId)
        {
            StopTailer();
            await (Supervisor.Get(id)?.StopAsync() ?? Task.CompletedTask);
            await Task.WhenAll(Supervisor.All.Where(s => s.Spec.Id.StartsWith("php-")).Select(s => s.StopAsync()));
        }
        else await (Supervisor.Get(id)?.StopAsync() ?? Task.CompletedTask);
    }

    public Task StopAllAsync()
    {
        StopDbProxy();
        StopTailer();
        return Supervisor.StopAllAsync();
    }

    // ------------------------------------------------------------------ profiling
    void StartDbProxy()
    {
        if (_dbProxy != null) return;
        Profiling.IConnectionOwners owners = OperatingSystem.IsWindows() ? new Profiling.WindowsConnectionOwners() : Profiling.NullConnectionOwners.Instance;
        var proxy = new Profiling.MySqlProxy(Settings.MariaDbPort, Settings.MariaDbInternalPort, Profile, owners);
        proxy.Start();
        _dbProxy = proxy;
    }

    void StopDbProxy() { _dbProxy?.Dispose(); _dbProxy = null; }

    void StartTailer()
    {
        StopTailer();
        try { File.Delete(ProfileLogPath); } catch { /* still in use: the tailer starts at the end anyway */ }
        _tailer = new Profiling.AccessLogTailer(ProfileLogPath, Settings.WebServer, Profile);
        _tailer.Start();
    }

    void StopTailer() { _tailer?.Dispose(); _tailer = null; }

    /// <summary>
    /// Switching profiling moves MariaDB's port and changes the web server's logging, so whatever was running
    /// is restarted. Off by default: with it off nothing is logged, proxied or parsed.
    /// </summary>
    public async Task SetProfilingAsync(bool on, IProgress<string>? p = null, CancellationToken ct = default)
    {
        if (Settings.ProfilingEnabled == on) return;
        bool Up(string id) => Supervisor.Get(id)?.State is ServiceState.Running or ServiceState.Starting;
        var dbWas = Up("mariadb");
        var webWas = Up(WebServerId);

        if (dbWas) await StopServiceAsync("mariadb");
        if (webWas) await StopServiceAsync(WebServerId);
        Settings.ProfilingEnabled = on;
        Save();
        if (!on) Profile.Clear();
        p?.Report(on ? "Profiling on" : "Profiling off");
        if (dbWas) await StartServiceAsync("mariadb", p, ct);
        if (webWas) await StartServiceAsync(WebServerId, p, ct);
    }

    Task StartPhpWorkersAsync(CancellationToken ct) => Task.WhenAll(
        BranchesInUse
            .SelectMany(b => PhpPorts.Ports(b, Settings.PhpWorkersPerVersion).Select((port, i) => (b, port, i)))
            .Select(x => Supervisor.Register(PhpWorkerSpec(x.b, x.port, x.i)).StartAsync(ct: ct)));

    // ---------------------------------------------------------------- web config
    async Task WriteWebConfigAsync(IProgress<string>? p, CancellationToken ct)
    {
        var certs = new CertManager(Paths, Installer.PathOf(Catalog.Mkcert));
        if (!Settings.CaInstalled)
        {
            p?.Report("Installing local certificate authority (Windows may ask you to confirm)...");
            await certs.InstallCaAsync(ct);
            Settings.CaInstalled = true;
            Save();
        }

        var specs = BuildSiteSpecs();
        foreach (var s in specs.Where(s => s.Https)) await certs.EnsureAsync(s.Domain, ct);

        var prefix = Path.Combine(Paths.Etc, WebServerId);
        Directory.CreateDirectory(Path.Combine(prefix, "logs"));
        Directory.CreateDirectory(Path.Combine(prefix, "temp"));

        if (Settings.WebServer == WebServerKind.Nginx)
        {
            var fcgi = Path.Combine(prefix, "fastcgi_params");
            File.WriteAllText(fcgi, ConfigWriter.FastCgiParams);
            File.WriteAllText(Path.Combine(prefix, "nginx.conf"),
                ConfigWriter.Nginx(Settings, specs, Installer.DirOf(ActivePkg("nginx")), prefix, fcgi, Settings.ProfilingEnabled));
        }
        else
        {
            File.WriteAllText(Path.Combine(prefix, "httpd.conf"),
                ConfigWriter.Apache(Settings, specs, Installer.DirOf(ActivePkg("apache")), prefix, Settings.ProfilingEnabled, ApacheOptionalModules()));
        }
        WritePhpMyAdminConfig();
    }

    void WritePhpMyAdminConfig()
    {
        var dir = Installer.DirOf(Catalog.PhpMyAdmin);
        if (!File.Exists(Path.Combine(dir, "index.php"))) return;
        if (string.IsNullOrEmpty(Settings.PmaSecret))
        {
            Settings.PmaSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            Save();
        }
        var tmp = Path.Combine(Paths.Data, "pma-tmp");
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(dir, "config.inc.php"),
            ConfigWriter.PhpMyAdminConfig(Settings.PmaSecret, tmp, DbBackendPort));
        // php-cgi reads .user.ini from the script's folder before the script runs. phpMyAdmin is older than the newest PHP,
        // and a single deprecation notice printed before it starts its session is enough to break its login page.
        File.WriteAllText(Path.Combine(dir, ".user.ini"), ConfigWriter.PhpMyAdminUserIni);
    }

    public async Task ReloadWebServerAsync(CancellationToken ct = default)
    {
        var svc = Supervisor.Get(WebServerId);
        if (svc is not { State: ServiceState.Running }) return;

        if (Settings.WebServer == WebServerKind.Nginx)
        {
            var spec = svc.Spec;
            var r = await ProcessRunner.RunAsync(spec.Exe, spec.Args.Concat(new[] { "-s", "reload" }).ToArray(), spec.WorkDir, ct: ct);
            if (!r.Ok) throw new InvalidOperationException("nginx reload failed:\n" + r.Output);
        }
        else
        {
            await svc.StopAsync();                       // Apache has no console-mode reload on Windows
            await Supervisor.Register(SpecFor(WebServerId)).StartAsync(ct: ct);
        }
    }

    public async Task SwitchWebServerAsync(WebServerKind kind, IProgress<string>? p = null, CancellationToken ct = default)
    {
        if (kind == Settings.WebServer) return;
        var wasRunning = Supervisor.Get(WebServerId)?.State == ServiceState.Running;
        await StopServiceAsync(WebServerId);
        Settings.WebServer = kind;
        Save();
        if (wasRunning) await StartWebAsync(p, ct);
    }

    // -------------------------------------------------------------------- sites
    public void ValidateSite(Site s)
    {
        if (!NameRx.IsMatch(s.Name))
            throw new ArgumentException("Site name: lowercase letters, digits and hyphens only (e.g. 'my-app').");
        if (Reserved.Contains(s.Name))
            throw new ArgumentException($"'{s.Name}' is reserved.");
        if (!PhpResolver.SupportedBranches.Contains(s.Php))
            throw new ArgumentException($"Unsupported PHP version '{s.Php}'.");
        if (!Directory.Exists(s.Root))
            throw new DirectoryNotFoundException($"Project folder not found: {s.Root}");
        s.PublicDir = Site.NormalizePublicDir(s.PublicDir);   // blank or "/" = serve the project folder itself
        if (s.PublicDir.Length > 0 && !Directory.Exists(Path.Combine(s.Root, s.PublicDir)))
            throw new DirectoryNotFoundException(
                $"There is no \"{s.PublicDir}\" folder inside {s.Root}. Leave the public folder blank to serve the project folder itself.");
    }

    public async Task<Site> AddSiteAsync(Site site, IProgress<string>? p = null, CancellationToken ct = default)
    {
        ValidateSite(site);
        await EnsurePhpAsync(site.Php, p, ct);
        Settings.Sites.RemoveAll(x => x.Name == site.Name);
        Settings.Sites.Add(site);
        Save();
        await ApplyAsync(p, ct);
        return site;
    }

    public async Task RemoveSiteAsync(string name, IProgress<string>? p = null, CancellationToken ct = default)
    {
        Settings.Sites.RemoveAll(x => x.Name == name);
        Save();
        await ApplyAsync(p, ct);
    }

    /// <summary>Re-sync hosts file and (if the web server is up) its config. Safe to call again after granting hosts access.</summary>
    public async Task ApplyAsync(IProgress<string>? p = null, CancellationToken ct = default)
    {
        HostsFile.Apply(AllDomains());
        if (Supervisor.Get(WebServerId)?.State == ServiceState.Running)
        {
            foreach (var b in BranchesInUse) await EnsurePhpAsync(b, p, ct);
            await WriteWebConfigAsync(p, ct);
            await StartPhpWorkersAsync(ct);
            await ReloadWebServerAsync(ct);
        }
    }

    public void Dispose()
    {
        StopDbProxy();
        StopTailer();
        Supervisor.Dispose();
    }
}
