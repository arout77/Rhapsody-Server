using System.Text.Json;
using System.Text.RegularExpressions;

namespace RhapsodyServer.Core;

/// <summary>What a Rhapsody site has and what is out there. <see cref="Error"/> is set when the check couldn't be made (offline, Packagist down).</summary>
public sealed record CoreUpdateInfo(string? Installed, string? Latest, bool UpdateAvailable, string? Constraint, string? Error);

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

    /// <summary>The highest stable release (no dev branches, alpha, beta or RC) in Packagist's metadata for the package, or null.</summary>
    public static string? PickLatestStable(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("packages", out var pk) || !pk.TryGetProperty(Package, out var list) || list.ValueKind != JsonValueKind.Array) return null;
            string? best = null;
            foreach (var p in list.EnumerateArray())
            {
                if (!p.TryGetProperty("version", out var v) || v.GetString() is not { } raw) continue;
                var s = Clean(raw);
                if (!Stable.IsMatch(s)) continue;
                if (best == null || VersionUtil.Compare(s, best) > 0) best = s;
            }
            return best;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Compares what is installed with what is out there. A development branch is never called out of date.</summary>
    public static CoreUpdateInfo Evaluate(string? installed, string? latest, string? constraint)
    {
        if (installed == null) return new(null, latest, false, constraint, "Couldn't read the installed version from vendor/composer/installed.json.");
        if (latest == null) return new(installed, null, false, constraint, "Couldn't find out the newest version.");
        var older = Stable.IsMatch(installed) && VersionUtil.Compare(installed, latest) < 0;
        return new(installed, latest, older, constraint, null);
    }

    static readonly object Gate = new();
    static (DateTime At, string? Version)? _cache;
    public static void ForgetLatest() { lock (Gate) _cache = null; }

    /// <summary>The newest stable release. Cached for ten minutes unless <paramref name="force"/>.</summary>
    public static async Task<string?> LatestAsync(HttpClient http, bool force, CancellationToken ct)
    {
        lock (Gate) if (!force && _cache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(10) && c.Version != null) return c.Version;
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://repo.packagist.org/p2/" + Package + ".json");
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var v = PickLatestStable(await res.Content.ReadAsStringAsync(ct));
        lock (Gate) _cache = (DateTime.UtcNow, v);
        return v;
    }

    /// <summary>Installed version, newest version and whether an update is due, for one site folder. Never throws for a network problem: the error is in the result.</summary>
    public static async Task<CoreUpdateInfo> CheckAsync(HttpClient http, string root, bool force, CancellationToken ct)
    {
        var installed = InstalledVersion(root);
        var constraint = Constraint(root);
        try { return Evaluate(installed, await LatestAsync(http, force, ct), constraint); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        { return new(installed, null, false, constraint, "Couldn't reach Packagist to check. Are you online?"); }
    }
}
