using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhapsodyServer.Core;

public enum WebServerKind { Nginx, Apache }

public sealed class Site
{
    public string Name { get; set; } = "";            // "myapp" -> myapp.test
    public string Root { get; set; } = "";            // project folder
    public string PublicDir { get; set; } = "";       // docroot relative to Root ("" = the project folder itself)
    public string Php { get; set; } = "8.3";          // PHP branch
    public bool Https { get; set; } = true;

    /// <summary>
    /// What the user types in the "public folder" box. Blank, "/" and "." all mean "serve the project folder itself"
    /// (Rhapsody and other apps with a root front controller). Anything else is a sub-folder like "public" or "web/public".
    /// Throws for paths that would leave the project folder.
    /// </summary>
    public static string NormalizePublicDir(string? raw)
    {
        var t = (raw ?? "").Trim().Trim('"').Trim();
        if (t.Length == 0) return "";
        if (t.Contains(':') || t.StartsWith(@"\\"))
            throw new ArgumentException("The public folder is relative to the project, e.g. \"public\". Leave it blank to serve the project folder itself.");
        var parts = t.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x != ".").ToList();
        if (parts.Any(x => x == ".."))
            throw new ArgumentException("The public folder has to be inside the project folder (no \"..\").");
        if (parts.Any(x => x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("The public folder name has characters Windows doesn't allow.");
        return string.Join('/', parts);
    }
}

public sealed class Settings
{
    public string Tld { get; set; } = "test";
    public WebServerKind WebServer { get; set; } = WebServerKind.Nginx;
    public string DefaultPhp { get; set; } = "8.3";
    public string Timezone { get; set; } = "UTC";
    public string SitesRoot { get; set; } = "";
    public string RhapsodyPackage { get; set; } = "arout/rhapsody-app";

    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
    public int MariaDbPort { get; set; } = 3306;
    public int RedisPort { get; set; } = 6379;
    public int SmtpPort { get; set; } = 1025;
    public int MailUiPort { get; set; } = 8025;
    public int PhpWorkersPerVersion { get; set; } = 2;

    /// <summary>
    /// Built-in profiler. When on, MariaDB moves to <see cref="MariaDbInternalPort"/> and a recording proxy
    /// takes over <see cref="MariaDbPort"/>, so apps keep using the port they always did.
    /// </summary>
    public bool ProfilingEnabled { get; set; }
    public int MariaDbInternalPort { get; set; } = 3316;

    public int UiPort { get; set; } = 7766;
    public string UiToken { get; set; } = "";   // generated once; lets a bookmarked dashboard keep working

    /// <summary>Which installed series is active for apache / nginx / mariadb (PHP uses DefaultPhp).</summary>
    public Dictionary<string, string> Active { get; set; } = new();

    public bool CaInstalled { get; set; }
    public string PmaSecret { get; set; } = "";
    public Dictionary<string, string> Versions { get; set; } = new();
    public List<Site> Sites { get; set; } = new();

    /// <summary>PHP extensions the user enabled, per PHP branch. A branch that isn't listed uses the defaults.</summary>
    public Dictionary<string, List<string>> PhpExtensions { get; set; } = new();

    /// <summary>Optional Apache modules the user enabled. Null means the defaults (rewrite, headers, setenvif).</summary>
    public List<string>? ApacheModules { get; set; }

    public string ResolveSitesRoot() => string.IsNullOrWhiteSpace(SitesRoot)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Sites")
        : SitesRoot;
}

public static class SettingsStore
{
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static Settings Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Opts) ?? new Settings()
            : new Settings();

    public static void Save(string path, Settings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opts));
        File.Move(tmp, path, overwrite: true);   // atomic replace: never a half-written file
    }
}
