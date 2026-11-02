using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using RhapsodyServer.Core;
using RhapsodyServer.Core.Profiling;
using RhapsodyServer.Core.Web;

// Dependency-free smoke tests (no NuGet needed).
//   dotnet run --project tests/RhapsodyServer.SmokeTests            run the tests
//   dotnet run --project tests/RhapsodyServer.SmokeTests -- --demo 7799 secret   serve the dashboard on fake data
static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

if (args.Length >= 3 && args[0] == "--demo")
{
    var demoBackend = new FakeBackend(); demoBackend.SeedDemo();
    var demo = new DashboardServer(demoBackend, args[2]);
    demo.Start(int.Parse(args[1]));
    Console.WriteLine($"demo at {demo.OpenUrl}");
    await Task.Delay(Timeout.Infinite);
}

int passed = 0, failed = 0;
async Task Check(string name, Func<Task> body)
{
    try { await body(); passed++; Console.WriteLine($"  PASS  {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"  FAIL  {name}\n        {ex.GetType().Name}: {ex.Message}"); }
}
void CheckS(string name, Action body) => Check(name, () => { body(); return Task.CompletedTask; }).GetAwaiter().GetResult();
void True(bool cond, string msg = "assertion failed") { if (!cond) throw new Exception(msg); }
void Eq<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected [{expected}] got [{actual}]"); }

var tmpRoot = Path.Combine(Path.GetTempPath(), "rhapsody-server-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmpRoot);

static byte[] MakeZip(params (string path, string content)[] files)
{
    using var ms = new MemoryStream();
    using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        foreach (var (path, content) in files)
        {
            using var w = new StreamWriter(z.CreateEntry(path).Open());
            w.Write(content);
        }
    return ms.ToArray();
}

// ======================================================================= resolvers
Console.WriteLine("PHP resolver");
CheckS("releases.json: picks NTS x64 zip and version", () =>
{
    var json = """
    {"8.3":{"version":"8.3.14","nts-vs16-x64":{"zip":{"path":"php-8.3.14-nts-Win32-vs16-x64.zip","sha256":"ABC"}},"ts-vs16-x64":{"zip":{"path":"php-8.3.14-Win32-vs16-x64.zip"}}}}
    """;
    var b = PhpResolver.FromReleasesJson(json, "8.3")!;
    Eq("8.3.14", b.Version); Eq("vs16", b.Runtime); Eq("ABC", b.Sha256);
    True(b.Url.EndsWith("php-8.3.14-nts-Win32-vs16-x64.zip"));
    True(PhpResolver.FromReleasesJson(json, "7.1") == null);
    True(PhpResolver.FromReleasesJson("not json", "8.3") == null);
    Eq("php", b.ToPackage().Id); Eq("8.3", b.ToPackage().Series);
});
CheckS("archive listing: numeric patch order, ignores other packs/branches", () =>
{
    var html = """
    <a href="php-7.4.9-nts-Win32-vc15-x64.zip">x</a>
    <a href="php-7.4.33-nts-Win32-vc15-x64.zip">x</a>
    <a href="php-7.4.33-Win32-vc15-x64.zip">ts build</a>
    <a href="php-devel-pack-7.4.33-nts-Win32-vc15-x64.zip">devel</a>
    <a href="php-7.41.0-nts-Win32-vc15-x64.zip">other branch</a>
    <a href="php-7.1.33-nts-Win32-VC14-x64.zip">7.1</a>
    """;
    Eq("7.4.33", PhpResolver.FromArchiveListing(html, "7.4")!.Version);
    var b71 = PhpResolver.FromArchiveListing(html, "7.1")!;
    Eq("7.1.33", b71.Version); Eq("vc14", b71.Runtime);
    True(b71.Url.Contains("/archives/"));
    True(PhpResolver.FromArchiveListing(html, "8.0") == null);
});
CheckS("worker ports are readable and don't collide", () =>
{
    Eq(9840, PhpPorts.BasePort("8.4")); Eq(9710, PhpPorts.BasePort("7.1"));
    var all = PhpResolver.SupportedBranches.SelectMany(b => PhpPorts.Ports(b, 10)).ToList();
    Eq(all.Count, all.Distinct().Count());
});

Console.WriteLine("Apache / nginx / MariaDB resolvers (page shapes are assumptions; see README)");
CheckS("apache: newest win64 build, newest VS, ignores win32, absolute URL", () =>
{
    var html = """
    <a href="/download/VS17/binaries/httpd-2.4.62-240904-win64-VS17.zip">2.4.62</a>
    <a href="/download/VS17/binaries/httpd-2.4.63-250207-win64-VS17.zip">2.4.63 VS17</a>
    <a href="/download/VS16/binaries/httpd-2.4.63-250207-win64-VS16.zip">2.4.63 VS16</a>
    <a href="/download/VS17/binaries/httpd-2.4.64-250301-win32-VS17.zip">32-bit</a>
    """;
    var l = ApacheResolver.FromDownloadPage(html);
    Eq(1, l.Count); Eq("2.4", l[0].Series); Eq("2.4.63", l[0].Version);
    Eq("https://www.apachelounge.com/download/VS17/binaries/httpd-2.4.63-250207-win64-VS17.zip", l[0].Url);
    Eq("Apache24", l[0].InnerRoot);
});
CheckS("nginx: highest patch per series; latest stable skips mainline", () =>
{
    var html = """
    nginx-1.29.1.zip nginx-1.29.0.zip nginx-1.28.1.zip nginx-1.28.0.zip nginx-1.26.3.zip nginx/Windows-1.28.1
    """;
    var l = NginxResolver.FromDownloadPage(html);
    Eq("1.29,1.28,1.26", string.Join(",", l.Select(p => p.Series)));
    Eq("1.28.1", l.First(p => p.Series == "1.28").Version);
    Eq("1.28", PackageSources.Latest("nginx", l)!.Series);
    Eq("1.29", PackageSources.Latest("php", l)!.Series);   // non-nginx: plain newest
});
CheckS("mariadb: only LTS series, highest patch, archive URL", () =>
{
    var html = """
    <a href="mariadb-11.8.1/">x</a><a href="mariadb-11.8.2/">x</a><a href="mariadb-12.0.1/">rolling</a>
    <a href="mariadb-10.11.9/">x</a><a href="mariadb-10.11.10/">x</a><a href="mariadb-5.5.1/">old</a>
    """;
    var l = MariaDbResolver.FromArchiveListing(html);
    Eq("11.8,10.11", string.Join(",", l.Select(p => p.Series)));
    Eq("10.11.10", l[1].Version);
    Eq("https://archive.mariadb.org/mariadb-11.8.2/winx64-packages/mariadb-11.8.2-winx64.zip", l[0].Url);
});
CheckS("version compare is numeric", () =>
{
    True(VersionUtil.Compare("10.11", "10.6") > 0); True(VersionUtil.Compare("8.3.2", "8.3.10") < 0);
    Eq("11.8", VersionUtil.Series("11.8.2"));
});

// ===================================================================== hosts / config
Console.WriteLine("Hosts file");
CheckS("merge is idempotent and preserves user lines + CRLF", () =>
{
    var original = "# my hosts\r\n127.0.0.1 localhost\r\n10.0.0.5 nas.lan\r\n";
    var once = HostsFile.Merge(original, new[] { "b.test", "a.test" });
    Eq(once, HostsFile.Merge(once, new[] { "b.test", "a.test" }));
    True(once.StartsWith(original.TrimEnd()), "user content must stay first");
    True(once.Contains("127.0.0.1 a.test\r\n127.0.0.1 b.test"), "sorted, CRLF kept");
});
CheckS("merge removes stale entries and block when empty", () =>
{
    var s1 = HostsFile.Merge("127.0.0.1 localhost\n", new[] { "a.test", "b.test" });
    var s2 = HostsFile.Merge(s1, new[] { "a.test" });
    True(!s2.Contains("b.test") && s2.Contains("a.test"));
    Eq("127.0.0.1 localhost\n", HostsFile.Merge(s2, Array.Empty<string>()));
});
CheckS("Apply writes once then no-ops; CanWrite reflects reality", () =>
{
    var f = Path.Combine(tmpRoot, "hosts");
    File.WriteAllText(f, "127.0.0.1 localhost\n");
    True(HostsFile.CanWrite(f)); True(!HostsFile.CanWrite(Path.Combine(tmpRoot, "nope")));
    True(HostsFile.Apply(new[] { "x.test" }, f)); True(!HostsFile.Apply(new[] { "x.test" }, f));
});

Console.WriteLine("Config generation");
var settings = new Settings();
var sites = new List<SiteSpec>
{
    new("shop.test", "C:\\Sites\\shop\\public", null, "8.4", true, "C:\\c\\shop.pem", "C:\\c\\shop-key.pem"),
    new("old.test", "C:\\Sites\\old", null, "7.1", false, "", ""),
    new("mail.test", null, "127.0.0.1:8025", "8.3", true, "C:\\c\\mail.pem", "C:\\c\\mail-key.pem"),
};
CheckS("nginx: upstream per PHP branch, vhosts, forward slashes, loopback only", () =>
{
    var c = ConfigWriter.Nginx(settings, sites, "C:\\o\\nginx", "C:\\o\\etc\\nginx", "C:\\o\\etc\\nginx\\fastcgi_params");
    True(c.Contains("upstream php_8_4") && c.Contains("upstream php_7_1") && !c.Contains("upstream php_8_3"));
    True(c.Contains("server 127.0.0.1:9840;") && c.Contains("server 127.0.0.1:9841;"));
    True(c.Contains("fastcgi_pass php_7_1;") && c.Contains("root \"C:/Sites/shop/public\";"));
    True(c.Contains("proxy_pass http://127.0.0.1:8025;") && c.Contains("listen 127.0.0.1:80;"));
    True(!c.Contains("C:\\"), "no backslash paths in nginx.conf");
    Eq(2, c.Split("ssl_certificate ").Length - 1);
});
CheckS("apache: balancer when workers>1, direct fcgi when 1", () =>
{
    var many = ConfigWriter.Apache(settings, sites, "C:\\o\\apache", "C:\\o\\etc\\apache");
    True(many.Contains("<Proxy \"balancer://php_8_4\">") && many.Contains("SetHandler \"proxy:balancer://php_8_4\""));
    var one = ConfigWriter.Apache(new Settings { PhpWorkersPerVersion = 1 }, sites, "C:\\o\\apache", "C:\\o\\etc\\apache");
    True(!one.Contains("<Proxy \"balancer") && !one.Contains("SetHandler \"proxy:balancer"), "no balancer with a single worker");
    True(one.Contains("SetHandler \"proxy:fcgi://127.0.0.1:9840\"") && one.Contains("<VirtualHost 127.0.0.1:443>"));
});
CheckS("php.ini: 7.1 uses prefixed dll names, 8.x doesn't", () =>
{
    var old = ConfigWriter.PhpIni("7.1", "C:\\php", "C:\\tmp", settings);
    var cur = ConfigWriter.PhpIni("8.4", "C:\\php", "C:\\tmp", settings);
    True(old.Contains("extension=php_gd2.dll") && old.Contains("zend_extension=php_opcache.dll") && !old.Contains("sodium"));
    True(cur.Contains("zend_extension=opcache") && cur.Contains("extension=sodium") && cur.Contains("smtp_port=1025"));
    True(cur.Contains("extension_dir=\"C:/php/ext\""));
});
CheckS("my.ini / redis.conf / pma config render", () =>
{
    var my = ConfigWriter.MyIni("C:\\m", "C:\\d", 3306);
    True(my.Contains("datadir=C:/d") && my.Contains("skip-name-resolve"));
    True(ConfigWriter.RedisConf("C:\\r", 6379).Contains("port 6379"));
    True(ConfigWriter.PhpMyAdminConfig("secret", "C:\\t", 3306).Contains("'secret'"));
});

// ================================================================== scaffold / env
Console.WriteLine("Rhapsody .env");
const string RealisticEnv = """
# Rhapsody Environment Configuration
APP_URL="https://old.test"
APP_NAME="Rhapsody Framework"
APP_KEY=""
DB_HOST="localhost"
DB_PORT="3306"
DB_NAME="marketplace"
DB_USER="root"
DB_PASS=""
DB_DRIVER="mysql" # Supported databases: mysql, pgsql, sqlite, or sqlsrv
MAIL_HOST="smtp.example.com"
MAIL_PORT="587"
MAIL_USERNAME="someone@example.com"
MAIL_PASSWORD="hunter2"
CACHE_DRIVER=redis
REDIS_HOST=127.0.0.1
REDIS_PORT=6379
REDIS_PASSWORD=oldsecret
VITE_PORT=5173
""";
CheckS("UpsertEnv keeps quote style, inline comments and untouched keys", () =>
{
    var vals = RhapsodyScaffold.EnvValues(new Settings(), "my-app", "my-app.test");
    var r = RhapsodyScaffold.UpsertEnv(RealisticEnv, vals);
    True(r.Contains("DB_NAME=\"my_app\"") && r.Contains("DB_HOST=\"127.0.0.1\"") && r.Contains("DB_PORT=\"3306\""), "quoted keys stay quoted");
    True(r.Contains("DB_DRIVER=\"mysql\" # Supported databases: mysql, pgsql, sqlite, or sqlsrv"), "inline comment survives");
    True(r.Contains("MAIL_PORT=\"1025\"") && r.Contains("MAIL_USERNAME=\"\"") && !r.Contains("hunter2") && !r.Contains("someone@example.com"));
    True(r.Contains("REDIS_PASSWORD=\n") || r.EndsWith("REDIS_PASSWORD=\n"), "unquoted stays unquoted, now empty");
    True(r.Contains("CACHE_DRIVER=redis") && r.Contains("VITE_PORT=5173") && r.Contains("# Rhapsody Environment Configuration"));
    True(r.Contains("APP_NAME=\"My App\"") && r.Contains("APP_URL=\"https://my-app.test\""));
    True(r.Contains("MAIL_TRANSPORT=smtp") || r.Contains("MAIL_TRANSPORT=\"smtp\""), "missing key appended");
    Eq(1, r.Split("DB_NAME").Length - 1);
});
CheckS("APP_KEY is 128 hex chars and differs each time; db name sanitised", () =>
{
    var k = RhapsodyScaffold.NewAppKey();
    Eq(128, k.Length); True(k.All(Uri.IsHexDigit)); True(k != RhapsodyScaffold.NewAppKey());
    Eq("my_app", RhapsodyScaffold.DbName("my-app")); Eq("My Cool App", RhapsodyScaffold.TitleOf("my-cool-app"));
    var written = RhapsodyScaffold.UpsertEnv(RealisticEnv, RhapsodyScaffold.EnvValues(new Settings(), "x", "x.test"));
    var keyLine = written.Split('\n').First(l => l.StartsWith("APP_KEY="));
    Eq(128, keyLine["APP_KEY=".Length..].Trim('"', '\r').Length);   // existing empty "" was replaced by a real key, quotes kept
    True(keyLine.StartsWith("APP_KEY=\""));
});
CheckS("settings round-trip (enum as string, new fields)", () =>
{
    var f = Path.Combine(tmpRoot, "settings.json");
    var s = new Settings { WebServer = WebServerKind.Apache, PhpWorkersPerVersion = 3 };
    s.Active["mariadb"] = "11.8"; s.Sites.Add(new Site { Name = "x", Root = "C:\\x", Php = "8.1" });
    SettingsStore.Save(f, s);
    True(File.ReadAllText(f).Contains("\"Apache\""));
    var l = SettingsStore.Load(f);
    Eq(WebServerKind.Apache, l.WebServer); Eq("8.1", l.Sites[0].Php); Eq("11.8", l.Active["mariadb"]); Eq(7766, l.UiPort);
    True(!File.Exists(f + ".tmp"));
});

// ================================================================ installer / bundle
Console.WriteLine("Installer and bundle");
await Check("ExtractFlatten: wrapper folder flattened; innerRoot beside readme files", () =>
{
    var a = MakeZip(("nginx-1.28.0/nginx.exe", "x"), ("nginx-1.28.0/conf/mime.types", "x"));
    File.WriteAllBytes(Path.Combine(tmpRoot, "a.zip"), a);
    Installer.ExtractFlatten(Path.Combine(tmpRoot, "a.zip"), Path.Combine(tmpRoot, "outA"));
    True(File.Exists(Path.Combine(tmpRoot, "outA", "nginx.exe")) && !Directory.Exists(Path.Combine(tmpRoot, "outA.extracting")));
    File.WriteAllBytes(Path.Combine(tmpRoot, "b.zip"), MakeZip(("Apache24/bin/httpd.exe", "x"), ("ReadMe.txt", "hi")));
    Installer.ExtractFlatten(Path.Combine(tmpRoot, "b.zip"), Path.Combine(tmpRoot, "outB"), "Apache24");
    True(File.Exists(Path.Combine(tmpRoot, "outB", "bin", "httpd.exe")) && !File.Exists(Path.Combine(tmpRoot, "outB", "ReadMe.txt")));
    return Task.CompletedTask;
});

// A fake vendor: serves zips by URL so BundleBuilder and the installer run with no real network.
var payloads = new Dictionary<string, byte[]>
{
    ["https://v/nginx-1.28.0.zip"] = MakeZip(("nginx-1.28.0/nginx.exe", "n")),
    ["https://v/apache-2.4.63.zip"] = MakeZip(("Apache24/bin/httpd.exe", "a"), ("ReadMe.txt", "r")),
    ["https://v/mariadb-11.8.2.zip"] = MakeZip(("mariadb-11.8.2-winx64/bin/mariadbd.exe", "m")),
    ["https://v/php-8.5.1.zip"] = MakeZip(("php-cgi.exe", "p"), ("php.exe", "p")),
    ["https://v/php-8.4.1.zip"] = MakeZip(("php-cgi.exe", "p"), ("php.exe", "p")),
};
var vendorHttp = () => new HttpClient(new MapHandler(payloads));
var pkgs = new[]
{
    Catalog.Versioned("nginx", "1.28", "1.28.0", "https://v/nginx-1.28.0.zip"),
    Catalog.Versioned("apache", "2.4", "2.4.63", "https://v/apache-2.4.63.zip"),
    Catalog.Versioned("mariadb", "11.8", "11.8.2", "https://v/mariadb-11.8.2.zip"),
    Catalog.Versioned("php", "8.5", "8.5.1", "https://v/php-8.5.1.zip"),
};
var bundleDir = Path.Combine(tmpRoot, "bundle");
await Check("BundleBuilder downloads, hashes and writes a manifest that round-trips", async () =>
{
    var inst = new Installer(new ServerPaths(Path.Combine(tmpRoot, "builder")), vendorHttp());
    var m = await BundleBuilder.BuildAsync(inst, pkgs, bundleDir);
    Eq(4, m.Entries.Count);
    var src = BundleSource.Locate(bundleDir)!;
    Eq("2.4.63", src.Find("apache")!.Version); Eq("11.8", src.Find("mariadb", "11.8")!.Series);
    True(src.Find("php", "8.4") == null);
    foreach (var e in src.Entries) { True(File.Exists(src.PathOf(e))); Eq(64, e.Sha256.Length); src.Verify(e); }
    True(BundleSource.Locate(Path.Combine(tmpRoot, "no-bundle")) == null);
});
await Check("fresh install from the bundle works with the network completely unavailable", async () =>
{
    var paths = new ServerPaths(Path.Combine(tmpRoot, "fresh")); paths.EnsureCreated();
    var inst = new Installer(paths, new HttpClient(new ThrowingHandler()), BundleSource.Locate(bundleDir));
    using var stack = new Stack(paths, inst);
    True(stack.EffectiveActive("apache") == null);
    var apache = await stack.EnsureActiveAsync("apache");
    await stack.EnsureActiveAsync("mariadb"); await stack.EnsureActiveAsync("nginx"); await stack.EnsurePhpAsync("8.5");
    Eq("2.4.63", apache.Version); Eq("2.4", stack.EffectiveActive("apache")); Eq("11.8", stack.EffectiveActive("mariadb"));
    True(File.Exists(Path.Combine(paths.Bin, "apache", "2.4", "bin", "httpd.exe")), "InnerRoot honoured");
    True(File.Exists(Path.Combine(paths.Bin, "mariadb", "11.8", "bin", "mariadbd.exe")), "wrapper folder flattened");
    True(File.Exists(Path.Combine(paths.Etc, "php", "8.5", "php.ini")), "php.ini written on install");
    Eq("11.8.2", stack.Settings.Versions["mariadb-11.8"]);
    True(!Directory.GetFiles(paths.Downloads).Any(), "unpacked straight from the bundle, no second copy");
    var reload = new Stack(paths, new Installer(paths, new HttpClient(new ThrowingHandler())));   // settings persisted
    Eq("2.4", reload.Settings.Active["apache"]);
});
await Check("a corrupted bundle file is refused, not installed", async () =>
{
    var badDir = Path.Combine(tmpRoot, "badbundle"); Directory.CreateDirectory(badDir);
    foreach (var f in Directory.GetFiles(bundleDir)) File.Copy(f, Path.Combine(badDir, Path.GetFileName(f)));
    File.AppendAllText(Path.Combine(badDir, "apache-2.4.63.zip"), "garbage");
    var paths = new ServerPaths(Path.Combine(tmpRoot, "corrupt")); paths.EnsureCreated();
    using var stack = new Stack(paths, new Installer(paths, new HttpClient(new ThrowingHandler()), BundleSource.Locate(badDir)));
    try { await stack.EnsureActiveAsync("apache"); throw new Exception("should have thrown"); }
    catch (InvalidDataException ex) { True(ex.Message.Contains("corrupt")); }
    True(stack.EffectiveActive("apache") == null);
});
await Check("no bundle + no download link gives an actionable message", async () =>
{
    var paths = new ServerPaths(Path.Combine(tmpRoot, "nolink")); paths.EnsureCreated();
    var inst = new Installer(paths, new HttpClient(new ThrowingHandler()));
    try { await inst.InstallAsync(Catalog.Versioned("apache", "2.4", "2.4.66", "")); throw new Exception("should have thrown"); }
    catch (FileNotFoundException ex) { True(ex.Message.Contains("apache-2.4.66.zip")); }
});

Console.WriteLine("Versions (install more, switch, remove)");
await Check("install a non-bundled version online; offline falls back to bundle only when it has one", async () =>
{
    var paths = new ServerPaths(Path.Combine(tmpRoot, "ver")); paths.EnsureCreated();
    // resolver pages served by the fake vendor, so the "list available" path runs for real
    var web = new Dictionary<string, byte[]>(payloads)
    {
        [PhpResolver.ReleasesBase + "releases.json"] = System.Text.Encoding.UTF8.GetBytes(
            """{"8.4":{"version":"8.4.1","nts-vs17-x64":{"zip":{"path":"php-8.4.1-nts-Win32-vs17-x64.zip"}}}}"""),
        [PhpResolver.ReleasesBase + "archives/"] = System.Text.Encoding.UTF8.GetBytes("<html></html>"),
        [PhpResolver.ReleasesBase + "php-8.4.1-nts-Win32-vs17-x64.zip"] = payloads["https://v/php-8.4.1.zip"],
    };
    using var stack = new Stack(paths, new Installer(paths, new HttpClient(new MapHandler(web)), BundleSource.Locate(bundleDir)));
    await stack.EnsurePhpAsync("8.5");
    Eq("8.5,", string.Join(",", stack.InstalledSeries("php")) + ",");
    await stack.InstallVersionAsync("php", "8.4");
    Eq("8.5,8.4", string.Join(",", stack.InstalledSeries("php")));
    Eq("8.4.1", stack.Settings.Versions["php-8.4"]);
    try { await stack.InstallVersionAsync("php", "7.1"); throw new Exception("should have thrown"); }
    catch (InvalidOperationException ex) { True(ex.Message.Contains("7.1")); }
});
await Check("set active / uninstall guards protect the default, in-use and active versions", async () =>
{
    var paths = new ServerPaths(Path.Combine(tmpRoot, "guard")); paths.EnsureCreated();
    using var stack = new Stack(paths, new Installer(paths, new HttpClient(new ThrowingHandler()), BundleSource.Locate(bundleDir)));
    await stack.EnsurePhpAsync("8.5");
    Directory.CreateDirectory(Path.Combine(paths.Bin, "php", "8.1")); File.WriteAllText(Path.Combine(paths.Bin, "php", "8.1", "php-cgi.exe"), "x");
    Directory.CreateDirectory(Path.Combine(paths.Bin, "php", "8.0")); File.WriteAllText(Path.Combine(paths.Bin, "php", "8.0", "php-cgi.exe"), "x");
    var proj = Path.Combine(tmpRoot, "guard-proj"); Directory.CreateDirectory(proj);
    stack.Settings.DefaultPhp = "8.5";
    stack.Settings.Sites.Add(new Site { Name = "s", Root = proj, Php = "8.1" });
    foreach (var s in new[] { "8.5", "8.1" })
        try { await stack.UninstallVersionAsync("php", s); throw new Exception($"{s} should be protected"); } catch (InvalidOperationException) { }
    await stack.UninstallVersionAsync("php", "8.0");
    True(!stack.IsSeriesInstalled("php", "8.0"));
    try { await stack.SetActiveAsync("php", "7.4"); throw new Exception("not installed"); } catch (InvalidOperationException) { }
    await stack.EnsureActiveAsync("mariadb");
    try { await stack.UninstallVersionAsync("mariadb", "11.8"); throw new Exception("active"); } catch (InvalidOperationException) { }
});

// ========================================================================== ports / supervisor
Console.WriteLine("Ports and supervisor");
CheckS("PortProbe sees a real listener appear and disappear", () =>
{
    var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
    var port = ((IPEndPoint)l.LocalEndpoint).Port;
    True(PortProbe.IsListening(port)); l.Stop(); True(!PortProbe.IsListening(port));
});
await Check("start -> Running -> stop -> Stopped, events fire in order", async () =>
{
    var states = new List<ServiceState>();
    var svc = new ManagedService(new ServiceSpec("t1", "sleeper", "sleep", new[] { "30" }, tmpRoot, Path.Combine(tmpRoot, "logs", "t1.log")));
    svc.StateChanged += s => { lock (states) states.Add(s.State); };
    await svc.StartAsync(); Eq(ServiceState.Running, svc.State);
    await svc.StopAsync(); Eq(ServiceState.Stopped, svc.State);
    Eq("Starting,Running,Stopped", string.Join(",", states));
});
await Check("unexpected exit is reported as Crashed with exit code", async () =>
{
    var svc = new ManagedService(new ServiceSpec("t2", "crasher", "sh", new[] { "-c", "sleep 0.4; exit 3" }, tmpRoot, Path.Combine(tmpRoot, "logs", "t2.log")));
    await svc.StartAsync();
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (svc.State == ServiceState.Running && DateTime.UtcNow < deadline) await Task.Delay(50);
    Eq(ServiceState.Crashed, svc.State); Eq(3, svc.LastExitCode);
});
await Check("instant crash during startup throws with a log pointer", async () =>
{
    var svc = new ManagedService(new ServiceSpec("t3", "dud", "sh", new[] { "-c", "exit 1" }, tmpRoot, Path.Combine(tmpRoot, "logs", "t3.log")));
    try { await svc.StartAsync(); throw new Exception("should have thrown"); } catch (InvalidOperationException ex) { True(ex.Message.Contains("t3.log")); }
    Eq(ServiceState.Crashed, svc.State);
});
await Check("ReadyPort gates Running; child output lands in the log", async () =>
{
    var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port;
    var log = Path.Combine(tmpRoot, "logs", "t4.log");
    var svc = new ManagedService(new ServiceSpec("t4", "echoer", "sh", new[] { "-c", "echo hello-from-child; sleep 30" }, tmpRoot, log, ReadyPort: port));
    await svc.StartAsync(); Eq(ServiceState.Running, svc.State);
    await svc.StopAsync(); l.Stop();
    True(File.ReadAllText(log).Contains("hello-from-child"));
});
await Check("custom stop command runs before kill", async () =>
{
    var marker = Path.Combine(tmpRoot, "stopped.marker");
    var svc = new ManagedService(new ServiceSpec("t5", "graceful", "sleep", new[] { "30" }, tmpRoot, Path.Combine(tmpRoot, "logs", "t5.log"),
        Stop: new StopCommand("sh", new[] { "-c", $"touch '{marker}'" })));
    await svc.StartAsync(); await svc.StopAsync(TimeSpan.FromMilliseconds(300));
    True(File.Exists(marker)); Eq(ServiceState.Stopped, svc.State);
});

Console.WriteLine("Stack");
CheckS("site specs include user sites + phpmyadmin + mail; validation; shared op gate", () =>
{
    using var stack = new Stack(new ServerPaths(Path.Combine(tmpRoot, "home2")));
    var dir = Path.Combine(tmpRoot, "proj"); Directory.CreateDirectory(dir);
    stack.Settings.Sites.Add(new Site { Name = "demo", Root = dir, Php = "8.1" });
    var specs = stack.BuildSiteSpecs();
    Eq("demo.test,phpmyadmin.test,mail.test", string.Join(",", specs.Select(s => s.Domain)));
    Eq(dir, specs[0].DocRoot);   // no public folder = the project folder itself (Rhapsody)
    foreach (var bad in new[] { "mail", "Bad_Name" })
        try { stack.ValidateSite(new Site { Name = bad, Root = dir }); throw new Exception("should reject " + bad); } catch (ArgumentException) { }
    try { stack.ValidateSite(new Site { Name = "ok", Root = dir, Php = "5.6" }); throw new Exception("should reject"); } catch (ArgumentException) { }
    stack.ValidateSite(new Site { Name = "ok-1", Root = dir, Php = "8.5" });
    True(stack.Settings.UiToken.Length >= 30, "UI token generated on first run");
    var gate = new ManualResetEventSlim();
    var first = stack.RunExclusiveAsync("one", () => Task.Run(() => gate.Wait(2000)));
    Eq("one", stack.CurrentOperation);
    try { stack.RunExclusiveAsync("two", () => Task.CompletedTask).GetAwaiter().GetResult(); throw new Exception("should be busy"); }
    catch (InvalidOperationException ex) { True(ex.Message.Contains("busy")); }
    gate.Set(); first.GetAwaiter().GetResult(); True(stack.CurrentOperation == null);
});
CheckS("service specs: nginx quit command, mariadb per-series datadir, php worker env", () =>
{
    var paths = new ServerPaths(Path.Combine(tmpRoot, "home3"));
    using var stack = new Stack(paths);
    foreach (var (id, series) in new[] { ("nginx", "1.28"), ("mariadb", "11.8") })
    { var d = Path.Combine(paths.Bin, id, series); Directory.CreateDirectory(Path.Combine(d, "bin")); File.WriteAllText(Path.Combine(d, Catalog.Shapes[id].Exe), "x"); }
    var n = stack.SpecFor("nginx");
    True(n.Stop!.Args.Contains("quit") && n.ReadyPort == 80);
    True(stack.SpecFor("mariadb").Args[0].Contains(Path.Combine("mariadb", "11.8", "my.ini")));
    True(stack.SpecFor("mailpit").Args.Contains("127.0.0.1:1025"));
    Eq("0", stack.PhpEnv("8.4")["PHP_FCGI_MAX_REQUESTS"]);
});



// ============================================ opcache on 8.5, modules, docroot, bundle check
Console.WriteLine("PHP 8.5 opcache, extension and module toggles");
CheckS("php.ini 8.5: OPcache is built in, so no zend_extension line (the 'Failed loading Zend extension' warning)", () =>
{
    var ini85 = ConfigWriter.PhpIni("8.5", "C:\\php", "C:\\tmp", settings);
    True(!ini85.Contains("zend_extension"), "8.5 must not ask for an opcache DLL");
    True(ini85.Contains("opcache.enable=1") && ini85.Contains("extension=mbstring"));
    True(ConfigWriter.PhpIni("8.4", "C:\\php", "C:\\tmp", settings).Contains("zend_extension=opcache"), "8.4 still loads the DLL");
    var off = ConfigWriter.PhpIni("8.5", "C:\\php", "C:\\tmp", settings, new PhpIniOptions(new[] { "mbstring" }, false, false));
    True(off.Contains("opcache.enable=0") && !off.Contains("extension=gd") && off.Contains("extension=mbstring"));
    var old = ConfigWriter.PhpIni("7.1", "C:\\php", "C:\\tmp", settings, new PhpIniOptions(new[] { "gd" }, true));
    True(old.Contains("extension=php_gd2.dll") && old.Contains("zend_extension=php_opcache.dll"));
});
CheckS("extension names: gd2 -> gd, opcache and xdebug are not plain extensions, essentials can't be dropped", () =>
{
    Eq("gd", PhpExtensions.NameOfDll("php_gd2.dll")); Eq("pdo_mysql", PhpExtensions.NameOfDll("php_pdo_mysql.dll"));
    var dir = Path.Combine(tmpRoot, "phpx"); Directory.CreateDirectory(Path.Combine(dir, "ext"));
    foreach (var f in new[] { "php_gd.dll", "php_curl.dll", "php_opcache.dll", "php_xdebug.dll", "php_zip.dll" }) File.WriteAllText(Path.Combine(dir, "ext", f), "x");
    var found = PhpExtensions.Discover(dir, "8.4");
    Eq("curl,gd,opcache,zip", string.Join(",", found));
    True(PhpExtensions.Discover(dir, "8.5").Contains("opcache"), "8.5 always offers opcache (built in)");
    var eff = PhpExtensions.Effective(new[] { "gd" }, "8.4", new[] { "curl", "gd", "openssl", "mbstring", "mysqli", "zip" });
    True(new[] { "curl", "openssl", "mbstring", "mysqli", "gd" }.All(eff.Contains) && !eff.Contains("zip"));
});
CheckS("apache config: required modules always, chosen extras, bases first, dotfiles never served", () =>
{
    var def = ConfigWriter.Apache(settings, sites, "C:\\o\\apache", "C:\\o\\etc\\apache");
    True(def.Contains("LoadModule ssl_module modules/mod_ssl.so") && def.Contains("LoadModule rewrite_module") && def.Contains("LoadModule headers_module"));
    True(!def.Contains("deflate_module"));
    True(def.Contains("<FilesMatch \"^\\.\">") && def.Contains("<DirectoryMatch \"/\\.(?!well-known)\">"), "dotfile rules");
    var more = ConfigWriter.Apache(settings, sites, "C:\\o\\apache", "C:\\o\\etc\\apache", false, new[] { "deflate", "cache_disk" });
    True(more.Contains("deflate_module") && !more.Contains("LoadModule rewrite_module"), "only what was chosen");
    True(more.IndexOf("LoadModule cache_module") is var a && a > 0 && a < more.IndexOf("LoadModule cache_disk_module"), "cache before cache_disk");
    Eq(1, more.Split("LoadModule ssl_module").Length - 1);
    var none = ConfigWriter.Apache(settings, sites, "C:\\o\\apache", "C:\\o\\etc\\apache", false, new[] { "ssl", "proxy" });
    Eq(1, none.Split("LoadModule ssl_module").Length - 1);   // asking for a required one doesn't duplicate it
});
CheckS("public folder: blank, '/' and '.' mean the project root; names stay; '..' and drive paths are refused", () =>
{
    foreach (var root in new[] { null, "", "  ", "/", "\\", ".", "./", "\"\"" }) Eq("", Site.NormalizePublicDir(root));
    Eq("public", Site.NormalizePublicDir("public")); Eq("public", Site.NormalizePublicDir("/public/")); Eq("web/public", Site.NormalizePublicDir("web\\public"));
    foreach (var bad in new[] { "..", "../x", "a/../../b", "C:\\x", "\\\\server\\share" })
        try { Site.NormalizePublicDir(bad); throw new Exception("should reject " + bad); } catch (ArgumentException) { }
});
CheckS("sites: root by default, a named sub-folder must exist, Rhapsody scaffold registers the root", () =>
{
    using var stack = new Stack(new ServerPaths(Path.Combine(tmpRoot, "home-docroot")));
    var proj = Path.Combine(tmpRoot, "rh-proj"); Directory.CreateDirectory(proj);
    var site = new Site { Name = "rh", Root = proj, PublicDir = "/" };
    stack.ValidateSite(site); Eq("", site.PublicDir);
    try { stack.ValidateSite(new Site { Name = "rh", Root = proj, PublicDir = "public" }); throw new Exception("missing folder should fail"); }
    catch (DirectoryNotFoundException ex) { True(ex.Message.Contains("blank")); }
    Directory.CreateDirectory(Path.Combine(proj, "public"));
    var sub = new Site { Name = "rh", Root = proj, PublicDir = "public" }; stack.ValidateSite(sub); Eq("public", sub.PublicDir);
    Eq("", new Site().PublicDir);
    True(File.ReadAllText(FindUp("src/RhapsodyServer.Core/RhapsodyScaffold.cs")).Contains("PublicDir = \"\""), "scaffold uses the root");
});
CheckS("stack: extension toggles rewrite php.ini from the files on disk (8.5 has no opcache DLL)", () =>
{
    var paths = new ServerPaths(Path.Combine(tmpRoot, "home-ext"));
    using var stack = new Stack(paths);
    foreach (var (b, dll) in new[] { ("8.5", false), ("8.4", true) })
    {
        var d = Path.Combine(paths.Bin, "php", b); Directory.CreateDirectory(Path.Combine(d, "ext"));
        File.WriteAllText(Path.Combine(d, "php-cgi.exe"), "x");
        foreach (var f in new[] { "curl", "gd", "mbstring", "mysqli", "openssl", "zip", "intl" }.Select(x => $"php_{x}.dll").Concat(dll ? new[] { "php_opcache.dll" } : Array.Empty<string>()))
            File.WriteAllText(Path.Combine(d, "ext", f), "x");
    }
    stack.SetPhpExtensionsAsync("8.5", new[] { "gd" }).GetAwaiter().GetResult();
    var ini = File.ReadAllText(Path.Combine(paths.Etc, "php", "8.5", "php.ini"));
    True(!ini.Contains("zend_extension") && ini.Contains("extension=gd") && !ini.Contains("extension=zip") && !ini.Contains("extension=intl"), ini);
    True(ini.Contains("extension=openssl") && ini.Contains("extension=mysqli"), "essentials stay on");
    stack.SetPhpExtensionsAsync("8.4", new[] { "gd", "opcache" }).GetAwaiter().GetResult();
    True(File.ReadAllText(Path.Combine(paths.Etc, "php", "8.4", "php.ini")).Contains("zend_extension=opcache"));
    stack.SetPhpExtensionsAsync("8.4", new[] { "gd" }).GetAwaiter().GetResult();
    var off = File.ReadAllText(Path.Combine(paths.Etc, "php", "8.4", "php.ini"));
    True(!off.Contains("zend_extension") && off.Contains("opcache.enable=0"), "opcache off = not loaded");
    try { stack.SetPhpExtensionsAsync("8.5", new[] { "nonsense" }).GetAwaiter().GetResult(); throw new Exception("should reject"); } catch (ArgumentException) { }
    var list = stack.PhpExtensionList("8.5");
    True(list.Single(m => m.Name == "openssl").Locked && !list.Single(m => m.Name == "zip").Enabled && list.Single(m => m.Name == "gd").Enabled);
    var again = new Stack(paths);
    True(again.Settings.PhpExtensions["8.5"].Contains("gd"), "choice survives a restart");
});
CheckS("stack: apache module toggles are saved, bases come along, unknown ones refused", () =>
{
    var paths = new ServerPaths(Path.Combine(tmpRoot, "home-mod"));
    using var stack = new Stack(paths);
    True(stack.ApacheModuleList() == null, "nothing to list before Apache is installed");
    var d = Path.Combine(paths.Bin, "apache", "2.4"); Directory.CreateDirectory(Path.Combine(d, "bin")); Directory.CreateDirectory(Path.Combine(d, "modules"));
    File.WriteAllText(Path.Combine(d, "bin", "httpd.exe"), "x");
    foreach (var m in new[] { "rewrite", "headers", "setenvif", "deflate", "cache", "cache_disk", "ssl", "proxy" }) File.WriteAllText(Path.Combine(d, "modules", $"mod_{m}.so"), "x");
    var before = stack.ApacheModuleList()!;
    True(before.Single(m => m.Name == "rewrite").Enabled && !before.Single(m => m.Name == "deflate").Enabled && before.Single(m => m.Name == "ssl").Locked);
    stack.SetApacheModulesAsync(new[] { "deflate", "cache_disk", "ssl" }).GetAwaiter().GetResult();
    var after = stack.ApacheModuleList()!;
    True(after.Single(m => m.Name == "deflate").Enabled && after.Single(m => m.Name == "cache").Enabled && !after.Single(m => m.Name == "rewrite").Enabled);
    True(!stack.Settings.ApacheModules!.Contains("ssl"), "required modules aren't stored as a choice");
    try { stack.SetApacheModulesAsync(new[] { "mod_nonsense" }).GetAwaiter().GetResult(); throw new Exception("should reject"); } catch (ArgumentException) { }
});
CheckS("bundle check: complete bundle passes; a missing Apache, missing file or bad checksum is reported", () =>
{
    var dir = Path.Combine(tmpRoot, "bundle-check"); Directory.CreateDirectory(dir);
    BundleEntry E(string id, string file, string? sha = null)
    {
        File.WriteAllText(Path.Combine(dir, file), "data-" + id);
        return new BundleEntry(id, "1.0", "1.0.0", file, sha ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("data-" + id))).ToLowerInvariant(), "Zip", "x.exe", null);
    }
    var all = BundleCheck.RequiredIds.Select(id => E(id, id + ".zip")).ToList();
    void Write(IEnumerable<BundleEntry> es) => File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new BundleManifest { Entries = es.ToList() }));
    Write(all); Eq(0, BundleCheck.Problems(dir).Count);
    Write(all.Where(e => e.Id != "apache"));
    True(BundleCheck.Problems(dir).Single().Contains("Apache is not in the bundle"));
    Write(all); File.Delete(Path.Combine(dir, "nginx.zip"));
    True(BundleCheck.Problems(dir).Single().Contains("missing"));
    Write(all.Select(e => e.Id == "php" ? e with { Sha256 = new string('0', 64) } : e)); File.WriteAllText(Path.Combine(dir, "nginx.zip"), "data-nginx");
    True(BundleCheck.Problems(dir).Single().Contains("corrupt"));
    True(BundleCheck.Problems(Path.Combine(tmpRoot, "no-such-bundle")).Single().Contains("Run bundle.cmd"));
});
CheckS("versions: installing a bundled series needs no network (the bundle is used first)", () =>
{
    var paths = new ServerPaths(Path.Combine(tmpRoot, "home-offline-apache"));
    var zip = MakeZip(("Apache24/bin/httpd.exe", "x"), ("Apache24/modules/mod_rewrite.so", "x"));
    var bdir = Path.Combine(tmpRoot, "bundle-offline"); Directory.CreateDirectory(bdir);
    File.WriteAllBytes(Path.Combine(bdir, "apache-2.4.66.zip"), zip);
    var entry = new BundleEntry("apache", "2.4", "2.4.66", "apache-2.4.66.zip",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zip)).ToLowerInvariant(), "Zip", "bin/httpd.exe", "Apache24");
    var bundle = new BundleSource(bdir, new BundleManifest { Entries = { entry } });
    using var stack = new Stack(paths, new Installer(paths, new HttpClient(new ThrowingHandler()), bundle));
    stack.InstallVersionAsync("apache", "2.4").GetAwaiter().GetResult();
    True(stack.IsSeriesInstalled("apache", "2.4") && stack.ApacheModuleList()!.Any(m => m.Name == "rewrite"));
});

static string FindUp(string rel)
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, rel))) return Path.Combine(d.FullName, rel);
    throw new FileNotFoundException(rel);
}


// ======================================================================== profiler
Console.WriteLine("Profiler");
static byte[] Pkt(byte seq, params byte[] payload) =>
    new byte[] { (byte)payload.Length, (byte)(payload.Length >> 8), (byte)(payload.Length >> 16), seq }.Concat(payload).ToArray();
static byte[] Cmd(byte cmd, string text) => Pkt(0, new[] { cmd }.Concat(System.Text.Encoding.UTF8.GetBytes(text)).ToArray());
static byte[] Ok() => Pkt(1, 0x00, 0, 0, 2, 0, 0, 0);
static byte[] Err(int code, string msg) =>
    Pkt(1, new byte[] { 0xFF, (byte)code, (byte)(code >> 8), (byte)'#' }.Concat(System.Text.Encoding.ASCII.GetBytes("42S02" + msg)).ToArray());
static void Feed(MySqlTap t, bool fromClient, byte[] bytes) { if (fromClient) t.FromClient(bytes, bytes.Length); else t.FromServer(bytes, bytes.Length); }

CheckS("packet reader: reassembles packets split across reads and reports sequence + length", () =>
{
    var got = new List<(byte seq, string text, int len)>();
    var r = new PacketReader();
    var data = Cmd(0x03, "SELECT 1").Concat(Pkt(5, 1, 2, 3)).Concat(Pkt(7)).ToArray();
    foreach (var b in data)                                            // worst case: one byte at a time
        r.Feed(new[] { b }, (seq, payload, len) => got.Add((seq, System.Text.Encoding.UTF8.GetString(payload.ToArray()), len)));
    Eq(3, got.Count);
    Eq((byte)0, got[0].seq); Eq(9, got[0].len); True(got[0].text.EndsWith("SELECT 1"));
    Eq((byte)5, got[1].seq); Eq(3, got[1].len);
    Eq((byte)7, got[2].seq); Eq(0, got[2].len);                        // empty packets still count
});
CheckS("tap: a query answered with OK becomes one record with its SQL and timings", () =>
{
    var store = new ProfileStore();
    var tap = new MySqlTap(1, 50000, store, () => (4242, "php-cgi"));
    Feed(tap, true, Cmd(0x03, "SELECT * FROM users WHERE id = 7"));
    Thread.Sleep(30);
    Feed(tap, false, Ok());
    var q = store.Snapshot(true).Queries.Single();
    Eq("SELECT * FROM users WHERE id = 7", q.Sql);
    True(q.FirstByteMs >= 25 && q.FirstByteMs < 1000, $"first byte {q.FirstByteMs}");
    Eq(4242, q.Pid!.Value); Eq("php-cgi", q.Process); True(q.Error == null && !q.Prepared);
});
CheckS("tap: errors carry code and message; a streaming result set grows the total time", () =>
{
    var store = new ProfileStore();
    var tap = new MySqlTap(1, 50001, store, () => (null, null));
    Feed(tap, true, Cmd(0x03, "SELECT * FROM nope"));
    Feed(tap, false, Err(1146, " Table 'x.nope' doesn't exist"));
    var bad = store.Snapshot(true).Queries.Single();
    True(bad.Error!.StartsWith("#1146") && bad.Error.Contains("doesn't exist"), bad.Error);

    Feed(tap, true, Cmd(0x03, "SELECT * FROM big"));
    Feed(tap, false, Pkt(1, 0x02));                                    // column count: a result set
    var q = store.Snapshot(true).Queries.First();
    var first = q.TotalMs;
    Thread.Sleep(40);
    Feed(tap, false, Pkt(2, 1, 2, 3, 4));                              // a row arrives later
    True(q.TotalMs >= first + 30, $"total {q.TotalMs} vs first {first}");
    Eq(q.FirstByteMs, q.FirstByteMs);
    True(q.FirstByteMs < q.TotalMs);
});
CheckS("tap: prepared statements show their SQL on EXECUTE; the PREPARE itself is not listed; CLOSE forgets it", () =>
{
    var store = new ProfileStore();
    var tap = new MySqlTap(1, 50002, store, () => (null, null));
    Feed(tap, true, Cmd(0x16, "SELECT name FROM users WHERE id = ?"));
    Feed(tap, false, Pkt(1, 0x00, 9, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0));    // PREPARE OK, statement id 9
    Eq(0, store.Snapshot(true).Queries.Length);
    Feed(tap, true, Pkt(0, 0x17, 9, 0, 0, 0, 0, 1, 0, 0, 0));           // EXECUTE #9
    Feed(tap, false, Ok());
    var q = store.Snapshot(true).Queries.Single();
    Eq("SELECT name FROM users WHERE id = ?", q.Sql); True(q.Prepared);
    Feed(tap, true, Pkt(0, 0x19, 9, 0, 0, 0));                          // CLOSE #9 (no reply)
    Feed(tap, true, Pkt(0, 0x17, 9, 0, 0, 0, 0, 1, 0, 0, 0));
    Feed(tap, false, Ok());
    True(store.Snapshot(true).Queries[0].Sql.Contains("#9"), "unknown id is labelled, not guessed");
});
CheckS("tap: handshake traffic is ignored and a long SQL text is truncated, not dropped", () =>
{
    var store = new ProfileStore();
    var tap = new MySqlTap(1, 50003, store, () => (null, null));
    Feed(tap, true, Pkt(1, new byte[40]));                              // client handshake response (seq 1)
    Feed(tap, false, Pkt(2, 0x00, 0, 0, 2, 0, 0, 0));                   // auth OK, no command pending
    Eq(0, store.Snapshot(true).Queries.Length);
    Feed(tap, true, Cmd(0x03, "INSERT INTO t VALUES ('" + new string('x', 40000) + "')"));
    Feed(tap, false, Ok());
    var q = store.Snapshot(true).Queries.Single();
    True(q.Sql.StartsWith("INSERT INTO t") && q.Sql.EndsWith("…") && q.Sql.Length < 20000);
});

await Check("proxy: forwards bytes untouched, clears TLS/compression in the greeting, logs queries with the owning process", async () =>
{
    // a tiny fake MariaDB: greeting, then OK for every command after a 30 ms "execution"
    var fakeDb = new TcpListener(IPAddress.Loopback, 0); fakeDb.Start();
    var dbPort = ((IPEndPoint)fakeDb.LocalEndpoint).Port;
    var dbTask = Task.Run(async () =>
    {
        using var c = await fakeDb.AcceptTcpClientAsync(); var ns = c.GetStream();
        var greet = new List<byte> { 10 }; greet.AddRange(System.Text.Encoding.ASCII.GetBytes("11.8.2-MariaDB")); greet.Add(0);
        greet.AddRange(new byte[] { 1, 0, 0, 0 }); greet.AddRange(new byte[8]); greet.Add(0);
        greet.AddRange(new byte[] { 0xFF, 0xFF }); greet.AddRange(new byte[20]);   // capabilities: everything on
        await ns.WriteAsync(Pkt(0, greet.ToArray()));
        var buf = new byte[4096];
        while (true)
        {
            var n = await ns.ReadAsync(buf); if (n == 0) break;
            await Task.Delay(30);
            await ns.WriteAsync(Ok());
        }
    });
    var store = new ProfileStore();
    using var proxy = new MySqlProxy(0, dbPort, store, new FakeOwners());
    proxy.Start();

    using var cl = new TcpClient(); await cl.ConnectAsync(IPAddress.Loopback, proxy.Port);
    var s = cl.GetStream(); s.ReadTimeout = 5000;
    var hdr = new byte[4]; await ReadAll(s, hdr);
    var payload = new byte[hdr[0] | (hdr[1] << 8)]; await ReadAll(s, payload);
    var capsAt = 1 + "11.8.2-MariaDB".Length + 1 + 4 + 8 + 1;
    Eq(0, payload[capsAt] & 0x20); Eq(0, payload[capsAt + 1] & 0x08);   // compression + TLS cleared
    Eq(0xDF, (int)payload[capsAt]); Eq(0xF7, (int)payload[capsAt + 1]); // nothing else touched

    await s.WriteAsync(Cmd(0x03, "SELECT SLEEP(0.03)"));
    var reply = new byte[Ok().Length]; await ReadAll(s, reply);
    True(reply.SequenceEqual(Ok()), "client receives the server's bytes unchanged");

    var q = store.Snapshot(true).Queries.Single();
    Eq("SELECT SLEEP(0.03)", q.Sql); Eq(4242, q.Pid!.Value); Eq("php-cgi", q.Process);
    True(q.FirstByteMs >= 25, $"measured {q.FirstByteMs} ms");
    cl.Dispose(); fakeDb.Stop();
    try { await dbTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
});
static async Task ReadAll(NetworkStream s, byte[] buf) { var got = 0; while (got < buf.Length) { var n = await s.ReadAsync(buf.AsMemory(got)); if (n == 0) throw new Exception("closed"); got += n; } }

await Check("proxy: an unreachable database drops the client instead of crashing the proxy", async () =>
{
    var dead = FreePort();
    using var proxy = new MySqlProxy(0, dead, new ProfileStore());
    proxy.Start();
    using var cl = new TcpClient(); await cl.ConnectAsync(IPAddress.Loopback, proxy.Port);
    var n = await cl.GetStream().ReadAsync(new byte[16]);
    Eq(0, n);                                                          // closed
    using var again = new TcpClient(); await again.ConnectAsync(IPAddress.Loopback, proxy.Port);   // still accepting
});

CheckS("access log: nginx and Apache lines parse; the profiler's own requests are skipped", () =>
{
    var ng = AccessLogTailer.Parse("1700000000.500\t0.250\t0.240\tGET\tiwf.test\t/matches?id=3\t200\t5120\tabc123", WebServerKind.Nginx)!;
    Eq("GET", ng.Method); Eq("iwf.test", ng.Host); Eq("/matches?id=3", ng.Uri); Eq(200, ng.Status);
    True(Math.Abs(ng.DurationMs - 250) < 0.01 && Math.Abs(ng.UpstreamMs!.Value - 240) < 0.01);
    Eq(1700000000500L, ng.EndedAt.ToUnixTimeMilliseconds()); Eq(1700000000250L, ng.StartedAt.ToUnixTimeMilliseconds());
    Eq("abc123", ng.RequestId);
    True(AccessLogTailer.Parse("1700000000.500\t0.010\t-\tGET\ta.test\t/\t304\t0\tx", WebServerKind.Nginx)!.UpstreamMs == null);
    True(Math.Abs(AccessLogTailer.Parse("1700000000.5\t0.1\t0.01, 0.09\tGET\ta.test\t/\t200\t0\tx", WebServerKind.Nginx)!.UpstreamMs!.Value - 10) < 0.01, "first upstream hop");

    var ap = AccessLogTailer.Parse("1700000001000\t125000\tPOST\tbible.test\t/api/save\t201\t88\t-", WebServerKind.Apache)!;
    Eq("POST", ap.Method); Eq(201, ap.Status); True(Math.Abs(ap.DurationMs - 125) < 0.01);
    Eq(1700000000875L, ap.StartedAt.ToUnixTimeMilliseconds());

    True(AccessLogTailer.Parse("1700000000.5\t0.1\t-\tGET\ta.test\t/__rs/beacon\t204\t0\tx", WebServerKind.Nginx) == null);
    True(AccessLogTailer.Parse("garbage", WebServerKind.Nginx) == null && AccessLogTailer.Parse("", WebServerKind.Apache) == null);
});
CheckS("access log: follows appended lines, waits for a half-written line, survives truncation", () =>
{
    var file = Path.Combine(tmpRoot, "tail", "profile.tsv"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.WriteAllText(file, "1700000000.000\t0.001\t-\tGET\told.test\t/old\t200\t0\tx\n");     // history: not replayed
    var store = new ProfileStore();
    using var tail = new AccessLogTailer(file, WebServerKind.Nginx, store); tail.Start();
    tail.PollOnce(); Eq(0, store.Snapshot(true).Requests.Length);

    File.AppendAllText(file, "1700000001.000\t0.100\t0.09\tGET\ta.test\t/one\t200\t10\tr1\n1700000002.000\t0.2");   // second line cut off
    tail.PollOnce(); Eq(1, store.Snapshot(true).Requests.Length);
    File.AppendAllText(file, "00\t0.19\tGET\ta.test\t/two\t200\t10\tr2\n");
    tail.PollOnce(); var reqs = store.Snapshot(true).Requests;
    Eq(2, reqs.Length); Eq("/two", reqs[0].Request.Uri); Eq("/one", reqs[1].Request.Uri);   // newest first

    File.WriteAllText(file, "1700000009.000\t0.050\t-\tGET\ta.test\t/after-rotate\t200\t1\tr3\n");   // log truncated
    tail.PollOnce(); Eq("/after-rotate", store.Snapshot(true).Requests[0].Request.Uri);
});
CheckS("store: keeps only the newest entries and matches queries to the request that was running", () =>
{
    var store = new ProfileStore(maxRequests: 3, maxQueries: 4);
    var t0 = DateTimeOffset.UtcNow;
    for (var i = 0; i < 5; i++)
        store.Add(new RequestRecord { StartedAt = t0.AddSeconds(i), DurationMs = 100, Method = "GET", Host = "a.test", Uri = "/" + i, Status = 200 });
    store.Add(new QueryRecord { StartedAt = t0.AddSeconds(4).AddMilliseconds(40), Sql = "SELECT 'in request 4'", TotalMs = 12 });
    store.Add(new QueryRecord { StartedAt = t0.AddSeconds(4).AddMilliseconds(500), Sql = "SELECT 'between requests'", TotalMs = 1 });
    var snap = store.Snapshot(true);
    Eq(3, snap.Requests.Length); Eq("/4", snap.Requests[0].Request.Uri); Eq(5L, snap.TotalRequests);
    Eq(1, snap.Requests[0].Queries.Count); Eq("SELECT 'in request 4'", snap.Requests[0].Queries[0].Sql); Eq(12.0, snap.Requests[0].SqlMs);
    Eq(0, snap.Requests[1].Queries.Count);
    for (var i = 0; i < 10; i++) store.Add(new QueryRecord { StartedAt = t0, Sql = "q" + i });
    Eq(4, store.Snapshot(true, maxQueries: 100).Queries.Length);
    store.Clear(); Eq(0, store.Snapshot(true).Requests.Length);
});
CheckS("config: profiling adds the profile log; off leaves logging off", () =>
{
    var st = new Settings();
    var site = new SiteSpec("a.test", @"C:\sites\a", null, "8.3", false, "", "");
    var off = ConfigWriter.Nginx(st, new[] { site }, @"C:\nginx", @"C:\etc\nginx", @"C:\etc\nginx\fastcgi_params");
    True(off.Contains("access_log off;") && !off.Contains("rs_profile"));
    var on = ConfigWriter.Nginx(st, new[] { site }, @"C:\nginx", @"C:\etc\nginx", @"C:\etc\nginx\fastcgi_params", true);
    True(on.Contains("log_format rs_profile") && on.Contains("access_log \"C:/etc/nginx/logs/profile.tsv\" rs_profile;") && !on.Contains("access_log off;"));
    True(on.Contains("$request_time") && on.Contains("$upstream_response_time") && on.Contains("$request_id"));
    var apOff = ConfigWriter.Apache(st, new[] { site }, @"C:\apache", @"C:\etc\apache");
    True(!apOff.Contains("CustomLog"));
    var apOn = ConfigWriter.Apache(st, new[] { site }, @"C:\apache", @"C:\etc\apache", true);
    True(apOn.Contains("LogFormat \"%{end:msec}t") && apOn.Contains("CustomLog \"C:/etc/apache/logs/profile.tsv\" rs_profile"));
});
CheckS("stack: with profiling on MariaDB moves to the internal port and the proxy owns the public one", () =>
{
    var paths = new ServerPaths(Path.Combine(tmpRoot, "home-prof"));
    using var stack = new Stack(paths);
    var d = Path.Combine(paths.Bin, "mariadb", "11.8"); Directory.CreateDirectory(Path.Combine(d, "bin"));
    File.WriteAllText(Path.Combine(d, Catalog.Shapes["mariadb"].Exe), "x");
    Eq(3306, stack.DbBackendPort); Eq(3306, stack.SpecFor("mariadb").ReadyPort!.Value);
    stack.Settings.ProfilingEnabled = true;
    Eq(3316, stack.DbBackendPort); Eq(3316, stack.SpecFor("mariadb").ReadyPort!.Value);
    True(stack.SpecFor("mariadb").Stop!.Args.Contains("--port=3316"), "shutdown goes straight to MariaDB, not through the proxy");
    Eq(3306, stack.Settings.MariaDbPort);                               // apps keep using the port they always did
    True(new Settings().ProfilingEnabled == false, "off by default");
});

// ===================================================================== dashboard API
Console.WriteLine("Dashboard API (real HTTP server, fake backend)");
const string Tok = "test-token-123";
var fake = new FakeBackend { StepMs = 60 };
using var server = new DashboardServer(fake, Tok);
server.Start(FreePort());
using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
HttpRequestMessage Req(HttpMethod m, string path, object? body = null, bool token = true)
{
    var r = new HttpRequestMessage(m, path);
    if (token) r.Headers.Add("X-Rhapsody-Server-Token", Tok);
    if (body != null) r.Content = JsonContent.Create(body);
    return r;
}
async Task<(HttpStatusCode, JsonElement)> Call(HttpRequestMessage r)
{
    using var res = await http.SendAsync(r);
    var txt = await res.Content.ReadAsStringAsync();
    return (res.StatusCode, txt.StartsWith('{') || txt.StartsWith('[') ? JsonDocument.Parse(txt).RootElement.Clone() : default);
}

await Check("page: served with a per-response CSP nonce that matches the inline tags", async () =>
{
    using var res = await http.GetAsync("/");
    var html = await res.Content.ReadAsStringAsync();
    var csp = res.Headers.GetValues("Content-Security-Policy").Single();
    var nonce = System.Text.RegularExpressions.Regex.Match(csp, "script-src 'nonce-([^']+)'").Groups[1].Value;
    True(nonce.Length > 10 && html.Contains($"<script nonce=\"{nonce}\">") && html.Contains($"<style nonce=\"{nonce}\">"));
    True(!html.Contains("__NONCE__") && csp.Contains("default-src 'none'") && csp.Contains("frame-ancestors 'none'"));
    using var res2 = await http.GetAsync("/");
    True(res2.Headers.GetValues("Content-Security-Policy").Single() != csp, "nonce changes per response");
    True(!html.Contains("style=\""), "no inline style attributes (CSP would block them)");
});
await Check("auth: missing/wrong token is 401, right token works", async () =>
{
    Eq(HttpStatusCode.Unauthorized, (await Call(Req(HttpMethod.Get, "/api/state", token: false))).Item1);
    var wrong = Req(HttpMethod.Get, "/api/state", token: false); wrong.Headers.Add("X-Rhapsody-Server-Token", "nope");
    Eq(HttpStatusCode.Unauthorized, (await Call(wrong)).Item1);
    var (code, body) = await Call(Req(HttpMethod.Get, "/api/state"));
    Eq(HttpStatusCode.OK, code); True(body.GetProperty("state").GetProperty("services").GetArrayLength() >= 4);
});
await Check("DNS rebinding: a foreign Host header is never served, even with the token", async () =>
{
    // The OS listener may refuse it before our handler (400/404) or our own check may (403). Either is a refusal.
    var r = Req(HttpMethod.Get, "/api/state"); r.Headers.Host = $"evil.example:{server.Port}";
    var (code, body) = await Call(r);
    True((int)code >= 400 && !body.ValueKind.Equals(JsonValueKind.Object) || !body.TryGetProperty("state", out _), $"served a foreign host: {code}");
    True((int)code is 400 or 403 or 404, $"unexpected status {code}");
    var p = new HttpRequestMessage(HttpMethod.Get, "/"); p.Headers.Host = "evil.example";
    True((int)(await Call(p)).Item1 >= 400, "page served to a foreign host");
    var ok = Req(HttpMethod.Get, "/api/state"); ok.Headers.Host = $"localhost:{server.Port}";
    Eq(HttpStatusCode.OK, (await Call(ok)).Item1);   // the friendly name works too
});
await Check("cross-site requests: a foreign Origin is refused even with the token; same origin is fine", async () =>
{
    var bad = Req(HttpMethod.Post, "/api/services/start-all"); bad.Headers.Add("Origin", "https://evil.example");
    Eq(HttpStatusCode.Forbidden, (await Call(bad)).Item1);
    Eq(ServiceStateOf("nginx"), "stopped");
    var good = Req(HttpMethod.Get, "/api/state"); good.Headers.Add("Origin", $"http://127.0.0.1:{server.Port}");
    Eq(HttpStatusCode.OK, (await Call(good)).Item1);
});
string ServiceStateOf(string id)
{
    var s = fake.GetState().Services.First(x => x.Id == id).State; return s;
}
await Check("operations: start-all waits for completion and state reflects it", async () =>
{
    var (code, body) = await Call(Req(HttpMethod.Post, "/api/services/start-all"));
    Eq(HttpStatusCode.OK, code); True(body.GetProperty("ok").GetBoolean());
    Eq("running", ServiceStateOf("nginx")); Eq("running", ServiceStateOf("mariadb"));
    var (_, st) = await Call(Req(HttpMethod.Get, "/api/state"));
    True(st.GetProperty("op").GetProperty("running").GetBoolean() == false);
    await Call(Req(HttpMethod.Post, "/api/services/nginx/stop"));
    Eq("stopped", ServiceStateOf("nginx"));
});
await Check("a second operation while one runs gets 409", async () =>
{
    var slow = Call(Req(HttpMethod.Post, "/api/services/start-all"));
    await Task.Delay(80);
    var (code, body) = await Call(Req(HttpMethod.Post, "/api/services/redis/stop"));
    Eq(HttpStatusCode.Conflict, code); True(body.GetProperty("error").GetString()!.Contains("Busy"));
    Eq(HttpStatusCode.OK, (await slow).Item1);
});
await Check("backend errors come back as 400 with the message; bad JSON is 400", async () =>
{
    var (code, body) = await Call(Req(HttpMethod.Post, "/api/sites/scaffold", new { name = "fail", php = "8.5" }));
    Eq(HttpStatusCode.BadRequest, code); True(body.GetProperty("error").GetString()!.Contains("composer create-project failed"));
    var (c2, b2) = await Call(Req(HttpMethod.Post, "/api/sites", new { name = "Bad_Name", root = "C:\\x", php = "8.5" }));
    Eq(HttpStatusCode.BadRequest, c2); True(b2.GetProperty("error").GetString()!.Contains("lowercase"));
    var raw = Req(HttpMethod.Post, "/api/sites"); raw.Content = new StringContent("{nope", System.Text.Encoding.UTF8, "application/json");
    Eq(HttpStatusCode.BadRequest, (await Call(raw)).Item1);
});
await Check("sites and versions flows", async () =>
{
    Eq(HttpStatusCode.OK, (await Call(Req(HttpMethod.Post, "/api/sites/scaffold", new { name = "new-app", php = "8.4" }))).Item1);
    var (_, st) = await Call(Req(HttpMethod.Get, "/api/state"));
    True(st.GetProperty("state").GetProperty("sites").EnumerateArray().Any(s => s.GetProperty("domain").GetString() == "new-app.test"));
    Eq(HttpStatusCode.OK, (await Call(Req(HttpMethod.Put, "/api/sites/new-app/php", new { php = "8.3" }))).Item1);
    Eq(HttpStatusCode.OK, (await Call(Req(HttpMethod.Delete, "/api/sites/new-app"))).Item1);
    Eq(HttpStatusCode.OK, (await Call(Req(HttpMethod.Post, "/api/versions/install", new { id = "php", series = "8.4" }))).Item1);
    var (_, v) = await Call(Req(HttpMethod.Get, "/api/versions"));
    var php = v.EnumerateArray().First(c => c.GetProperty("id").GetString() == "php");
    var v84 = php.GetProperty("versions").EnumerateArray().First(x => x.GetProperty("series").GetString() == "8.4");
    True(v84.GetProperty("installed").GetBoolean() && v84.GetProperty("removable").GetBoolean());
    Eq(HttpStatusCode.OK, (await Call(Req(HttpMethod.Post, "/api/versions/activate", new { id = "php", series = "8.4" }))).Item1);
    Eq(HttpStatusCode.OK, (await Call(Req(HttpMethod.Post, "/api/versions/activate", new { id = "php", series = "8.5" }))).Item1);
});
await Check("logs: valid ids work, traversal attempts do not", async () =>
{
    Eq(HttpStatusCode.OK, (await Call(Req(HttpMethod.Get, "/api/logs/mariadb?lines=50"))).Item1);
    Eq(HttpStatusCode.NotFound, (await Call(Req(HttpMethod.Get, "/api/logs/..%2F..%2Fetc%2Fpasswd"))).Item1);
    Eq(HttpStatusCode.NotFound, (await Call(Req(HttpMethod.Get, "/api/nope"))).Item1);
});
await Check("event stream: needs the token; streams op + changed events live", async () =>
{
    using var denied = await http.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead);
    Eq(HttpStatusCode.Unauthorized, denied.StatusCode);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var res = await http.GetAsync($"/api/events?t={Tok}", HttpCompletionOption.ResponseHeadersRead, cts.Token);
    Eq("text/event-stream", res.Content.Headers.ContentType!.MediaType);
    using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cts.Token));
    var seen = new List<string>();
    var pump = Task.Run(async () => { while (await reader.ReadLineAsync(cts.Token) is { } line) lock (seen) seen.Add(line); });
    await Task.Delay(150);
    await Call(Req(HttpMethod.Post, "/api/services/redis/start"));
    await Task.Delay(200);
    cts.Cancel(); try { await pump; } catch (OperationCanceledException) { }
    string all; lock (seen) all = string.Join("\n", seen);
    True(all.Contains(": connected") && all.Contains("event: op") && all.Contains("event: changed"), all);
    True(all.Contains("Starting redis"), "progress message streamed");
});

await Check("profile API: token required; enable/disable run as operations; data and clear work", async () =>
{
    Eq(HttpStatusCode.Unauthorized, (await Call(Req(HttpMethod.Get, "/api/profile", token: false))).Item1);
    var (code, off) = await Call(Req(HttpMethod.Get, "/api/profile"));
    Eq(HttpStatusCode.OK, code); True(!off.GetProperty("enabled").GetBoolean());

    Eq(HttpStatusCode.OK, (await Call(Req(HttpMethod.Post, "/api/profile/enable"))).Item1);
    fake.Store.Add(new RequestRecord { StartedAt = DateTimeOffset.UtcNow, DurationMs = 80, Method = "GET", Host = "iwf.test", Uri = "/rankings", Status = 200 });
    fake.Store.Add(new QueryRecord { StartedAt = DateTimeOffset.UtcNow, Sql = "SELECT * FROM wrestlers", TotalMs = 7, FirstByteMs = 6 });
    var (_, on) = await Call(Req(HttpMethod.Get, "/api/profile"));
    True(on.GetProperty("enabled").GetBoolean());
    Eq("/rankings", on.GetProperty("requests")[0].GetProperty("request").GetProperty("uri").GetString());
    Eq("SELECT * FROM wrestlers", on.GetProperty("queries")[0].GetProperty("sql").GetString());

    Eq(HttpStatusCode.OK, (await Call(Req(HttpMethod.Post, "/api/profile/clear"))).Item1);
    Eq(0, (await Call(Req(HttpMethod.Get, "/api/profile"))).Item2.GetProperty("requests").GetArrayLength());
    Eq(HttpStatusCode.OK, (await Call(Req(HttpMethod.Post, "/api/profile/disable"))).Item1);
    True(!(await Call(Req(HttpMethod.Get, "/api/profile"))).Item2.GetProperty("enabled").GetBoolean());
});
await Check("profile API: a foreign Origin can't read recorded SQL or toggle profiling", async () =>
{
    var r = Req(HttpMethod.Get, "/api/profile"); r.Headers.Add("Origin", "https://evil.example");
    Eq(HttpStatusCode.Forbidden, (await Call(r)).Item1);
    var w = Req(HttpMethod.Post, "/api/profile/enable"); w.Headers.Add("Origin", "https://evil.example");
    Eq(HttpStatusCode.Forbidden, (await Call(w)).Item1);
    True(!fake.Profiling);
});
await Check("profile API: new activity is pushed over the event stream, throttled", async () =>
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var res = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/events?t={Tok}"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
    using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cts.Token));
    var seen = new List<string>();
    var pump = Task.Run(async () => { while (await reader.ReadLineAsync(cts.Token) is { } line) lock (seen) seen.Add(line); });
    await Task.Delay(150);
    for (var i = 0; i < 50; i++) fake.Store.Add(new QueryRecord { StartedAt = DateTimeOffset.UtcNow, Sql = "SELECT " + i });
    fake.RaiseProfileChanged();
    await Task.Delay(500);
    cts.Cancel(); try { await pump; } catch (OperationCanceledException) { }
    string all; lock (seen) all = string.Join("\n", seen);
    var n = System.Text.RegularExpressions.Regex.Matches(all, "event: profile").Count;
    True(n >= 1 && n <= 3, $"expected a handful of profile events, got {n}");
});


Console.WriteLine("Dashboard: folder picker, modules, branding");
await Check("pick-folder: needs the token; returns the chosen path; cancel gives null; one window at a time", async () =>
{
    Eq(HttpStatusCode.Unauthorized, (await Call(Req(HttpMethod.Post, "/api/pick-folder", new { initial = "C:\\x" }, token: false))).Item1);
    fake.NextPick = @"C:\Users\andrew\Sites\My Blog";
    var (code, body) = await Call(Req(HttpMethod.Post, "/api/pick-folder", new { initial = @"C:\Users\andrew" }));
    Eq(HttpStatusCode.OK, code); Eq(@"C:\Users\andrew\Sites\My Blog", body.GetProperty("path").GetString()); Eq(@"C:\Users\andrew", fake.PickedFrom);
    fake.NextPick = null;
    var (_, none) = await Call(Req(HttpMethod.Post, "/api/pick-folder", new { }));
    Eq(JsonValueKind.Null, none.GetProperty("path").ValueKind);
    fake.PickDelayMs = 300; fake.NextPick = "C:\\a";
    var first = Call(Req(HttpMethod.Post, "/api/pick-folder", new { }));
    await Task.Delay(100);
    Eq(HttpStatusCode.Conflict, (await Call(Req(HttpMethod.Post, "/api/pick-folder", new { }))).Item1);
    Eq(HttpStatusCode.OK, (await first).Item1);
    fake.PickDelayMs = 0;
    var cross = Req(HttpMethod.Post, "/api/pick-folder", new { }); cross.Headers.Add("Origin", "https://evil.example");
    Eq(HttpStatusCode.Forbidden, (await Call(cross)).Item1);
    fake.CanPickFolder = false;
    Eq(HttpStatusCode.NotImplemented, (await Call(Req(HttpMethod.Post, "/api/pick-folder", new { }))).Item1);
    var (_, st) = await Call(Req(HttpMethod.Get, "/api/state"));
    True(!st.GetProperty("state").GetProperty("canPickFolder").GetBoolean(), "UI hides Browse when the host can't show a window");
    fake.CanPickFolder = true;
    var (_, st2) = await Call(Req(HttpMethod.Get, "/api/state"));
    True(st2.GetProperty("state").GetProperty("canPickFolder").GetBoolean());
});
await Check("modules API: list PHP extensions per version, toggle them, errors come back as 400", async () =>
{
    var (code, body) = await Call(Req(HttpMethod.Get, "/api/modules/php"));
    Eq(HttpStatusCode.OK, code); Eq("8.5", body.GetProperty("branch").GetString());
    var branches = body.GetProperty("branches").EnumerateArray().Select(x => x.GetString()).ToArray();
    True(new[] { "8.5", "8.3", "8.1" }.All(branches.Contains), "installed branches: " + string.Join(",", branches));
    var openssl = body.GetProperty("extensions").EnumerateArray().First(e => e.GetProperty("name").GetString() == "openssl");
    True(openssl.GetProperty("locked").GetBoolean() && openssl.GetProperty("enabled").GetBoolean(), "openssl locked+on: " + openssl);
    var (_, other) = await Call(Req(HttpMethod.Get, "/api/modules/php?branch=8.3")); Eq("8.3", other.GetProperty("branch").GetString());
    var (_, fallback) = await Call(Req(HttpMethod.Get, "/api/modules/php?branch=5.6")); Eq("8.5", fallback.GetProperty("branch").GetString());

    var (c2, _) = await Call(Req(HttpMethod.Put, "/api/modules/php/8.5", new { enabled = new[] { "gd", "intl" } }));
    Eq(HttpStatusCode.OK, c2);
    var (_, after) = await Call(Req(HttpMethod.Get, "/api/modules/php?branch=8.5"));
    string[] on = after.GetProperty("extensions").EnumerateArray().Where(e => e.GetProperty("enabled").GetBoolean()).Select(e => e.GetProperty("name").GetString()!).ToArray();
    True(on.Contains("gd") && on.Contains("intl") && on.Contains("openssl") && !on.Contains("zip"), string.Join(",", on));
    var (c3, b3) = await Call(Req(HttpMethod.Put, "/api/modules/php/8.5", new { enabled = new[] { "nope" } }));
    Eq(HttpStatusCode.BadRequest, c3); True(b3.GetProperty("error").GetString()!.Contains("no extension named nope"));
    Eq(HttpStatusCode.NotFound, (await Call(Req(HttpMethod.Put, "/api/modules/php/not-a-version", new { enabled = new string[0] }))).Item1);
    Eq(HttpStatusCode.Unauthorized, (await Call(Req(HttpMethod.Put, "/api/modules/php/8.5", new { enabled = new string[0] }, token: false))).Item1);

    var (_, ap) = await Call(Req(HttpMethod.Get, "/api/modules/apache"));
    True(ap.GetProperty("installed").GetBoolean() && !ap.GetProperty("serving").GetBoolean());
    var (c4, _) = await Call(Req(HttpMethod.Put, "/api/modules/apache", new { enabled = new[] { "deflate", "cache_disk" } }));
    Eq(HttpStatusCode.OK, c4);
    var (_, ap2) = await Call(Req(HttpMethod.Get, "/api/modules/apache"));
    var mods = ap2.GetProperty("modules").EnumerateArray().ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("enabled").GetBoolean());
    True(mods["deflate"] && mods["cache"] && !mods["rewrite"] && mods["ssl"], "bases come along; required stay on");
});
await Check("sites API: blank public folder stays blank (root), a typed folder is kept", async () =>
{
    Eq(HttpStatusCode.OK, (await Call(Req(HttpMethod.Post, "/api/sites", new { name = "rootsite", root = @"C:\p", publicDir = "", php = "8.5" }))).Item1);
    Eq(HttpStatusCode.OK, (await Call(Req(HttpMethod.Post, "/api/sites", new { name = "pubsite", root = @"C:\q", publicDir = "public", php = "8.5" }))).Item1);
    var sites = fake.GetState().Sites;
    Eq("", sites.First(x => x.Name == "rootsite").PublicDir); Eq("public", sites.First(x => x.Name == "pubsite").PublicDir);
    Eq("", sites.First(x => x.Name == "rhapsody").PublicDir);
});
await Check("branding: logo and icon are served (image/png, no token needed); nothing else under /assets", async () =>
{
    foreach (var f in new[] { "logo.png", "icon.png" })
    {
        using var r = await http.GetAsync("/assets/" + f);
        Eq(HttpStatusCode.OK, r.StatusCode); Eq("image/png", r.Content.Headers.ContentType!.MediaType);
        var bytes = await r.Content.ReadAsByteArrayAsync();
        True(bytes.Length > 1000 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G', "a real PNG");
    }
    foreach (var bad in new[] { "/assets/nope.png", "/assets/", "/assets/logo.png/x", "/assets/..%2fWeb%2findex.html" })
        True((int)(await http.GetAsync(bad)).StatusCode is 400 or 404 or 200 && !(await (await http.GetAsync(bad)).Content.ReadAsStringAsync()).Contains("<html"), "only the allow-list: " + bad);
    var html = await (await http.GetAsync("/")).Content.ReadAsStringAsync();
    True(html.Contains("/assets/logo.png") && html.Contains("rel=\"icon\"") && html.Contains("href=\"#/modules\"") && html.Contains("id=\"btnBrowse\""));
});

try { Directory.Delete(tmpRoot, true); } catch { }
Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class FakeOwners : IConnectionOwners
{
    public (int? Pid, string? Name) Lookup(int clientPort, int serverPort) => (4242, "php-cgi");
}

sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
        throw new HttpRequestException("network disabled in test");
}

sealed class MapHandler(Dictionary<string, byte[]> map) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
        Task.FromResult(map.TryGetValue(r.RequestUri!.ToString(), out var b)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
}
