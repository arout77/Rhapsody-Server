using System.Text.RegularExpressions;

namespace RhapsodyServer.Core;

/// <summary>
/// Each resolver turns a vendor's download page into installable Packages. Parsing is pure
/// (string in, packages out) so it is unit-tested without a network. If a vendor reshuffles their
/// page, only the matching regex here needs to change.
/// </summary>
public static class ApacheResolver
{
    public const string Base = "https://www.apachelounge.com";
    public const string DownloadPage = Base + "/download/";

    static readonly Regex Rx = new(
        @"href=""(?<href>[^""]*?/binaries/httpd-(?<ver>\d+\.\d+\.\d+)-(?<build>\d+)-win64-VS(?<vs>\d+)\.zip)""",
        RegexOptions.IgnoreCase);

    /// <summary>One package per 2.x series: newest version, and the newest Visual Studio build of it.</summary>
    public static IReadOnlyList<Package> FromDownloadPage(string html) =>
        Rx.Matches(html)
            .Select(m => (Href: m.Groups["href"].Value, Ver: m.Groups["ver"].Value,
                          Build: m.Groups["build"].Value, Vs: int.Parse(m.Groups["vs"].Value)))
            .GroupBy(x => VersionUtil.Series(x.Ver))
            .Select(g => g.OrderByDescending(x => x.Ver, VersionUtil.Comparer)
                          .ThenByDescending(x => x.Vs)
                          .ThenByDescending(x => x.Build, StringComparer.Ordinal).First())
            .Select(x => Catalog.Versioned("apache", VersionUtil.Series(x.Ver), x.Ver,
                                           new Uri(new Uri(Base), x.Href).ToString()))
            .OrderByDescending(p => p.Series, VersionUtil.Comparer)
            .ToList();
}

public static class NginxResolver
{
    public const string DownloadPage = "https://nginx.org/en/download.html";
    static readonly Regex Rx = new(@"nginx-(?<v>\d+\.\d+\.\d+)\.zip", RegexOptions.IgnoreCase);

    /// <summary>nginx convention: even minor = stable line, odd minor = mainline.</summary>
    public static bool IsStable(string series) => int.Parse(series.Split('.')[1]) % 2 == 0;

    public static IReadOnlyList<Package> FromDownloadPage(string html) =>
        Rx.Matches(html).Select(m => m.Groups["v"].Value).Distinct()
            .GroupBy(VersionUtil.Series)
            .Select(g => g.OrderByDescending(v => v, VersionUtil.Comparer).First())
            .Select(v => Catalog.Versioned("nginx", VersionUtil.Series(v), v, $"https://nginx.org/download/nginx-{v}.zip"))
            .OrderByDescending(p => p.Series, VersionUtil.Comparer)
            .ToList();
}

public static class MariaDbResolver
{
    public const string ArchiveIndex = "https://archive.mariadb.org/";

    /// <summary>Long-term-support series only. Bump this list when MariaDB announces a new LTS.</summary>
    public static readonly string[] LtsSeries = { "10.6", "10.11", "11.4", "11.8" };

    static readonly Regex Rx = new(@"href=""mariadb-(?<v>\d+\.\d+\.\d+)/""", RegexOptions.IgnoreCase);

    public static IReadOnlyList<Package> FromArchiveListing(string html) =>
        Rx.Matches(html).Select(m => m.Groups["v"].Value).Distinct()
            .Where(v => LtsSeries.Contains(VersionUtil.Series(v)))
            .GroupBy(VersionUtil.Series)
            .Select(g => g.OrderByDescending(v => v, VersionUtil.Comparer).First())
            .Select(v => Catalog.Versioned("mariadb", VersionUtil.Series(v), v,
                $"https://archive.mariadb.org/mariadb-{v}/winx64-packages/mariadb-{v}-winx64.zip"))
            .OrderByDescending(p => p.Series, VersionUtil.Comparer)
            .ToList();
}

public static class PackageSources
{
    /// <summary>Everything currently downloadable for a versioned tool. Throws if the vendor can't be reached.</summary>
    public static async Task<IReadOnlyList<Package>> ListAsync(HttpClient http, string id, CancellationToken ct = default) => id switch
    {
        "php" => await PhpResolver.ListAsync(http, ct),
        "apache" => ApacheResolver.FromDownloadPage(await http.GetStringAsync(ApacheResolver.DownloadPage, ct)),
        "nginx" => NginxResolver.FromDownloadPage(await http.GetStringAsync(NginxResolver.DownloadPage, ct)),
        "mariadb" => MariaDbResolver.FromArchiveListing(await http.GetStringAsync(MariaDbResolver.ArchiveIndex, ct)),
        _ => throw new ArgumentException($"'{id}' has no version list")
    };

    /// <summary>The one to bundle / install by default: newest, and for nginx the newest *stable* line.</summary>
    public static Package? Latest(string id, IEnumerable<Package> list)
    {
        var l = list.ToList();
        if (id == "nginx") l = l.Where(p => NginxResolver.IsStable(p.Series)).ToList();
        return l.OrderByDescending(p => p.Series, VersionUtil.Comparer).FirstOrDefault();
    }
}
