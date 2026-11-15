using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RhapsodyServer.Core.Profiling;

/// <summary>What PHP itself reported about one request: exact process id, memory, files, and the id that ties it to the web server's log line.</summary>
public sealed class PhpRecord
{
    public long Id { get; internal set; }
    /// <summary>16 hex characters made by the collector and sent back to the web server as the X-RS-Request-Id header.</summary>
    public string RequestId { get; init; } = "";
    /// <summary>The php-cgi worker that ran the request. Each worker serves one request at a time, so its queries are exactly this request's.</summary>
    public int Pid { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public double DurationMs { get; init; }
    public long PeakMemoryBytes { get; init; }
    public int Includes { get; init; }
    public int Status { get; init; }
    public string Method { get; init; } = "";
    public string Host { get; init; } = "";
    public string Uri { get; init; } = "";
    /// <summary>The Xdebug cachegrind file this request wrote, when it was profiled with Xdebug.</summary>
    public string? ProfileFile { get; init; }
    public DateTimeOffset EndedAt => StartedAt.AddMilliseconds(DurationMs);
}

/// <summary>What the browser measured while loading one page (Core Web Vitals and navigation timing), sent by the probe script.</summary>
public sealed class BrowserRecord
{
    public string RequestId { get; init; } = "";
    public DateTimeOffset ReceivedAt { get; init; }
    public double? TtfbMs { get; init; }
    public double? FcpMs { get; init; }
    public double? LcpMs { get; init; }
    public double? Cls { get; init; }
    public double? InpMs { get; init; }
    public double? DomContentLoadedMs { get; init; }
    public double? LoadMs { get; init; }
    public long? TransferBytes { get; init; }
    public int? Resources { get; init; }
}

/// <summary>
/// The PHP file that PHP runs before every script (auto_prepend_file) while profiling is on. It writes one line per
/// request to a log that <see cref="PhpLogTailer"/> reads, adds a Server-Timing header, sends the request id back to
/// the web server, and (for page loads in a browser) adds the small probe script that reports Web Vitals.
/// Written for PHP 7.1 and newer: no typed properties, arrow functions or hrtime().
/// </summary>
public static class PhpCollectorScript
{
    public const string FileName = "rs_collect.php";
    public const string RequestIdHeader = "X-RS-Request-Id";

    /// <summary>The line layout the script writes. Tab-separated: URLs can't hold a raw tab, and the script strips them from every field.</summary>
    public const string LineVersion = "v1";

    public static string NewBeaconKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    static string PhpString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    /// <param name="logPath">Where the script appends its lines.</param>
    /// <param name="beaconKey">A random value the probe script sends back, so a web page on the internet can't plant fake timings.</param>
    /// <param name="injectProbe">Add the browser probe to HTML pages.</param>
    /// <param name="hintHosts">Site host names (lower case) that have template hints switched on.</param>
    /// <param name="hintBlocks">Also mark blocks, not only templates.</param>
    public static string Generate(string logPath, string beaconKey, bool injectProbe, IReadOnlyCollection<string>? hintHosts = null, bool hintBlocks = false) => $$"""
        <?php
        // Rhapsody Server profiler collector. Generated; manual edits are overwritten.
        // Runs before every PHP script while the profiler is on. It only observes: it never changes what your code returns,
        // except for two extra response headers and (page loads only) one <script> tag before </head>.
        if (PHP_SAPI === 'cli' || PHP_SAPI === 'phpdbg' || isset($GLOBALS['__rs_collector'])) { return; }
        $GLOBALS['__rs_collector'] = true;
        (function () {
            $log = {{PhpString(Util.Fwd(logPath))}};
            $key = {{PhpString(beaconKey)}};
            $probe = {{(injectProbe ? "true" : "false")}};
            $uri = isset($_SERVER['REQUEST_URI']) ? (string)$_SERVER['REQUEST_URI'] : '';
            if (strncmp($uri, '/__rs/', 6) === 0) { return; }
            $t0 = isset($_SERVER['REQUEST_TIME_FLOAT']) ? (float)$_SERVER['REQUEST_TIME_FLOAT'] : microtime(true);
            try { $id = bin2hex(random_bytes(8)); } catch (\Throwable $e) { $id = substr(md5(uniqid('', true)), 0, 16); }
            if (!headers_sent()) {
                header('{{RequestIdHeader}}: ' . $id, true);
                header_register_callback(function () use ($t0) {
                    // Runs just before PHP sends the headers, so this is "time until the response started".
                    $ms = round((microtime(true) - $t0) * 1000, 1);
                    $mem = round(memory_get_peak_usage(true) / 1048576, 1);
                    header('Server-Timing: php;dur=' . $ms . ';desc="PHP until response", mem;desc="' . $mem . ' MB peak", files;desc="' . count(get_included_files()) . ' files"', false);
                });
            }
            $method = isset($_SERVER['REQUEST_METHOD']) ? (string)$_SERVER['REQUEST_METHOD'] : '';
            $host = isset($_SERVER['HTTP_HOST']) ? (string)$_SERVER['HTTP_HOST'] : '';

            // Template hints: for sites that have them on, tell the framework (it only reacts in development) and let the page overlay load.
            $hintHosts = array({{string.Join(", ", (hintHosts ?? Array.Empty<string>()).Select(h => PhpString(h.ToLowerInvariant())))}});
            $hintOn = false;
            if ($hintHosts && in_array(strtolower(preg_replace('/:\d+$/', '', $host)), $hintHosts, true)) {
                $hintOn = true;
                $hv = '{{(hintBlocks ? "blocks" : "templates")}}';
                $_SERVER['{{TemplateHintsOverlay.EnvFlag}}'] = $hv; $_ENV['{{TemplateHintsOverlay.EnvFlag}}'] = $hv; @putenv('{{TemplateHintsOverlay.EnvFlag}}=' . $hv);
            }

            // Browser probe: only for a page load in a browser. Streaming responses (event streams, downloads) are left alone.
            $dest = isset($_SERVER['HTTP_SEC_FETCH_DEST']) ? (string)$_SERVER['HTTP_SEC_FETCH_DEST'] : '';
            $accept = isset($_SERVER['HTTP_ACCEPT']) ? (string)$_SERVER['HTTP_ACCEPT'] : '';
            $isPage = $dest !== '' ? ($dest === 'document') : (strpos($accept, 'text/html') !== false);
            if (($probe || $hintOn) && $method === 'GET' && $isPage && !ini_get('zlib.output_compression')) {
                $tag = '<script src="/__rs/probe.js" data-rid="' . $id . '" data-key="' . $key . '"' . ($hintOn ? ' data-hints="1"' : '') . ' defer></script>';
                $first = true; $done = false;
                ob_start(function ($buf) use (&$first, &$done, $tag) {
                    if ($done) { return $buf; }
                    if ($first) {
                        $first = false;
                        $ct = ''; $hasLen = false;
                        foreach (headers_list() as $h) {
                            if (stripos($h, 'Content-Type:') === 0) { $ct = $h; }
                            if (stripos($h, 'Content-Length:') === 0 || stripos($h, 'Content-Encoding:') === 0) { $hasLen = true; }
                        }
                        if ($hasLen || ($ct !== '' && stripos($ct, 'text/html') === false)) { $done = true; return $buf; }
                    }
                    $pos = stripos($buf, '</head>');
                    if ($pos === false) { $pos = stripos($buf, '<body'); }
                    $done = true;
                    return $pos === false ? $buf : substr($buf, 0, $pos) . $tag . substr($buf, $pos);
                }, 16384);
            }

            register_shutdown_function(function () use ($log, $id, $t0, $method, $host, $uri) {
                $clean = function ($s) { return str_replace(array("\t", "\r", "\n"), ' ', (string)$s); };
                $prof = function_exists('xdebug_get_profiler_filename') ? (string)@xdebug_get_profiler_filename() : '';
                $status = function_exists('http_response_code') ? (int)http_response_code() : 0;
                $line = implode("\t", array(
                    '{{LineVersion}}', $id, getmypid(), (int)round($t0 * 1000), round((microtime(true) - $t0) * 1000, 2),
                    memory_get_peak_usage(true), count(get_included_files()), $status ?: 200,
                    $clean($method), $clean($host), $clean($uri), $clean($prof)
                )) . "\n";
                @file_put_contents($log, $line, FILE_APPEND | LOCK_EX);
            });
        })();
        """.Replace("\r\n", "\n");
}

/// <summary>Follows a log file line by line, from the end of the file when it starts. The profiler's collector log uses this.</summary>
public sealed class LineTailer : IDisposable
{
    readonly string _path;
    readonly Action<string> _onLine;
    readonly object _gate = new();
    FileSystemWatcher? _watcher;
    Timer? _safety;
    long _pos;
    readonly StringBuilder _partial = new();

    public LineTailer(string path, Action<string> onLine) { _path = path; _onLine = onLine; }

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
        catch { /* the safety timer picks everything up */ }
        _safety = new Timer(_ => PollOnce(), null, 1000, 1000);
    }

    public void PollOnce()
    {
        string text;
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < _pos) { _pos = 0; _partial.Clear(); }
                if (fs.Length == _pos) return;
                fs.Seek(_pos, SeekOrigin.Begin);
                var buf = new byte[fs.Length - _pos];
                var n = fs.Read(buf, 0, buf.Length);
                _pos += n;
                _partial.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
            catch (IOException) { return; }

            text = _partial.ToString();
            var cut = text.LastIndexOf('\n');
            if (cut < 0) return;
            _partial.Clear().Append(text, cut + 1, text.Length - cut - 1);
            text = text[..cut];
        }
        foreach (var line in text.Split('\n')) _onLine(line.TrimEnd('\r'));
    }

    public void Dispose() { _watcher?.Dispose(); _safety?.Dispose(); }
}

/// <summary>Reads the collector's log into <see cref="PhpRecord"/>s.</summary>
public sealed class PhpLogTailer : IDisposable
{
    readonly LineTailer _tail;

    public PhpLogTailer(string path, ProfileStore store) =>
        _tail = new LineTailer(path, line => { if (Parse(line) is { } r) store.Add(r); });

    public void Start() => _tail.Start();
    public void PollOnce() => _tail.PollOnce();
    public void Dispose() => _tail.Dispose();

    /// <summary>v1 \t id \t pid \t start-ms \t duration-ms \t peak-bytes \t includes \t status \t method \t host \t uri \t xdebug-file</summary>
    public static PhpRecord? Parse(string line)
    {
        var f = line.Split('\t');
        if (f.Length < 11 || f[0] != PhpCollectorScript.LineVersion) return null;
        var inv = CultureInfo.InvariantCulture;
        if (!long.TryParse(f[3], NumberStyles.Integer, inv, out var startMs)) return null;
        if (!double.TryParse(f[4], NumberStyles.Float, inv, out var dur) || dur < 0) return null;
        int.TryParse(f[2], NumberStyles.Integer, inv, out var pid);
        long.TryParse(f[5], NumberStyles.Integer, inv, out var peak);
        int.TryParse(f[6], NumberStyles.Integer, inv, out var inc);
        int.TryParse(f[7], NumberStyles.Integer, inv, out var status);
        var uri = f[10];
        return new PhpRecord
        {
            RequestId = f[1],
            Pid = pid,
            StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(startMs),
            DurationMs = dur,
            PeakMemoryBytes = peak,
            Includes = inc,
            Status = status,
            Method = f[8],
            Host = f[9],
            Uri = uri,
            ProfileFile = f.Length > 11 && f[11].Length > 0 ? f[11] : null,
        };
    }
}
