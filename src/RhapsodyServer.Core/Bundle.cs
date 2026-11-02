using System.Security.Cryptography;
using System.Text.Json;

namespace RhapsodyServer.Core;

public sealed record BundleEntry(
    string Id, string Series, string Version, string File, string Sha256,
    string Kind, string Exe, string? InnerRoot);

public sealed class BundleManifest
{
    public string Created { get; set; } = "";
    public List<BundleEntry> Entries { get; set; } = new();
}

/// <summary>
/// The "ships with the app" component store: bundle/manifest.json plus the archives it lists.
/// The Installer asks it before going to the network, so a fresh install works fully offline.
/// </summary>
public sealed class BundleSource
{
    public string Dir { get; }
    public BundleManifest Manifest { get; }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public BundleSource(string dir, BundleManifest manifest) { Dir = dir; Manifest = manifest; }

    public static BundleSource? Locate(string? dir = null)
    {
        dir ??= Environment.GetEnvironmentVariable("RHAPSODY_SERVER_BUNDLE") ?? Path.Combine(AppContext.BaseDirectory, "bundle");
        var file = Path.Combine(dir, "manifest.json");
        if (!File.Exists(file)) return null;
        var m = JsonSerializer.Deserialize<BundleManifest>(File.ReadAllText(file), Json);
        return m == null ? null : new BundleSource(dir, m);
    }

    public IReadOnlyList<BundleEntry> Entries => Manifest.Entries;

    /// <summary>Newest entry for an id, optionally restricted to one series.</summary>
    public BundleEntry? Find(string id, string? series = null) =>
        Entries.Where(e => e.Id == id && (series == null || e.Series == series))
               .OrderByDescending(e => e.Version, VersionUtil.Comparer).FirstOrDefault();

    public BundleEntry? FindExact(string id, string version) =>
        Entries.FirstOrDefault(e => e.Id == id && e.Version == version);

    public string PathOf(BundleEntry e) => Path.Combine(Dir, e.File);

    public Package ToPackage(BundleEntry e)
    {
        var kind = Enum.TryParse<PackageKind>(e.Kind, out var k) ? k : PackageKind.Zip;
        var name = Catalog.Shapes.TryGetValue(e.Id, out var s) ? s.Name : e.Id;
        return new Package(e.Id, name, e.Series, e.Version, "", kind, e.Exe,
            kind == PackageKind.SingleFile ? e.File : null, e.InnerRoot, e.Sha256);
    }

    /// <summary>Catches a truncated copy or a bad download. (It is an integrity check, not tamper protection.)</summary>
    public void Verify(BundleEntry e)
    {
        if (string.IsNullOrEmpty(e.Sha256)) return;
        using var fs = File.OpenRead(PathOf(e));
        if (!Convert.ToHexString(SHA256.HashData(fs)).Equals(e.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{e.File} in the bundle is corrupt (checksum mismatch). Re-run the bundler.");
    }
}

public static class BundleBuilder
{
    /// <summary>
    /// Latest PHP (or the branches you name), latest Apache, latest stable nginx, newest MariaDB LTS,
    /// plus the small fixed tools. This is what "ships with the app".
    /// </summary>
    /// <param name="local">Archives the builder already has on disk (id -> package whose file is in <paramref name="localFiles"/>); their vendor is not contacted.</param>
    public static async Task<IReadOnlyList<Package>> DefaultSelectionAsync(
        HttpClient http, IReadOnlyCollection<string>? phpBranches = null, IProgress<string>? p = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, Package>? local = null)
    {
        var result = new List<Package>();
        foreach (var id in Catalog.VersionedIds)
        {
            if (local != null && local.TryGetValue(id, out var own)) { result.Add(own); continue; }
            p?.Report($"Looking up {Catalog.Shapes[id].Name}...");
            var list = await PackageSources.ListAsync(http, id, ct);
            if (id == "php" && phpBranches is { Count: > 0 })
            {
                foreach (var b in phpBranches)
                    result.Add(list.FirstOrDefault(x => x.Series == b)
                        ?? throw new InvalidOperationException($"PHP {b} was not found on windows.php.net"));
            }
            else
            {
                result.Add(PackageSources.Latest(id, list)
                    ?? throw new InvalidOperationException($"No downloadable {Catalog.Shapes[id].Name} version was found"));
            }
        }
        result.AddRange(Catalog.Fixed);
        return result;
    }

    /// <param name="localFiles">package id -> archive on disk, copied in instead of downloaded.</param>
    public static async Task<BundleManifest> BuildAsync(
        Installer installer, IEnumerable<Package> packages, string outDir, IProgress<string>? p = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? localFiles = null)
    {
        Directory.CreateDirectory(outDir);
        var manifest = new BundleManifest { Created = DateTime.UtcNow.ToString("O") };
        foreach (var pkg in packages)
        {
            var target = Path.Combine(outDir, pkg.CacheFile);
            if (localFiles != null && localFiles.TryGetValue(pkg.Id, out var file))
            {
                p?.Report($"Using your {pkg.Name} {pkg.Version}: {file}");
                File.Copy(file, target, overwrite: true);
            }
            else await installer.DownloadAsync(target, pkg.Url, pkg.Sha256, $"{pkg.Name} {pkg.Version}", p, ct);
            await using var fs = File.OpenRead(target);
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            manifest.Entries.Add(new BundleEntry(pkg.Id, pkg.Series, pkg.Version, pkg.CacheFile, sha,
                pkg.Kind.ToString(), pkg.Exe, pkg.InnerRoot));
        }
        File.WriteAllText(Path.Combine(outDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return manifest;
    }
}

/// <summary>Is this bundle complete enough to ship? Run before building an installer, so an incomplete one can't go out.</summary>
public static class BundleCheck
{
    /// <summary>Everything a first run can need. If one is missing, first run would have to download it.</summary>
    public static readonly string[] RequiredIds = { "php", "apache", "nginx", "mariadb", "redis", "mailpit", "phpmyadmin", "mkcert", "composer", "vcredist" };

    static string NameOf(string id) =>
        Catalog.Shapes.TryGetValue(id, out var s) ? s.Name
        : id switch { "redis" => "Redis", "mailpit" => "Mailpit", "phpmyadmin" => "phpMyAdmin", "mkcert" => "mkcert", "composer" => "Composer", "vcredist" => "VC++ runtime", _ => id };

    /// <summary>Empty list = good to ship.</summary>
    public static IReadOnlyList<string> Problems(string dir)
    {
        var problems = new List<string>();
        BundleSource? src;
        try { src = BundleSource.Locate(dir); }
        catch (Exception ex) { return new[] { $"{Path.Combine(dir, "manifest.json")} can't be read: {ex.Message}" }; }
        if (src == null) return new[] { $"No bundle found in {dir} (manifest.json is missing). Run bundle.cmd first." };

        foreach (var id in RequiredIds)
        {
            var entries = src.Entries.Where(e => e.Id == id).ToList();
            if (entries.Count == 0) { problems.Add($"{NameOf(id)} is not in the bundle, so Rhapsody Server would have to download it the first time it is used."); continue; }
            foreach (var e in entries)
            {
                if (!File.Exists(src.PathOf(e))) { problems.Add($"{NameOf(id)} {e.Version}: {e.File} is listed but the file is missing."); continue; }
                try { src.Verify(e); } catch (InvalidDataException ex) { problems.Add(ex.Message); }
            }
        }
        return problems;
    }
}
