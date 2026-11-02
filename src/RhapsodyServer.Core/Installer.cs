using System.IO.Compression;
using System.Security.Cryptography;

namespace RhapsodyServer.Core;

/// <summary>
/// Installs a Package under bin/{id}/{series}. Source order: previous download, then the bundle shipped
/// with the app, then the network. Idempotent.
/// </summary>
public sealed class Installer
{
    readonly ServerPaths _paths;
    public HttpClient Http { get; }
    public BundleSource? Bundle { get; set; }

    public Installer(ServerPaths paths, HttpClient? http = null, BundleSource? bundle = null)
    {
        _paths = paths;
        Bundle = bundle;
        Http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        if (Http.DefaultRequestHeaders.UserAgent.Count == 0)
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RhapsodyServer/0.2");
    }

    public string DirOf(Package p) => _paths.ComponentDir(p.Id, p.Series);
    public string PathOf(Package p) => Path.Combine(DirOf(p), p.Exe);
    public bool IsInstalled(Package p) => File.Exists(PathOf(p));

    public async Task<string> EnsureAsync(Package p, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!IsInstalled(p)) await InstallAsync(p, progress, ct);
        return DirOf(p);
    }

    public async Task InstallAsync(Package p, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var dest = DirOf(p);
        var label = $"{p.Name} {p.Version}";
        var source = await ObtainAsync(p, label, progress, ct);

        if (p.Kind == PackageKind.Zip)
        {
            progress?.Report($"Unpacking {label}...");
            ExtractFlatten(source, dest, p.InnerRoot);
        }
        else
        {
            Directory.CreateDirectory(dest);
            File.Copy(source, Path.Combine(dest, p.FileName!), overwrite: true);
        }
        if (!File.Exists(Path.Combine(dest, p.Exe)))
            throw new InvalidOperationException($"{label} unpacked, but {p.Exe} was not found in {dest}");
    }

    async Task<string> ObtainAsync(Package p, string label, IProgress<string>? progress, CancellationToken ct)
    {
        var cached = Path.Combine(_paths.Downloads, p.CacheFile);
        if (File.Exists(cached)) return cached;

        if (Bundle?.FindExact(p.Id, p.Version) is { } entry && File.Exists(Bundle.PathOf(entry)))
        {
            progress?.Report($"Verifying bundled {label}...");
            Bundle.Verify(entry);
            return Bundle.PathOf(entry);        // unpack straight from the bundle, no second copy on disk
        }

        if (string.IsNullOrEmpty(p.Url))
            throw new FileNotFoundException($"{label} isn't bundled and has no download link. Save its archive as:\n{cached}");

        await DownloadAsync(cached, p.Url, p.Sha256, label, progress, ct);
        return cached;
    }

    public async Task DownloadAsync(string target, string url, string? sha256, string label, IProgress<string>? p, CancellationToken ct)
    {
        if (File.Exists(target)) return;   // already there (resumable bundle builds, hand-dropped files)
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (new Uri(url).Host.EndsWith("apachelounge.com", StringComparison.OrdinalIgnoreCase))
            req.Headers.Referrer = new Uri(ApacheResolver.DownloadPage);   // Apache Lounge refuses requests without it

        using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Downloading {label} failed ({(int)resp.StatusCode}) from {url}\n" +
                    $"You can save the file manually as:\n{target}");

            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(part);
            var buf = new byte[81920];
            long read = 0; int lastDecile = -1, n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total is > 0)
                {
                    var pct = (int)(read * 100 / total.Value);
                    if (pct / 10 != lastDecile) { lastDecile = pct / 10; p?.Report($"Downloading {label}: {pct}%"); }
                }
            }
        }

        if (sha256 != null)
        {
            await using var fs = File.OpenRead(part);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new InvalidOperationException($"Checksum mismatch for {label}; download discarded.");
            }
        }
        File.Move(part, target, overwrite: true);
    }

    public static void ExtractFlatten(string zip, string dest, string? innerRoot = null)
    {
        var tmp = dest + ".extracting";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        ZipFile.ExtractToDirectory(zip, tmp);

        var root = tmp;
        if (innerRoot != null && Directory.Exists(Path.Combine(tmp, innerRoot)))
            root = Path.Combine(tmp, innerRoot);
        else if (Directory.GetFiles(tmp).Length == 0 && Directory.GetDirectories(tmp).Length == 1)
            root = Directory.GetDirectories(tmp)[0];   // zip had a single wrapper folder

        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.Move(root, dest);
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
}
