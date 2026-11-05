using System.Text.RegularExpressions;

namespace RhapsodyServer.Core;

/// <param name="DbName">Database to create. Blank = the site name with underscores.</param>
/// <param name="DbUser">Database user. Blank = root, which has no password in this stack.</param>
/// <param name="DbPass">Password for a non-root user.</param>
public sealed record ScaffoldRequest(string Name, string Php, string? ParentDir = null, string? Package = null,
    string? DbName = null, string? DbUser = null, string? DbPass = null);

/// <summary>
/// One action: composer create-project, database, .env, vhost, certificate, hosts entry, reload.
/// The package name comes from Settings.RhapsodyPackage so it can change without a rebuild.
/// .env key names follow a real Rhapsody .env (DB_NAME/DB_USER/DB_PASS, MAIL_*, REDIS_*, APP_KEY...).
/// </summary>
public static class RhapsodyScaffold
{
    public static string DbName(string name) => name.Replace('-', '_');

    /// <summary>
    /// Updates keys that exist, appends keys that don't, leaves everything else alone.
    /// An existing line keeps its quoting style and any trailing "# comment" (Rhapsody's .env quotes most values).
    /// </summary>
    public static string UpsertEnv(string content, IReadOnlyDictionary<string, string> values)
    {
        var nl = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = content.Length == 0
            ? new List<string>()
            : content.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();

        foreach (var (key, value) in values)
        {
            var rx = new Regex(
                $@"^(?<pre>\s*{Regex.Escape(key)}\s*=\s*)(?<val>""(?:[^""\\]|\\.)*""|'[^']*'|[^#]*?)(?<post>\s+#.*)?\s*$");
            var i = lines.FindIndex(l => rx.IsMatch(l));
            if (i < 0) { lines.Add($"{key}={QuoteIfNeeded(value)}"); continue; }

            var m = rx.Match(lines[i]);
            var wasQuoted = m.Groups["val"].Value.StartsWith('"');
            var rendered = wasQuoted ? $"\"{value.Replace("\"", "\\\"")}\"" : QuoteIfNeeded(value, keepEmptyBare: true);
            lines[i] = m.Groups["pre"].Value + rendered + m.Groups["post"].Value;
        }
        return string.Join(nl, lines) + nl;
    }

    static string QuoteIfNeeded(string v, bool keepEmptyBare = false) =>
        v.Length == 0 ? (keepEmptyBare ? "" : "\"\"")
        : v.IndexOfAny(new[] { ' ', '#', '"' }) >= 0 ? "\"" + v.Replace("\"", "\\\"") + "\""
        : v;

    /// <summary>"my-app" -> "My App"</summary>
    public static string TitleOf(string name) =>
        string.Join(' ', name.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    /// <summary>Rhapsody's APP_KEY is 64 random bytes as hex (128 characters).</summary>
    public static string NewAppKey() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)).ToLowerInvariant();

    /// <summary>The keys Rhapsody's .env uses, pointed at this stack. Mail goes to Mailpit; DB and Redis have no password.</summary>
    public static Dictionary<string, string> EnvValues(Settings s, string siteName, string domain,
        string? dbName = null, string? dbUser = null, string? dbPass = null, string? appKey = null) => new()
    {
        ["APP_URL"] = $"https://{domain}",
        ["APP_NAME"] = TitleOf(siteName),
        ["APP_KEY"] = appKey ?? NewAppKey(),
        ["DB_HOST"] = "127.0.0.1",          // not "localhost": on Windows that can try IPv6 first and stall
        ["DB_PORT"] = s.MariaDbPort.ToString(),
        ["DB_NAME"] = string.IsNullOrWhiteSpace(dbName) ? DbName(siteName) : dbName,
        ["DB_USER"] = string.IsNullOrWhiteSpace(dbUser) ? "root" : dbUser,
        ["DB_PASS"] = dbPass ?? "",
        ["DB_DRIVER"] = "mysql",
        ["MAIL_TRANSPORT"] = "smtp",
        ["MAIL_HOST"] = "127.0.0.1",
        ["MAIL_PORT"] = s.SmtpPort.ToString(),
        ["MAIL_USERNAME"] = "",
        ["MAIL_PASSWORD"] = "",
        ["MAIL_FROM_ADDRESS"] = $"hello@{domain}",
        ["MAIL_FROM_NAME"] = TitleOf(siteName),
        ["REDIS_HOST"] = "127.0.0.1",
        ["REDIS_PORT"] = s.RedisPort.ToString(),
        ["REDIS_PASSWORD"] = ""
    };

    static readonly Regex DbIdent = new("^[A-Za-z0-9_]{1,64}$");
    static readonly Regex DbUserRx = new("^[A-Za-z0-9_]{1,32}$");

    /// <summary>SQL for a string literal in single quotes.</summary>
    public static string SqlString(string v) => "'" + v.Replace("\\", "\\\\").Replace("'", "''") + "'";

    /// <summary>
    /// What to run, as root, so the database (and the user, when it isn't root) exist before anything connects to them.
    /// Re-running is harmless: databases and users are only created when missing, and the user's password is set to the given one.
    /// </summary>
    public static string DatabaseSql(string db, string user, string pass)
    {
        var sql = $"CREATE DATABASE IF NOT EXISTS `{db}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;";
        if (user == "root") return sql;
        var hosts = new[] { "127.0.0.1", "localhost" };
        foreach (var h in hosts)
            sql += $" CREATE USER IF NOT EXISTS {SqlString(user)}@{SqlString(h)} IDENTIFIED BY {SqlString(pass)};" +
                   $" ALTER USER {SqlString(user)}@{SqlString(h)} IDENTIFIED BY {SqlString(pass)};" +
                   $" GRANT ALL PRIVILEGES ON `{db}`.* TO {SqlString(user)}@{SqlString(h)};";
        return sql + " FLUSH PRIVILEGES;";
    }

    /// <summary>Checks the optional database fields and fills in the defaults.</summary>
    public static (string Db, string User, string Pass) ResolveDb(ScaffoldRequest req)
    {
        var db = string.IsNullOrWhiteSpace(req.DbName) ? DbName(req.Name) : req.DbName.Trim();
        var user = string.IsNullOrWhiteSpace(req.DbUser) ? "root" : req.DbUser.Trim();
        var pass = req.DbPass ?? "";
        if (!DbIdent.IsMatch(db)) throw new ArgumentException("Database name: letters, digits and underscores only (up to 64).");
        if (!DbUserRx.IsMatch(user)) throw new ArgumentException("Database user: letters, digits and underscores only (up to 32).");
        if (pass.Any(char.IsControl)) throw new ArgumentException("The database password can't contain line breaks or control characters.");
        if (user == "root" && pass.Length > 0)
            throw new ArgumentException("root has no password in Rhapsody Server. Leave the password blank, or choose another database user.");
        return (db, user, pass);
    }

    /// <summary>
    /// The skeleton's own Composer scripts, in the order Composer would have run them during create-project.
    /// Only the ones the project actually defines.
    /// </summary>
    public static IReadOnlyList<string> ScriptsToRun(string composerJson)
    {
        string[] order = { "post-root-package-install", "post-autoload-dump", "post-install-cmd", "post-create-project-cmd" };
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(composerJson);
            if (!doc.RootElement.TryGetProperty("scripts", out var sc) || sc.ValueKind != System.Text.Json.JsonValueKind.Object)
                return Array.Empty<string>();
            return order.Where(n => sc.TryGetProperty(n, out _)).ToList();
        }
        catch (System.Text.Json.JsonException) { return Array.Empty<string>(); }
    }

    public static async Task<Site> RunAsync(Stack stack, ScaffoldRequest req, IProgress<string>? p = null, CancellationToken ct = default)
    {
        var s = stack.Settings;
        if (!Stack.NameRx.IsMatch(req.Name))
            throw new ArgumentException("Site name: lowercase letters, digits and hyphens only (e.g. 'my-app').");
        var (db, dbUser, dbPass) = ResolveDb(req);

        var parent = req.ParentDir ?? s.ResolveSitesRoot();
        var dir = Path.Combine(parent, req.Name);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new IOException($"{dir} already exists and isn't empty.");
        Directory.CreateDirectory(parent);
        var package = req.Package ?? s.RhapsodyPackage;

        await stack.EnsurePhpAsync(req.Php, p, ct);
        await stack.Installer.EnsureAsync(Catalog.Composer, p, ct);

        // The skeleton's own install scripts run database migrations (Phinx) while composer is still creating the project,
        // before there is a .env to read. So the database has to be up, created and reachable first, and the
        // credentials have to reach those scripts as environment variables (Phinx and dotenv both honour them over an empty .env).
        p?.Report("Starting MariaDB...");
        await stack.StartServiceAsync("mariadb", p, ct);

        p?.Report($"Creating database {db}...");
        var r = await ProcessRunner.RunAsync(stack.MariaDbCli(),
            new[] { "-h127.0.0.1", $"-P{s.MariaDbPort}", "-uroot", "-e", DatabaseSql(db, dbUser, dbPass) },
            ct: ct);
        if (!r.Ok) throw new InvalidOperationException("Creating the database failed:\n" + r.Output);

        var values = EnvValues(s, req.Name, stack.Domain(req.Name), db, dbUser, dbPass);
        var env = stack.PhpEnv(req.Php, forComposer: true).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (k, v) in values) env[k] = v;

        // Composer runs the skeleton's install scripts (which run the Phinx migrations) right after downloading, and Phinx
        // reads its credentials from the project's .env, not from environment variables. So the download happens with scripts
        // off, our .env is written, and only then are the skeleton's scripts run, in Composer's own order.
        p?.Report($"Creating project from {package}...");
        var php = stack.PhpExe(req.Php);
        var composer = stack.Installer.PathOf(Catalog.Composer);
        var preExisted = Directory.Exists(dir);
        r = await ProcessRunner.RunAsync(php,
            new[] { composer, "create-project", package, dir, "--no-interaction", "--prefer-dist", "--no-scripts" },
            parent, env, TimeSpan.FromMinutes(15), ct);
        if (!r.Ok)
        {
            // A half-downloaded folder would make the next attempt fail with "already exists and isn't empty".
            if (!preExisted) { try { Directory.Delete(dir, true); } catch { } }
            throw new InvalidOperationException($"composer create-project failed (exit code {r.ExitCode}):\n" + r.Output.Trim());
        }

        var envPath = Path.Combine(dir, ".env");
        var example = Path.Combine(dir, ".env.example");
        var scripts = ScriptsToRun(File.ReadAllText(Path.Combine(dir, "composer.json")));

        void WriteEnv()
        {
            if (!File.Exists(envPath) && File.Exists(example)) File.Copy(example, envPath);
            var existing = File.Exists(envPath) ? File.ReadAllText(envPath) : "";
            File.WriteAllText(envPath, UpsertEnv(existing, values));   // the same values (including APP_KEY) the scripts will read
        }

        WriteEnv();   // before any script runs, so Phinx finds the real database, user and password
        foreach (var script in scripts)
        {
            p?.Report($"Running {script}...");
            r = await ProcessRunner.RunAsync(php, new[] { composer, "run-script", script, "--no-interaction" }, dir, env, TimeSpan.FromMinutes(10), ct);
            if (!r.Ok) throw new InvalidOperationException($"The project was downloaded to {dir}, but its \"{script}\" step failed (exit code {r.ExitCode}):\n" + r.Output.Trim());
            WriteEnv();   // a script may rewrite .env; put our values back
        }

        p?.Report("Starting services...");
        await stack.StartAllAsync(p, ct);

        p?.Report("Registering site...");
        // Rhapsody's front controller (index.php) is in the project root, not in public/, so the docroot is the root.
        return await stack.AddSiteAsync(new Site { Name = req.Name, Root = dir, PublicDir = "", Php = req.Php, Https = true }, p, ct);
    }
}
