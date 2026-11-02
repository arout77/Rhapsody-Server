using System.Text.Json;
using System.Text.RegularExpressions;

namespace RhapsodyServer.Core;

public sealed record PhpBuild(string Branch, string Version, string Url, string FileName, string? Sha256)
{
    /// <summary>vc14 / vc15 / vs16 / vs17. All need the (latest) VC++ 2015-2022 x64 redistributable.</summary>
    public string Runtime => Regex.Match(FileName, @"-(vc\d+|vs\d+)-x64", RegexOptions.IgnoreCase)
        .Groups[1].Value.ToLowerInvariant();

    public Package ToPackage() => Catalog.Versioned("php", Branch, Version, Url, Sha256);
}

/// <summary>
/// Finds the newest Non-Thread-Safe x64 build of a PHP branch on windows.php.net.
/// Current branches come from releases.json; end-of-life ones from the archives index.
/// The parsing is pure (string in, build out) so it is unit-tested without a network.
/// </summary>
public static class PhpResolver
{
    public const string ReleasesBase = "https://windows.php.net/downloads/releases/";
    public static readonly string[] SupportedBranches = { "7.1", "7.4", "8.0", "8.1", "8.3", "8.4", "8.5" };

    public static PhpBuild? FromReleasesJson(string json, string branch)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException) { return null; }
        using var _ = doc;
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty(branch, out var b)) return null;

        var version = b.TryGetProperty("version", out var v) ? v.GetString() : null;
        if (version == null) return null;

        foreach (var prop in b.EnumerateObject())
        {
            if (!prop.Name.StartsWith("nts-", StringComparison.OrdinalIgnoreCase) ||
                !prop.Name.EndsWith("-x64", StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind != JsonValueKind.Object ||
                !prop.Value.TryGetProperty("zip", out var zip) ||
                !zip.TryGetProperty("path", out var path)) continue;

            var file = path.GetString();
            if (file == null) continue;
            var sha = zip.TryGetProperty("sha256", out var s) ? s.GetString() : null;
            return new PhpBuild(branch, version, ReleasesBase + file, file, sha);
        }
        return null;
    }

    public static PhpBuild? FromArchiveListing(string html, string branch)
    {
        var rx = new Regex(
            $@"php-(?<ver>{Regex.Escape(branch)}\.(?<patch>\d+))-nts-Win32-(?:vc|vs)\d+-x64\.zip",
            RegexOptions.IgnoreCase);

        var best = rx.Matches(html)
            .Select(m => (File: m.Value, Ver: m.Groups["ver"].Value, Patch: int.Parse(m.Groups["patch"].Value)))
            .OrderByDescending(x => x.Patch)   // numeric: 7.4.33 beats 7.4.9
            .FirstOrDefault();

        return best.File == null
            ? null
            : new PhpBuild(branch, best.Ver, ReleasesBase + "archives/" + best.File, best.File, null);
    }

    /// <summary>Every supported branch that windows.php.net currently offers. Fetches each index once.</summary>
    public static async Task<IReadOnlyList<Package>> ListAsync(HttpClient http, CancellationToken ct = default)
    {
        string? json = null, html = null;
        try { json = await http.GetStringAsync(ReleasesBase + "releases.json", ct); } catch (HttpRequestException) { }
        try { html = await http.GetStringAsync(ReleasesBase + "archives/", ct); } catch (HttpRequestException) { }
        if (json == null && html == null) throw new HttpRequestException("windows.php.net could not be reached");

        var list = new List<Package>();
        foreach (var branch in SupportedBranches)
        {
            var b = (json != null ? FromReleasesJson(json, branch) : null)
                 ?? (html != null ? FromArchiveListing(html, branch) : null);
            if (b != null) list.Add(b.ToPackage());
        }
        return list.OrderByDescending(p => p.Series, VersionUtil.Comparer).ToList();
    }

    public static async Task<PhpBuild> ResolveAsync(HttpClient http, string branch, CancellationToken ct = default)
    {
        try
        {
            var json = await http.GetStringAsync(ReleasesBase + "releases.json", ct);
            var b = FromReleasesJson(json, branch);
            if (b != null) return b;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { /* try archives */ }

        var html = await http.GetStringAsync(ReleasesBase + "archives/", ct);
        return FromArchiveListing(html, branch)
            ?? throw new InvalidOperationException($"No NTS x64 build of PHP {branch} found on windows.php.net");
    }
}
