namespace RhapsodyServer.Core;

public enum PackageKind { Zip, SingleFile }

/// <summary>One installable thing at one specific version.</summary>
/// <param name="Series">Folder key under bin/{id}/. For versioned tools it's the major.minor line (8.4, 11.8, 2.4).</param>
/// <param name="Exe">File that proves it is installed, relative to its folder.</param>
/// <param name="InnerRoot">Folder inside the zip to use as the root (Apache Lounge ships "Apache24/" beside readme files).</param>
public sealed record Package(
    string Id, string Name, string Series, string Version, string Url, PackageKind Kind, string Exe,
    string? FileName = null, string? InnerRoot = null, string? Sha256 = null)
{
    /// <summary>Name used in downloads/ and in the bundle. Identical on purpose, so a bundled file satisfies a download.</summary>
    public string CacheFile => Kind == PackageKind.Zip ? $"{Id}-{Version}.zip" : FileName!;
}

public sealed record Shape(string Name, string Exe, string? InnerRoot);

/// <summary>
/// Tools that exist in several versions (php, apache, nginx, mariadb) are resolved at runtime (see Resolvers.cs).
/// Everything else is pinned here. NOTE: these URLs follow each project's published layout but were not
/// verifiable from the build sandbox; if one 404s, bump it here or drop the file into downloads/.
/// </summary>
public static class Catalog
{
    public static readonly string[] VersionedIds = { "php", "apache", "nginx", "mariadb" };

    public static readonly IReadOnlyDictionary<string, Shape> Shapes = new Dictionary<string, Shape>
    {
        ["php"] = new("PHP", "php-cgi.exe", null),
        ["apache"] = new("Apache", "bin/httpd.exe", "Apache24"),
        ["nginx"] = new("nginx", "nginx.exe", null),
        ["mariadb"] = new("MariaDB", "bin/mariadbd.exe", null),
    };

    public static Package Versioned(string id, string series, string version, string url, string? sha = null)
    {
        var s = Shapes[id];
        return new Package(id, s.Name, series, version, url, PackageKind.Zip, s.Exe, null, s.InnerRoot, sha);
    }

    // Last Windows port of Redis by tporadowski (5.0.x). Newer Redis/Valkey need WSL or Memurai.
    public static readonly Package Redis = new("redis", "Redis", "5.0.14.1", "5.0.14.1",
        "https://github.com/tporadowski/redis/releases/download/v5.0.14.1/Redis-x64-5.0.14.1.zip",
        PackageKind.Zip, "redis-server.exe");

    public static readonly Package Mailpit = new("mailpit", "Mailpit", "latest", "latest",
        "https://github.com/axllent/mailpit/releases/latest/download/mailpit-windows-amd64.zip",
        PackageKind.Zip, "mailpit.exe");

    public static readonly Package PhpMyAdmin = new("phpmyadmin", "phpMyAdmin", "5.2.3", "5.2.3",
        "https://files.phpmyadmin.net/phpMyAdmin/5.2.3/phpMyAdmin-5.2.3-all-languages.zip",
        PackageKind.Zip, "index.php");

    public static readonly Package Mkcert = new("mkcert", "mkcert", "1.4.4", "1.4.4",
        "https://github.com/FiloSottile/mkcert/releases/download/v1.4.4/mkcert-v1.4.4-windows-amd64.exe",
        PackageKind.SingleFile, "mkcert.exe", FileName: "mkcert.exe");

    public static readonly Package Composer = new("composer", "Composer", "stable", "stable",
        "https://getcomposer.org/download/latest-stable/composer.phar",
        PackageKind.SingleFile, "composer.phar", FileName: "composer.phar");

    /// <summary>Microsoft's VC++ 2015-2022 x64 runtime. PHP, Apache and MariaDB builds all need it.</summary>
    public static readonly Package VcRedist = new("vcredist", "VC++ runtime", "latest", "latest",
        "https://aka.ms/vs/17/release/vc_redist.x64.exe",
        PackageKind.SingleFile, "vc_redist.x64.exe", FileName: "vc_redist.x64.exe");

    /// <summary>Small fixed tools that go into every bundle.</summary>
    public static IEnumerable<Package> Fixed => new[] { Redis, Mailpit, PhpMyAdmin, Mkcert, Composer, VcRedist };
}

public static class VersionUtil
{
    public static int Compare(string a, string b)
    {
        var pa = a.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var pb = b.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var d = (i < pa.Length ? pa[i] : 0).CompareTo(i < pb.Length ? pb[i] : 0);
            if (d != 0) return d;
        }
        return 0;
    }

    public static readonly Comparer<string> Comparer = Comparer<string>.Create(Compare);
    public static string Series(string version) => string.Join('.', version.Split('.').Take(2));
}
