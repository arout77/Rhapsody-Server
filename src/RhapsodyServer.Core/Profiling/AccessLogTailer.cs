using System.Globalization;
using System.Text;

namespace RhapsodyServer.Core.Profiling;

/// <summary>
/// Follows the web server's profile log and turns each line into a <see cref="RequestRecord"/>.
/// The log is tab-separated (URLs can't contain a raw tab, so no JSON-escaping trouble):
///   nginx : end-time(s) \t request-time(s) \t upstream-time \t method \t host \t uri \t status \t bytes \t request-id
///   apache: end-time(ms) \t duration(us) \t method \t host \t uri \t status \t bytes \t -
/// </summary>
public sealed class AccessLogTailer : IDisposable
{
    public static string LogFormatNginx =>
        "log_format rs_profile '$msec\\t$request_time\\t$upstream_response_time\\t$request_method\\t$host\\t$request_uri\\t$status\\t$body_bytes_sent\\t$request_id';";

    public const string LogFormatApache =
        "LogFormat \"%{end:msec}t\\t%D\\t%m\\t%{Host}i\\t%U%q\\t%>s\\t%B\\t-\" rs_profile";

    readonly string _path;
    readonly WebServerKind _kind;
    readonly ProfileStore _store;
    readonly object _gate = new();
    FileSystemWatcher? _watcher;
    Timer? _safety;
    long _pos;
    readonly StringBuilder _partial = new();

    public AccessLogTailer(string path, WebServerKind kind, ProfileStore store)
    {
        _path = path; _kind = kind; _store = store;
    }

    /// <summary>Starts at the current end of the file: history from before profiling was switched on isn't replayed.</summary>
    public void Start()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _pos = File.Exists(_path) ? new FileInfo(_path).Length : 0;
        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(_path)!, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (_, _) => PollOnce();
        }
        catch { /* the safety timer below still picks everything up */ }
        _safety = new Timer(_ => PollOnce(), null, 1000, 1000);
    }

    public void PollOnce()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < _pos) { _pos = 0; _partial.Clear(); }          // truncated or rotated
                if (fs.Length == _pos) return;
                fs.Seek(_pos, SeekOrigin.Begin);
                var buf = new byte[fs.Length - _pos];
                var n = fs.Read(buf, 0, buf.Length);
                _pos += n;
                _partial.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
            catch (IOException) { return; }                                    // being written right now: next poll

            var text = _partial.ToString();
            var cut = text.LastIndexOf('\n');
            if (cut < 0) return;                                               // no complete line yet
            _partial.Clear().Append(text, cut + 1, text.Length - cut - 1);
            foreach (var line in text[..cut].Split('\n'))
                if (Parse(line.TrimEnd('\r'), _kind) is { } r) _store.Add(r);
        }
    }

    public static RequestRecord? Parse(string line, WebServerKind kind)
    {
        var f = line.Split('\t');
        var nginx = kind == WebServerKind.Nginx;
        if (f.Length < (nginx ? 9 : 8)) return null;
        var inv = CultureInfo.InvariantCulture;

        double durMs;
        DateTimeOffset end;
        double? upstream = null;
        int o;                                                                  // offset of "method"
        if (nginx)
        {
            if (!double.TryParse(f[0], NumberStyles.Float, inv, out var sec)) return null;
            if (!double.TryParse(f[1], NumberStyles.Float, inv, out var rt)) return null;
            end = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(sec * 1000));
            durMs = rt * 1000;
            var up = f[2].Split(',')[0].Trim();
            if (double.TryParse(up, NumberStyles.Float, inv, out var u)) upstream = u * 1000;
            o = 3;
        }
        else
        {
            if (!long.TryParse(f[0], NumberStyles.Integer, inv, out var ms)) return null;
            if (!long.TryParse(f[1], NumberStyles.Integer, inv, out var us)) return null;
            end = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            durMs = us / 1000.0;
            o = 2;
        }

        var uri = f[o + 2];
        if (uri.StartsWith("/__rs/", StringComparison.Ordinal)) return null;   // the profiler's own traffic
        if (uri.Length == 0 || uri == "-" || f[o] == "-") return null;         // a connection that never sent a request (nginx logs it as a 408)
        if (IsAsset(uri)) return null;                                          // images, scripts, styles, fonts...: only pages are profiled
        if (!int.TryParse(f[o + 3], NumberStyles.Integer, inv, out var status)) status = 0;
        long.TryParse(f[o + 4], NumberStyles.Integer, inv, out var bytes);

        return new RequestRecord
        {
            StartedAt = end - TimeSpan.FromMilliseconds(durMs),
            DurationMs = durMs,
            Method = f[o],
            Host = f[o + 1],
            Uri = uri,
            Status = status,
            Bytes = bytes,
            RequestId = nginx && f.Length > o + 5 ? f[o + 5] : "",
            UpstreamMs = upstream,
        };
    }

    static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "css", "scss", "sass", "less", "js", "mjs", "cjs", "jsx", "ts", "tsx", "vue", "map", "wasm",
        "png", "jpg", "jpeg", "gif", "webp", "avif", "apng", "bmp", "svg", "ico", "cur",
        "woff", "woff2", "ttf", "otf", "eot",
        "webmanifest", "mp3", "m4a", "ogg", "wav", "mp4", "webm",
    };

    static readonly string[] AssetPrefixes = { "/@vite", "/@id/", "/@fs/", "/@react-refresh", "/__vite", "/node_modules/" };

    /// <summary>True for a request that fetches a static file (or a Vite dev-server module) and not a page or an API call.</summary>
    public static bool IsAsset(string uri)
    {
        var end = uri.IndexOfAny(new[] { '?', '#' });
        var path = end >= 0 ? uri[..end] : uri;
        foreach (var p in AssetPrefixes)
            if (path.StartsWith(p, StringComparison.Ordinal)) return true;
        var dot = path.LastIndexOf('.');
        return dot > path.LastIndexOf('/') && AssetExtensions.Contains(path[(dot + 1)..]);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _safety?.Dispose();
    }
}
