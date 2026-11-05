using System.Text.RegularExpressions;

namespace RhapsodyServer.Core;

/// <summary>One toggle on the Modules page.</summary>
/// <param name="Locked">On and can't be turned off, because something Rhapsody Server itself runs needs it.</param>
public sealed record ModuleInfo(string Name, string Description, bool Enabled, bool Locked, string? Note = null);

/// <summary>
/// Which PHP extensions exist for a PHP build and which of them are switched on. Pure helpers: the Stack
/// reads the disk and stores the choice, ConfigWriter turns it into php.ini lines.
/// </summary>
public static class PhpExtensions
{
    /// <summary>
    /// Always on. Composer needs openssl and curl, phpMyAdmin (which runs on the default PHP) needs mbstring and mysqli.
    /// Letting these be switched off would only break the tools Rhapsody Server provides.
    /// </summary>
    public static readonly string[] Essential = { "openssl", "curl", "mbstring", "mysqli" };

    public const string EssentialReason = "Needed by Composer and phpMyAdmin";

    static readonly string[] Common =
    {
        "fileinfo", "gd", "intl", "pdo_mysql", "pdo_sqlite", "sqlite3", "zip", "exif", "gettext", "soap", "sockets"
    };

    /// <summary>OPcache is a separate DLL up to PHP 8.4 and built into PHP itself from 8.5.</summary>
    public static bool OpcacheIsBuiltIn(string branch) => VersionUtil.Compare(branch, "8.5") >= 0;

    /// <summary>What is on out of the box. The same set Rhapsody Server has always enabled.</summary>
    public static IReadOnlyList<string> Defaults(string branch)
    {
        var l = Essential.Concat(Common).ToList();
        if (branch != "7.1") l.Add("sodium");   // added in 7.2
        l.Add("opcache");
        return l;
    }

    /// <summary>"php_gd2.dll" becomes "gd" (PHP 7.1 prefixes and renames a few).</summary>
    public static string NameOfDll(string fileName)
    {
        var n = Path.GetFileNameWithoutExtension(fileName);
        if (n.StartsWith("php_", StringComparison.OrdinalIgnoreCase)) n = n[4..];
        return n.Equals("gd2", StringComparison.OrdinalIgnoreCase) ? "gd" : n.ToLowerInvariant();
    }

    /// <summary>
    /// Every extension this PHP build can load, by name. Includes "opcache" (a DLL up to 8.4, built in from 8.5).
    /// Zend extensions that are not part of PHP's own download (xdebug) are left out: they need zend_extension, not extension.
    /// </summary>
    public static IReadOnlyList<string> Discover(string phpDir, string branch)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var ext = Path.Combine(phpDir, "ext");
        if (Directory.Exists(ext))
            foreach (var f in Directory.EnumerateFiles(ext, "php_*.dll"))
            {
                var n = NameOfDll(f);
                if (n is "opcache" or "xdebug") continue;
                names.Add(n);
            }
        if (OpcacheIsBuiltIn(branch) || File.Exists(Path.Combine(ext, "php_opcache.dll"))) names.Add("opcache");
        return names.ToList();
    }

    /// <summary>The names to switch on: the user's choice (or the defaults), only ones this build has, plus the essentials.</summary>
    public static IReadOnlyList<string> Effective(IEnumerable<string>? chosen, string branch, IReadOnlyCollection<string> available) =>
        (chosen ?? Defaults(branch)).Concat(Essential)
            .Select(x => x.ToLowerInvariant()).Distinct()
            .Where(available.Contains)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

    static readonly HashSet<string> NeedsOutsideSoftware = new() { "oci8_12c", "oci8_19", "pdo_oci", "pdo_firebird" };

    public static string? NoteFor(string name) =>
        NeedsOutsideSoftware.Contains(name) ? "Needs the vendor's client library installed on this computer" : null;

    public static string Describe(string name) => Descriptions.TryGetValue(name, out var d) ? d : "";

    static readonly Dictionary<string, string> Descriptions = new()
    {
        ["bz2"] = "bzip2 compression",
        ["curl"] = "HTTP requests to other servers (cURL)",
        ["dba"] = "Simple key-value database files",
        ["exif"] = "Read photo metadata (EXIF)",
        ["ffi"] = "Call C libraries from PHP",
        ["fileinfo"] = "Detect file types by content",
        ["ftp"] = "FTP client",
        ["gd"] = "Create and resize images",
        ["gettext"] = "Translations (gettext)",
        ["gmp"] = "Very large integers",
        ["imap"] = "IMAP mail client",
        ["intl"] = "Internationalisation: collation, number and date formats",
        ["ldap"] = "LDAP directory access",
        ["mbstring"] = "Multibyte (UTF-8) strings",
        ["mysqli"] = "MySQL/MariaDB (mysqli)",
        ["odbc"] = "ODBC databases",
        ["opcache"] = "Keeps compiled PHP in memory so pages load faster",
        ["openssl"] = "Encryption and HTTPS",
        ["pdo_firebird"] = "PDO driver for Firebird",
        ["pdo_mysql"] = "PDO driver for MySQL/MariaDB",
        ["pdo_odbc"] = "PDO driver for ODBC",
        ["pdo_pgsql"] = "PDO driver for PostgreSQL",
        ["pdo_sqlite"] = "PDO driver for SQLite",
        ["pgsql"] = "PostgreSQL",
        ["shmop"] = "Shared memory",
        ["snmp"] = "SNMP network monitoring",
        ["soap"] = "SOAP web services",
        ["sockets"] = "Low-level network sockets",
        ["sodium"] = "Modern cryptography (libsodium)",
        ["sqlite3"] = "SQLite 3",
        ["sysvshm"] = "System V shared memory",
        ["tidy"] = "Clean up HTML",
        ["zip"] = "Read and write zip files"
    };
}

/// <summary>Which Apache modules exist in an install and which are loaded.</summary>
public static class ApacheModuleCatalog
{
    /// <summary>
    /// Always loaded: the generated config uses them (PHP over FastCGI, HTTPS, the mail inbox proxy, several PHP workers).
    /// Order matters: a module has to come after the one it builds on.
    /// </summary>
    public static readonly string[] Required =
    {
        "authz_core", "authz_host", "dir", "mime", "alias", "log_config",
        "proxy", "proxy_fcgi", "proxy_http", "proxy_wstunnel",
        "socache_shmcb", "ssl"
    };

    public const string RequiredReason = "Rhapsody Server's own config uses it (PHP, HTTPS, mail inbox)";

    /// <summary>Optional modules that are on until the user says otherwise.</summary>
    public static readonly string[] Defaults = { "rewrite", "headers", "setenvif" };

    /// <summary>A module that has to be loaded after another. Turning one on turns its base on too.</summary>
    static readonly Dictionary<string, string[]> Needs = new()
    {
        ["cache_disk"] = new[] { "cache" },
        ["cache_socache"] = new[] { "cache" },
        ["dav_fs"] = new[] { "dav" },
        ["dav_lock"] = new[] { "dav" },
        ["proxy_html"] = new[] { "xml2enc" },
        ["authnz_ldap"] = new[] { "ldap" },
        ["session_cookie"] = new[] { "session" },
        ["session_crypto"] = new[] { "session" },
        ["session_dbd"] = new[] { "session", "dbd" },
        ["auth_form"] = new[] { "session" },
        ["authn_dbd"] = new[] { "dbd" },
        ["authz_dbd"] = new[] { "dbd" },
        ["authn_socache"] = new[] { "socache_shmcb" },
    };

    /// <summary>File names are mod_{name}.so; a few modules register under a different identifier.</summary>
    static readonly Dictionary<string, string> IdentifierOf = new() { ["apreq2"] = "apreq" };

    public static string LoadLine(string name) =>
        $"LoadModule {(IdentifierOf.TryGetValue(name, out var id) ? id : name)}_module modules/mod_{name}.so";

    /// <summary>Every module this Apache build ships. Null when the folder layout isn't recognised.</summary>
    public static IReadOnlyList<string>? Discover(string apacheDir)
    {
        var dir = Path.Combine(apacheDir, "modules");
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "mod_*.so")
            .Select(f => Path.GetFileNameWithoutExtension(f)[4..].ToLowerInvariant())
            .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>The optional modules to load: the user's choice (or the defaults) that this build really has, bases first.</summary>
    public static IReadOnlyList<string> Optional(IEnumerable<string>? chosen, IReadOnlyCollection<string>? available)
    {
        var result = new List<string>();
        void Add(string m)
        {
            m = m.ToLowerInvariant();
            if (Required.Contains(m) || result.Contains(m)) return;
            if (available != null && !available.Contains(m)) return;
            if (Needs.TryGetValue(m, out var deps)) foreach (var d in deps) Add(d);
            result.Add(m);
        }
        foreach (var m in chosen ?? Defaults) Add(m);
        return result;
    }

    public static string Describe(string name) => Descriptions.TryGetValue(name, out var d) ? d : "";

    static readonly Dictionary<string, string> Descriptions = new()
    {
        ["access_compat"] = "Old-style Allow/Deny rules (needed by some .htaccess files)",
        ["actions"] = "Run scripts for certain file types or methods",
        ["alias"] = "Alias and Redirect rules",
        ["allowmethods"] = "Restrict which HTTP methods are allowed",
        ["asis"] = "Send files exactly as they are, headers included",
        ["auth_basic"] = "Password prompt (HTTP basic authentication)",
        ["auth_digest"] = "Password prompt (digest authentication)",
        ["authn_core"] = "Core of the authentication system",
        ["authn_file"] = "Users from a password file",
        ["authz_groupfile"] = "Groups from a file",
        ["authz_host"] = "Allow or deny by IP address or host name",
        ["authz_user"] = "Allow named users",
        ["autoindex"] = "Folder listings when there is no index file",
        ["brotli"] = "Brotli compression",
        ["cache"] = "Response caching (base for the cache modules)",
        ["cache_disk"] = "Response caching on disk",
        ["cgi"] = "Run CGI scripts",
        ["data"] = "data: URLs",
        ["deflate"] = "Compress responses with gzip",
        ["dir"] = "Serve index.php / index.html for folders",
        ["env"] = "Set environment variables",
        ["expires"] = "Browser cache lifetimes (Expires headers)",
        ["filter"] = "Output filters",
        ["headers"] = "Add, change or remove HTTP headers",
        ["http2"] = "HTTP/2",
        ["include"] = "Server-side includes (.shtml)",
        ["info"] = "Server configuration page",
        ["isapi"] = "ISAPI extensions",
        ["log_config"] = "Access logging",
        ["mime"] = "File types",
        ["mime_magic"] = "Detect file types by content",
        ["negotiation"] = "Pick a language or format by what the browser asks for",
        ["proxy"] = "Reverse proxy base",
        ["proxy_fcgi"] = "Talk FastCGI to PHP",
        ["proxy_http"] = "Proxy to HTTP servers",
        ["proxy_wstunnel"] = "Proxy WebSockets",
        ["reqtimeout"] = "Time limits for slow requests",
        ["rewrite"] = "URL rewriting (.htaccess rules, pretty URLs)",
        ["setenvif"] = "Set variables from the request",
        ["socache_shmcb"] = "Shared cache for HTTPS sessions",
        ["speling"] = "Fix small typos in URLs",
        ["ssl"] = "HTTPS",
        ["status"] = "Live server status page",
        ["substitute"] = "Search and replace in responses",
        ["unique_id"] = "A unique id for every request",
        ["userdir"] = "~user folders",
        ["usertrack"] = "Cookie-based visitor tracking",
        ["vhost_alias"] = "Mass virtual hosts from the folder name",
        ["watchdog"] = "Background task runner",
        ["xml2enc"] = "Character set handling for the XML/HTML filters"
    };

    static readonly Regex Safe = new("^[a-z0-9_]+$");
    public static bool IsValidName(string name) => Safe.IsMatch(name);
}
