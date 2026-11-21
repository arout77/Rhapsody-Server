using System.Text.Json;
using System.Text.RegularExpressions;

namespace RhapsodyServer.Core;

/// <summary>What a Rhapsody site has and what is out there. <see cref="Latest"/> is the newest release the site's composer.json constraint allows. <see cref="Newer"/> is a newer release the constraint holds the site back from (such as 2.0.0 for "^1.0"), or null. <see cref="Error"/> is set when the check couldn't be made (offline, Packagist down).</summary>
public sealed record CoreUpdateInfo(string? Installed, string? Latest, bool UpdateAvailable, string? Constraint, string? Error, string? Newer = null);

/// <summary>
/// Is a newer rhapsody-core out? The installed version is read from the site's own vendor folder (no PHP needed); the newest stable
/// release comes from Packagist, once per few minutes however many sites ask. Updating runs Composer in the site's folder, so the
/// site's own composer.json decides how far it may move.
/// </summary>
public static class RhapsodyUpdates
{
    public const string Package = "arout/rhapsody-core";
    static readonly Regex Stable = new(@"^\d+(\.\d+){0,3}$", RegexOptions.Compiled);

    static string Clean(string v) => v.Trim().TrimStart('v', 'V');

    /// <summary>The version of rhapsody-core in vendor/, or null when it can't be read. A branch such as "dev-main" is returned as it is.</summary>
    public static string? InstalledVersion(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            var f = Path.Combine(root, "vendor", "composer", "installed.json");
            if (!File.Exists(f)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement                                   // Composer 1
                : doc.RootElement.TryGetProperty("packages", out var pk) ? pk : default;                                     // Composer 2
            if (list.ValueKind != JsonValueKind.Array) return null;
            foreach (var p in list.EnumerateArray())
                if (p.TryGetProperty("name", out var n) && string.Equals(n.GetString(), Package, StringComparison.OrdinalIgnoreCase)
                    && p.TryGetProperty("version", out var v) && v.GetString() is { Length: > 0 } s)
                    return Clean(s);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return null;
    }

    /// <summary>What the site's composer.json asks for (such as "^1.4"), or null.</summary>
    public static string? Constraint(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            var f = Path.Combine(root, "composer.json");
            if (!File.Exists(f)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            return doc.RootElement.TryGetProperty("require", out var r) && r.TryGetProperty(Package, out var c) ? c.GetString() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Every stable release (no dev branches, alpha, beta or RC) in Packagist's metadata for the package, cleaned of any "v".</summary>
    public static List<string> StableVersions(string json)
    {
        var all = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("packages", out var pk) || !pk.TryGetProperty(Package, out var list) || list.ValueKind != JsonValueKind.Array) return all;
            foreach (var p in list.EnumerateArray())
            {
                if (!p.TryGetProperty("version", out var v) || v.GetString() is not { } raw) continue;
                var s = Clean(raw);
                if (Stable.IsMatch(s)) all.Add(s);
            }
        }
        catch (JsonException) { }
        return all;
    }

    /// <summary>The highest of <paramref name="versions"/> that <paramref name="constraint"/> allows (any, when it is null or not understood), or null.</summary>
    public static string? Highest(IEnumerable<string> versions, string? constraint = null)
    {
        string? best = null;
        foreach (var s in versions)
            if (Allows(constraint, s) && (best == null || VersionUtil.Compare(s, best) > 0)) best = s;
        return best;
    }

    /// <summary>The highest stable release in Packagist's metadata that <paramref name="constraint"/> allows (any, when it is null), or null.</summary>
    public static string? PickLatestStable(string json, string? constraint = null) => Highest(StableVersions(json), constraint);

    /// <summary>
    /// Does a composer.json constraint allow this stable version? Understands ^1.2, ~1.2, 1.*, 1.2.3, >=1.2 &lt;2.0 (space or comma) and "||".
    /// Anything else, including dev branches and "*", is treated as allowing everything, so a constraint we don't understand never hides an update.
    /// </summary>
    public static bool Allows(string? constraint, string version)
    {
        if (string.IsNullOrWhiteSpace(constraint)) return true;
        var v = Clean(version);
        foreach (var alt in constraint.Split("||", StringSplitOptions.RemoveEmptyEntries))
        {
            var ok = true;
            // "as" aliases and stability flags such as "@dev" aren't version limits.
            var parts = Regex.Split(alt.Trim().Replace(" as ", " ").Replace(",", " "), @"\s+").Where(x => x.Length > 0).ToList();
            if (parts.Count == 0) return true;
            foreach (var raw in parts) { if (!AllowsOne(raw, v)) { ok = false; break; } }
            if (ok) return true;
        }
        return false;
    }

    static bool AllowsOne(string c, string v)
    {
        var m = Regex.Match(c, @"^(\^|~|>=|<=|>|<|=|!=)?v?(\d+(?:\.\d+){0,3})(\.\*|\.x)?(?:@\w+)?$", RegexOptions.IgnoreCase);
        if (!m.Success) return true;                                          // "*", "dev-main", anything unknown: don't restrict
        var op = m.Groups[1].Value; var n = m.Groups[2].Value; var wild = m.Groups[3].Success;
        var np = n.Split('.').Select(int.Parse).ToArray();
        int Part(string x, int i) { var a = x.Split('.'); return i < a.Length && int.TryParse(a[i], out var r) ? r : 0; }
        bool SameLead(int count) { for (var i = 0; i < count; i++) if (Part(v, i) != Part(n, i)) return false; return true; }
        var cmp = VersionUtil.Compare(v, n);
        if (wild) return SameLead(np.Length);                                 // 1.* / 1.2.*
        switch (op)
        {
            case "^":                                                         // up to the next major (or the next minor/patch while the first parts are 0)
                if (cmp < 0) return false;
                var lead = np.Length > 0 && np[0] > 0 ? 1 : np.Length > 1 && np[1] > 0 ? 2 : 3;
                return SameLead(lead);
            case "~":                                                         // ~1.2 means >=1.2 <2.0; ~1.2.3 means >=1.2.3 <1.3
                return cmp >= 0 && SameLead(np.Length <= 2 ? 1 : np.Length - 1);
            case ">=": return cmp >= 0;
            case "<=": return cmp <= 0;
            case ">": return cmp > 0;
            case "<": return cmp < 0;
            case "!=": return cmp != 0;
            default:                                                          // exact, or a bare 1.2 (which Composer reads as exactly 1.2)
                return cmp == 0;
        }
    }

    /// <summary>Compares what is installed with what is out there. A development branch is never called out of date. <paramref name="latest"/> is the newest release the constraint allows; <paramref name="newest"/> is the newest of all, reported as <see cref="CoreUpdateInfo.Newer"/> when the constraint holds the site back from it.</summary>
    public static CoreUpdateInfo Evaluate(string? installed, string? latest, string? constraint, string? newest = null)
    {
        if (installed == null) return new(null, latest, false, constraint, "Couldn't read the installed version from vendor/composer/installed.json.");
        if (latest == null) return new(installed, null, false, constraint, "Couldn't find out the newest version.");
        var older = Stable.IsMatch(installed) && VersionUtil.Compare(installed, latest) < 0;
        var held = newest != null && VersionUtil.Compare(newest, latest) > 0 && (!Stable.IsMatch(installed) || VersionUtil.Compare(newest, installed) > 0) ? newest : null;
        return new(installed, latest, older, constraint, null, held);
    }

    static readonly object Gate = new();
    static (DateTime At, List<string> Versions)? _cache;
    public static void ForgetLatest() { lock (Gate) _cache = null; }

    /// <summary>Every stable release. Cached for ten minutes unless <paramref name="force"/>.</summary>
    public static async Task<List<string>> StableVersionsAsync(HttpClient http, bool force, CancellationToken ct)
    {
        lock (Gate) if (!force && _cache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(10) && c.Versions.Count > 0) return c.Versions;
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://repo.packagist.org/p2/" + Package + ".json");
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var all = StableVersions(await res.Content.ReadAsStringAsync(ct));
        lock (Gate) _cache = (DateTime.UtcNow, all);
        return all;
    }

    /// <summary>The newest stable release that <paramref name="constraint"/> allows (any, when null).</summary>
    public static async Task<string?> LatestAsync(HttpClient http, bool force, CancellationToken ct, string? constraint = null) =>
        Highest(await StableVersionsAsync(http, force, ct), constraint);

    /// <summary>Installed version, newest version and whether an update is due, for one site folder. Never throws for a network problem: the error is in the result.</summary>
    public static async Task<CoreUpdateInfo> CheckAsync(HttpClient http, string root, bool force, CancellationToken ct)
    {
        var installed = InstalledVersion(root);
        var constraint = Constraint(root);
        try { var all = await StableVersionsAsync(http, force, ct); return Evaluate(installed, Highest(all, constraint), constraint, Highest(all)); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        { return new(installed, null, false, constraint, "Couldn't reach Packagist to check. Are you online?"); }
    }
}
