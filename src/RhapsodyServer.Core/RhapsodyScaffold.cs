using System.Text.RegularExpressions;

namespace RhapsodyServer.Core;

public sealed record ScaffoldRequest(string Name, string Php, string? ParentDir = null, string? Package = null);

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
    public static Dictionary<string, string> EnvValues(Settings s, string siteName, string domain) => new()
    {
        ["APP_URL"] = $"https://{domain}",
        ["APP_NAME"] = TitleOf(siteName),
        ["APP_KEY"] = NewAppKey(),
        ["DB_HOST"] = "127.0.0.1",          // not "localhost": on Windows that can try IPv6 first and stall
        ["DB_PORT"] = s.MariaDbPort.ToString(),
        ["DB_NAME"] = DbName(siteName),
        ["DB_USER"] = "root",
        ["DB_PASS"] = "",
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

    public static async Task<Site> RunAsync(Stack stack, ScaffoldRequest req, IProgress<string>? p = null, CancellationToken ct = default)
    {
        var s = stack.Settings;
        if (!Stack.NameRx.IsMatch(req.Name))
            throw new ArgumentException("Site name: lowercase letters, digits and hyphens only (e.g. 'my-app').");

        var parent = req.ParentDir ?? s.ResolveSitesRoot();
        var dir = Path.Combine(parent, req.Name);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new IOException($"{dir} already exists and isn't empty.");
        Directory.CreateDirectory(parent);
        var package = req.Package ?? s.RhapsodyPackage;

        await stack.EnsurePhpAsync(req.Php, p, ct);
        await stack.Installer.EnsureAsync(Catalog.Composer, p, ct);

        p?.Report($"Creating project from {package}...");
        var r = await ProcessRunner.RunAsync(
            stack.PhpExe(req.Php),
            new[] { stack.Installer.PathOf(Catalog.Composer), "create-project", package, dir, "--no-interaction", "--prefer-dist" },
            parent, stack.PhpEnv(req.Php, forComposer: true), TimeSpan.FromMinutes(15), ct);
        if (!r.Ok) throw new InvalidOperationException($"composer create-project failed (exit code {r.ExitCode}):\n" + r.Output.Trim());

        p?.Report("Starting services...");
        await stack.StartAllAsync(p, ct);

        var db = DbName(req.Name);
        p?.Report($"Creating database {db}...");
        r = await ProcessRunner.RunAsync(stack.MariaDbCli(),
            new[] { "-h127.0.0.1", $"-P{s.MariaDbPort}", "-uroot", "-e",
                    $"CREATE DATABASE IF NOT EXISTS `{db}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci" },
            ct: ct);
        if (!r.Ok) throw new InvalidOperationException("Creating the database failed:\n" + r.Output);

        var envPath = Path.Combine(dir, ".env");
        var example = Path.Combine(dir, ".env.example");
        if (!File.Exists(envPath) && File.Exists(example)) File.Copy(example, envPath);
        var env = File.Exists(envPath) ? File.ReadAllText(envPath) : "";
        File.WriteAllText(envPath, UpsertEnv(env, EnvValues(s, req.Name, stack.Domain(req.Name))));

        p?.Report("Registering site...");
        // Rhapsody's front controller (index.php) is in the project root, not in public/, so the docroot is the root.
        return await stack.AddSiteAsync(new Site { Name = req.Name, Root = dir, PublicDir = "", Php = req.Php, Https = true }, p, ct);
    }
}
